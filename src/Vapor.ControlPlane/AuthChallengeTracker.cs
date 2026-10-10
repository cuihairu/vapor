using System.Collections.Concurrent;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

public sealed class AuthChallengeTracker
{
	private readonly ConcurrentDictionary<string, AuthChallengeEvent> _pending = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Raises (or replaces) the pending challenge for an account and returns the
	/// stored record with <see cref="AuthChallengeEvent.Attempt"/> materialized:
	/// 1 on the first raise, previous+1 on every re-raise, so operators can see
	/// how many times an account has been stuck on a prompt.
	/// </summary>
	public AuthChallengeEvent Upsert(AuthChallengeEvent e)
	{
		return _pending.AddOrUpdate(
			e.AccountName,
			e with { Attempt = 1 },
			(_, old) => e with { Attempt = old.Attempt + 1 });
	}

	public void Clear(string accountName)
	{
		_pending.TryRemove(accountName, out _);
	}

	public IReadOnlyList<AuthChallengeEvent> List()
	{
		return _pending.Values
			.OrderByDescending(e => e.Timestamp)
			.ToList();
	}

	public AuthChallengeEvent? Get(string accountName)
	{
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return null;
		}

		return _pending.TryGetValue(accountName.Trim(), out AuthChallengeEvent? challenge) ? challenge : null;
	}
}


