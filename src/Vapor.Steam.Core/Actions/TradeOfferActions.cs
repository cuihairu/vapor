using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to send a trade offer to another Steam user.
/// </summary>
public sealed class SendTradeOfferAction : IAction
{
	private readonly ILogger<SendTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, SteamTradeClient> _tradeClientFactory;

	public SendTradeOfferAction(ILogger<SendTradeOfferAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal SendTradeOfferAction(
		ILogger<SendTradeOfferAction> logger,
		Func<SteamWebHandler, SteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "send_trade_offer";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Send a trade offer to another Steam user",
		RequiresLogin: true,
		TimeoutSeconds: 60
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		// Get parameters
		var partnerSteamIdParam = PayloadReader.GetString(payload, "partner_steam_id");
		var tradeUrl = PayloadReader.GetString(payload, "trade_url");
		var token = PayloadReader.GetString(payload, "token");
		var message = PayloadReader.GetString(payload, "message");

		// Get items arrays
		var itemsToGive = ParseTradeAssets(payload, "items_to_give");
		var itemsToReceive = ParseTradeAssets(payload, "items_to_receive");

		// Parse partner Steam ID
		ulong partnerSteamId;

		if (!string.IsNullOrEmpty(tradeUrl))
		{
			var tradeUrlParams = TradeUrlParams.TryParse(tradeUrl);
			if (tradeUrlParams == null)
			{
				return new ActionResult(false, "Invalid trade URL format", null);
			}

			partnerSteamId = tradeUrlParams.PartnerSteamId;
			token ??= tradeUrlParams.Token;
		}
		else if (!string.IsNullOrEmpty(partnerSteamIdParam))
		{
			if (!ulong.TryParse(partnerSteamIdParam, out partnerSteamId))
			{
				return new ActionResult(false, "Invalid partner_steam_id parameter", null);
			}
		}
		else
		{
			return new ActionResult(false, "Either partner_steam_id or trade_url is required", null);
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

			var result = await tradeClient.SendTradeOfferAsync(
				partnerSteamId,
				itemsToGive,
				itemsToReceive,
				token,
				message,
				cancellationToken
			).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to send trade offer", null);
			}

			_logger.LogInformation("Sent trade offer {TradeOfferId} to {PartnerSteamId}",
				result.TradeOfferId, partnerSteamId);

			var output = new Dictionary<string, object?>
			{
				["trade_offer_id"] = result.TradeOfferId?.ToString(),
				["partner_steam_id"] = partnerSteamId.ToString(),
				["requires_mobile_confirmation"] = result.RequiresMobileConfirmation
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to send trade offer to {PartnerSteamId}", partnerSteamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static List<TradeAsset> ParseTradeAssets(IReadOnlyDictionary<string, object?> payload, string key)
	{
		var assets = new List<TradeAsset>();

		if (!payload.TryGetValue(key, out var itemsObj) || itemsObj == null)
		{
			return assets;
		}

		// Handle array of dictionaries
		if (itemsObj is IEnumerable<Dictionary<string, object?>> itemsDicts)
		{
			foreach (var item in itemsDicts)
			{
				var asset = ParseSingleAsset(item);
				if (asset != null)
				{
					assets.Add(asset);
				}
			}
		}
		// Handle array of objects
		else if (itemsObj is IEnumerable<object?> items)
		{
			foreach (var item in items)
			{
				if (item is Dictionary<string, object?> itemDict)
				{
					var asset = ParseSingleAsset(itemDict);
					if (asset != null)
					{
						assets.Add(asset);
					}
				}
			}
		}

		return assets;
	}

	private static TradeAsset? ParseSingleAsset(Dictionary<string, object?> item)
	{
		uint appId = 730;
		ulong contextId = 2;
		ulong assetId = 0;
		int amount = 1;

		if (item.TryGetValue("app_id", out var appIdObj) && appIdObj != null)
		{
			uint.TryParse(appIdObj.ToString(), out appId);
		}

		if (item.TryGetValue("context_id", out var contextIdObj) && contextIdObj != null)
		{
			ulong.TryParse(contextIdObj.ToString(), out contextId);
		}

		if (item.TryGetValue("asset_id", out var assetIdObj) && assetIdObj != null)
		{
			ulong.TryParse(assetIdObj.ToString(), out assetId);
		}

		if (item.TryGetValue("amount", out var amountObj) && amountObj != null)
		{
			int.TryParse(amountObj.ToString(), out amount);
		}

		if (assetId == 0)
		{
			return null;
		}

		return new TradeAsset
		{
			AppId = appId,
			ContextId = contextId,
			AssetId = assetId,
			Amount = amount > 0 ? amount : 1
		};
	}
}

/// <summary>
/// Action to accept a trade offer.
/// </summary>
public sealed class AcceptTradeOfferAction : IAction
{
	private readonly ILogger<AcceptTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, SteamTradeClient> _tradeClientFactory;

	public AcceptTradeOfferAction(ILogger<AcceptTradeOfferAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal AcceptTradeOfferAction(
		ILogger<AcceptTradeOfferAction> logger,
		Func<SteamWebHandler, SteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "accept_trade_offer";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Accept a trade offer",
		RequiresLogin: true,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var tradeOfferIdParam = PayloadReader.GetString(payload, "trade_offer_id");
		var partnerSteamIdParam = PayloadReader.GetString(payload, "partner_steam_id");

		if (string.IsNullOrEmpty(tradeOfferIdParam) || !ulong.TryParse(tradeOfferIdParam, out var tradeOfferId))
		{
			return new ActionResult(false, "Valid trade_offer_id is required", null);
		}

		if (string.IsNullOrEmpty(partnerSteamIdParam) || !ulong.TryParse(partnerSteamIdParam, out var partnerSteamId))
		{
			return new ActionResult(false, "Valid partner_steam_id is required", null);
		}

		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);

			var result = await tradeClient.AcceptTradeOfferAsync(tradeOfferId, partnerSteamId, cancellationToken).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to accept trade offer", null);
			}

			_logger.LogInformation("Accepted trade offer {TradeOfferId}", tradeOfferId);

			var output = new Dictionary<string, object?>
			{
				["trade_offer_id"] = tradeOfferId.ToString(),
				["requires_mobile_confirmation"] = result.RequiresMobileConfirmation
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to accept trade offer {TradeOfferId}", tradeOfferId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}

/// <summary>
/// Action to decline a trade offer.
/// </summary>
public sealed class DeclineTradeOfferAction : IAction
{
	private readonly ILogger<DeclineTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, SteamTradeClient> _tradeClientFactory;

	public DeclineTradeOfferAction(ILogger<DeclineTradeOfferAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal DeclineTradeOfferAction(
		ILogger<DeclineTradeOfferAction> logger,
		Func<SteamWebHandler, SteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "decline_trade_offer";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Decline a trade offer",
		RequiresLogin: true,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var tradeOfferIdParam = PayloadReader.GetString(payload, "trade_offer_id");

		if (string.IsNullOrEmpty(tradeOfferIdParam) || !ulong.TryParse(tradeOfferIdParam, out var tradeOfferId))
		{
			return new ActionResult(false, "Valid trade_offer_id is required", null);
		}

		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);

			var result = await tradeClient.DeclineTradeOfferAsync(tradeOfferId, cancellationToken).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to decline trade offer", null);
			}

			_logger.LogInformation("Declined trade offer {TradeOfferId}", tradeOfferId);

			var output = new Dictionary<string, object?>
			{
				["trade_offer_id"] = tradeOfferId.ToString()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to decline trade offer {TradeOfferId}", tradeOfferId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}

/// <summary>
/// Action to cancel a trade offer.
/// </summary>
public sealed class CancelTradeOfferAction : IAction
{
	private readonly ILogger<CancelTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, SteamTradeClient> _tradeClientFactory;

	public CancelTradeOfferAction(ILogger<CancelTradeOfferAction> logger)
	{
		_logger = logger;
		_tradeClientFactory = webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal CancelTradeOfferAction(
		ILogger<CancelTradeOfferAction> logger,
		Func<SteamWebHandler, SteamTradeClient> tradeClientFactory)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
	}

	public string Name => "cancel_trade_offer";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Cancel a trade offer you sent",
		RequiresLogin: true,
		TimeoutSeconds: 30
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var tradeOfferIdParam = PayloadReader.GetString(payload, "trade_offer_id");

		if (string.IsNullOrEmpty(tradeOfferIdParam) || !ulong.TryParse(tradeOfferIdParam, out var tradeOfferId))
		{
			return new ActionResult(false, "Valid trade_offer_id is required", null);
		}

		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);

			var result = await tradeClient.CancelTradeOfferAsync(tradeOfferId, cancellationToken).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to cancel trade offer", null);
			}

			_logger.LogInformation("Canceled trade offer {TradeOfferId}", tradeOfferId);

			var output = new Dictionary<string, object?>
			{
				["trade_offer_id"] = tradeOfferId.ToString()
			};

			return new ActionResult(true, null, output);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to cancel trade offer {TradeOfferId}", tradeOfferId);
			return new ActionResult(false, ex.Message, null);
		}
	}
}
