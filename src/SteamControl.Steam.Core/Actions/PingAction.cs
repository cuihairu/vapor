using Microsoft.Extensions.Logging;

namespace SteamControl.Steam.Core.Actions;

public sealed class PingAction : IAction
{
	private readonly ILogger<PingAction> _logger;

	public PingAction(ILogger<PingAction> logger)
	{
		_logger = logger;
	}

	public string Name => "ping";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Pings the session and returns pong",
		RequiresLogin: false,
		TimeoutSeconds: 10
	);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		_logger.LogInformation("Ping received for account {AccountName}", session.AccountName);

		var output = new Dictionary<string, object?>
		{
			["pong"] = true,
			["account"] = session.AccountName,
			["state"] = session.State.ToString(),
			["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
		};

		return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
	}
}
