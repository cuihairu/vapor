using System.Collections.Concurrent;

namespace SteamControl.ControlPlane;

public sealed record SessionSnapshot(
	string AccountName,
	string State,
	string EventType,
	string? Message,
	DateTimeOffset UpdatedAt
);

public sealed class SessionTracker
{
	private readonly ConcurrentDictionary<string, SessionSnapshot> _sessions = new(StringComparer.OrdinalIgnoreCase);

	public void Update(string accountName, string eventType, string state, string? message, DateTimeOffset? updatedAt = null)
	{
		var snapshot = new SessionSnapshot(
			AccountName: accountName,
			State: state,
			EventType: eventType,
			Message: message,
			UpdatedAt: updatedAt ?? DateTimeOffset.UtcNow
		);

		_sessions.AddOrUpdate(accountName, snapshot, (_, __) => snapshot);
	}

	public IReadOnlyList<SessionSnapshot> List()
	{
		return _sessions.Values
			.OrderBy(s => s.AccountName, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}
}

