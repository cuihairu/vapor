using Microsoft.Extensions.Logging;

namespace SteamControl.Steam.Core.Actions;

public sealed class EchoAction : IAction
{
	private readonly ILogger<EchoAction> _logger;

	public EchoAction(ILogger<EchoAction> logger)
	{
		_logger = logger;
	}

	public string Name => "echo";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Echos back the provided payload",
		RequiresLogin: false,
		TimeoutSeconds: 10
	);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		_logger.LogInformation("Echo action for {AccountName}", session.AccountName);

		var output = new Dictionary<string, object?>
		{
			["echo"] = payload,
			["account"] = session.AccountName
		};

		return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
	}
}
