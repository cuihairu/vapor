using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Protocol;

using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;

using Xunit;

namespace Vapor.Plugins.Dota2.Tests;

public class Dota2SendTradeActionTests : Dota2TestBase
{
	private const string TradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=111222333&token=abc123";

	private static async Task<(Dota2SendTradeAction Action, FakeTradeClient Client)> CreateActionAsync(
		FakeTradeClient client,
		TradeRateLimiter? rateLimiter = null,
		bool tradeEnabled = true)
	{
		var plugin = new Dota2Plugin(_ => client);
		var configuration = new Dictionary<string, string>();
		if (tradeEnabled)
		{
			configuration["trade.enabled"] = "true";
		}

		await plugin.InitializeAsync(
			new TestPluginContext(configuration, rateLimiter is null ? null : new FixedServiceProvider(rateLimiter)),
			CancellationToken.None);

		return (
			new Dota2SendTradeAction(plugin, NullLogger<Dota2SendTradeAction>.Instance, rateLimiter, _ => client),
			client);
	}

	/// <summary>
	/// The end-to-end wiring variant: plugin initialized through the real
	/// InitializeAsync, action pulled from GetActions() with host loggers and
	/// the limiter resolved through host services.
	/// </summary>
	private static async Task<Dota2SendTradeAction> WiredActionAsync(
		FakeTradeClient client,
		TradeRateLimiter? limiter = null)
	{
		var plugin = new Dota2Plugin(_ => client);
		await plugin.InitializeAsync(
			new TestPluginContext(new Dictionary<string, string> { ["trade.enabled"] = "true" },
				limiter is null ? null : new FixedServiceProvider(limiter)),
			CancellationToken.None);
		return (Dota2SendTradeAction)plugin.GetActions().Single(action => action.Name == "dota2_send_trade");
	}

