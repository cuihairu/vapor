using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to get a user's Steam inventory.
/// </summary>
public sealed class GetInventoryAction : IAction
{
	private readonly ILogger<GetInventoryAction> _logger;
	private readonly Func<SteamWebHandler, SteamTradeClient> _tradeClientFactory;

	public GetInventoryAction(ILogger<GetInventoryAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	// Constructor for testing with custom factory
	internal GetInventoryAction(
		ILogger<GetInventoryAction> logger,
		Func<SteamWebHandler, SteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "get_inventory";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Get a user's Steam inventory",
		RequiresLogin: true,
		TimeoutSeconds: 60
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		// Get parameters
		var steamIdParam = PayloadReader.GetString(payload, "steam_id");
		var appIdParam = PayloadReader.GetString(payload, "app_id");
		var contextIdParam = PayloadReader.GetString(payload, "context_id");

		// Parse steam_id (default to current session's steam ID)
		ulong steamId;
		if (!string.IsNullOrEmpty(steamIdParam))
		{
			if (!ulong.TryParse(steamIdParam, out steamId))
			{
				return new ActionResult(false, "Invalid steam_id parameter", null);
			}
		}
		else
		{
			// Use current session's steam ID
			// For now, return an error if no steam_id is provided
			return new ActionResult(false, "steam_id parameter is required", null);
		}

		// Parse app_id (default to 730/CS2)
		uint appId = 730;
		if (!string.IsNullOrEmpty(appIdParam) && uint.TryParse(appIdParam, out var parsedAppId))
		{
			appId = parsedAppId;
		}

		// Parse context_id (default to 2 for standard inventory)
		ulong contextId = 2;
		if (!string.IsNullOrEmpty(contextIdParam) && ulong.TryParse(contextIdParam, out var parsedContextId))
		{
			contextId = parsedContextId;
		}

		// Get SteamWebHandler from session
		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);

			// Get inventory with pagination support
			var allItems = new List<InventoryItem>();
			ulong? startAssetId = null;
			bool hasMore;

			do
			{
				var response = await tradeClient.GetInventoryAsync(steamId, appId, contextId, startAssetId, cancellationToken).ConfigureAwait(false);

				if (!response.Success)
				{
					return new ActionResult(false, response.Error ?? "Failed to get inventory", null);
				}

				allItems.AddRange(response.Items);
				hasMore = response.HasMore;
				startAssetId = response.LastAssetId;

				// Safety limit to prevent infinite loops
				if (allItems.Count > 50000)
				{
					_logger.LogWarning("Inventory size exceeded 50000 items, stopping pagination");
					break;
				}
			} while (hasMore && startAssetId.HasValue);

			_logger.LogInformation("Retrieved {Count} items from inventory for SteamID {SteamId}, AppID {AppId}",
				allItems.Count, steamId, appId);

			var output = new Dictionary<string, object?>
			{
				["steam_id"] = steamId.ToString(),
				["app_id"] = appId,
				["context_id"] = contextId,
				["total_count"] = allItems.Count,
				["items"] = allItems.Select(i => new Dictionary<string, object?>
				{
					["asset_id"] = i.AssetId.ToString(),
					["class_id"] = i.ClassId.ToString(),
					["instance_id"] = i.InstanceId.ToString(),
					["amount"] = i.Amount,
					["name"] = i.Name,
					["market_name"] = i.MarketName,
					["market_hash_name"] = i.MarketHashName,
					["type"] = i.Type,
					["tradable"] = i.Tradable,
					["marketable"] = i.Marketable
				}).ToList()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get inventory for {SteamId}", steamId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}
