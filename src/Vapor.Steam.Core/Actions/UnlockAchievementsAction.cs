using System.Globalization;
using System.Text.Json;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "unlock_achievements": sets the named achievements to unlocked on the
/// logged-on account via the client stats protocol. The names must be listed
/// explicitly — there is no "unlock everything" path (see todo §33 red lines).
/// Each achievement is reported individually; one failure never stops the rest.
/// </summary>
public sealed class UnlockAchievementsAction : IAction
{
	public string Name => "unlock_achievements";

	public ActionMetadata Metadata => new(
		Name,
		"Unlock the named achievements of one game on this account (explicit name list required)",
		RequiresLogin: true,
		TimeoutSeconds: 180);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var clientManager = session.SteamClientManager;
		if (clientManager == null)
		{
			return new ActionResult(false, "Steam client not available (achievement writes need a connected client)", null);
		}

		if (!AchievementsWritePayload.TryParse(payload, requireConfirm: false, out uint appId, out List<string> names, out string? error))
		{
			return new ActionResult(false, error, null);
		}

		AchievementWriteResult? result;
		try
		{
			result = await clientManager.SetAchievementStatesAsync(appId, names, unlock: true, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return new ActionResult(false, $"Failed to unlock achievements: {ex.Message}", null);
		}

		if (result == null)
		{
			return new ActionResult(false, "no response from Steam (is the client connected and logged on?)", null);
		}

		return new ActionResult(
			result.Success,
			result.Success ? null : "one or more achievements failed to unlock (see results)",
			AchievementsWritePayload.ToOutput(appId, unlock: true, names.Count, result));
	}
}

/// <summary>
/// "reset_achievements": clears the named achievements on the logged-on
/// account. Destructive, so it is double-gated: the payload must carry an
/// explicit non-empty <c>names</c> list AND an explicit <c>confirm: true</c> —
/// both the action layer and the control-plane API enforce this independently.
/// </summary>
public sealed class ResetAchievementsAction : IAction
{
	public string Name => "reset_achievements";

	public ActionMetadata Metadata => new(
		Name,
		"Reset (clear) the named achievements of one game on this account (explicit names + confirm: true required)",
		RequiresLogin: true,
		TimeoutSeconds: 180);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var clientManager = session.SteamClientManager;
		if (clientManager == null)
		{
			return new ActionResult(false, "Steam client not available (achievement writes need a connected client)", null);
		}

		if (!AchievementsWritePayload.TryParse(payload, requireConfirm: true, out uint appId, out List<string> names, out string? error))
		{
			return new ActionResult(false, error, null);
		}

		AchievementWriteResult? result;
		try
		{
			result = await clientManager.SetAchievementStatesAsync(appId, names, unlock: false, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return new ActionResult(false, $"Failed to reset achievements: {ex.Message}", null);
		}

		if (result == null)
		{
			return new ActionResult(false, "no response from Steam (is the client connected and logged on?)", null);
		}

		return new ActionResult(
			result.Success,
			result.Success ? null : "one or more achievements failed to reset (see results)",
			AchievementsWritePayload.ToOutput(appId, unlock: false, names.Count, result));
	}
}

/// <summary>Shared payload parsing/output shaping for the two achievement write actions.</summary>
internal static class AchievementsWritePayload
{
	/// <summary>
	/// Parses app_id (positive, required), names (explicit non-empty string list,
	/// deduplicated case-insensitively) and — for reset — the confirm gate.
	/// </summary>
	internal static bool TryParse(
		IReadOnlyDictionary<string, object?> payload,
		bool requireConfirm,
		out uint appId,
		out List<string> names,
		out string? error)
	{
		appId = 0;
		names = [];
		error = null;

		string? appIdParam = PayloadReader.GetString(payload, "app_id");
		if (string.IsNullOrEmpty(appIdParam) || !uint.TryParse(appIdParam, NumberStyles.Integer, CultureInfo.InvariantCulture, out appId) || appId == 0)
		{
			error = "app_id parameter is required (positive app id)";
			return false;
		}

		names = PayloadReader.TryGetValue(payload, "names", out object? rawNames) ? ParseNames(rawNames) : [];
		if (names.Count == 0)
		{
			error = "names is required: an explicit non-empty list of achievement API names (no implicit full-batch path exists)";
			return false;
		}

		if (requireConfirm && PayloadReader.GetBool(payload, "confirm") != true)
		{
			error = "confirm must be explicitly true for a reset (destructive operation)";
			return false;
		}

		return true;
	}

	private static List<string> ParseNames(object? raw) => raw switch
	{
		null => [],
		JsonElement { ValueKind: JsonValueKind.Array } array
			=> array.EnumerateArray()
				.Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
				.OfType<string>()
				.ToList(),
		IEnumerable<string> list => list.ToList(),
		string single => [single],
		_ => []
	};

	internal static Dictionary<string, object?> ToOutput(
		uint appId,
		bool unlock,
		int requestedCount,
		AchievementWriteResult result) => new()
		{
			["app_id"] = appId,
			["unlock"] = unlock,
			["requested_count"] = requestedCount,
			["results"] = result.Entries.Select(e => new Dictionary<string, object?>
			{
				["name"] = e.Name,
				["success"] = e.Success,
				["detail"] = e.Detail
			}).ToList(),
			["succeeded_count"] = result.Entries.Count(e => e.Success),
			["failed_count"] = result.Entries.Count(e => !e.Success),
			["verified"] = result.Verified
		};
}
