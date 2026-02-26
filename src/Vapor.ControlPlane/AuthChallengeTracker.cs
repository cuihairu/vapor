using System.Collections.Concurrent;

namespace Vapor.ControlPlane;

public sealed class AuthChallengeTracker
{
	private readonly ConcurrentDictionary<string, AuthChallengeEvent> _pending = new(StringComparer.OrdinalIgnoreCase);

	public void Upsert(AuthChallengeEvent e)
	{
		_pending.AddOrUpdate(e.AccountName, e, (_, __) => e);
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
}


