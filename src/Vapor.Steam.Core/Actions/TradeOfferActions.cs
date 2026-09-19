using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to send a trade offer to another Steam user.
/// Enforces asset ownership validation (items must exist, be tradable, off cooldown,
/// and in sufficient quantity in the sender's inventory) and per-account rate limiting
/// before the offer is submitted to Steam.
/// </summary>
public sealed class SendTradeOfferAction : IAction
{
	private readonly ILogger<SendTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly TradeRateLimiter? _rateLimiter;

	public SendTradeOfferAction(ILogger<SendTradeOfferAction> logger, TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_rateLimiter = rateLimiter;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal SendTradeOfferAction(
		ILogger<SendTradeOfferAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_rateLimiter = rateLimiter;
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
		var skipVerification = PayloadReader.GetBool(payload, "skip_verification") ?? false;

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

			using var lease = await AcquireLeaseAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
			if (lease == null)
			{
				return new ActionResult(false, "Trade rate limit exceeded for this account, try again later", null);
			}

			if (itemsToGive.Count > 0 && !skipVerification)
			{
				var ownershipError = await VerifyOwnershipAsync(tradeClient, itemsToGive, cancellationToken).ConfigureAwait(false);
				if (ownershipError != null)
				{
					_logger.LogWarning("Ownership verification failed for account {Account}: {Error}", session.AccountName, ownershipError);
					return new ActionResult(false, ownershipError, null);
				}
			}
			else if (itemsToGive.Count > 0)
			{
				_logger.LogWarning("Ownership verification skipped by explicit request for account {Account}", session.AccountName);
			}

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
				["trade_offer_id"] = result.TradeOfferId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["partner_steam_id"] = partnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["requires_mobile_confirmation"] = result.RequiresMobileConfirmation,
				["ownership_verified"] = itemsToGive.Count > 0 && !skipVerification
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to send trade offer to {PartnerSteamId}", partnerSteamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private async Task<string?> VerifyOwnershipAsync(
		ISteamTradeClient tradeClient,
		List<TradeAsset> itemsToGive,
		CancellationToken cancellationToken)
	{
		ulong? ownSteamId = tradeClient.GetOwnSteamId();
		if (ownSteamId == null)
		{
			return "Unable to resolve own SteamID from the web session for ownership verification; pass skip_verification=true to bypass";
		}

		// Fetch inventory per referenced (appId, contextId) pair, paginating only
		// until every requested asset has been seen.
		var pendingAssetIds = itemsToGive.Select(a => a.AssetId).ToHashSet();
		var inventory = new List<Models.InventoryItem>();

		foreach (var group in itemsToGive.GroupBy(a => (a.AppId, a.ContextId)))
		{
			ulong? startAssetId = null;
			bool hasMore;
			int pages = 0;

			do
			{
				var response = await tradeClient.GetInventoryAsync(ownSteamId.Value, group.Key.AppId, group.Key.ContextId, startAssetId, cancellationToken).ConfigureAwait(false);
				if (!response.Success)
				{
					return $"Failed to load inventory for ownership verification (app {group.Key.AppId}): {response.Error}";
				}

				inventory.AddRange(response.Items);
				hasMore = response.HasMore;
				startAssetId = response.LastAssetId;

				foreach (var item in response.Items)
				{
					pendingAssetIds.Remove(item.AssetId);
				}

				if (++pages > 50)
				{
					_logger.LogWarning("Ownership verification pagination exceeded 50 pages, continuing with partial inventory");
					break;
				}
			} while (hasMore && startAssetId.HasValue && pendingAssetIds.Count > 0);
		}

		var validation = TradeAssetValidator.ValidateOwnership(itemsToGive, inventory);
		if (!validation.IsValid)
		{
			return $"Ownership verification failed: {validation.CombinedError}";
		}

		return null;
	}

	private async Task<TradeRateLease?> AcquireLeaseAsync(string accountName, CancellationToken cancellationToken)
	{
		if (_rateLimiter == null)
		{
			return NoopLease;
		}

		return await _rateLimiter.AcquireAsync(accountName, cancellationToken).ConfigureAwait(false);
	}

	internal static readonly TradeRateLease NoopLease = new(static () => { });

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
			// Unreadable app id keeps the CS:GO default (730); the entry is dropped later
			// anyway when asset_id is unusable.
			_ = uint.TryParse(appIdObj.ToString(), out appId);
		}

