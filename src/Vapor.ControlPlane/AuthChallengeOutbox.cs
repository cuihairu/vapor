using System.Collections.Concurrent;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Short-lived in-memory outbox for <c>code_provided_*</c> auth-challenge
/// deliveries (ARCH-09). The submit endpoint stages a code here <em>before</em>
/// it clears the pending challenge and publishes the live SSE event, so an agent
/// whose SSE stream is disconnected (or reconnecting) still receives the code
/// when it re-subscribes. Delivery is confirmed by the owning agent's SSE stream
/// and the entry is removed; otherwise it expires after a short TTL. This is not
/// a general SSE history log: only the agent that owns the account gets a replay.
/// </summary>
public sealed class AuthChallengeOutbox
{
	private readonly ConcurrentDictionary<string, PendingDelivery> _pending = new(StringComparer.OrdinalIgnoreCase);
	private readonly TimeProvider _timeProvider;

	public AuthChallengeOutbox(TimeProvider? timeProvider = null)
	{
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <summary>Number of staged (unconfirmed) code deliveries.</summary>
	public int Count => _pending.Count;

	/// <summary>
	/// Stages (or replaces) the pending code delivery for an account, stamping the
	/// event timestamp with the outbox clock so a replayed event is not stale.
	/// </summary>
	public void Stage(AuthChallengeEvent e, TimeSpan ttl)
	{
		DateTimeOffset now = _timeProvider.GetUtcNow();
		TimeSpan remainingBeforeMax = DateTimeOffset.MaxValue - now;
		DateTimeOffset expiresAt = ttl <= TimeSpan.Zero
			? now
			: now + TimeSpan.FromTicks(Math.Min(ttl.Ticks, remainingBeforeMax.Ticks));

		_pending[e.AccountName.Trim()] = new PendingDelivery(e with { Timestamp = now }, expiresAt);
	}

	/// <summary>Reads a staged delivery for an account using the outbox clock.</summary>
	public AuthChallengeEvent? TryGet(string accountName) => TryGet(accountName, _timeProvider.GetUtcNow());

	/// <summary>
	/// Reads a staged delivery at a supplied time; an expired entry is removed and
	/// reported as absent. Blank account names never match.
	/// </summary>
	public AuthChallengeEvent? TryGet(string accountName, DateTimeOffset now)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return null;
		}

		string key = accountName.Trim();
		if (!_pending.TryGetValue(key, out PendingDelivery? pending))
		{
			return null;
		}

		if (pending.ExpiresAt <= now)
		{
			_pending.TryRemove(new KeyValuePair<string, PendingDelivery>(key, pending));
			return null;
		}

		return pending.Event;
	}

	/// <summary>
	/// Confirms delivery: removes the staged entry whose challenge type and code
	/// match the delivered event. A newer staged entry for the same account (a
	/// subsequent submit) is left intact.
	/// </summary>
	public void Acknowledge(string accountName, string? challengeType, string? code)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return;
		}

		string key = accountName.Trim();
		if (_pending.TryGetValue(key, out PendingDelivery? pending)
			&& string.Equals(pending.Event.ChallengeType, challengeType, StringComparison.Ordinal)
			&& string.Equals(pending.Event.Code, code, StringComparison.Ordinal))
		{
			_pending.TryRemove(new KeyValuePair<string, PendingDelivery>(key, pending));
		}
	}

	/// <summary>Removes every expired entry using the outbox clock.</summary>
	public int Expire() => Expire(_timeProvider.GetUtcNow());

	/// <summary>Removes every entry whose expiry is at or before <paramref name="now"/>.</summary>
	public int Expire(DateTimeOffset now)
	{
		int removed = 0;
		foreach (KeyValuePair<string, PendingDelivery> entry in _pending)
		{
			if (entry.Value.ExpiresAt <= now
				&& _pending.TryRemove(new KeyValuePair<string, PendingDelivery>(entry.Key, entry.Value)))
			{
				removed++;
			}
		}

		return removed;
	}

	private sealed record PendingDelivery(AuthChallengeEvent Event, DateTimeOffset ExpiresAt);
}
