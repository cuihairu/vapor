using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// Configuration for SteamWebHandler.
/// </summary>
public sealed record SteamWebHandlerConfig
{
	/// <summary>
	/// User agent string to use for requests.
	/// </summary>
	public string UserAgent { get; init; } = "Vapor/1.0";

	/// <summary>
	/// Connection timeout in seconds.
	/// </summary>
	public int ConnectionTimeout { get; init; } = 30;

	/// <summary>
	/// Request timeout in seconds.
	/// </summary>
	public int RequestTimeout { get; init; } = 60;

	/// <summary>
	/// Maximum number of retry attempts (including the initial attempt).
	/// </summary>
	public int MaxRetries { get; init; } = 5;

	/// <summary>
	/// Base delay between retries in milliseconds; 5xx/network errors back off exponentially.
	/// </summary>
	public int RetryDelayMs { get; init; } = 1000;

	/// <summary>
	/// Minimum interval between outgoing requests (client-side throttling). 0 disables.
	/// </summary>
	public int RateLimitIntervalMs { get; init; } = 1000;

	/// <summary>
	/// Upper bound for a single retry delay, in milliseconds.
	/// </summary>
	public int MaxRetryDelayMs { get; init; } = 30_000;

	/// <summary>
	/// Upper bound applied to the Retry-After header on 429 responses, in seconds.
	/// </summary>
	public int MaxRetryAfterSeconds { get; init; } = 60;

	/// <summary>
	/// Whether the circuit breaker rejects requests while open.
	/// </summary>
	public bool EnableCircuitBreaker { get; init; } = true;

	/// <summary>
	/// Consecutive failures before the circuit breaker opens.
	/// </summary>
	public int CircuitBreakerFailureThreshold { get; init; } = 10;

	/// <summary>
	/// How long the circuit breaker stays open before allowing a half-open probe.
	/// </summary>
	public TimeSpan CircuitBreakerOpenDuration { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Base URL for Steam Community.
	/// </summary>
	public Uri SteamCommunityUrl { get; init; } = new("https://steamcommunity.com");

	/// <summary>
	/// Base URL for Steam Store.
	/// </summary>
	public Uri SteamStoreUrl { get; init; } = new("https://store.steampowered.com");

	/// <summary>
	/// Base URL for Steam Help.
	/// </summary>
	public Uri SteamHelpUrl { get; init; } = new("https://help.steampowered.com");
}

/// <summary>
/// Handles Steam Web API requests with session management and cookie handling.
/// Retry policy distinguishes rate limiting (429, honoring Retry-After) from
/// server errors (5xx, exponential backoff); a circuit breaker trips after
/// repeated failures and metrics are collected for observability.
/// </summary>
public sealed class SteamWebHandler : IDisposable
{
	private readonly SteamWebHandlerConfig _config;
	private readonly ILogger<SteamWebHandler> _logger;
	private readonly Dictionary<string, string> _sessionCookies = new();
	private readonly HttpClient _httpClient;
	private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
	private readonly HttpCircuitBreaker _circuitBreaker;
	private int _requestCount;
	private DateTime _lastRequestTime = DateTime.MinValue;
	private bool _disposed;

	/// <summary>
	/// Request counters exposed for observability.
	/// </summary>
	public WebRequestMetrics Metrics { get; } = new();

	public SteamWebHandler(SteamWebHandlerConfig config, ILogger<SteamWebHandler> logger)
	{
		_config = config ?? new SteamWebHandlerConfig();
		_logger = logger;

		var handler = new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			AutomaticDecompression = System.Net.DecompressionMethods.All,
			PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15)
		};

