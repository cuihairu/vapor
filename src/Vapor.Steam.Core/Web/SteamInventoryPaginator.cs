using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// Shared inventory pagination for every inventory reader: walk
/// <c>start_assetid</c> pages through the trade client until Steam reports no
/// more, with the same defensive 50 000-item cap the host-era
/// <c>get_inventory</c> shipped with.
/// </summary>
public static class SteamInventoryPaginator
{
	public const int MaxItems = 50000;

	public static async Task<(List<InventoryItem> Items, string? Error)> FetchAsync(
		ISteamTradeClient tradeClient,
		ulong steamId,
		uint appId,
		ulong contextId,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var allItems = new List<InventoryItem>();
		ulong? startAssetId = null;
		bool hasMore;

		do
		{
			cancellationToken.ThrowIfCancellationRequested();
			InventoryResponse response = await tradeClient
				.GetInventoryAsync(steamId, appId, contextId, startAssetId, cancellationToken)
				.ConfigureAwait(false);

			if (!response.Success)
			{
				return ([], response.Error ?? "Failed to get inventory");
			}

			allItems.AddRange(response.Items);
			hasMore = response.HasMore;
			startAssetId = response.LastAssetId;

			// Safety limit to prevent infinite loops.
			if (allItems.Count > MaxItems)
			{
				logger.LogWarning("Inventory size exceeded {Max} items, stopping pagination", MaxItems);
				break;
			}
		}
		while (hasMore && startAssetId.HasValue);

		return (allItems, null);
	}
}
