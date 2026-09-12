using System.Text.Json;

namespace Vapor.Steam.Core.Caching;

/// <summary>
/// JSON envelope stored in Redis for <see cref="RedisVaporCache"/>: the serialized
/// payload plus absolute fresh/stale expiry timestamps (epoch milliseconds). Absolute
/// timestamps keep entry semantics consistent across instances with skewed clocks;
/// the Redis key TTL is only the outer safety net (fresh + stale window).
/// </summary>
internal static class RedisCacheEntry
{
	public static string Encode<T>(T value, DateTimeOffset? freshExpiresAt, DateTimeOffset? staleExpiresAt) where T : class
	{
		ArgumentNullException.ThrowIfNull(value);

		string payload = JsonSerializer.Serialize(value);
		return JsonSerializer.Serialize(new Envelope(
			V: payload,
			E: freshExpiresAt?.ToUnixTimeMilliseconds(),
			S: staleExpiresAt?.ToUnixTimeMilliseconds()));
	}

	/// <summary>Decodes an envelope; returns false for any malformed or foreign payload.</summary>
	public static bool TryDecode(string json, out string? payload, out long? freshExpiresAtMs, out long? staleExpiresAtMs)
	{
		Envelope? envelope;
		try
		{
			envelope = JsonSerializer.Deserialize<Envelope>(json);
		}
		catch (JsonException)
		{
			envelope = null;
		}

		if (envelope?.V is null)
		{
			payload = null;
			freshExpiresAtMs = null;
			staleExpiresAtMs = null;
			return false;
		}

		payload = envelope.V;
		freshExpiresAtMs = envelope.E;
		staleExpiresAtMs = envelope.S;
		return true;
	}

	/// <summary>True while the entry may be served as a cache hit (null expiry = never expires).</summary>
	public static bool IsFresh(long? freshExpiresAtMs, DateTimeOffset now) =>
		freshExpiresAtMs is null || freshExpiresAtMs.Value > now.ToUnixTimeMilliseconds();

	/// <summary>True when the fresh window has lapsed but the stale-while-revalidate window is still open.</summary>
	public static bool IsStaleServable(long? freshExpiresAtMs, long? staleExpiresAtMs, DateTimeOffset now) =>
		!IsFresh(freshExpiresAtMs, now)
		&& staleExpiresAtMs is not null
		&& staleExpiresAtMs.Value > now.ToUnixTimeMilliseconds();

	private sealed record Envelope(string? V, long? E, long? S);
}
