using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>Decline-trade-offer action tests: active-offer success, state validation, rate-limit, client-failure and metadata shape.</summary>
public sealed partial class TradeOfferActionExecutionTests
{
	// --- DeclineTradeOfferAction ---
	[Fact]
	public async Task Decline_ClientThrowsOperationCanceled_Rethrows()
	{
		var client = new FakeTradeClient
		{
			DeclineHandler = _ => throw new OperationCanceledException()
		};
		var action = CreateDeclineAction(client);

		await Assert.ThrowsAsync<OperationCanceledException>(() => action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200", ["verify_state"] = false },
			CancellationToken.None));
	}

	[Fact]
	public async Task Decline_ActiveReceivedOffer_Succeeds()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 200, IsOurOffer = false, State = TradeOfferState.Active }
			},
			DeclineHandler = _ => new TradeOfferResult { Success = true }
		};
		var action = CreateDeclineAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("200", result.Output!["trade_offer_id"]);
		Assert.Equal(true, result.Output["state_verified"]);
	}

	[Fact]
	public async Task Decline_NoWebHandler_ReturnsError()
	{
		var action = CreateDeclineAction(new FakeTradeClient());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: false),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler not available", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Decline_RateLimited_ReturnsError()
	{
		var action = CreateDeclineAction(new FakeTradeClient(), CreateExhaustedRateLimiter());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rate limit exceeded", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Decline_LoadOfferFails_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult { Success = false, Error = null }
		};
		var action = CreateDeclineAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Unable to load trade offer 200 for state verification (no offer returned)", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Decline_StateValidationFails_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 200, IsOurOffer = true, State = TradeOfferState.Active }
			}
		};
		var action = CreateDeclineAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("cannot be declined", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Decline_ClientFailure_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 200, IsOurOffer = false, State = TradeOfferState.Active }
			},
			DeclineHandler = _ => new TradeOfferResult { Success = false, Error = "server rejected" }
		};
		var action = CreateDeclineAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("server rejected", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Decline_ClientThrows_ReturnsErrorWithMessage()
	{
		var client = new FakeTradeClient
		{
			DeclineHandler = _ => throw new InvalidOperationException("network glitch")
		};
		var action = CreateDeclineAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200", ["verify_state"] = false },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("network glitch", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Decline_Metadata_HasExpectedValues()
	{
		var action = CreateDeclineAction(new FakeTradeClient());
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
		Assert.True(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task Decline_ClientFailureWithoutError_ReturnsFallbackMessage()
	{
		var client = new FakeTradeClient
		{
			DeclineHandler = _ => new TradeOfferResult { Success = false, Error = null }
		};
		var action = CreateDeclineAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "200", ["verify_state"] = false },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to decline trade offer", result.Error, StringComparison.Ordinal);
	}

	private static DeclineTradeOfferAction CreateDeclineAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<DeclineTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

}