		_httpClient = new(handler)
		{
			Timeout = TimeSpan.FromSeconds(_config.RequestTimeout)
		};
		_circuitBreaker = new HttpCircuitBreaker(_config.CircuitBreakerFailureThreshold, _config.CircuitBreakerOpenDuration);
	}

	internal SteamWebHandler(SteamWebHandlerConfig config, ILogger<SteamWebHandler> logger, HttpMessageHandler handler)
	{
		_config = config ?? new SteamWebHandlerConfig();
		_logger = logger;
		_httpClient = new(handler)
		{
			Timeout = TimeSpan.FromSeconds(_config.RequestTimeout)
		};
		_circuitBreaker = new HttpCircuitBreaker(_config.CircuitBreakerFailureThreshold, _config.CircuitBreakerOpenDuration);
	}

	/// <summary>
	/// Current circuit breaker state.
	/// </summary>
	public CircuitBreakerState CircuitState => _circuitBreaker.State;

	/// <summary>
	/// Gets the underlying HttpClient for advanced scenarios.
	/// </summary>
	public HttpClient HttpClient => _httpClient;

	/// <summary>
	/// Performs a GET request to the specified URL.
	/// </summary>
	public async Task<SteamWebResponse> GetAsync(
		Uri url,
		Dictionary<string, string>? headers = null,
		CancellationToken cancellationToken = default)
	{
		if (url == null)
		{
			throw new ArgumentNullException(nameof(url));
		}

		return await SendRequestAsync(System.Net.Http.HttpMethod.Get, url, null, headers, cancellationToken);
	}

	/// <summary>
	/// Performs a POST request to the specified URL.
	/// </summary>
	public async Task<SteamWebResponse> PostAsync(
		Uri url,
		HttpContent? content,
		Dictionary<string, string>? headers = null,
		CancellationToken cancellationToken = default)
	{
		if (url == null)
		{
			throw new ArgumentNullException(nameof(url));
		}

		return await SendRequestAsync(System.Net.Http.HttpMethod.Post, url, content, headers, cancellationToken);
	}

	/// <summary>
	/// Sets session cookies for Steam authentication.
	/// </summary>
	public void SetSessionCookies(string sessionId, string steamLoginSecure)
	{
		if (string.IsNullOrEmpty(sessionId))
		{
			throw new ArgumentNullException(nameof(sessionId));
		}

		if (string.IsNullOrEmpty(steamLoginSecure))
		{
			throw new ArgumentNullException(nameof(steamLoginSecure));
		}

		_sessionCookies["sessionid"] = sessionId;
		_sessionCookies["steamLogin"] = steamLoginSecure;
		_sessionCookies["steamLoginSecure"] = steamLoginSecure;

		_logger.LogDebug("Session cookies updated");
	}

	/// <summary>
	/// Clears all session cookies.
	/// </summary>
	public void ClearCookies()
	{
		_sessionCookies.Clear();
		_logger.LogDebug("Cookies cleared");
	}

	/// <summary>
	/// Returns the current session id, when one has been set. Some community
	/// endpoints require it echoed in the POST body (e.g. market listing
	/// cancellation); it is a plain session cookie, not a credential.
	/// </summary>
	public bool TryGetSessionId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sessionId)
	{
		return _sessionCookies.TryGetValue("sessionid", out sessionId) && !string.IsNullOrEmpty(sessionId);
	}

	/// <summary>
	/// Gets all current cookies as a dictionary.
	/// </summary>
	public IReadOnlyDictionary<string, string> GetAllCookies()
	{
		var allCookies = new Dictionary<string, string>();

		foreach (var kvp in _sessionCookies)
		{
			allCookies[kvp.Key] = kvp.Value;
		}

		return allCookies;
	}

	/// <summary>
	/// Resolves the logged-on user's SteamID from the session cookies (the
	/// steamlogin cookies encode it as "steamid%7C%7Ctoken"). Returns null when
	/// the cookies do not carry a usable identity.
	/// </summary>
	public ulong? TryResolveOwnSteamId()
	{
		var cookies = GetAllCookies();

		foreach (string cookieName in new[] { "steamLoginSecure", "steamLogin", "steamlogin[secure]", "steamlogin" })
		{
			if (!cookies.TryGetValue(cookieName, out string? value) || string.IsNullOrEmpty(value))
			{
				continue;
			}

			// Cookie format: "<steamid>%7C%7C<token>" (URL-encoded pipe separators).
			string steamIdPart = value.Split("%7C%7C")[0].Split('|')[0];
			if (ulong.TryParse(steamIdPart, out ulong steamId) && steamId >= 76561197960265728UL)
			{
				return steamId;
			}
		}

		return null;
	}

	private async Task<SteamWebResponse> SendRequestAsync(
		System.Net.Http.HttpMethod method,
		Uri url,
		HttpContent? content,
		Dictionary<string, string>? headers,
		CancellationToken cancellationToken)
	{
		ThrowIfDisposed();

		if (_config.EnableCircuitBreaker && !_circuitBreaker.TryAllowRequest())
		{
			Metrics.RecordCircuitBreakerRejection();
			throw new CircuitBreakerOpenException(
				$"Circuit breaker is open; request to {url.Host} was rejected without hitting the network");
		}

		// Rate limiting
		await ApplyRateLimitingAsync(cancellationToken).ConfigureAwait(false);

		Exception? lastException = null;

		for (int attempt = 0; attempt < _config.MaxRetries; attempt++)
		{
			if (attempt > 0)
			{
				Metrics.RecordRetry();
				await Task.Delay(_pendingRetryDelayMs, cancellationToken).ConfigureAwait(false);
			}

			try
			{
				using var request = CreateRequest(method, url, content, headers, cancellationToken);
				using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

				var statusCode = (int)response.StatusCode;
				var responseHeaders = new Dictionary<string, string>();

				foreach (var header in response.Headers)
				{
					responseHeaders[header.Key] = string.Join(", ", header.Value);
				}

				string? responseBody = null;
				if (response.Content != null)
				{
					responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
				}

				var webResponse = new SteamWebResponse(
					response.StatusCode,
					statusCode,
					responseHeaders,
					responseBody
				);

				if (statusCode == 429)
				{
					Metrics.RecordRateLimited();
					RecordBreakerFailure();

					if (attempt < _config.MaxRetries - 1)
					{
						// Rate limited: honor Retry-After when present, bounded by config.
						int retryAfterMs = ResolveRetryAfterMs(responseHeaders);
						_pendingRetryDelayMs = retryAfterMs;
						_logger.LogWarning(
							"Rate limited (429) on attempt {Attempt}/{MaxRetries} for {Url}; retrying in {DelayMs}ms",
							attempt + 1, _config.MaxRetries, url, retryAfterMs);
						continue;
					}
				}
				else if (statusCode >= 500)
				{
					Metrics.RecordServerError();
					RecordBreakerFailure();

					if (attempt < _config.MaxRetries - 1)
					{
						// Server error: exponential backoff.
						_pendingRetryDelayMs = ComputeExponentialBackoffMs(attempt);
						_logger.LogWarning(
							"Server error ({StatusCode}) on attempt {Attempt}/{MaxRetries} for {Url}; retrying in {DelayMs}ms",
							statusCode, attempt + 1, _config.MaxRetries, url, _pendingRetryDelayMs);
						continue;
					}
				}
				else if (statusCode >= 400)
				{
					Metrics.RecordClientError();
					RecordBreakerSuccess();
				}
				else
				{
					Metrics.RecordSuccess();
					RecordBreakerSuccess();
				}

				return webResponse;
			}
			catch (HttpRequestException ex) when (attempt < _config.MaxRetries - 1)
			{
				lastException = ex;
				Metrics.RecordNetworkFailure();
				RecordBreakerFailure();
				_pendingRetryDelayMs = ComputeExponentialBackoffMs(attempt);
				_logger.LogWarning(ex, "Request attempt {Attempt}/{MaxRetries} failed", attempt + 1, _config.MaxRetries);
			}
			catch (Exception ex)
			{
				Metrics.RecordNetworkFailure();
				RecordBreakerFailure();
				_logger.LogError(ex, "Request failed after {Attempts} attempts", attempt + 1);
				throw;
			}
		}

		throw new InvalidOperationException(
			$"Request failed after all retry attempts", lastException);
	}

	private int _pendingRetryDelayMs;

	private int ComputeExponentialBackoffMs(int attempt)
	{
		long delay = (long)_config.RetryDelayMs * (1 << Math.Min(attempt, 10));
		return (int)Math.Min(Math.Max(delay, 1), _config.MaxRetryDelayMs);
	}

	private int ResolveRetryAfterMs(IReadOnlyDictionary<string, string> responseHeaders)
	{
		if (responseHeaders.TryGetValue("Retry-After", out var value) &&
			int.TryParse(value.Trim(), out int seconds) && seconds > 0)
		{
			int capped = Math.Min(seconds, _config.MaxRetryAfterSeconds);
			return capped * 1000;
		}

		// No Retry-After: fall back to a conservative rate-limit delay.
		return Math.Min(_config.RetryDelayMs * 4, _config.MaxRetryDelayMs);
	}

	private void RecordBreakerSuccess()
	{
		if (_config.EnableCircuitBreaker)
		{
			_circuitBreaker.RecordSuccess();
		}
	}

	private void RecordBreakerFailure()
	{
		if (_config.EnableCircuitBreaker)
		{
			_circuitBreaker.RecordFailure();
		}
	}

	private HttpRequestMessage CreateRequest(
		System.Net.Http.HttpMethod method,
		Uri url,
		HttpContent? content,
		Dictionary<string, string>? headers,
		CancellationToken cancellationToken)
	{
		var request = new HttpRequestMessage
		{
			Method = method,
			RequestUri = url
		};

		if (content != null)
		{
			request.Content = content;
		}

		// Add default headers
		if (!string.IsNullOrEmpty(_config.UserAgent))
		{
			request.Headers.TryAddWithoutValidation("User-Agent", _config.UserAgent);
		}

		// Add Steam-specific headers
		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
		request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
		request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

		// Add session cookies
		foreach (var kvp in _sessionCookies)
		{
			request.Headers.TryAddWithoutValidation("Cookie", $"{kvp.Key}={kvp.Value}");
		}

		// Add custom headers
		if (headers != null)
		{
			foreach (var header in headers)
			{
				request.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		// Referer
		if (url.Host.Contains("steamcommunity.com"))
		{
			request.Headers.TryAddWithoutValidation("Referer", _config.SteamCommunityUrl.ToString());
		}
		else if (url.Host.Contains("steampowered.com"))
		{
			request.Headers.TryAddWithoutValidation("Referer", _config.SteamStoreUrl.ToString());
		}

		// Origin header
		if (url.Host.Contains("steamcommunity.com"))
		{
			request.Headers.TryAddWithoutValidation("Origin", _config.SteamCommunityUrl.ToString());
		}
		else if (url.Host.Contains("steampowered.com"))
		{
			request.Headers.TryAddWithoutValidation("Origin", _config.SteamStoreUrl.ToString());
		}

		return request;
	}

	private async Task ApplyRateLimitingAsync(CancellationToken cancellationToken)
	{
		await _rateLimitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var now = DateTime.UtcNow;
			var timeSinceLastRequest = now - _lastRequestTime;

			// Client-side throttling between outgoing requests.
			if (timeSinceLastRequest.TotalMilliseconds < _config.RateLimitIntervalMs)
			{
				var delayMs = _config.RateLimitIntervalMs - (int)timeSinceLastRequest.TotalMilliseconds;
				if (delayMs > 0)
				{
					await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
				}
			}

			_lastRequestTime = now;
			_requestCount++;
			Metrics.RecordTotal();
		}
		finally
		{
			_rateLimitLock.Release();
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamWebHandler));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_httpClient.Dispose();
		_rateLimitLock.Dispose();
		_disposed = true;
	}
}

/// <summary>
/// Response from a Steam Web API request.
/// </summary>
public sealed record SteamWebResponse(
	System.Net.HttpStatusCode StatusCode,
	int StatusCodeNumber,
	IReadOnlyDictionary<string, string> Headers,
	string? Body = null
)
{
	public bool IsSuccess => (int)StatusCode >= 200 && (int)StatusCode < 300;
	public bool IsRedirect => (int)StatusCode >= 300 && (int)StatusCode < 400;
	public bool IsClientError => (int)StatusCode >= 400 && (int)StatusCode < 500;
	public bool IsServerError => (int)StatusCode >= 500;
}
