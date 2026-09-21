using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Steam;

/// <summary>
/// Tracks the offset between the local clock and Steam's server time. TOTP codes and
/// confirmation hashes must be computed against Steam time; when the offset has been
/// synced it is applied automatically, otherwise local time is used.
/// </summary>
public sealed class SteamTimeSynchronizer
{
	/// <summary>Steam's QueryTime endpoint used to obtain server time.</summary>
	public static readonly Uri QueryTimeEndpoint =
		new("https://api.steampowered.com/ITwoFactorService/QueryTime/v1/");

	private readonly TimeProvider _timeProvider;
	private readonly Func<CancellationToken, Task<long>> _serverTimeQuery;
	private readonly ILogger? _logger;
	private long _offsetSeconds;

	public SteamTimeSynchronizer(
		Func<CancellationToken, Task<long>> serverTimeQuery,
		TimeProvider? timeProvider = null,
		ILogger? logger = null)
	{
		_serverTimeQuery = serverTimeQuery ?? throw new ArgumentNullException(nameof(serverTimeQuery));
		_timeProvider = timeProvider ?? TimeProvider.System;
		_logger = logger;
	}

	/// <summary>Current offset (server time minus local time) in seconds.</summary>
	public long OffsetSeconds => Interlocked.Read(ref _offsetSeconds);

	/// <summary>Whether a sync has ever succeeded.</summary>
	public bool HasSynced { get; private set; }

	/// <summary>UTC time of the last successful sync, if any.</summary>
	public DateTimeOffset? LastSyncedAt { get; private set; }

	/// <summary>
	/// Current Steam time as Unix seconds: local time plus the synced offset.
	/// </summary>
	public long GetCurrentSteamTime() => _timeProvider.GetUtcNow().ToUnixTimeSeconds() + OffsetSeconds;

	/// <summary>
	/// Queries Steam for the current server time and stores the local/server offset.
	/// On failure the previous offset is kept.
	/// </summary>
	public async Task SyncAsync(CancellationToken cancellationToken = default)
	{
		var localBefore = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
		var serverTime = await _serverTimeQuery(cancellationToken).ConfigureAwait(false);
		var localAfter = _timeProvider.GetUtcNow().ToUnixTimeSeconds();

		// Assume the request lands roughly midway; halves round-trip error.
		var localMidpoint = (localBefore + localAfter) / 2;
		var offset = serverTime - localMidpoint;

		Interlocked.Exchange(ref _offsetSeconds, offset);
		HasSynced = true;
		LastSyncedAt = _timeProvider.GetUtcNow();

		_logger?.LogInformation("Steam time synced: offset {OffsetSeconds}s", offset);
	}

	/// <summary>
	/// Default server-time query: POSTs to Steam's ITwoFactorService/QueryTime endpoint and
	/// parses the server_time field from the response. Shared by every consumer that does
	/// not supply its own query delegate.
	/// </summary>
	/// <remarks>
	/// Excluded: a thin forwarder to the endpoint-injectable overload (which the
	/// stub-server tests drive directly). The static-readonly endpoint cannot be
	/// re-pointed from tests on .NET 10 (initonly setter throws), so the forwarder
	/// itself is only exercisable against the real network — see tests/TESTING.md.
	/// </remarks>
	[ExcludeFromCodeCoverage]
	public static Task<long> QuerySteamServerTimeAsync(CancellationToken cancellationToken)
	{
		return QuerySteamServerTimeAsync(QueryTimeEndpoint, cancellationToken);
	}

	/// <summary>Endpoint-injectable variant (internal so tests can point at a local stub).</summary>
	internal static async Task<long> QuerySteamServerTimeAsync(Uri endpoint, CancellationToken cancellationToken)
	{
		using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		using var content = new System.Net.Http.FormUrlEncodedContent(new Dictionary<string, string>());
		using var response = await httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

		response.EnsureSuccessStatusCode();

		string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		using var doc = System.Text.Json.JsonDocument.Parse(body);

		if (doc.RootElement.TryGetProperty("response", out var responseElem)
			&& responseElem.TryGetProperty("server_time", out var serverTimeElem)
			&& long.TryParse(serverTimeElem.GetString(), out var serverTime))
		{
			return serverTime;
		}

		throw new InvalidOperationException("Steam QueryTime response did not contain response.server_time");
	}
}