		if (item.TryGetValue("context_id", out var contextIdObj) && contextIdObj != null)
		{
			_ = ulong.TryParse(contextIdObj.ToString(), out contextId); // unreadable keeps default 2
		}

		if (item.TryGetValue("asset_id", out var assetIdObj) && assetIdObj != null)
		{
			// asset_id == 0 after a failed parse drops the entry below — intentional.
			_ = ulong.TryParse(assetIdObj.ToString(), out assetId);
		}

		if (item.TryGetValue("amount", out var amountObj) && amountObj != null)
		{
			_ = int.TryParse(amountObj.ToString(), out amount); // unreadable keeps default 1
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
/// Verifies via the state machine that the offer is Active, received (not sent by us),
/// not expired, and from the expected partner before accepting.
/// </summary>
public sealed class AcceptTradeOfferAction : IAction
{
	private readonly ILogger<AcceptTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly TradeRateLimiter? _rateLimiter;

	public AcceptTradeOfferAction(ILogger<AcceptTradeOfferAction> logger, TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_rateLimiter = rateLimiter;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal AcceptTradeOfferAction(
		ILogger<AcceptTradeOfferAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_rateLimiter = rateLimiter;
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
		var verifyState = PayloadReader.GetBool(payload, "verify_state") ?? true;

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

			using var lease = await AcquireLeaseAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
			if (lease == null)
			{
				return new ActionResult(false, "Trade rate limit exceeded for this account, try again later", null);
			}

			if (verifyState)
			{
				var stateError = await VerifyOfferStateForAcceptAsync(tradeClient, tradeOfferId, partnerSteamId, cancellationToken).ConfigureAwait(false);
				if (stateError != null)
				{
					_logger.LogWarning("Accept validation failed for offer {TradeOfferId}: {Error}", tradeOfferId, stateError);
					return new ActionResult(false, stateError, null);
				}
			}

			var result = await tradeClient.AcceptTradeOfferAsync(tradeOfferId, partnerSteamId, cancellationToken).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to accept trade offer", null);
			}

			_logger.LogInformation("Accepted trade offer {TradeOfferId}", tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture));

			var output = new Dictionary<string, object?>
			{
				["trade_offer_id"] = tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["requires_mobile_confirmation"] = result.RequiresMobileConfirmation,
				["state_verified"] = verifyState
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to accept trade offer {TradeOfferId}", tradeOfferId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static async Task<string?> VerifyOfferStateForAcceptAsync(
		ISteamTradeClient tradeClient,
		ulong tradeOfferId,
		ulong partnerSteamId,
		CancellationToken cancellationToken)
	{
		var offerResult = await tradeClient.GetTradeOfferAsync(tradeOfferId, cancellationToken).ConfigureAwait(false);
		if (!offerResult.Success || offerResult.TradeOffer == null)
		{
			return $"Unable to load trade offer {tradeOfferId} for state verification ({offerResult.Error ?? "no offer returned"}); pass verify_state=false to bypass";
		}

		return TradeOfferStateMachine.ValidateForAccept(offerResult.TradeOffer, partnerSteamId);
	}

	private async Task<TradeRateLease?> AcquireLeaseAsync(string accountName, CancellationToken cancellationToken)
	{
		if (_rateLimiter == null)
		{
			return SendTradeOfferAction.NoopLease;
		}

		return await _rateLimiter.AcquireAsync(accountName, cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>
/// Action to decline a trade offer.
/// Verifies the offer is a received, Active offer before declining.
/// </summary>
public sealed class DeclineTradeOfferAction : IAction
{
	private readonly ILogger<DeclineTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly TradeRateLimiter? _rateLimiter;

	public DeclineTradeOfferAction(ILogger<DeclineTradeOfferAction> logger, TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_rateLimiter = rateLimiter;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal DeclineTradeOfferAction(
		ILogger<DeclineTradeOfferAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_rateLimiter = rateLimiter;
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
		var verifyState = PayloadReader.GetBool(payload, "verify_state") ?? true;

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

			using var lease = await AcquireLeaseAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
			if (lease == null)
			{
				return new ActionResult(false, "Trade rate limit exceeded for this account, try again later", null);
			}

			if (verifyState)
			{
				var offerResult = await tradeClient.GetTradeOfferAsync(tradeOfferId, cancellationToken).ConfigureAwait(false);
				if (!offerResult.Success || offerResult.TradeOffer == null)
				{
					return new ActionResult(false, $"Unable to load trade offer {tradeOfferId} for state verification ({offerResult.Error ?? "no offer returned"}); pass verify_state=false to bypass", null);
				}

				var stateError = TradeOfferStateMachine.ValidateForDecline(offerResult.TradeOffer);
				if (stateError != null)
				{
					_logger.LogWarning("Decline validation failed for offer {TradeOfferId}: {Error}", tradeOfferId, stateError);
					return new ActionResult(false, stateError, null);
				}
			}

			var result = await tradeClient.DeclineTradeOfferAsync(tradeOfferId, cancellationToken).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to decline trade offer", null);
			}

			_logger.LogInformation("Declined trade offer {TradeOfferId}", tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture));

			var output = new Dictionary<string, object?>
			{
				["trade_offer_id"] = tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["state_verified"] = verifyState
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to decline trade offer {TradeOfferId}", tradeOfferId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private async Task<TradeRateLease?> AcquireLeaseAsync(string accountName, CancellationToken cancellationToken)
	{
		if (_rateLimiter == null)
		{
			return SendTradeOfferAction.NoopLease;
		}

		return await _rateLimiter.AcquireAsync(accountName, cancellationToken).ConfigureAwait(false);
	}
}

/// <summary>
/// Action to cancel a trade offer.
/// Verifies the offer was sent by us and is still pending before canceling.
/// </summary>
public sealed class CancelTradeOfferAction : IAction
{
	private readonly ILogger<CancelTradeOfferAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;
	private readonly TradeRateLimiter? _rateLimiter;

	public CancelTradeOfferAction(ILogger<CancelTradeOfferAction> logger, TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_rateLimiter = rateLimiter;
		_tradeClientFactory = static webHandler => new SteamTradeClient(webHandler, NullLogger<SteamTradeClient>.Instance);
	}

	internal CancelTradeOfferAction(
		ILogger<CancelTradeOfferAction> logger,
		Func<SteamWebHandler, ISteamTradeClient> tradeClientFactory,
		TradeRateLimiter? rateLimiter = null)
	{
		_logger = logger;
		_tradeClientFactory = tradeClientFactory;
		_rateLimiter = rateLimiter;
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
		var verifyState = PayloadReader.GetBool(payload, "verify_state") ?? true;

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

			using var lease = await AcquireLeaseAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
			if (lease == null)
			{
				return new ActionResult(false, "Trade rate limit exceeded for this account, try again later", null);
			}

			if (verifyState)
			{
				var offerResult = await tradeClient.GetTradeOfferAsync(tradeOfferId, cancellationToken).ConfigureAwait(false);
				if (!offerResult.Success || offerResult.TradeOffer == null)
				{
					return new ActionResult(false, $"Unable to load trade offer {tradeOfferId} for state verification ({offerResult.Error ?? "no offer returned"}); pass verify_state=false to bypass", null);
				}

				var stateError = TradeOfferStateMachine.ValidateForCancel(offerResult.TradeOffer);
				if (stateError != null)
				{
					_logger.LogWarning("Cancel validation failed for offer {TradeOfferId}: {Error}", tradeOfferId, stateError);
					return new ActionResult(false, stateError, null);
				}
			}

			var result = await tradeClient.CancelTradeOfferAsync(tradeOfferId, cancellationToken).ConfigureAwait(false);

			if (!result.Success)
			{
				return new ActionResult(false, result.Error ?? "Failed to cancel trade offer", null);
			}

			_logger.LogInformation("Canceled trade offer {TradeOfferId}", tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture));

			var output = new Dictionary<string, object?>
			{
				["trade_offer_id"] = tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["state_verified"] = verifyState
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to cancel trade offer {TradeOfferId}", tradeOfferId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	private async Task<TradeRateLease?> AcquireLeaseAsync(string accountName, CancellationToken cancellationToken)
	{
		if (_rateLimiter == null)
		{
			return SendTradeOfferAction.NoopLease;
		}

		return await _rateLimiter.AcquireAsync(accountName, cancellationToken).ConfigureAwait(false);
	}
}
