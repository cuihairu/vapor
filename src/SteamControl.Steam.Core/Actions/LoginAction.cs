using Microsoft.Extensions.Logging;

namespace SteamControl.Steam.Core.Actions;

public sealed class LoginAction : IAction
{
	private readonly ILogger<LoginAction> _logger;

	public LoginAction(ILogger<LoginAction> logger)
	{
		_logger = logger;
	}

	public string Name => "login";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Login to Steam",
		RequiresLogin: false,
		TimeoutSeconds: 60
	);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		_logger.LogInformation("Login action for {AccountName}", session.AccountName);

		var output = new Dictionary<string, object?>
		{
			["account"] = session.AccountName,
			["state"] = session.State.ToString(),
			["action"] = "login"
		};

		return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
	}
}
