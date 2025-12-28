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
		if (key is null)
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
		if (key.Length == 0)
		{
			return string.Empty;
		}

		if (key.Contains('-', StringComparison.Ordinal))
		{
			var parts = key.Split('-', StringSplitOptions.None);
			if (parts.Length >= 3)
			{
				return string.Join(
					"-",
					parts.Select((part, i) => (i == 0 || i == parts.Length - 1) ? part : new string('*', part.Length))
				);
			}
		}

		// Short keys: mask everything.
		if (key.Length <= 8)
		{
			return new string('*', key.Length);
		}

		// Medium keys: preserve first/last char.
		if (key.Length <= 20)
		{
			return $"{key[0]}{new string('*', key.Length - 2)}{key[^1]}";
		}

		// Long keys: preserve first/last 10 chars.
		const int edgeLen = 10;
		return key[..edgeLen] + new string('*', key.Length - (edgeLen * 2)) + key[^edgeLen..];
	}
}
