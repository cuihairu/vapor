using Microsoft.Extensions.Logging;

namespace SteamControl.Steam.Core.Actions;

public sealed class IdleAction : IAction
{
	private readonly ILogger<IdleAction> _logger;

	public IdleAction(ILogger<IdleAction> logger)
	{
		_logger = logger;
	}

	public string Name => "idle";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Idles the session (simulates being online)",
		RequiresLogin: true,
		TimeoutSeconds: 300
	);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		int durationSeconds = PayloadReader.GetInt32(payload, "duration") ?? 60;

		_logger.LogInformation("Idle action for {AccountName} for {Duration}s", session.AccountName, durationSeconds);

		var output = new Dictionary<string, object?>
		{
			["action"] = "idle",
			["duration"] = durationSeconds,
			["state"] = session.State.ToString()
		};

		return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
	}
}
