using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>Cancel-trade-offer action tests: pending-offer success, state validation, rate-limit, client-failure and metadata shape.</summary>
public sealed partial class TradeOfferActionExecutionTests
{
	// --- CancelTradeOfferAction ---
	[Fact]
	public async Task Cancel_PendingSentOffer_Succeeds()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 300, IsOurOffer = true, State = TradeOfferState.CreatedNeedsConfirmation }
			},
			CancelHandler = _ => new TradeOfferResult { Success = true }
		};
		var action = CreateCancelAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("300", result.Output!["trade_offer_id"]);
		Assert.Equal(true, result.Output["state_verified"]);
	}

	[Fact]
	public async Task Cancel_NoWebHandler_ReturnsError()
	{
		var action = CreateCancelAction(new FakeTradeClient());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: false),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler not available", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Cancel_RateLimited_ReturnsError()
	{
		var action = CreateCancelAction(new FakeTradeClient(), CreateExhaustedRateLimiter());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rate limit exceeded", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Cancel_LoadOfferFails_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult { Success = false, Error = "gone" }
		};
		var action = CreateCancelAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Unable to load trade offer 300 for state verification (gone)", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Cancel_StateValidationFails_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 300, IsOurOffer = false, State = TradeOfferState.Active }
			}
		};
		var action = CreateCancelAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("cannot be canceled", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Cancel_ClientFailure_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 300, IsOurOffer = true, State = TradeOfferState.Active }
			},
			CancelHandler = _ => new TradeOfferResult { Success = false, Error = "already accepted by partner" }
		};
		var action = CreateCancelAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("already accepted by partner", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Cancel_ClientThrows_ReturnsErrorWithMessage()
	{
		var client = new FakeTradeClient
		{
			CancelHandler = _ => throw new InvalidOperationException("steam down")
		};
		var action = CreateCancelAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300", ["verify_state"] = false },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam down", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Cancel_ClientThrowsOperationCanceled_Rethrows()
	{
		var client = new FakeTradeClient
		{
			CancelHandler = _ => throw new OperationCanceledException()
		};
		var action = CreateCancelAction(client);

		await Assert.ThrowsAsync<OperationCanceledException>(() => action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300", ["verify_state"] = false },
			CancellationToken.None));
	}

	[Fact]
	public void Cancel_Metadata_HasExpectedValues()
	{
		var action = CreateCancelAction(new FakeTradeClient());
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
		Assert.True(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task Cancel_LoadOfferFailsWithoutError_MentionsNoOfferReturned()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult { Success = false, Error = null }
		};
		var action = CreateCancelAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Unable to load trade offer 300 for state verification (no offer returned)", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Cancel_ClientFailureWithoutError_ReturnsFallbackMessage()
	{
		var client = new FakeTradeClient
		{
			GetOfferHandler = _ => new TradeOfferResult
			{
				Success = true,
				TradeOffer = new TradeOffer { TradeOfferId = 300, IsOurOffer = true, State = TradeOfferState.Active }
			},
			CancelHandler = _ => new TradeOfferResult { Success = false, Error = null }
		};
		var action = CreateCancelAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["trade_offer_id"] = "300" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to cancel trade offer", result.Error, StringComparison.Ordinal);
	}

	private static CancelTradeOfferAction CreateCancelAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<CancelTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

}
