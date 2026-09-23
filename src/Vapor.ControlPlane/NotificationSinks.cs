using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapor.ControlPlane;

/// <summary>
/// A sink-agnostic notification delivered to all matching <see cref="INotificationSink"/>s.
/// Normalizes the three broker event streams (job events, session events, auth challenges)
/// into a single shape.
/// </summary>
public sealed record NotificationEvent(
	string Id,
	string Category,
	string Type,
	string? JobId,
	string? AccountName,
	string? State,
	string? Message,
	DateTimeOffset Timestamp,
	IReadOnlyDictionary<string, object?>? Payload
);

/// <summary>
/// Sink-level filter rules. A null/empty set matches everything; set membership is
/// case-insensitive (build sets with <see cref="StringComparer.OrdinalIgnoreCase"/>).
/// </summary>
public sealed record NotificationRule(
	IReadOnlySet<string>? Categories = null,
	IReadOnlySet<string>? Types = null,
	IReadOnlySet<string>? Accounts = null
)
{
	public static NotificationRule MatchAll { get; } = new();

	public bool Matches(NotificationEvent e)
	{
		if (Categories is { Count: > 0 } && !Categories.Contains(e.Category))
		{
			return false;
		}

		if (Types is { Count: > 0 } && !Types.Contains(e.Type))
		{
			return false;
		}

		if (Accounts is { Count: > 0 } && (e.AccountName is null || !Accounts.Contains(e.AccountName)))
		{
			return false;
		}

		return true;
	}
}

/// <summary>
/// A delivery target for notifications. Implementations own their delivery mechanics
/// (HTTP, queue, log, …) and delivery counters; exceptions are caught by the dispatcher
/// so one failing sink never blocks the others.
/// </summary>
public interface INotificationSink
{
	string Name { get; }

	NotificationRule Rule { get; }

	/// <summary>Notifications delivered successfully since startup.</summary>
	long Sent { get; }

	/// <summary>Notifications abandoned after exhausting retries since startup.</summary>
	long Failed { get; }

	/// <summary>Retry attempts made since startup.</summary>
	long Retried { get; }

	Task HandleAsync(NotificationEvent notification, CancellationToken cancellationToken);
}

/// <summary>
/// Delivers notifications as signed HTTP POSTs (JSON) to a webhook endpoint. Transient
/// failures (non-2xx, network errors) retry with exponential backoff; when the retry
/// budget is exhausted the delivery fails (counted, logged upstream) and the sink moves on.
/// </summary>
public sealed class WebhookNotificationSink : INotificationSink, IDisposable
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly Uri _url;
	private readonly string? _secret;
	private readonly int _maxRetries;
	private readonly TimeSpan _baseDelay;
	private readonly HttpClient _http;
	private readonly bool _ownsHttp;
	private readonly ILogger _logger;
	private long _sent;
	private long _failed;
	private long _retried;

	public WebhookNotificationSink(
		Uri url,
		string? secret,
		int maxRetries,
		TimeSpan baseDelay,
		ILogger<WebhookNotificationSink> logger,
		HttpClient? httpClient = null)
	{
		_url = url;
		_secret = string.IsNullOrWhiteSpace(secret) ? null : secret;
		_maxRetries = Math.Max(0, maxRetries);
		_baseDelay = baseDelay;
		_logger = logger;
		_ownsHttp = httpClient is null;
		_http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
	}

	public void Dispose()
	{
		if (_ownsHttp)
		{
			_http.Dispose();
		}
	}

	public string Name => "webhook";

	public NotificationRule Rule { get; init; } = NotificationRule.MatchAll;

	public long Sent => Interlocked.Read(ref _sent);
	public long Failed => Interlocked.Read(ref _failed);
	public long Retried => Interlocked.Read(ref _retried);

	public async Task HandleAsync(NotificationEvent notification, CancellationToken cancellationToken)
	{
		string body = JsonSerializer.Serialize(BuildEnvelope(notification), SerializerOptions);
		Exception? lastError = null;

		for (int attempt = 0; ; attempt++)
		{
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Post, _url)
				{
					Content = new StringContent(body, Encoding.UTF8, "application/json"),
				};
				if (_secret is not null)
				{
					ApplySignature(request, body);
				}

				using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
				{
					Interlocked.Increment(ref _sent);
					if (attempt > 0)
					{
						_logger.LogInformation(
							"Webhook delivered after {Attempts} attempts ({NotificationCategory}/{NotificationType})",
							attempt + 1, notification.Category, notification.Type);
					}

					return;
				}

				lastError = new HttpRequestException($"webhook returned {(int)response.StatusCode}");
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				lastError = ex;
			}

			if (attempt >= _maxRetries)
			{
				break;
			}

			Interlocked.Increment(ref _retried);
			TimeSpan delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}

		Interlocked.Increment(ref _failed);
		throw lastError!; // non-null by loop invariant: success returns inside the loop, every failure path assigns lastError, and the OCE rethrow never reaches here
	}

	private static Dictionary<string, object?> BuildEnvelope(NotificationEvent n) => new()
	{
		["id"] = n.Id,
		["category"] = n.Category,
		["type"] = n.Type,
		["jobId"] = n.JobId,
		["accountName"] = n.AccountName,
		["state"] = n.State,
		["message"] = n.Message,
		["timestamp"] = n.Timestamp,
		["payload"] = n.Payload,
	};

	private void ApplySignature(HttpRequestMessage request, string body)
	{
		long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		string signedPayload = $"{timestamp}.{body}";

		using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret!));
		byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));

		request.Headers.Add("X-Vapor-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
		request.Headers.Add("X-Vapor-Signature", $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}");
	}
}
