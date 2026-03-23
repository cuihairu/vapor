using Microsoft.Extensions.Logging;
using SteamKit2;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Actions;

public sealed class RedeemKeyAction : IAction
{
	private const int MaxAttempts = 3;
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

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		string? key = PayloadReader.GetString(payload, "key");
		if (key is null)
		{
			return new ActionResult(false, "key is required", null);
		}

		_logger.LogInformation("Redeem key action for {AccountName}: {Key}", session.AccountName, MaskKey(key));

		// Check if we have a Steam client manager
		if (session.SteamClientManager == null)
		{
			return new ActionResult(
				false,
				"Steam client not available (stub mode)",
				new Dictionary<string, object?>
				{
					["action"] = "redeem_key",
					["key"] = MaskKey(key),
					["state"] = session.State.ToString()
				}
			);
		}

		RedeemKeyResult? result = null;
		var attempts = 0;
		for (var attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			attempts = attempt;
			result = await session.SteamClientManager.RedeemKeyAsync(key, cancellationToken).ConfigureAwait(false);

			if (result == null)
			{
				break;
			}

			if (!ShouldRetry(result.Result) || attempt == MaxAttempts)
			{
				break;
			}

			var delay = TimeSpan.FromMilliseconds(250 * attempt);
			_logger.LogWarning(
				"Transient redeem failure for {AccountName}: {Result} on attempt {Attempt}/{MaxAttempts}, retrying after {DelayMs}ms",
				session.AccountName,
				result.Result,
				attempt,
				MaxAttempts,
				delay.TotalMilliseconds);
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}

		if (result == null)
		{
			return new ActionResult(
				false,
				"Failed to redeem key: no response from Steam",
				new Dictionary<string, object?>
				{
					["action"] = "redeem_key",
					["key"] = MaskKey(key),
					["result"] = "timeout",
					["resultCode"] = (int)EResult.Timeout,
					["attempts"] = attempts
				}
			);
		}

		bool success = result.Result == EResult.OK ||
		               result.Result == EResult.AlreadyOwned ||
		               result.Result == EResult.DuplicateRequest;

		var output = new Dictionary<string, object?>
		{
			["action"] = "redeem_key",
			["key"] = MaskKey(key),
			["result"] = result.Result.ToString(),
			["resultCode"] = (int)result.Result,
			["attempts"] = attempts,
			["success"] = success
		};

		if (result.RequestId != null)
		{
			output["requestId"] = result.RequestId;
		}

		if (result.DurationMs > 0)
		{
			output["durationMs"] = result.DurationMs;
		}

		if (result.GrantedAppIDs?.Count > 0)
		{
			output["grantedAppIds"] = result.GrantedAppIDs;
		}

		if (result.GrantedPackageIDs?.Count > 0)
		{
			output["grantedPackageIds"] = result.GrantedPackageIDs;
		}

		if (result.ReceiptDetails != null)
		{
			output["receiptDetails"] = result.ReceiptDetails;
		}

		string? errorMessage = success
			? null
			: GetErrorMessage(result.Result);

		return new ActionResult(success, errorMessage, output);
	}

	private static string GetErrorMessage(EResult result) =>
		result switch
		{
			EResult.AlreadyOwned => "This key is already owned on this account",
			EResult.DuplicateRequest => "This key is already being processed",
			EResult.InvalidParam => "Invalid key format",
			EResult.RateLimitExceeded => "Too many key redemption attempts. Please try again later.",
			EResult.Timeout => "Request timed out",
			_ => $"Failed to redeem key: {result}"
		};

	private static bool ShouldRetry(EResult result) =>
		result is EResult.Timeout
			or EResult.ServiceUnavailable
			or EResult.Busy
			or EResult.TryAnotherCM;

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
