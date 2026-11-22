using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>Send-trade-offer action tests: URL/token parsing, rate-limit and client-failure branches, ownership verification and skip-verification path.</summary>
public sealed partial class TradeOfferActionExecutionTests
{
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

	// --- null-Error fallback arms (result.Error ?? "Failed to ...") ---
	[Fact]
	public async Task Send_ClientFailureWithoutError_ReturnsFallbackMessage()
	{
		var client = new FakeTradeClient
		{
			SendHandler = (_, _, _, _, _) => new TradeOfferResult { Success = false, Error = null }
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?> { ["partner_steam_id"] = PartnerParam },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Failed to send trade offer", result.Error);
	}

	[Fact]
	public async Task Send_SuccessWithoutTradeOfferId_OutputCarriesNullId()
	{
		var client = new FakeTradeClient
		{
			InventoryHandler = (_, _, _, _) => new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 123, AppId = 730, Tradable = true }]
			},
			SendHandler = (_, _, _, _, _) => new TradeOfferResult { Success = true, TradeOfferId = null }
		};
		var action = CreateSendAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: true),
			new Dictionary<string, object?>
			{
				["partner_steam_id"] = PartnerParam,
				["items_to_give"] = new List<Dictionary<string, object?>>
				{
					new() { ["asset_id"] = "123", ["app_id"] = "730", ["context_id"] = "2" }
				}
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Output!["trade_offer_id"]);
		Assert.Equal(true, result.Output["ownership_verified"]);
	}

	// --- helpers ---
	private static SendTradeOfferAction CreateSendAction(
		ISteamTradeClient client,
		TradeRateLimiter? rateLimiter = null) =>
		new(
			NullLogger<SendTradeOfferAction>.Instance,
			_ => client,
			rateLimiter);

}
