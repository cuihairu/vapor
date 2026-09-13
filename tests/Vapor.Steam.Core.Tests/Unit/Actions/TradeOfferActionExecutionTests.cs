using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>
/// Execution-path tests for the four trade offer actions (send/accept/decline/cancel),
/// driving the full success and failure branches through a fake
/// <see cref="ISteamTradeClient"/> injected via the internal constructor.
/// </summary>
public sealed class TradeOfferActionExecutionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;
	private const ulong PartnerSteamId = 76561197972611406UL; // account id 12345678
	private const string PartnerParam = "76561197972611406";

	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	// --- SendTradeOfferAction ---

	[Fact]
	public async Task Send_TradeUrl_ParsesPartnerAndTokenAndSends()
	{
		ulong? sentPartner = null;
		string? sentToken = null;
		var client = new FakeTradeClient
		{
			SendHandler = (partner, _, _, token, _) =>
			{
				sentPartner = partner;
				sentToken = token;
				return new TradeOfferResult { Success = true, TradeOfferId = 4242 };
			}
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["trade_url"] = "https://steamcommunity.com/tradeoffer/new/?partner=12345678&token=abc123"
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(PartnerSteamId, sentPartner);
		Assert.Equal("abc123", sentToken);
		Assert.Equal("4242", result.Output!["trade_offer_id"]);
		Assert.Equal(PartnerSteamId.ToString(), result.Output["partner_steam_id"]);
		Assert.Equal(false, result.Output["ownership_verified"]);
	}

	[Fact]
	public async Task Send_InvalidPartnerSteamId_ReturnsError()
	{
		var action = CreateSendAction(new FakeTradeClient());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["partner_steam_id"] = "not-a-number" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Invalid partner_steam_id", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Send_NoWebHandler_ReturnsError()
	{
		var action = CreateSendAction(new FakeTradeClient());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: false),
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler not available", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Send_RateLimited_ReturnsError()
	{
		var action = CreateSendAction(new FakeTradeClient(), CreateExhaustedRateLimiter());

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rate limit exceeded", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Send_ClientFailure_ReturnsError()
	{
		var client = new FakeTradeClient
		{
			SendHandler = (_, _, _, _, _) => new TradeOfferResult { Success = false, Error = "offer would put you in escrow" }
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("escrow", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Send_ClientThrows_ReturnsErrorWithMessage()
	{
		var client = new FakeTradeClient
		{
			SendHandler = (_, _, _, _, _) => throw new InvalidOperationException("steam community down")
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam community down", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Send_ClientThrowsOperationCanceled_Rethrows()
	{
		var client = new FakeTradeClient
		{
			SendHandler = (_, _, _, _, _) => throw new OperationCanceledException()
		};
		var action = CreateSendAction(client);

		await Assert.ThrowsAsync<OperationCanceledException>(() => action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerParam },
			CancellationToken.None));
	}

	[Fact]
	public async Task Send_OwnershipUnknownAsset_FailsVerification()
	{
		var client = new FakeTradeClient
		{
			InventoryHandler = (_, _, _, _) => new InventoryResponse { Success = true, Items = [] }
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerParam,
				["items_to_give"] = new List<Dictionary<string, object?>>
				{
					new() { ["asset_id"] = "999" }
				}
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not found in the inventory", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Send_OwnershipInventoryLoadFails_FailsVerification()
	{
		var client = new FakeTradeClient
		{
			InventoryHandler = (_, _, _, _) => new InventoryResponse { Success = false, Error = "inventory is private" }
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerParam,
				["items_to_give"] = new List<Dictionary<string, object?>>
				{
					new() { ["asset_id"] = "123" }
				}
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to load inventory for ownership verification (app 730): inventory is private", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Send_OwnershipPaginationOverFiftyPages_GivesUpAndFails()
	{
		// Every page yields a new asset id and hasMore=true; the requested asset
		// never appears, so pagination walks past the 50-page guard.
		var client = new FakeTradeClient
		{
			InventoryHandler = (_, _, _, startAssetId) =>
			{
				ulong next = (startAssetId ?? 0) + 1;
				return new InventoryResponse
				{
					Success = true,
					Items = [new InventoryItem { AssetId = next, AppId = 730, Tradable = true }],
					HasMore = true,
					LastAssetId = next
				};
			}
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerParam,
				["items_to_give"] = new List<Dictionary<string, object?>>
				{
					new() { ["asset_id"] = "999" }
				}
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not found in the inventory", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Send_OwnershipCannotResolveOwnSteamId_FailsVerification()
	{
		var client = new FakeTradeClient { OwnSteamIdProvider = () => null };
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerParam,
				["items_to_give"] = new List<Dictionary<string, object?>>
				{
					new() { ["asset_id"] = "123" }
				}
			},
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Unable to resolve own SteamID", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Send_OwnedTradableItem_VerifiesAndSends()
	{
		TradeAsset[]? sentAssets = null;
		var client = new FakeTradeClient
		{
			InventoryHandler = (_, _, _, _) => new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 123, AppId = 730, Tradable = true, Amount = 1 }]
			},
			SendHandler = (_, give, _, _, _) =>
			{
				sentAssets = give.ToArray();
				return new TradeOfferResult { Success = true, TradeOfferId = 7 };
			}
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerParam,
				["items_to_give"] = new object[]
				{
					// object[] (not List<Dictionary<...>>) drives the IEnumerable<object?> branch;
					// string fields drive the TryParse fallbacks; "junk" exercises the
					// non-dictionary item skip.
					new Dictionary<string, object?> { ["asset_id"] = "123", ["app_id"] = "730", ["context_id"] = "2", ["amount"] = "1" },
					new Dictionary<string, object?> { ["asset_id"] = "" },
					"junk"
				}
			},
			CancellationToken.None);

		Assert.True(result.Success);
		TradeAsset asset = Assert.Single(sentAssets!);
		Assert.Equal(123UL, asset.AssetId);
		Assert.Equal(730u, asset.AppId);
		Assert.Equal(2UL, asset.ContextId);
		Assert.Equal(1, asset.Amount);
		Assert.Equal(true, result.Output!["ownership_verified"]);
	}

	[Fact]
	public async Task Send_SkipVerification_BypassesInventory()
	{
		var client = new FakeTradeClient
		{
			InventoryHandler = (_, _, _, _) => throw new InvalidOperationException("must not be called")
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerParam,
				["skip_verification"] = true,
				["items_to_give"] = new List<Dictionary<string, object?>>
				{
					new() { ["asset_id"] = "999" }
				}
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(false, result.Output!["ownership_verified"]);
	}

	// --- AcceptTradeOfferAction ---

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

	// --- DeclineTradeOfferAction ---

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

	// --- helpers ---

	private static SendTradeOfferAction CreateSendAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<SendTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

	private static AcceptTradeOfferAction CreateAcceptAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<AcceptTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

	private static DeclineTradeOfferAction CreateDeclineAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<DeclineTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

	private static CancelTradeOfferAction CreateCancelAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<CancelTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

	private BotSession CreateSession(bool withWebHandler)
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession(
			"test_account",
			credentials,
			registry.Object,
			_sessionLoggerMock.Object,
			null,
			withWebHandler ? CreateWebHandler() : null,
			null);
		_sessions.Add(session);
		return session;
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	/// <summary>
	/// A limiter whose single window slot and concurrency slot are both held by an
	/// unreleased lease, so any action acquire times out and returns null.
	/// </summary>
	private static TradeRateLimiter CreateExhaustedRateLimiter()
	{
		var limiter = new TradeRateLimiter(new TradeRateLimiterOptions
		{
			MaxOperationsPerWindow = 1,
			Window = TimeSpan.FromMinutes(5),
			MaxConcurrentOperations = 1,
			AcquireTimeout = TimeSpan.FromMilliseconds(100)
		});
		_ = limiter.AcquireAsync("test_account").GetAwaiter().GetResult();
		return limiter;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}

	/// <summary>Fake trade client whose every operation is scripted via delegates.</summary>
	private sealed class FakeTradeClient : ISteamTradeClient
	{
		public Func<ulong?> OwnSteamIdProvider { get; set; } = () => OwnSteamId;

		public Func<ulong, uint, ulong, ulong?, InventoryResponse> InventoryHandler { get; set; } =
			(_, _, _, _) => new InventoryResponse { Success = true };

		public Func<ulong, TradeOfferResult> GetOfferHandler { get; set; } =
			_ => new TradeOfferResult { Success = true };

		public Func<ulong, IReadOnlyList<TradeAsset>, IReadOnlyList<TradeAsset>, string?, string?, TradeOfferResult> SendHandler { get; set; } =
			(_, _, _, _, _) => new TradeOfferResult { Success = true, TradeOfferId = 1 };

		public Func<ulong, ulong, TradeOfferResult> AcceptHandler { get; set; } =
			(_, _) => new TradeOfferResult { Success = true };

		public Func<ulong, TradeOfferResult> DeclineHandler { get; set; } =
			_ => new TradeOfferResult { Success = true };

		public Func<ulong, TradeOfferResult> CancelHandler { get; set; } =
			_ => new TradeOfferResult { Success = true };

		public ulong? GetOwnSteamId() => OwnSteamIdProvider();

		public Task<InventoryResponse> GetInventoryAsync(
			ulong steamId, uint appId = 730, ulong contextId = 2, ulong? startAssetId = null, CancellationToken cancellationToken = default) =>
			Task.FromResult(InventoryHandler(steamId, appId, contextId, startAssetId));

		public Task<TradeOffersResponse> GetTradeOffersAsync(bool activeOnly = true, CancellationToken cancellationToken = default) =>
			Task.FromResult(new TradeOffersResponse { Success = true });

		public Task<TradeOfferResult> GetTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(GetOfferHandler(tradeOfferId));

		public Task<TradeOfferResult> SendTradeOfferAsync(
			ulong partnerSteamId,
			IReadOnlyList<TradeAsset> itemsToGive,
			IReadOnlyList<TradeAsset> itemsToReceive,
			string? token = null,
			string? message = null,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(SendHandler(partnerSteamId, itemsToGive, itemsToReceive, token, message));

		public Task<TradeOfferResult> AcceptTradeOfferAsync(ulong tradeOfferId, ulong partnerSteamId, CancellationToken cancellationToken = default) =>
			Task.FromResult(AcceptHandler(tradeOfferId, partnerSteamId));

		public Task<TradeOfferResult> DeclineTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(DeclineHandler(tradeOfferId));

		public Task<TradeOfferResult> CancelTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			Task.FromResult(CancelHandler(tradeOfferId));
	}
}
