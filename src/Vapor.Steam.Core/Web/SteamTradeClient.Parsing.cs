using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Web;

/// <summary>Response parsing helpers for trade-offer payloads.</summary>
public sealed partial class SteamTradeClient
{
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
						? ulong.Parse(classIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
						: 0;
					var instanceId = desc.TryGetProperty("instanceid", out var instanceIdElem)
						? ulong.Parse(instanceIdElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
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
				? ulong.Parse(idElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
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
					? ulong.Parse(idElem.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture)
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

}
