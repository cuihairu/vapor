using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Web;

/// <summary>Trade-offer lifecycle surface: list/get/send/accept/decline/cancel plus session-id and offer-json helpers.</summary>
public sealed partial class SteamTradeClient
{
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

			using var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["partner"] = partnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

			using var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["tradeofferid"] = tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["partner"] = partnerSteamId.ToString(System.Globalization.CultureInfo.InvariantCulture),
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

			using var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["tradeofferid"] = tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture)
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

			using var content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["sessionid"] = sessionId,
				["serverid"] = "1",
				["tradeofferid"] = tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture)
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

	/// <summary>
	/// Resolves the logged-in user's own 64-bit SteamID from web session cookies
	/// (the steamlogin cookie encodes it as "steamid%7C%7Ctoken").
	/// Returns null when the session cookies do not carry a usable identity.
	/// </summary>
	public ulong? GetOwnSteamId()
	{
		ThrowIfDisposed();

		return _webHandler.TryResolveOwnSteamId();
	}

	private Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
		SteamWebApiKeyFetcher.FetchAsync(_webHandler, cancellationToken);

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
			sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{{\"appid\":{item.AppId},\"contextid\":\"{item.ContextId}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
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
			sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{{\"appid\":{item.AppId},\"contextid\":\"{item.ContextId}\",\"amount\":{item.Amount},\"assetid\":\"{item.AssetId}\"}}");
		}
		sb.Append("],");
		sb.Append("\"currency\":[],");
		sb.Append("\"ready\":false");
		sb.Append('}');

		sb.Append('}');

		return sb.ToString();
	}

}
