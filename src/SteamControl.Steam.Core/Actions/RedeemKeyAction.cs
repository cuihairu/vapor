using Microsoft.Extensions.Logging;

namespace SteamControl.Steam.Core.Actions;

public sealed class RedeemKeyAction : IAction
{
	private readonly ILogger<RedeemKeyAction> _logger;

	public RedeemKeyAction(ILogger<RedeemKeyAction> logger)
	{
		_logger = logger;
	}

	public string Name => "redeem_key";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Redeem a Steam product key",
		RequiresLogin: true,
		TimeoutSeconds: 60
	);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		string? key = PayloadReader.GetString(payload, "key");
		if (string.IsNullOrWhiteSpace(key))
		{
			return Task.FromResult<ActionResult>(new ActionResult(false, "key is required", null));
		}

		_logger.LogInformation("Redeem key action for {AccountName}: {Key}", session.AccountName, MaskKey(key));

		var output = new Dictionary<string, object?>
		{
			["action"] = "redeem_key",
			["key"] = MaskKey(key),
			["state"] = session.State.ToString()
		};

		return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
	}

	private static string MaskKey(string key)
	{
		if (key.Length <= 8)
		{
			return new string('*', key.Length);
		}

		return key[..4] + new string('*', key.Length - 8) + key[^4..];
	}
}
