using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Vapor.Plugins.Csgo;
using Vapor.Protocol;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

using Xunit;

namespace Vapor.Plugins.Csgo.Tests;

public class CaseInventoryActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000001UL;

	private readonly List<BotSession> _sessions = [];

	public void Dispose()
	{
		foreach (BotSession session in _sessions)
		{
			session.Dispose();
		}
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	private static SteamWebHandler CreateWebHandlerWithCookies()
	{
		var webHandler = CreateWebHandler();
		webHandler.SetSessionCookies("session-1", $"{OwnSteamId}%7C%7Ctoken");
		return webHandler;
	}

	private BotSession CreateSession(SteamWebHandler? webHandler)
	{
		var session = new BotSession(
			"test_account",
			new AccountCredentials("test_account", "password"),
			null!,
			NullLogger<BotSession>.Instance,
			null,
			webHandler,
			null);
		_sessions.Add(session);
		return session;
	}

	private static (CaseInventoryAction Action, FakeTradeClient Client) CreateAction(FakeTradeClient client)
	{
		var plugin = new CsgoPlugin();
		var action = new CaseInventoryAction(plugin, NullLogger<CaseInventoryAction>.Instance, _ => client);
		return (action, client);
	}

	private static Dictionary<string, object?> Payload(params (string Key, object? Value)[] values) =>
		values.ToDictionary(entry => entry.Key, entry => entry.Value);

	[Fact]
	public void Metadata_DeclaresReadOnlyLoginRequired()
	{
		CaseInventoryAction action = new(new CsgoPlugin());

		Assert.Equal("case_inventory", action.Name);
		Assert.Equal("case_inventory", action.Metadata.Name);
		Assert.Equal(ActionSafety.ReadOnly, action.Metadata.Safety);
		Assert.True(action.Metadata.RequiresLogin);
		Assert.Equal(60, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public void TradeClientFactory_FallsBackToTheRealClient_AndHonorsInjection()
	{
		var plugin = new CsgoPlugin();
		Assert.IsType<SteamTradeClient>(plugin.TradeClientFactory(CreateWebHandler()));

		var fake = new FakeTradeClient();
		var injected = new CsgoPlugin(tradeClientFactory: _ => fake);
		Assert.Same(fake, injected.TradeClientFactory(CreateWebHandler()));
	}

	[Fact]
	public void Constructor_RejectsNullPlugin()
	{
		Assert.Throws<ArgumentNullException>(() =>
			new CaseInventoryAction(null!, NullLogger<CaseInventoryAction>.Instance));
	}

	[Fact]
	public async Task ExecuteAsync_RejectsNullPayload()
	{
		CaseInventoryAction action = new(new CsgoPlugin());

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			action.ExecuteAsync(CreateSession(null), null!, CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_RequiresSteamId_WhenNoWebSession()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Page()));

		ActionResult result = await action.ExecuteAsync(CreateSession(null), Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no web session", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RequiresSteamId_WhenCookiesCarryNoSteamId()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Page()));

		ActionResult result = await action.ExecuteAsync(CreateSession(CreateWebHandler()), Payload(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("cookies do not carry", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsUnparsableSteamId()
	{
		var (action, _) = CreateAction(new FakeTradeClient(Page()));

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
		var (action, _) = CreateAction(new FakeTradeClient(Page()));

		ActionResult result = await action.ExecuteAsync(
			CreateSession(null),
			Payload(("steam_id", OwnSteamId.ToString())),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_ResolvesSteamId_FromSessionCookies()
	{
		var client = new FakeTradeClient(Page());
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(OwnSteamId.ToString(), result.Output!["steam_id"]);
		Assert.Equal(OwnSteamId, client.RequestedSteamIds.Single());
	}

	[Fact]
	public async Task ExecuteAsync_ClassifiesInventory_AndCarriesLedgerTotals()
	{
		string catalogPath = WriteCatalog("vapor.test.custom", "Custom Case");
		var plugin = new CsgoPlugin();
		await plugin.InitializeAsync(TestContext(("cases.path", catalogPath)), CancellationToken.None);
		plugin.Ledger.Record(new ConsumeEntry("vapor.cases.starter", cases: 2, keys: 2));

		var client = new FakeTradeClient(Page(
			new InventoryItem { AssetId = 1, Name = "Custom Case", MarketHashName = "Custom Case", Amount = 2 },
			new InventoryItem { AssetId = 2, Name = "Custom Case Key", MarketHashName = "Custom Case Key", Amount = 3 },
			new InventoryItem { AssetId = 3, Name = "Gloves", MarketHashName = "Gloves | Black", Amount = 4 }));
		var action = new CaseInventoryAction(plugin, NullLogger<CaseInventoryAction>.Instance, _ => client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandlerWithCookies()),
			Payload(),
			CancellationToken.None);

		Assert.True(result.Success);
		IReadOnlyDictionary<string, object?> output = result.Output!;
		Assert.Equal("readonly", output["mode"]);
		Assert.Equal(OwnSteamId.ToString(), output["steam_id"]);
		Assert.Equal(730u, output["app_id"]);
		Assert.Equal(2ul, output["context_id"]);
		Assert.Equal(3, output["total_count"]);
		Assert.Equal(3, output["key_count"]);
		Assert.Equal(4, output["other_count"]);

		var cases = Assert.IsType<List<Dictionary<string, object?>>>(output["cases"]);
		Assert.Single(cases);
		Assert.Equal("Custom Case", cases[0]["name"]);
		Assert.Equal("vapor.test.custom", cases[0]["case_id"]);
		Assert.Equal(2, cases[0]["count"]);

		var ledger = Assert.IsType<Dictionary<string, object?>>(output["ledger"]);
		Assert.Equal(1, ledger["entries"]);
		Assert.Equal(2, ledger["cases"]);
		Assert.Equal(2, ledger["keys"]);

		// The CS2 backpack page, read exactly once with the fixed app/context pair.
		Assert.Single(client.RequestedSteamIds);
		Assert.Equal(OwnSteamId, client.RequestedSteamIds[0]);
		Assert.Equal(730u, client.RequestedAppIds[0]);
		Assert.Equal(2ul, client.RequestedContextIds[0]);
		Assert.Null(client.RequestedStartAssetIds.Single());
	}

	[Fact]
	public async Task ExecuteAsync_Paginates_ThroughAllPages()
	{
		var client = new FakeTradeClient(
			new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 1, MarketHashName = "Case A", Amount = 1 }],
				HasMore = true,
				LastAssetId = 999
			},
			new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 2, MarketHashName = "Case B", Amount = 2 }],
				HasMore = false
			});
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", OwnSteamId.ToString())),
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total_count"]);

		// The second page was requested with the first page's continuation token.
		Assert.Equal(2, client.RequestedSteamIds.Count);
		Assert.Equal([null, 999UL], client.RequestedStartAssetIds.ToArray());
	}

	[Fact]
	public async Task ExecuteAsync_PropagatesPaginatorError()
	{
		var client = new FakeTradeClient(new InventoryResponse { Success = false, Error = "Steam said no" });
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", OwnSteamId.ToString())),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam said no", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WrapsTransportFailures_AsFailedResults()
	{
		var client = new FakeTradeClient() { ThrownOnFirstRequest = true };
		var (action, _) = CreateAction(client);

		ActionResult result = await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("steam_id", OwnSteamId.ToString())),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("boom", result.Error);
	}

	private string WriteCatalog(string caseId, string caseName)
	{
		string path = Path.Combine(Path.GetTempPath(), $"vapor-case-inventory-{Guid.NewGuid():N}.json");
		File.WriteAllText(path, $$"""
			{ "cases": [ { "id": "{{caseId}}", "name": "{{caseName}}", "items": [
			  { "id": "x1", "name": "Pistol | X", "rarity": "milspec" },
			  { "id": "x2", "name": "SMG | X", "rarity": "restricted" },
			  { "id": "x3", "name": "Rifle | X", "rarity": "classified" },
			  { "id": "x4", "name": "Rifle | Y", "rarity": "covert" },
			  { "id": "x5", "name": "Knife | X", "rarity": "rare_special" } ] } ] }
			""");
		return path;
	}

	private static IPluginContext TestContext(params (string Key, string Value)[] configuration) => new TestPluginContext(
		configuration.ToDictionary(entry => entry.Key, entry => entry.Value));

	private sealed class TestPluginContext(IReadOnlyDictionary<string, string> configuration) : IPluginContext
	{
		public PluginInfo Info { get; } = new(
			"vapor.csgo", "Vapor Case Opening (test)", new Version(1, 0, 0), PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

		public IPluginHostServices Host { get; } =
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub());
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}

	/// <summary>Canned responses for the shared paginator (no Moq in this project).</summary>
	// Shared with CsgoOpenRealTests via a using alias.
	internal sealed class FakeTradeClient : ISteamTradeClient
	{
		private readonly Queue<InventoryResponse> _pages;

		public FakeTradeClient(params InventoryResponse[] pages)
		{
			_pages = new Queue<InventoryResponse>(pages);
			PageCount = pages.Length;
		}

		public bool ThrownOnFirstRequest { get; init; }

		public int PageCount { get; }

		public List<ulong?> RequestedStartAssetIds { get; } = [];

		public List<ulong> RequestedSteamIds { get; } = [];

		public List<uint> RequestedAppIds { get; } = [];

		public List<ulong> RequestedContextIds { get; } = [];

		public ulong? GetOwnSteamId() => throw new NotSupportedException("not used by case_inventory");

		public Task<InventoryResponse> GetInventoryAsync(
			ulong steamId,
			uint appId = 730,
			ulong contextId = 2,
			ulong? startAssetId = null,
			CancellationToken cancellationToken = default)
		{
			RequestedSteamIds.Add(steamId);
			RequestedAppIds.Add(appId);
			RequestedContextIds.Add(contextId);
			RequestedStartAssetIds.Add(startAssetId);

			if (ThrownOnFirstRequest)
			{
				throw new InvalidOperationException("boom");
			}

			return Task.FromResult(_pages.Dequeue());
		}

		public Task<TradeOffersResponse> GetTradeOffersAsync(bool activeOnly = true, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("not used by case_inventory");

		public Task<TradeOfferResult> GetTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("not used by case_inventory");

		public Task<TradeOfferResult> SendTradeOfferAsync(
			ulong partnerSteamId,
			IReadOnlyList<TradeAsset> itemsToGive,
			IReadOnlyList<TradeAsset> itemsToReceive,
			string? token = null,
			string? message = null,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("not used by case_inventory");

		public Task<TradeOfferResult> AcceptTradeOfferAsync(ulong tradeOfferId, ulong partnerSteamId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("not used by case_inventory");

		public Task<TradeOfferResult> DeclineTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("not used by case_inventory");

		public Task<TradeOfferResult> CancelTradeOfferAsync(ulong tradeOfferId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("not used by case_inventory");
	}

	private static InventoryResponse Page(params InventoryItem[] items) => new()
	{
		Success = true,
		Items = [.. items],
		HasMore = false
	};
}
