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
	/// Maximum number of retry attempts.
	/// </summary>
	public int MaxRetries { get; init; } = 5;

	/// <summary>
	/// Delay between retries in milliseconds.
	/// </summary>
	public int RetryDelayMs { get; init; } = 1000;

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
/// </summary>
public sealed class SteamWebHandler : IDisposable
{
	private readonly SteamWebHandlerConfig _config;
	private readonly ILogger<SteamWebHandler> _logger;
	private readonly Dictionary<string, string> _sessionCookies = new();
	private readonly Dictionary<string, string> _loginCookies = new();
	private readonly HttpClient _httpClient;
	private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
	private int _requestCount;
	private DateTime _lastRequestTime = DateTime.MinValue;
	private bool _disposed;

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
			DefaultRequestHeaders =
			{
				{ "User-Agent", _config.UserAgent }
			},
			Timeout = TimeSpan.FromSeconds(_config.RequestTimeout)
		};
	}

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
/// Clears all session and login cookies.
/// </summary>
	public void ClearCookies()
	{
		_sessionCookies.Clear();
		_loginCookies.Clear();
		_logger.LogDebug("Cookies cleared");
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

		foreach (var kvp in _loginCookies)
		{
			allCookies[kvp.Key] = kvp.Value;
		}

		return allCookies;
	}

	private async Task<SteamWebResponse> SendRequestAsync(
		System.Net.Http.HttpMethod method,
		Uri url,
		HttpContent? content,
		Dictionary<string, string>? headers,
		CancellationToken cancellationToken)
	{
		ThrowIfDisposed();

		// Rate limiting
		await ApplyRateLimitingAsync(cancellationToken).ConfigureAwait(false);

		for (int attempt = 0; attempt < _config.MaxRetries; attempt++)
		{
			if (attempt > 0)
			{
				await Task.Delay(_config.RetryDelayMs, cancellationToken).ConfigureAwait(false);
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

				return new SteamWebResponse(
					response.StatusCode,
					statusCode,
					responseHeaders,
					responseBody
				);
			}
			catch (HttpRequestException ex) when (attempt < _config.MaxRetries - 1)
			{
				_logger.LogWarning(ex, "Request attempt {Attempt}/{MaxRetries} failed", attempt + 1, _config.MaxRetries);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Request failed after {Attempts} attempts", attempt + 1);
				throw;
			}
		}

		throw new InvalidOperationException("Request failed after all retry attempts");
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

		// Add login cookies
		foreach (var kvp in _loginCookies)
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

			// Simple rate limiting: max 1 request per second
			if (timeSinceLastRequest.TotalMilliseconds < 1000)
			{
				var delayMs = 1000 - (int)timeSinceLastRequest.TotalMilliseconds;
				if (delayMs > 0)
				{
					await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
				}
			}

			_lastRequestTime = now;
			_requestCount++;
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
