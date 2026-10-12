using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Protocol;

using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;

using Xunit;

namespace Vapor.Plugins.Dota2.Tests;

public class Dota2TradesActionTests : Dota2TestBase
{
	private static (Dota2TradesAction Action, FakeTradeClient Client) CreateAction(FakeTradeClient client)
	{
		var plugin = new Dota2Plugin();
		var action = new Dota2TradesAction(plugin, NullLogger<Dota2TradesAction>.Instance, _ => client);
		return (action, client);
	}

	private static TradeOffersResponse Response(params TradeOffer[] offers) => new()
	{
		Success = true,
		ReceivedOffers = offers.Where(offer => !offer.IsOurOffer).ToArray(),
		SentOffers = offers.Where(offer => offer.IsOurOffer).ToArray()
	};

	[Fact]
	public void Metadata_DeclaresReadOnlyLoginRequired()
	{
		var action = new Dota2TradesAction(new Dota2Plugin());

		Assert.Equal("dota2_trades", action.Name);
		Assert.Equal("dota2_trades", action.Metadata.Name);
		Assert.Equal(ActionSafety.ReadOnly, action.Metadata.Safety);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsNullPayload()
	{
		var (action, _) = CreateAction(new FakeTradeClient());

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			action.ExecuteAsync(CreateSession(CreateWebHandler()), null!, CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_RequiresTheWebHandler()
	{
		var (action, _) = CreateAction(new FakeTradeClient());

		ActionResult result = await action.ExecuteAsync(CreateSession(null), Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_FiltersToDotaTouchingOffers_ByDefault()
	{
		var client = new FakeTradeClient
		{
			OffersResponse = Response(
				Pages.Offer(1, partner: 111, isOurOffer: false, give: [Pages.Asset(10)]) with { TimeExpires = DateTimeOffset.UnixEpoch.AddDays(3) },
				Pages.Offer(2, partner: 222, isOurOffer: false, give: [Pages.Asset(20, appId: 730)]),
				Pages.Offer(3, partner: 333, isOurOffer: true, receive: [Pages.Asset(30)]))
		};
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(),
			CancellationToken.None);

		Assert.True(result.Success);
		IReadOnlyDictionary<string, object?> output = result.Output!;
		Assert.Equal("readonly", output["mode"]);
		Assert.Equal(570u, output["app_id"]);
		Assert.True((bool)output["dota_only"]!);
		Assert.True((bool)output["active_only"]!);

		var received = Assert.IsType<Dictionary<string, object?>>(output["received"]);
		Assert.Equal(2, received["total"]);
		Assert.Equal(1, received["dota"]);
		var receivedOffers = Assert.IsType<List<Dictionary<string, object?>>>(received["offers"]);
		var offer = receivedOffers.Single();
		Assert.Equal("1", offer["trade_offer_id"]);
		Assert.Equal("111", offer["partner_steam_id"]);
		Assert.Equal("Active", offer["state"]);
		Assert.False((bool)offer["is_our_offer"]!);
		Assert.True((bool)offer["dota_involved"]!);
		Assert.Null(offer["message"]);
		Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(3).ToString("O"), offer["time_expires"]);
		Assert.NotNull(offer["time_created"]);

		var sent = Assert.IsType<Dictionary<string, object?>>(output["sent"]);
		Assert.Equal(1, sent["total"]);
		Assert.Equal(1, sent["dota"]);

		// active_only defaults to true on the transport call.
		Assert.True(client.OffersCalls.Single());
	}

	[Fact]
	public async Task ExecuteAsync_IncludeAll_KeepsForeignOffers_FlaggedAsNonDota()
	{
		var client = new FakeTradeClient
		{
			OffersResponse = Response(
				Pages.Offer(1, partner: 111, isOurOffer: false, give: [Pages.Asset(10)]),
				Pages.Offer(2, partner: 222, isOurOffer: false, give: [Pages.Asset(20, appId: 730)]))
		};
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(("include_all", true), ("active_only", false)),
			CancellationToken.None);

		Assert.True(result.Success);
		var received = Assert.IsType<Dictionary<string, object?>>(output(result)["received"]);
		Assert.Equal(2, received["total"]);
		Assert.Equal(1, received["dota"]);
		var offers = Assert.IsType<List<Dictionary<string, object?>>>(received["offers"]);
		Assert.Equal(2, offers.Count);
		var foreign = offers.Single(entry => entry["trade_offer_id"]!.ToString() == "2");
		Assert.False((bool)foreign["dota_involved"]!);
		Assert.Null(foreign["time_expires"]);

		// The payload flag reaches the transport call.
		Assert.False(client.OffersCalls.Single());
	}

	[Fact]
	public async Task ExecuteAsync_PropagatesTransportError()
	{
		var client = new FakeTradeClient
		{
			OffersResponse = new TradeOffersResponse { Success = false, Error = "offers down" }
		};
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("offers down", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_DefaultsErrorText_WhenTransportFailsWithoutOne()
	{
		var client = new FakeTradeClient
		{
			OffersResponse = new TradeOffersResponse { Success = false, Error = null }
		};
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Failed to load trade offers", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WrapsTransportExceptions()
	{
		var (action, _) = CreateAction(new FakeTradeClient() { ThrowOnInventory = true });

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("boom", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_LetsCancellationPropagate()
	{
		var (action, _) = CreateAction(new FakeTradeClient() { ThrowOceOnInventory = true });

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			action.ExecuteAsync(CreateSession(CreateWebHandlerWithCookies()), Payload(), CancellationToken.None));
	}

	private static IReadOnlyDictionary<string, object?> output(ActionResult result) => result.Output!;
}
