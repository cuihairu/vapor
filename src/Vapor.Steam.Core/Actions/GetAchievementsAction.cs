using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to list one game's achievements with their unlock state, parsed from
/// the community per-game stats page of the session's SteamID (or an explicit
/// <c>steam_id</c>). Read-only; the API names it surfaces are the write-side
/// identifiers for unlock_achievements.
/// </summary>
public sealed class GetAchievementsAction : IAction
{
	private readonly ILogger<GetAchievementsAction> _logger;
	private readonly Func<SteamWebHandler, SteamAchievementsClient> _achievementsClientFactory;

	public GetAchievementsAction(ILogger<GetAchievementsAction> logger)
	{
		_logger = logger;
		_achievementsClientFactory = webHandler => new SteamAchievementsClient(webHandler, NullLogger<SteamAchievementsClient>.Instance);
	}

	// Constructor for testing with a custom factory.
	internal GetAchievementsAction(
		ILogger<GetAchievementsAction> logger,
		Func<SteamWebHandler, SteamAchievementsClient> achievementsClientFactory)
	{
		_logger = logger;
		_achievementsClientFactory = achievementsClientFactory;
	}

	public string Name => "get_achievements";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"List one game's achievements and their unlock state (community stats page)",
		RequiresLogin: true,
		TimeoutSeconds: 120
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		string? appIdParam = PayloadReader.GetString(payload, "app_id");
		if (string.IsNullOrEmpty(appIdParam) || !uint.TryParse(appIdParam, out uint appId) || appId == 0)
		{
			return new ActionResult(false, "app_id parameter is required (positive app id)", null);
		}

		// steam_id is optional: when omitted, resolve the session's own SteamID
		// from its cookies (the control plane only knows account names).
		var steamIdParam = PayloadReader.GetString(payload, "steam_id");
		ulong steamId;
		if (string.IsNullOrEmpty(steamIdParam))
		{
			ulong? resolved = webHandler.TryResolveOwnSteamId();
			if (resolved is null)
			{
				return new ActionResult(false, "steam_id parameter is required when the session cookies do not carry a SteamID (is the session logged on?)", null);
			}

			steamId = resolved.Value;
		}
		else if (!ulong.TryParse(steamIdParam, out steamId))
		{
			return new ActionResult(false, "Invalid steam_id parameter", null);
		}

		try
		{
			var client = _achievementsClientFactory(webHandler);
			AchievementPageResult page = await client.GetAchievementsAsync(steamId, appId, cancellationToken).ConfigureAwait(false);

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["app_id"] = appId,
				["unlocked_count"] = page.UnlockedCount,
				["total_count"] = page.TotalCount,
				["achievements"] = page.Achievements.Select(a => new Dictionary<string, object?>
				{
					["api_name"] = a.ApiName,
					["display_name"] = a.DisplayName,
					["description"] = a.Description,
					["unlocked"] = a.Unlocked,
					["icon_url"] = a.IconUrl
				}).ToList()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get achievements for SteamID {SteamId} app {AppId}", steamId, appId);
			return new ActionResult(false, $"Failed to get achievements: {ex.Message}", null);
		}
	}
}
