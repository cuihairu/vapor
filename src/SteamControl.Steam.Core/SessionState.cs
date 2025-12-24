namespace SteamControl.Steam.Core;

public enum SessionState
{
	Disconnected,
	Connecting,
	ConnectingWaitAuthCode,
	ConnectingWait2FA,
	Connected,
	Reconnecting,
	DisconnectedByUser,
	Disconnecting,
	FatalError
}

public enum SessionEventType
{
	StateChanged,
	AuthCodeNeeded,
	TwoFactorCodeNeeded,
	Connected,
	Disconnected,
	Error
}

public sealed record SessionEvent(
	SessionEventType Type,
	string AccountName,
	SessionState? NewState = null,
	string? Message = null
)
{
	public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

	public SessionEvent()
		: this(default, string.Empty, null, null)
	{
	}

	public SessionEvent(
		SessionEventType type,
		string accountName,
		SessionState? newState,
		string? message,
		DateTimeOffset timestamp)
		: this(type, accountName, newState, message)
	{
		Timestamp = timestamp;
	}
}
