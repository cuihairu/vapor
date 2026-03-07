using System.Text.Json.Serialization;

namespace Vapor.Steam.Core.Models;

/// <summary>
/// Represents an item in a Steam inventory.
/// </summary>
public sealed record InventoryItem
{
	/// <summary>
	/// The unique asset ID of this item.
	/// </summary>
	public ulong AssetId { get; init; }

	/// <summary>
	/// The class ID of this item (shared across identical items).
	/// </summary>
	public ulong ClassId { get; init; }

	/// <summary>
	/// The instance ID of this item.
	/// </summary>
	public ulong InstanceId { get; init; }

	/// <summary>
	/// The amount of this item (for stackable items).
	/// </summary>
	public int Amount { get; init; }

	/// <summary>
	/// The AppID this item belongs to.
	/// </summary>
	public uint AppId { get; init; }

	/// <summary>
	/// The type of this item.
	/// </summary>
	public string? Type { get; init; }

	/// <summary>
	/// Whether this item is tradable.
	/// </summary>
	public bool Tradable { get; init; }

	/// <summary>
	/// Whether this item is marketable.
	/// </summary>
	public bool Marketable { get; init; }

	/// <summary>
	/// Commodity status (1 = commodity item).
	/// </summary>
	public int Commodity { get; init; }

	/// <summary>
	/// Tradability restriction date (if any).
	/// </summary>
	public DateTimeOffset? TradabilityDate { get; init; }

	/// <summary>
	/// Marketability restriction date (if any).
	/// </summary>
	public DateTimeOffset? MarketabilityDate { get; init; }

	/// <summary>
	/// Additional properties from the item description.
	/// </summary>
	public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// Tags associated with this item.
	/// </summary>
	public IReadOnlyList<ItemTag> Tags { get; init; } = [];

	/// <summary>
	/// Icon URL for this item.
	/// </summary>
	public string? IconUrl { get; init; }

	/// <summary>
	/// Icon URL for this item (large).
	/// </summary>
	public string? IconUrlLarge { get; init; }

	/// <summary>
	/// Name of this item.
	/// </summary>
	public string? Name { get; init; }

	/// <summary>
	/// Market name of this item.
	/// </summary>
	public string? MarketName { get; init; }

	/// <summary>
	/// Market hash name for price lookups.
	/// </summary>
	public string? MarketHashName { get; init; }
}

/// <summary>
/// Represents a tag on an inventory item.
/// </summary>
public sealed record ItemTag
{
	/// <summary>
	/// Internal tag name.
	/// </summary>
	public string? InternalName { get; init; }

	/// <summary>
	/// Localized tag name.
	/// </summary>
	public string? Name { get; init; }

	/// <summary>
	/// Tag category.
	/// </summary>
	public string? Category { get; init; }

	/// <summary>
	/// Localized category name.
	/// </summary>
	public string? CategoryName { get; init; }

	/// <summary>
	/// Tag color (hex).
	/// </summary>
	public string? Color { get; init; }
}

/// <summary>
/// Represents a trade offer on Steam.
/// </summary>
public sealed record TradeOffer
{
	/// <summary>
	/// The unique trade offer ID.
	/// </summary>
	public ulong TradeOfferId { get; init; }

	/// <summary>
	/// The SteamID of the trade offer sender.
	/// </summary>
	public ulong AccountIdOther { get; init; }

	/// <summary>
	/// The message included with the trade offer.
	/// </summary>
	public string? Message { get; init; }

	/// <summary>
	/// The number of items the sender is giving.
	/// </summary>
	public int ItemsToGiveCount { get; init; }

	/// <summary>
	/// The number of items the sender is receiving.
	/// </summary>
	public int ItemsToReceiveCount { get; init; }

	/// <summary>
	/// Whether the trade offer is from the current user.
	/// </summary>
	public bool IsOurOffer { get; init; }

	/// <summary>
	/// The creation time of the trade offer.
	/// </summary>
	public DateTimeOffset TimeCreated { get; init; }

	/// <summary>
	/// The expiration time of the trade offer.
	/// </summary>
	public DateTimeOffset? TimeExpires { get; init; }

	/// <summary>
	/// The state of the trade offer.
	/// </summary>
	public TradeOfferState State { get; init; }

	/// <summary>
	/// Items being sent in this trade.
	/// </summary>
	public IReadOnlyList<TradeAsset> ItemsToGive { get; init; } = [];

	/// <summary>
	/// Items being received in this trade.
	/// </summary>
	public IReadOnlyList<TradeAsset> ItemsToReceive { get; init; } = [];
}

/// <summary>
/// Represents the state of a trade offer.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TradeOfferState>))]
public enum TradeOfferState
{
	/// <summary>
	/// Invalid state.
	/// </summary>
	Invalid = 0,

	/// <summary>
	/// Trade offer is active and can be accepted.
	/// </summary>
	Active = 2,

	/// <summary>
	/// Trade offer has been accepted.
	/// </summary>
	Accepted = 3,

	/// <summary>
	/// Trade offer has been countered.
	/// </summary>
	Countered = 4,

	/// <summary>
	/// Trade offer has expired.
	/// </summary>
	Expired = 5,

	/// <summary>
	/// Trade offer has been declined.
	/// </summary>
	Declined = 6,

	/// <summary>
	/// Trade offer is invalid (items no longer available).
	/// </summary>
	InvalidItems = 7,

	/// <summary>
	/// Trade offer needs mobile confirmation.
	/// </summary>
	CreatedNeedsConfirmation = 8,

	/// <summary>
	/// Trade offer was canceled by the sender.
	/// </summary>
	Canceled = 9,

	/// <summary>
	/// Trade offer was canceled by the sender (second factor).
	/// </summary>
	CanceledBySecondFactor = 10,

