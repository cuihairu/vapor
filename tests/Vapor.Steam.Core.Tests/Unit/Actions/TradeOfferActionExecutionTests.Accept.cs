using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>Accept-trade-offer action tests: active-offer success, state validation, rate-limit and client-failure branches.</summary>
public sealed partial class TradeOfferActionExecutionTests
{
	// --- AcceptTradeOfferAction ---
	[Fact]
	public async Task Accept_ClientThrowsOperationCanceled_Rethrows()
	{
		var client = new FakeTradeClient
		{
			AcceptHandler = (_, _) => throw new OperationCanceledException()
		};
		var action = CreateAcceptAction(client);

		await Assert.ThrowsAsync<OperationCanceledException>(() => action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["trade_offer_id"] = "100",
				["partner_steam_id"] = PartnerParam,
				["verify_state"] = false
			},
			CancellationToken.None));
	}

	[Fact]
	public async Task Accept_ActiveReceivedOffer_Succeeds()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer
				{
					TradeOfferId = 100,
					IsOurOffer = false,
					State = TradeOfferState.Active,
					AccountIdOther = 12345678
				}
			},
			AcceptHandler = (_, _) => new TradeOfferResult { Success = true, RequiresMobileConfirmation = true }
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["trade_offer_id"] = "100",
				["partner_steam_id"] = PartnerParam
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("100", result.Output!["trade_offer_id"]);
		Assert.Equal(true, result.Output["requires_mobile_confirmation"]);
		Assert.Equal(true, result.Output["state_verified"]);
	}

	[Fact]
	public async Task Accept_NoWebHandler_ReturnsError()
	{
		var action = CreateAcceptAction(new FakeTradeClient());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: false),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler not available", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Accept_RateLimited_ReturnsError()
	{
		var action = CreateAcceptAction(new FakeTradeClient(), CreateExhaustedRateLimiter());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rate limit exceeded", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Accept_LoadOfferFails_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult { Success = false, Error = "offer not found" }
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Unable to load trade offer 100 for state verification (offer not found)", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Accept_StateValidationFails_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 100, IsOurOffer = true, State = TradeOfferState.Active }
			}
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("was sent by us and cannot be accepted", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Accept_ClientFailure_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 100, IsOurOffer = false, State = TradeOfferState.Active, AccountIdOther = 12345678 }
			},
			AcceptHandler = (_, _) => new TradeOfferResult { Success = false, Error = "already accepted" }
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("already accepted", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Accept_ClientThrows_ReturnsErrorWithMessage()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => throw new InvalidOperationException("timeout from steam"),
			AcceptHandler = (_, _) => throw new InvalidOperationException("timeout from steam")
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam, ["verify_state"] = false },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("timeout from steam", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Accept_VerifyStateFalse_SkipsStateLookup()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => throw new InvalidOperationException("must not be called"),
			AcceptHandler = (_, _) => new TradeOfferResult { Success = true }
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["trade_offer_id"] = "100",
				["partner_steam_id"] = PartnerParam,
				["verify_state"] = false
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(false, result.Output!["state_verified"]);
	}

	[Fact]
	public async Task Accept_ClientFailureWithoutError_ReturnsFallbackMessage()
	{
		var client = new FakeTradeClient
		{
			AcceptHandler = (_, _) => new TradeOfferResult { Success = false, Error = null }
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam, ["verify_state"] = false },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to accept trade offer", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Accept_LoadOfferFailsWithoutError_MentionsNoOfferReturned()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult { Success = false, Error = null }
		};
		var action = CreateAcceptAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "100", ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Unable to load trade offer 100 for state verification (no offer returned)", result.Error, StringComparison.Ordinal);
	}

	private static AcceptTradeOfferAction CreateAcceptAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<AcceptTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

}
