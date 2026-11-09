using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Web;

/// <summary>Inventory read surface: fetch and parse a user inventory, plus the flexible-int32 reader.</summary>
public sealed partial class SteamTradeClient
{
	/// <summary>
	/// Gets the inventory for a Steam user.
	/// </summary>
	/// <param name="steamId">The 64-bit SteamID of the user.</param>
	/// <param name="appId">The AppID to get inventory for (default: 730/CS2).</param>
	/// <param name="contextId">The context ID (default: 2 for standard inventory).</param>
	/// <param name="startAssetId">Asset ID to start from for pagination.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<InventoryResponse> GetInventoryAsync(
		ulong steamId,
		uint appId = SteamAppId,
		ulong contextId = DefaultContextId,
		ulong? startAssetId = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		try
		{
			// Build inventory URL (base URL is configurable — see ctor — so a local
			// mock can stand in for steamcommunity.com; 41-4).
			var url = $"{_inventoryBaseUrl}/inventory/{steamId}/{appId}/{contextId}?l=english&count=5000";

			if (startAssetId.HasValue)
			{
				url += $"&start_assetid={startAssetId.Value}";
			}

			var response = await _webHandler.GetAsync(new Uri(url), null, cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccess)
			{
				_logger.LogWarning("Inventory request failed: {StatusCode}", response.StatusCode);
				return new InventoryResponse
				{
					Success = false,
					Error = $"HTTP error: {response.StatusCode}"
				};
			}

			if (string.IsNullOrEmpty(response.Body))
			{
				return new InventoryResponse
				{
					Success = false,
					Error = "Empty response body"
				};
			}

			return ParseInventoryResponse(response.Body, appId, contextId);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get inventory for {SteamId}", steamId);
			return new InventoryResponse
			{
				Success = false,
				Error = ex.Message
			};
		}
	}

	private InventoryResponse ParseInventoryResponse(string body, uint appId, ulong contextId)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (!root.TryGetProperty("success", out var successElem) || successElem.GetBoolean() != true)
			{
				var error = "Unknown error";
				if (root.TryGetProperty("Error", out var errorElem))
				{
					error = errorElem.GetString() ?? error;
				}
				return new InventoryResponse
				{
					Success = false,
					Error = error,
					AppId = appId,
					ContextId = contextId
				};
			}

			var items = new List<InventoryItem>();
			var descriptions = new Dictionary<(ulong ClassId, ulong InstanceId), JsonElement>();

			// Parse descriptions
			if (root.TryGetProperty("descriptions", out var descriptionsElem))
			{
				foreach (var desc in descriptionsElem.EnumerateArray())
				{
					var classId = desc.TryGetProperty("classid", out var classIdElem)
						? ulong.Parse(classIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
						: 0;
					var instanceId = desc.TryGetProperty("instanceid", out var instanceIdElem)
						? ulong.Parse(instanceIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
						: 0;

					descriptions[(classId, instanceId)] = desc;
				}
			}

			// Parse assets
			if (root.TryGetProperty("assets", out var assetsElem))
			{
				foreach (var asset in assetsElem.EnumerateArray())
				{
					var classId = asset.TryGetProperty("classid", out var classIdElem)
						? ulong.Parse(classIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
						: 0;
					var instanceId = asset.TryGetProperty("instanceid", out var instanceIdElem)
						? ulong.Parse(instanceIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
						: 0;
					var assetId = asset.TryGetProperty("assetid", out var assetIdElem)
						? ulong.Parse(assetIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
						: 0;
					var amount = asset.TryGetProperty("amount", out var amountElem)
						? ReadInt32Flexible(amountElem, 1)
						: 1;

					// Get description info
					var item = new InventoryItem
					{
						AssetId = assetId,
						ClassId = classId,
						InstanceId = instanceId,
						Amount = amount,
						AppId = appId
					};

					if (descriptions.TryGetValue((classId, instanceId), out var desc))
					{
						item = item with
						{
							Tradable = desc.TryGetProperty("tradable", out var tradableElem) && tradableElem.GetInt32() == 1,
							Marketable = desc.TryGetProperty("marketable", out var marketableElem) && marketableElem.GetInt32() == 1,
							Commodity = desc.TryGetProperty("commodity", out var commodityElem) ? commodityElem.GetInt32() : 0,
							Type = desc.TryGetProperty("type", out var typeElem) ? typeElem.GetString() : null,
							Name = desc.TryGetProperty("name", out var nameElem) ? nameElem.GetString() : null,
							MarketName = desc.TryGetProperty("market_name", out var marketNameElem) ? marketNameElem.GetString() : null,
							MarketHashName = desc.TryGetProperty("market_hash_name", out var marketHashNameElem) ? marketHashNameElem.GetString() : null,
							IconUrl = desc.TryGetProperty("icon_url", out var iconUrlElem) ? iconUrlElem.GetString() : null,
							IconUrlLarge = desc.TryGetProperty("icon_url_large", out var iconUrlLargeElem) ? iconUrlLargeElem.GetString() : null
						};
					}

					items.Add(item);
				}
			}

			var totalInventoryCount = root.TryGetProperty("total_inventory_count", out var totalElem)
				? totalElem.GetInt32()
				: items.Count;

			var lastAssetId = root.TryGetProperty("last_assetid", out var lastAssetIdElem)
				? ulong.Parse(lastAssetIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
				: (ulong?)null;

			var hasMore = root.TryGetProperty("more_items", out var moreElem) && moreElem.GetInt32() == 1;

			return new InventoryResponse
			{
				Success = true,
				Items = items,
				TotalInventoryCount = totalInventoryCount,
				AppId = appId,
				ContextId = contextId,
				HasMore = hasMore,
				LastAssetId = lastAssetId > 0 ? lastAssetId : null
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to parse inventory response");
			return new InventoryResponse
			{
				Success = false,
				Error = $"Failed to parse response: {ex.Message}",
				AppId = appId,
				ContextId = contextId
			};
		}
	}

	/// <summary>
	/// Reads a JSON element as an Int32, tolerating Steam's habit of encoding
	/// numbers (like an asset's amount) either as a bare number or a quoted
	/// string depending on the endpoint. Falls back to <paramref name="fallback"/>
	/// when the element is absent, empty, or unparseable — a malformed amount
	/// degrades to the default rather than aborting the whole inventory read.
	/// </summary>
	private static int ReadInt32Flexible(JsonElement element, int fallback)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Number when element.TryGetInt32(out int numeric):
				return numeric;
			case JsonValueKind.String when int.TryParse(
				element.GetString(),
				System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture,
				out int parsed):
				return parsed;
			default:
				return fallback;
		}
	}

}
