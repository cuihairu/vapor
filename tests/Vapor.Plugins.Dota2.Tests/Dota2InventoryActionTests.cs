using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Protocol;

using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;

using Xunit;

namespace Vapor.Plugins.Dota2.Tests;

public class Dota2InventoryActionTests : Dota2TestBase
{
	private static (Dota2InventoryAction Action, FakeTradeClient Client) CreateAction(FakeTradeClient client)
	{
		var plugin = new Dota2Plugin();
		var action = new Dota2InventoryAction(plugin, NullLogger<Dota2InventoryAction>.Instance, _ => client);
		return (action, client);
	}

	[Fact]
	public void Metadata_DeclaresReadOnlyLoginRequired()
	{
		var action = new Dota2InventoryAction(new Dota2Plugin());

		Assert.Equal("dota2_inventory", action.Name);
		Assert.Equal("dota2_inventory", action.Metadata.Name);
		Assert.Equal(ActionSafety.ReadOnly, action.Metadata.Safety);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsNullPayload()
	{
		var (action, _) = CreateAction(new FakeTradeClient());

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			action.ExecuteAsync(CreateSession(CreateWebHandler()), null!, CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_RequiresSteamId_WhenNoWebSession()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Of()));

		ActionResult result = await action.ExecuteAsync(CreateSession(null), Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no web session", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresSteamId_WhenCookiesCarryNoSteamId()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Of()));

		ActionResult result = await action.ExecuteAsync(CreateSession(CreateWebHandler()), Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("cookies do not carry", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsUnparsableSteamId()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Of()));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", "not-a-number")),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("invalid steam_id", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresWebHandler_EvenWithExplicitSteamId()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Of()));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(null),
			Payload(("steam_id", FakeTradeClient.TestSteamId.ToString())),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesSteamId_FromSessionCookies_AndPinsApp570Context2()
	{
		var client = new FakeTradeClient(Pages.Of(
			Pages.Dota(1),
			Pages.Dota(2, tradable: false, tradabilityDate: DateTimeOffset.UtcNow.AddDays(5)),
			Pages.Dota(3, tradable: false)));
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(),
			CancellationToken.None);

		Assert.True(result.Success);
		IReadOnlyDictionary<string, object?> output = result.Output!;
		Assert.Equal("readonly", output["mode"]);
		Assert.Equal(FakeTradeClient.TestSteamId.ToString(), output["steam_id"]);
		Assert.Equal(570u, output["app_id"]);
		Assert.Equal(2ul, output["context_id"]);
		Assert.Equal(3, output["total_count"]);
		Assert.Equal(1, output["ready_count"]);
		Assert.Equal(1, output["cooldown_count"]);
		Assert.Equal(1, output["locked_count"]);
		Assert.NotNull(output["checked_at"]);

		var cooldownItems = Assert.IsType<List<Dictionary<string, object?>>>(output["cooldown_items"]);
		var cooldownEntry = cooldownItems.Single();
		Assert.Equal("2", cooldownEntry["asset_id"]);
		Assert.Contains("Arcana", (string?)cooldownEntry["name"]);
		Assert.NotNull(cooldownEntry["cooldown_until"]);

		var lockedItems = Assert.IsType<List<Dictionary<string, object?>>>(output["locked_items"]);
		var lockedEntry = lockedItems.Single();
		Assert.Equal("3", lockedEntry["asset_id"]);
		Assert.False(lockedEntry.ContainsKey("cooldown_until"));

		// The Dota 2 scope is the plugin's contract: app 570, context 2, one page.
		Assert.Equal(FakeTradeClient.TestSteamId, client.RequestedSteamIds.Single());
		Assert.Equal(570u, client.RequestedAppIds.Single());
		Assert.Equal(2ul, client.RequestedContextIds.Single());
	}

	[Fact]
	public async Task ExecuteAsync_Paginates_ThroughAllPages()
	{
		var client = new FakeTradeClient(
			new InventoryResponse
			{
				Success = true,
				Items = [Pages.Dota(1)],
				HasMore = true,
				LastAssetId = 999
			},
			new InventoryResponse
			{
				Success = true,
				Items = [Pages.Dota(2)],
				HasMore = false
			});
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", FakeTradeClient.TestSteamId.ToString())),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total_count"]);
		Assert.Equal(2, client.RequestedSteamIds.Count);
		Assert.Equal([null, 999UL], client.RequestedStartAssetIds.ToArray());
	}

	[Fact]
	public async Task ExecuteAsync_PropagatesPaginatorError()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Failed("Steam said no")));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", FakeTradeClient.TestSteamId.ToString())),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam said no", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WrapsTransportFailures_AsFailedResults()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Of()) { ThrowOnInventory = true });

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", FakeTradeClient.TestSteamId.ToString())),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("boom", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_LetsCancellationPropagate()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Of()) { ThrowOceOnInventory = true });

		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			action.ExecuteAsync(
				CreateSession(CreateWebHandler()),
				Payload(("steam_id", FakeTradeClient.TestSteamId.ToString())),
				CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_FallsBackThroughTheNameChain_InBothBuckets()
	{
		var future = DateTimeOffset.UtcNow.AddDays(4);
		var client = new FakeTradeClient(Pages.Of(
			Pages.Dota(1, tradable: false, name: "hash") with { MarketHashName = null, MarketName = "market", Name = "plain" },
			Pages.Dota(2, tradable: false, name: "hash") with { MarketHashName = null, MarketName = null, Name = "plain" },
			Pages.Dota(3, tradable: false, tradabilityDate: future, name: "hash") with { MarketHashName = null, MarketName = "market", Name = "plain" },
			Pages.Dota(4, tradable: false, tradabilityDate: future, name: "hash") with { MarketHashName = null, MarketName = null, Name = "plain" }));
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", FakeTradeClient.TestSteamId.ToString())),
			CancellationToken.None);

		Assert.True(result.Success);
		var cooldownItems = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["cooldown_items"]);
		Assert.Equal(["market", "plain"], cooldownItems.Select(entry => entry["name"]).ToArray());
		var lockedItems = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["locked_items"]);
		Assert.Equal(["market", "plain"], lockedItems.Select(entry => entry["name"]).ToArray());
	}

	[Fact]
	public async Task ExecuteAsync_CapsTheUnavailableLists_AtFiftyEntries()
	{
		// 60 locked items: exactly 50 listed, the total still counted honestly.
		var items = Enumerable.Range(1, 60)
			.Select(index => Pages.Dota((ulong)index, tradable: false))
			.ToArray();
		var (action, _) = CreateAction(new FakeTradeClient(Pages.Of(items)));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", FakeTradeClient.TestSteamId.ToString())),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(60, result.Output!["locked_count"]);
		var lockedItems = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["locked_items"]);
		Assert.Equal(50, lockedItems.Count);
	}
}