	/// <summary>
	/// Trade offer is in escrow.
	/// </summary>
	InEscrow = 11
}

/// <summary>
/// Represents an asset in a trade.
/// </summary>
public sealed record TradeAsset
{
	/// <summary>
	/// The AppID of the asset.
	/// </summary>
	public uint AppId { get; init; }

	/// <summary>
	/// The context ID of the asset (usually 2 for inventory).
	/// </summary>
	public ulong ContextId { get; init; }

	/// <summary>
	/// The asset ID of the item.
	/// </summary>
	public ulong AssetId { get; init; }

	/// <summary>
	/// The class ID of the item.
	/// </summary>
	public ulong ClassId { get; init; }

	/// <summary>
	/// The instance ID of the item.
	/// </summary>
	public ulong InstanceId { get; init; }

	/// <summary>
	/// The amount of the item (for stackables).
	/// </summary>
	public int Amount { get; init; }

	/// <summary>
	/// Whether this is a currency asset.
	/// </summary>
	public bool IsCurrency { get; init; }

	/// <summary>
	/// Creates a new TradeAsset from an inventory item.
	/// </summary>
	public static TradeAsset FromInventoryItem(InventoryItem item)
	{
		return new TradeAsset
		{
			AppId = item.AppId,
			ContextId = 2, // Default inventory context
			AssetId = item.AssetId,
			ClassId = item.ClassId,
			InstanceId = item.InstanceId,
			Amount = item.Amount,
			IsCurrency = false
		};
	}
}

/// <summary>
/// Result of a trade offer operation.
/// </summary>
public sealed record TradeOfferResult
{
	/// <summary>
	/// Whether the operation was successful.
	/// </summary>
	public bool Success { get; init; }

	/// <summary>
	/// Error message if the operation failed.
	/// </summary>
	public string? Error { get; init; }

	/// <summary>
	/// The trade offer ID (for new offers).
	/// </summary>
	public ulong? TradeOfferId { get; init; }

	/// <summary>
	/// Whether mobile confirmation is required.
	/// </summary>
	public bool RequiresMobileConfirmation { get; init; }

	/// <summary>
	/// The trade offer that was affected.
	/// </summary>
	public TradeOffer? TradeOffer { get; init; }
}

/// <summary>
/// Response from getting a user's inventory.
/// </summary>
public sealed record InventoryResponse
{
	/// <summary>
	/// Whether the request was successful.
	/// </summary>
	public bool Success { get; init; }

	/// <summary>
	/// Error message if the request failed.
	/// </summary>
	public string? Error { get; init; }

	/// <summary>
	/// The items in the inventory.
	/// </summary>
	public IReadOnlyList<InventoryItem> Items { get; init; } = [];

	/// <summary>
	/// Total number of items.
	/// </summary>
	public int TotalInventoryCount { get; init; }

	/// <summary>
	/// The AppID of the inventory.
	/// </summary>
	public uint AppId { get; init; }

	/// <summary>
	/// The context ID of the inventory.
	/// </summary>
	public ulong ContextId { get; init; }

	/// <summary>
	/// Whether there are more items to fetch.
	/// </summary>
	public bool HasMore { get; init; }

	/// <summary>
	/// The last asset ID for pagination.
	/// </summary>
	public ulong? LastAssetId { get; init; }
}

/// <summary>
/// Response from getting trade offers.
/// </summary>
public sealed record TradeOffersResponse
{
	/// <summary>
	/// Whether the request was successful.
	/// </summary>
	public bool Success { get; init; }

	/// <summary>
	/// Error message if the request failed.
	/// </summary>
	public string? Error { get; init; }

	/// <summary>
	/// Trade offers sent by the current user.
	/// </summary>
	public IReadOnlyList<TradeOffer> SentOffers { get; init; } = [];

	/// <summary>
	/// Trade offers received by the current user.
	/// </summary>
	public IReadOnlyList<TradeOffer> ReceivedOffers { get; init; } = [];

	/// <summary>
	/// The timestamp of the next update cursor.
	/// </summary>
	public DateTimeOffset? NextCursorTime { get; init; }
}

/// <summary>
/// Parsed trade URL parameters.
/// </summary>
public sealed record TradeUrlParams
{
	/// <summary>
	/// The partner's SteamID (64-bit).
	/// </summary>
	public ulong PartnerSteamId { get; init; }

	/// <summary>
	/// The trade offer token.
	/// </summary>
	public string? Token { get; init; }

	/// <summary>
	/// Parses a Steam trade URL.
	/// </summary>
	public static TradeUrlParams? TryParse(string tradeUrl)
	{
		if (string.IsNullOrWhiteSpace(tradeUrl))
		{
			return null;
		}

		try
		{
			var uri = new Uri(tradeUrl);
			if (!uri.Host.Contains("steamcommunity.com", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			// Parse query string manually
			var query = uri.Query.TrimStart('?');
			var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = pair.Split('=', 2);
				if (parts.Length == 2)
				{
					queryParams[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
				}
				else if (parts.Length == 1)
				{
					queryParams[Uri.UnescapeDataString(parts[0])] = string.Empty;
				}
			}

			if (!queryParams.TryGetValue("partner", out var partner) || string.IsNullOrEmpty(partner))
			{
				return null;
			}

			queryParams.TryGetValue("token", out var token);

			// Convert partner ID to 64-bit SteamID
			// Partner ID is the account ID, convert to 64-bit SteamID
			if (!ulong.TryParse(partner, out var accountId))
			{
				return null;
			}

			// Convert to 64-bit SteamID (76561197960265728 + accountId)
			var steamId = 76561197960265728UL + accountId;

			return new TradeUrlParams
			{
				PartnerSteamId = steamId,
				Token = token
			};
		}
		catch
		{
			return null;
		}
	}
}
