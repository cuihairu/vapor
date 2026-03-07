using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// Handles Steam trade-related Web API operations.
/// </summary>
public sealed class SteamTradeClient : IDisposable
{
	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamTradeClient> _logger;
	private readonly JsonSerializerOptions _jsonOptions;
	private bool _disposed;

	private const uint SteamAppId = 730; // Default to CS2 for inventory
	private const ulong DefaultContextId = 2; // Default inventory context

	public SteamTradeClient(SteamWebHandler webHandler, ILogger<SteamTradeClient> logger)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger;
		_jsonOptions = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
		};
	}

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
			// Build inventory URL
			var url = $"https://steamcommunity.com/inventory/{steamId}/{appId}/{contextId}?l=english&count=5000";

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

	/// <summary>
	/// Gets all trade offers for the current user.
	/// </summary>
	/// <param name="activeOnly">Whether to only get active offers.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<TradeOffersResponse> GetTradeOffersAsync(
		bool activeOnly = true,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		try
		{
			// Use the IEconService API
			var apiKey = await GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrEmpty(apiKey))
			{
				return new TradeOffersResponse
				{
					Success = false,
					Error = "Failed to get API key"
				};
			}

			var offersUrl = $"https://api.steampowered.com/IEconService/GetTradeOffers/v1/?" +
				$"key={Uri.EscapeDataString(apiKey)}&" +
				$"get_sent_offers=1&" +
				$"get_received_offers=1&" +
				$"get_descriptions=1&" +
				$"language=english&" +
				$"active_only={(activeOnly ? "1" : "0")}";

			var response = await _webHandler.GetAsync(new Uri(offersUrl), null, cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccess)
			{
				_logger.LogWarning("Trade offers request failed: {StatusCode}", response.StatusCode);
				return new TradeOffersResponse
				{
					Success = false,
					Error = $"HTTP error: {response.StatusCode}"
				};
			}

			if (string.IsNullOrEmpty(response.Body))
			{
				return new TradeOffersResponse
				{
					Success = false,
					Error = "Empty response body"
				};
			}

			return ParseTradeOffersResponse(response.Body);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get trade offers");
			return new TradeOffersResponse
			{
				Success = false,
				Error = ex.Message
			};
		}
	}

	/// <summary>
	/// Gets a specific trade offer by ID.
	/// </summary>
	/// <param name="tradeOfferId">The trade offer ID.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<TradeOfferResult> GetTradeOfferAsync(
		ulong tradeOfferId,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		try
		{
			var apiKey = await GetApiKeyAsync(cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrEmpty(apiKey))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "Failed to get API key"
				};
			}

			var url = $"https://api.steampowered.com/IEconService/GetTradeOffer/v1/?" +
				$"key={Uri.EscapeDataString(apiKey)}&" +
				$"tradeofferid={tradeOfferId}&" +
				$"language=english";

			var response = await _webHandler.GetAsync(new Uri(url), null, cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccess)
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = $"HTTP error: {response.StatusCode}"
				};
			}

			if (string.IsNullOrEmpty(response.Body))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "Empty response body"
				};
			}

			return ParseTradeOfferResponse(response.Body);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to get trade offer {TradeOfferId}", tradeOfferId);
			return new TradeOfferResult
			{
				Success = false,
				Error = ex.Message
			};
		}
	}

	/// <summary>
	/// Sends a trade offer to another user.
	/// </summary>
	/// <param name="partnerSteamId">The partner's 64-bit SteamID.</param>
	/// <param name="itemsToGive">Items to give in the trade.</param>
	/// <param name="itemsToReceive">Items to receive in the trade.</param>
	/// <param name="token">Trade offer token (from trade URL).</param>
	/// <param name="message">Message to include with the offer.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<TradeOfferResult> SendTradeOfferAsync(
		ulong partnerSteamId,
		IReadOnlyList<TradeAsset> itemsToGive,
		IReadOnlyList<TradeAsset> itemsToReceive,
		string? token = null,
		string? message = null,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		try
		{
			var sessionId = GetSessionId();
			if (string.IsNullOrEmpty(sessionId))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "No session ID available"
				};
			}

			// Build trade offer JSON
			var tradeOfferJson = BuildTradeOfferJson(itemsToGive, itemsToReceive, partnerSteamId, token);

			var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["partner"] = partnerSteamId.ToString(),
				["tradeoffermessage"] = message ?? string.Empty,
				["json_tradeoffer"] = tradeOfferJson,
				["captcha"] = string.Empty,
				["trade_offer_create_params"] = string.IsNullOrEmpty(token) ? "{}" : $"{{\"trade_offer_access_token\":\"{token}\"}}"
			});

			var headers = new Dictionary<string, string>
			{
				["X-Requested-With"] = "XMLHttpRequest"
			};

			var url = $"https://steamcommunity.com/tradeoffer/new/send";
			var response = await _webHandler.PostAsync(new Uri(url), content, headers, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "Unauthorized - session may have expired"
				};
			}

			if (!response.IsSuccess)
			{
				_logger.LogWarning("Send trade offer failed: {StatusCode}", response.StatusCode);
				return new TradeOfferResult
				{
					Success = false,
					Error = $"HTTP error: {response.StatusCode}"
				};
			}

			if (string.IsNullOrEmpty(response.Body))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "Empty response body"
				};
			}

			return ParseSendTradeOfferResponse(response.Body);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to send trade offer to {PartnerSteamId}", partnerSteamId);
			return new TradeOfferResult
			{
				Success = false,
				Error = ex.Message
			};
		}
	}

	/// <summary>
	/// Accepts a trade offer.
	/// </summary>
	/// <param name="tradeOfferId">The trade offer ID to accept.</param>
	/// <param name="partnerSteamId">The partner's 64-bit SteamID.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<TradeOfferResult> AcceptTradeOfferAsync(
		ulong tradeOfferId,
		ulong partnerSteamId,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		try
		{
			var sessionId = GetSessionId();
			if (string.IsNullOrEmpty(sessionId))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "No session ID available"
				};
			}

			var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["tradeofferid"] = tradeOfferId.ToString(),
				["partner"] = partnerSteamId.ToString(),
				["captcha"] = string.Empty
			});

			var headers = new Dictionary<string, string>
			{
				["X-Requested-With"] = "XMLHttpRequest"
			};

			var url = $"https://steamcommunity.com/tradeoffer/{tradeOfferId}/accept";
			var response = await _webHandler.PostAsync(new Uri(url), content, headers, cancellationToken).ConfigureAwait(false);

			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "Unauthorized - session may have expired"
				};
			}

			if (!response.IsSuccess)
			{
				_logger.LogWarning("Accept trade offer failed: {StatusCode}", response.StatusCode);
				return new TradeOfferResult
				{
					Success = false,
					Error = $"HTTP error: {response.StatusCode}"
				};
			}

			if (string.IsNullOrEmpty(response.Body))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "Empty response body"
				};
			}

			return ParseAcceptTradeOfferResponse(response.Body, tradeOfferId);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to accept trade offer {TradeOfferId}", tradeOfferId);
			return new TradeOfferResult
			{
				Success = false,
				Error = ex.Message
			};
		}
	}

	/// <summary>
	/// Declines a trade offer.
	/// </summary>
	/// <param name="tradeOfferId">The trade offer ID to decline.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<TradeOfferResult> DeclineTradeOfferAsync(
		ulong tradeOfferId,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		try
		{
			var sessionId = GetSessionId();
			if (string.IsNullOrEmpty(sessionId))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "No session ID available"
				};
			}

			var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["tradeofferid"] = tradeOfferId.ToString()
			});

			var headers = new Dictionary<string, string>
			{
				["X-Requested-With"] = "XMLHttpRequest"
			};

			var url = $"https://steamcommunity.com/tradeoffer/{tradeOfferId}/decline";
			var response = await _webHandler.PostAsync(new Uri(url), content, headers, cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccess)
			{
				_logger.LogWarning("Decline trade offer failed: {StatusCode}", response.StatusCode);
				return new TradeOfferResult
				{
					Success = false,
					Error = $"HTTP error: {response.StatusCode}"
				};
			}

			return new TradeOfferResult
			{
				Success = true,
				TradeOfferId = tradeOfferId
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to decline trade offer {TradeOfferId}", tradeOfferId);
			return new TradeOfferResult
			{
				Success = false,
				Error = ex.Message
			};
		}
	}

	/// <summary>
	/// Cancels a trade offer sent by the current user.
	/// </summary>
	/// <param name="tradeOfferId">The trade offer ID to cancel.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task<TradeOfferResult> CancelTradeOfferAsync(
		ulong tradeOfferId,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		try
		{
			var sessionId = GetSessionId();
			if (string.IsNullOrEmpty(sessionId))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "No session ID available"
				};
			}

			var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["tradeofferid"] = tradeOfferId.ToString()
			});

			var headers = new Dictionary<string, string>
			{
				["X-Requested-With"] = "XMLHttpRequest"
			};

			var url = $"https://steamcommunity.com/tradeoffer/{tradeOfferId}/cancel";
			var response = await _webHandler.PostAsync(new Uri(url), content, headers, cancellationToken).ConfigureAwait(false);

			if (!response.IsSuccess)
			{
				_logger.LogWarning("Cancel trade offer failed: {StatusCode}", response.StatusCode);
				return new TradeOfferResult
				{
					Success = false,
					Error = $"HTTP error: {response.StatusCode}"
				};
			}

			return new TradeOfferResult
			{
				Success = true,
				TradeOfferId = tradeOfferId
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to cancel trade offer {TradeOfferId}", tradeOfferId);
			return new TradeOfferResult
			{
				Success = false,
				Error = ex.Message
			};
		}
	}

	private string? GetSessionId()
	{
		var cookies = _webHandler.GetAllCookies();
		return cookies.TryGetValue("sessionid", out var sessionId) ? sessionId : null;
	}

	private async Task<string?> GetApiKeyAsync(CancellationToken cancellationToken)
	{
		// First, try to get the API key from cookies/session
		var url = "https://steamcommunity.com/dev/apikey";
		var response = await _webHandler.GetAsync(new Uri(url), null, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			return null;
		}

		// Parse the API key from the response
		// Look for the API key in the response body
		const string keyPattern = "<p>Key: ";
		var keyIndex = response.Body.IndexOf(keyPattern, StringComparison.OrdinalIgnoreCase);
		if (keyIndex >= 0)
		{
			var startIndex = keyIndex + keyPattern.Length;
			var endIndex = response.Body.IndexOf("</p>", startIndex, StringComparison.OrdinalIgnoreCase);
			if (endIndex > startIndex)
			{
				var key = response.Body[startIndex..endIndex].Trim();
				if (!string.IsNullOrEmpty(key) && key.Length > 20)
				{
					return key;
				}
			}
		}

		return null;
	}

	private string BuildTradeOfferJson(
		IReadOnlyList<TradeAsset> itemsToGive,
		IReadOnlyList<TradeAsset> itemsToReceive,
		ulong partnerSteamId,
		string? token)
	{
		var sb = new StringBuilder();
		sb.Append('{');

		// New version
		sb.Append("\"newversion\":true,");

		// Version
		sb.Append("\"version\":4,");

		// Me (items to give)
		sb.Append("\"me\":{");
		sb.Append("\"assets\":[");
		for (int i = 0; i < itemsToGive.Count; i++)
		{
			if (i > 0) sb.Append(',');
			var item = itemsToGive[i];
			sb.Append($"{{\"appid\":{item.AppId},\"contextid\":\"{item.ContextId}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}
		sb.Append("],");
		sb.Append("\"currency\":[],");
		sb.Append("\"ready\":false");
		sb.Append("},");

		// Them (items to receive)
		sb.Append("\"them\":{");
		sb.Append("\"assets\":[");
		for (int i = 0; i < itemsToReceive.Count; i++)
		{
			if (i > 0) sb.Append(',');
			var item = itemsToReceive[i];
			sb.Append($"{{\"appid\":{item.AppId},\"contextid\":\"{item.ContextId}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}
		sb.Append("],");
		sb.Append("\"currency\":[],");
		sb.Append("\"ready\":false");
		sb.Append("}");

		sb.Append('}');

		return sb.ToString();
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
						? ulong.Parse(classIdElem.GetString() ?? "0")
						: 0;
					var instanceId = desc.TryGetProperty("instanceid", out var instanceIdElem)
						? ulong.Parse(instanceIdElem.GetString() ?? "0")
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
						? ulong.Parse(classIdElem.GetString() ?? "0")
						: 0;
					var instanceId = asset.TryGetProperty("instanceid", out var instanceIdElem)
						? ulong.Parse(instanceIdElem.GetString() ?? "0")
						: 0;
					var assetId = asset.TryGetProperty("assetid", out var assetIdElem)
						? ulong.Parse(assetIdElem.GetString() ?? "0")
						: 0;
					var amount = asset.TryGetProperty("amount", out var amountElem)
						? amountElem.GetInt32()
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
				? ulong.Parse(lastAssetIdElem.GetString() ?? "0")
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

	private TradeOffersResponse ParseTradeOffersResponse(string body)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (!root.TryGetProperty("response", out var response))
			{
				return new TradeOffersResponse
				{
					Success = false,
					Error = "Invalid response format"
				};
			}

			var sentOffers = new List<TradeOffer>();
			var receivedOffers = new List<TradeOffer>();

			// Parse descriptions
			var descriptions = new Dictionary<(uint AppId, ulong ClassId, ulong InstanceId), JsonElement>();
			if (response.TryGetProperty("descriptions", out var descriptionsElem))
			{
				foreach (var desc in descriptionsElem.EnumerateArray())
				{
					var appId = desc.TryGetProperty("appid", out var appIdElem) ? appIdElem.GetUInt32() : 0;
					var classId = desc.TryGetProperty("classid", out var classIdElem)
						? ulong.Parse(classIdElem.GetString() ?? "0")
						: 0;
					var instanceId = desc.TryGetProperty("instanceid", out var instanceIdElem)
						? ulong.Parse(instanceIdElem.GetString() ?? "0")
						: 0;

					descriptions[(appId, classId, instanceId)] = desc;
				}
			}

			// Parse sent offers
			if (response.TryGetProperty("trade_offers_sent", out var sentElem))
			{
				foreach (var offer in sentElem.EnumerateArray())
				{
					sentOffers.Add(ParseTradeOffer(offer, true));
				}
			}

			// Parse received offers
			if (response.TryGetProperty("trade_offers_received", out var receivedElem))
			{
				foreach (var offer in receivedElem.EnumerateArray())
				{
					receivedOffers.Add(ParseTradeOffer(offer, false));
				}
			}

			return new TradeOffersResponse
			{
				Success = true,
				SentOffers = sentOffers,
				ReceivedOffers = receivedOffers
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to parse trade offers response");
			return new TradeOffersResponse
			{
				Success = false,
				Error = $"Failed to parse response: {ex.Message}"
			};
		}
	}

	private TradeOffer ParseTradeOffer(JsonElement offer, bool isOurOffer)
	{
		return new TradeOffer
		{
			TradeOfferId = offer.TryGetProperty("tradeofferid", out var idElem)
				? ulong.Parse(idElem.GetString() ?? "0")
				: 0,
			AccountIdOther = offer.TryGetProperty("accountid_other", out var otherElem)
				? (ulong)otherElem.GetInt32()
				: 0,
			Message = offer.TryGetProperty("message", out var msgElem) ? msgElem.GetString() : null,
			ItemsToGiveCount = offer.TryGetProperty("items_to_give", out var giveElem)
				? giveElem.EnumerateArray().Count()
				: 0,
			ItemsToReceiveCount = offer.TryGetProperty("items_to_receive", out var receiveElem)
				? receiveElem.EnumerateArray().Count()
				: 0,
			IsOurOffer = isOurOffer,
			TimeCreated = offer.TryGetProperty("time_created", out var createdElem)
				? DateTimeOffset.FromUnixTimeSeconds(createdElem.GetInt32())
				: DateTimeOffset.MinValue,
			TimeExpires = offer.TryGetProperty("time_expires", out var expiresElem)
				? DateTimeOffset.FromUnixTimeSeconds(expiresElem.GetInt32())
				: null,
			State = offer.TryGetProperty("trade_offer_state", out var stateElem)
				? (TradeOfferState)stateElem.GetInt32()
				: TradeOfferState.Invalid
		};
	}

	private TradeOfferResult ParseTradeOfferResponse(string body)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (!root.TryGetProperty("response", out var response))
			{
				return new TradeOfferResult
				{
					Success = false,
					Error = "Invalid response format"
				};
			}

			if (response.TryGetProperty("offer", out var offerElem))
			{
				var offer = ParseTradeOffer(offerElem, true);
				return new TradeOfferResult
				{
					Success = true,
					TradeOffer = offer
				};
			}

			return new TradeOfferResult
			{
				Success = false,
				Error = "No offer in response"
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to parse trade offer response");
			return new TradeOfferResult
			{
				Success = false,
				Error = $"Failed to parse response: {ex.Message}"
			};
		}
	}

	private TradeOfferResult ParseSendTradeOfferResponse(string body)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (root.TryGetProperty("success", out var successElem) && successElem.GetInt32() == 1)
			{
				var tradeOfferId = root.TryGetProperty("tradeofferid", out var idElem)
					? ulong.Parse(idElem.GetString() ?? "0")
					: 0;

				var requiresMobileConfirmation = root.TryGetProperty("requires_mobile_confirmation", out var mobileElem)
					&& mobileElem.GetBoolean();

				return new TradeOfferResult
				{
					Success = true,
					TradeOfferId = tradeOfferId,
					RequiresMobileConfirmation = requiresMobileConfirmation
				};
			}

			var error = "Unknown error";
			if (root.TryGetProperty("strError", out var errorElem))
			{
				error = errorElem.GetString() ?? error;
			}

			return new TradeOfferResult
			{
				Success = false,
				Error = error
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to parse send trade offer response");
			return new TradeOfferResult
			{
				Success = false,
				Error = $"Failed to parse response: {ex.Message}"
			};
		}
	}

	private TradeOfferResult ParseAcceptTradeOfferResponse(string body, ulong tradeOfferId)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (root.TryGetProperty("success", out var successElem) && successElem.GetInt32() == 1)
			{
				var requiresMobileConfirmation = root.TryGetProperty("requires_mobile_confirmation", out var mobileElem)
					&& mobileElem.GetBoolean();

				return new TradeOfferResult
				{
					Success = true,
					TradeOfferId = tradeOfferId,
					RequiresMobileConfirmation = requiresMobileConfirmation
				};
			}

			var error = "Unknown error";
			if (root.TryGetProperty("strError", out var errorElem))
			{
				error = errorElem.GetString() ?? error;
			}

			return new TradeOfferResult
			{
				Success = false,
				Error = error
			};
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to parse accept trade offer response");
			return new TradeOfferResult
			{
				Success = false,
				Error = $"Failed to parse response: {ex.Message}"
			};
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamTradeClient));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
	}
}