	private static Dictionary<string, object?> OfferPayload(
		params (string Key, object? Value)[] extra) => Payload(
		[
			("partner_steam_id", "111222333"),
			("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = "77" } }),
			.. extra
		]);

	[Fact]
	public void Metadata_DeclaresNonIdempotentLoginRequired()
	{
		var action = new Dota2SendTradeAction(new Dota2Plugin());

		Assert.Equal("dota2_send_trade", action.Name);
		Assert.Equal("dota2_send_trade", action.Metadata.Name);
		Assert.Equal(ActionSafety.NonIdempotent, action.Metadata.Safety);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsNullPayload()
	{
		var (action, _) = await CreateActionAsync(new FakeTradeClient());

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			action.ExecuteAsync(CreateSession(CreateWebHandler()), null!, CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_GateOff_FailsWithConfigAndEnvNames_AndTouchesNoTransport()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))), tradeEnabled: false);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("'trade.enabled'", result.Error);
		Assert.Contains("VAPOR_DOTA2_TRADE_ENABLED", result.Error);
		Assert.Equal(0, client.SendCalls);
		Assert.Empty(client.RequestedSteamIds);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresAPartner()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("either partner_steam_id or trade_url is required", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsAnUnparsableTradeUrl()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("trade_url", "https://example.com/nope")),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("invalid trade_url", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsAnUnparsablePartnerSteamId()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("partner_steam_id", "not-a-number")),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("invalid partner_steam_id parameter", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_EmptyOffer_IsRejected()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("partner_steam_id", "111222333")),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("at least one item", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_ScalarItemLists_AreRejected()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("partner_steam_id", "111222333"), ("items_to_give", 5)),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("at least one item", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_EntriesWithoutAssetId_AreErrors_NeverSilentDrops()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { new Dictionary<string, object?> { ["amount"] = "1" } })),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("need a positive 'asset_id'", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_ZeroAssetId_IsRejected()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = "0" } })),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("need a positive 'asset_id'", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_NonPositiveAmount_IsRejected()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = "77", ["amount"] = "0" } })),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("invalid 'amount'", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_ForeignAppId_IsRejectedNotRemapped()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = "77", ["app_id"] = "730" } })),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("not a Dota 2 item (app 730)", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_ForeignContextId_IsRejected()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = "77", ["context_id"] = "6" } })),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("context 6", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_NonObjectEntries_AreRejected()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { "just-a-string" })),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("must be item objects", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_OverTheCap_IsRejected()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));
		var items = Enumerable.Range(1, Dota2SendTradeAction.MaxAssetsPerOffer + 1)
			.Select(index => (object)new Dictionary<string, object?> { ["asset_id"] = index.ToString() })
			.ToArray();

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("partner_steam_id", "111222333"), ("items_to_give", items)),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains($"at most {Dota2SendTradeAction.MaxAssetsPerOffer}", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresTheWebHandler()
	{
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(null), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RateLimited_FailsWithoutSending()
	{
		var limiter = new TradeRateLimiter(new TradeRateLimiterOptions { AcquireTimeout = TimeSpan.FromMilliseconds(50) });
		TradeRateLease? held = await limiter.AcquireAsync("test_account");
		Assert.NotNull(held);
		var (action, client) = await CreateActionAsync(new FakeTradeClient(Pages.Of(Pages.Dota(77))), limiter);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rate limit exceeded", result.Error);
		Assert.Equal(0, client.SendCalls);
		held!.Dispose();
	}

	[Fact]
	public async Task ExecuteAsync_ValidationFailure_ReportsTheSevenDayCooldown_BeforeAnySend()
	{
		var client = new FakeTradeClient(Pages.Of(
			Pages.Dota(77, tradable: false, tradabilityDate: DateTimeOffset.UtcNow.AddDays(3))));
		var (action, _) = await CreateActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.StartsWith("trade validation failed:", result.Error);
		Assert.Contains("7-day market trade cooldown", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_ValidationFailure_NotTradableWithoutDate_NamesTheRule()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77, tradable: false)));
		var (action, _) = await CreateActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("up to 7 days", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_ValidationWithoutOwnSteamId_Fails()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77))) { OwnSteamId = null };
		var (action, _) = await CreateActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("unable to resolve the own SteamID", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_ValidationInventoryError_IsWrapped()
	{
		var client = new FakeTradeClient(Pages.Failed("inventory down"));
		var (action, _) = await CreateActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("failed to load the Dota 2 inventory for validation: inventory down", result.Error);
		Assert.Equal(0, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_HappyPath_SendsOneOffer_WithPinnedAppContext()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)));
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = 77 } }),
				("items_to_receive", new object[] { new Dictionary<string, object?> { ["asset_id"] = "88", ["amount"] = "2" } }),
				("message", "have fun")),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, client.SendCalls);
		(ulong partner, IReadOnlyList<TradeAsset> give, IReadOnlyList<TradeAsset> receive, string? token, string? message) = client.SentOffers[0];
		Assert.Equal(111222333UL, partner);
		TradeAsset given = give.Single();
		Assert.Equal(570u, given.AppId);
		Assert.Equal(2ul, given.ContextId);
		Assert.Equal(77UL, given.AssetId);
		Assert.Equal(88UL, receive.Single().AssetId);
		Assert.Null(token);
		Assert.Equal("have fun", message);

		IReadOnlyDictionary<string, object?> output = result.Output!;
		Assert.Equal("trade", output["mode"]);
		Assert.Equal(570u, output["app_id"]);
		Assert.Equal("4242", output["trade_offer_id"]);
		Assert.Equal("111222333", output["partner_steam_id"]);
		Assert.False((bool)output["requires_mobile_confirmation"]!);
		Assert.True((bool)output["ownership_verified"]!);
		Assert.Equal(1, output["items_to_give"]);
		Assert.Equal(1, output["items_to_receive"]);
	}

	[Fact]
	public async Task ExecuteAsync_TradeUrl_ExtractsPartnerAndToken()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)));
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("trade_url", TradeUrl), ("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = "77" } })),
			CancellationToken.None);

		Assert.True(result.Success);
		// Trade URLs carry the partner's 32-bit account id; the parsed param is
		// the full 64-bit SteamID (base + account id).
		Assert.Equal(76561198071488061UL, client.SentOffers[0].Partner);
		Assert.Equal("abc123", client.SentOffers[0].Token);
	}

	[Fact]
	public async Task ExecuteAsync_ReceiveOnlyOffer_SkipsOwnershipValidation_ButSends()
	{
		var client = new FakeTradeClient();
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("partner_steam_id", "111222333"), ("items_to_receive", new object[] { new Dictionary<string, object?> { ["asset_id"] = "88" } })),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, client.SendCalls);
		Assert.False((bool)result.Output!["ownership_verified"]!);
		// No inventory read happened: nothing to validate on the give side.
		Assert.Empty(client.RequestedSteamIds);
	}

	[Fact]
	public async Task ExecuteAsync_ExplicitDotaAppContext_IsAccepted()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)));
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new object[] { new Dictionary<string, object?> { ["asset_id"] = "77", ["app_id"] = "570", ["context_id"] = "2" } })),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, client.SendCalls);
	}

	[Fact]
	public async Task ExecuteAsync_SendFailure_IsReported()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)))
		{
			SendResult = new TradeOfferResult { Success = false, Error = "steam refused" }
		};
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("steam refused", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_SendFailureWithoutAnErrorText_UsesTheDefaultMessage()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)))
		{
			SendResult = new TradeOfferResult { Success = false, Error = null }
		};
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("failed to send the trade offer", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_SuccessWithoutAnOfferId_ReportsNull()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)))
		{
			SendResult = new TradeOfferResult { Success = true, TradeOfferId = null }
		};
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Output!["trade_offer_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_SendException_IsWrapped()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77))) { ThrowOnSend = true };
		Dota2SendTradeAction action = await WiredActionAsync(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("send boom", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_ValidationCancellation_Propagates()
	{
		var client = new FakeTradeClient(Pages.Of()) { ThrowOceOnInventory = true };
		Dota2SendTradeAction action = await WiredActionAsync(client);

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			action.ExecuteAsync(CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_JsonElementPayloads_ParseLikeDictionaryOnes()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77), Pages.Dota(78)));
		Dota2SendTradeAction action = await WiredActionAsync(client);

		using JsonDocument give = JsonDocument.Parse("""[{"asset_id": 77}, {"asset_id": "78"}]""");
		using JsonDocument receive = JsonDocument.Parse("""[{"asset_id": 88}]""");
		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", give.RootElement.Clone()),
				("items_to_receive", receive.RootElement.Clone())),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, client.SentOffers[0].Give.Count);
		Assert.Single(client.SentOffers[0].Receive);
	}

	[Fact]
	public async Task ExecuteAsync_JsonElementBadEntries_FailTheCall()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)));
		Dota2SendTradeAction action = await WiredActionAsync(client);

		using JsonDocument give = JsonDocument.Parse("""[{"asset_id": true}]""");
		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("partner_steam_id", "111222333"), ("items_to_give", give.RootElement.Clone())),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("need a positive 'asset_id'", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_SingleObjectEntries_AreAccepted()
	{
		// One bare Dictionary and one bare JsonElement object, no array wrapper.
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)));
		var plugin = new Dota2Plugin(_ => client);
		await plugin.InitializeAsync(
			new TestPluginContext(new Dictionary<string, string> { ["trade.enabled"] = "true" }),
			CancellationToken.None);
		var action = (Dota2SendTradeAction)plugin.GetActions().Single(a => a.Name == "dota2_send_trade");

		using JsonDocument single = JsonDocument.Parse("""{"asset_id": 78}""");
		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(
				("partner_steam_id", "111222333"),
				("items_to_give", new Dictionary<string, object?> { ["asset_id"] = "77" }),
				("items_to_receive", (object?)single.RootElement.Clone())),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Single(client.SentOffers[0].Give);
		Assert.Single(client.SentOffers[0].Receive);
	}

	[Fact]
	public async Task ExecuteAsync_WithTheSharedLimiter_SendsThroughTheLease()
	{
		var client = new FakeTradeClient(Pages.Of(Pages.Dota(77)));
		Dota2SendTradeAction action = await WiredActionAsync(client, new TradeRateLimiter());

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()), OfferPayload(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, client.SendCalls);
	}
}
