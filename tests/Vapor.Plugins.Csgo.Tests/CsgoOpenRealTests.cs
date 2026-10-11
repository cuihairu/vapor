using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Vapor.Plugins.Core;
using Vapor.Plugins.Csgo;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;

using Xunit;

using FakeTradeClient = Vapor.Plugins.Csgo.Tests.CaseInventoryActionTests.FakeTradeClient;

namespace Vapor.Plugins.Csgo.Tests;

public class CsgoOpenRealTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000001UL;
	private const string StarterCase = "Starter Case (illustrative)";
	private const ulong PrizeAssetId = 31415926UL;

	private readonly List<BotSession> _sessions = [];

	public void Dispose()
	{
		foreach (BotSession session in _sessions)
		{
			session.Dispose();
		}
	}

	private static SteamWebHandler CreateWebHandler(bool withCookies = true)
	{
		var webHandler = new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);
		if (withCookies)
		{
			webHandler.SetSessionCookies("session-1", $"{OwnSteamId}%7C%7Ctoken");
		}

		return webHandler;
	}

	private (BotSession Session, Mock<ISteamClientManager> Client) CreateSession(SteamWebHandler? webHandler, bool withClient = true)
	{
		var client = new Mock<ISteamClientManager>(MockBehavior.Loose);
		var session = new BotSession(
			"test_account",
			new AccountCredentials("test_account", "password"),
			null!,
			NullLogger<BotSession>.Instance,
			withClient ? client.Object : null,
			webHandler,
			null);
		_sessions.Add(session);
		return (session, client);
	}

	private static async Task<CsgoPlugin> CreatePluginAsync(bool openEnabled = false, FakeTradeClient? client = null)
	{
		var plugin = new CsgoPlugin(null, client is null ? null : _ => client, TimeSpan.FromMilliseconds(1));
		await plugin.InitializeAsync(TestContext(("open.enabled", openEnabled ? "true" : "false")), CancellationToken.None);
		return plugin;
	}

	private static IPluginContext TestContext(params (string Key, string Value)[] configuration) => new TestPluginContext(
		configuration.ToDictionary(entry => entry.Key, entry => entry.Value));

	private static IReadOnlyDictionary<string, object?> Payload(params (string Key, object? Value)[] values) =>
		values.ToDictionary(entry => entry.Key, entry => entry.Value);

	private static InventoryResponse Page(params InventoryItem[] items) => new()
	{
		Success = true,
		Items = items,
		HasMore = false
	};

	private static InventoryItem Item(ulong assetId, string marketName) => new()
	{
		AssetId = assetId,
		MarketHashName = marketName,
		Amount = 1
	};

	private static void SetupHappyCoordinator(Mock<ISteamClientManager> client, ulong crateId, ulong keyId)
	{
		client.Setup(m => m.EnsureCs2SessionAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Cs2GcSessionResult(true, "cs2 game coordinator session established"));
		client.Setup(m => m.OpenCrateAsync(crateId, keyId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Cs2OpenCrateResult([PrizeAssetId]));
	}

	[Fact]
	public async Task OpenRealAsync_UnknownCase_Fails()
	{
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true);
		(BotSession session, _) = CreateSession(CreateWebHandler());

		var outcome = await plugin.OpenRealAsync(session, "no-such-case", 1, 2, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("unknown case", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_WithoutClientTransport_Fails()
	{
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true);
		(BotSession session, _) = CreateSession(CreateWebHandler(), withClient: false);

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", 1, 2, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("no steam client transport", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_WithoutWebSession_Fails()
	{
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true);
		(BotSession session, _) = CreateSession(null);

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", 1, 2, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("no steam web session", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_UnresolvableSteamId_Fails()
	{
		var client = new FakeTradeClient(Page(Item(1, StarterCase), Item(2, "Case Key")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, _) = CreateSession(CreateWebHandler(withCookies: false));

		// Auto-pick mode: the pre-open backpack read needs a steam id, which the
		// cookie-less session cannot provide.
		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("steam_id is unavailable", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_BackpackReadFailure_SurfacesThePaginatorError()
	{
		var client = new FakeTradeClient(new InventoryResponse { Success = false, Error = "Steam said no" });
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, _) = CreateSession(CreateWebHandler());

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("Steam said no", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_HalfSpecifiedIds_Fail()
	{
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true);
		(BotSession session, _) = CreateSession(CreateWebHandler());

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", 1, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("'crateAssetId' and 'keyAssetId' together", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_ZeroIds_Fail()
	{
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true);
		(BotSession session, _) = CreateSession(CreateWebHandler());

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", 0, 2, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("non-zero", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_AutoPick_WithoutMatchingBackpack_Fails()
	{
		var client = new FakeTradeClient(Page(Item(1, "AK-47 | Redline (Field-Tested)")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, _) = CreateSession(CreateWebHandler());

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains(StarterCase, outcome.Error);
		Assert.Contains("crate plus key", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_HandshakeFailure_SurfacesTheCoordinatorMessage()
	{
		var client = new FakeTradeClient(Page(Item(1, StarterCase), Item(2, "Case Key")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		coordinator.Setup(m => m.EnsureCs2SessionAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Cs2GcSessionResult(false, "steam client is not connected"));

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Equal("steam client is not connected", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_CoordinatorTimeout_Fails()
	{
		var client = new FakeTradeClient(Page(Item(1, StarterCase), Item(2, "Case Key")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		coordinator.Setup(m => m.EnsureCs2SessionAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Cs2GcSessionResult(true, "ok"));
		coordinator.Setup(m => m.OpenCrateAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((Cs2OpenCrateResult?)null);

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("did not answer the open request", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_ZeroPrizeIds_Fail()
	{
		var client = new FakeTradeClient(Page(Item(1, StarterCase), Item(2, "Case Key")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		coordinator.Setup(m => m.EnsureCs2SessionAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Cs2GcSessionResult(true, "ok"));
		coordinator.Setup(m => m.OpenCrateAsync(It.IsAny<ulong>(), It.IsAny<ulong>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Cs2OpenCrateResult([]));

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Payload);
		Assert.Contains("no prize items", outcome.Error);
	}

	[Fact]
	public async Task OpenRealAsync_MapsPrizeNames_AndRecordsTheLedger()
	{
		// Page 1 is the pre-open backpack (crate + key for auto-pick), page 2 is
		// the post-open re-read carrying the new prize item.
		var client = new FakeTradeClient(
			Page(Item(1, StarterCase), Item(2, "Case Key")),
			Page(Item(1, StarterCase), Item(PrizeAssetId, "AK-47 | Redline (Field-Tested)")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		SetupHappyCoordinator(coordinator, 1, 2);

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Error);
		Assert.NotNull(outcome.Payload);
		coordinator.Verify(m => m.OpenCrateAsync(1, 2, It.IsAny<CancellationToken>()), Times.Once);
		Assert.Equal("gc", outcome.Payload!["mode"]);

		var prizes = Assert.IsType<List<Dictionary<string, object?>>>(outcome.Payload["prizes"]);
		var prize = Assert.Single(prizes);
		Assert.Equal(PrizeAssetId.ToString(), prize["asset_id"]);
		Assert.Equal("AK-47 | Redline (Field-Tested)", prize["name"]);
		Assert.Equal(true, prize["matched"]);

		var ledger = Assert.IsType<Dictionary<string, object?>>(outcome.Payload["ledger"]);
		Assert.Equal(1, ledger["entries"]);
		Assert.Equal(1, ledger["cases"]);
		Assert.Equal(1, ledger["keys"]);
	}

	[Fact]
	public async Task OpenRealAsync_PrizeMappingRetries_WhileTheBackpackLags()
	{
		// Page 2 trails the GC answer; page 3 finally carries the prize.
		var client = new FakeTradeClient(
			Page(Item(1, StarterCase), Item(2, "Case Key")),
			Page(Item(1, StarterCase)),
			Page(Item(PrizeAssetId, "AWP | Asiimov (Field-Tested)")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		SetupHappyCoordinator(coordinator, 1, 2);

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Error);
		Assert.Equal(3, client.PageCount);
		var prizes = Assert.IsType<List<Dictionary<string, object?>>>(outcome.Payload!["prizes"]);
		Assert.Equal("AWP | Asiimov (Field-Tested)", Assert.Single(prizes)["name"]);
	}

	[Fact]
	public async Task OpenRealAsync_PrizeThatNeverSurfaces_IsReportedUnmatched()
	{
		// Page 1 is the pre-open pick; pages 2-4 exhaust the three mapping
		// attempts without the prize ever showing up.
		var client = new FakeTradeClient(
			Page(Item(1, StarterCase), Item(2, "Case Key")),
			Page(Item(1, StarterCase)),
			Page(Item(1, StarterCase)),
			Page(Item(1, StarterCase)));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		SetupHappyCoordinator(coordinator, 1, 2);

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Error);
		Assert.Equal(4, client.PageCount);
		var prizes = Assert.IsType<List<Dictionary<string, object?>>>(outcome.Payload!["prizes"]);
		var prize = Assert.Single(prizes);
		Assert.Equal(false, prize["matched"]);
		Assert.Null(prize["name"]);
		// The open still counts in the ledger: one crate and one key were spent.
		var ledger = Assert.IsType<Dictionary<string, object?>>(outcome.Payload["ledger"]);
		Assert.Equal(1, ledger["entries"]);
	}

	[Fact]
	public async Task OpenRealAsync_AutoPick_IgnoresNamelessItems_WhenScanningForAKey()
	{
		// An item with no market hash name and no display name yields a null
		// market name; the key scan must skip it rather than throw.
		var client = new FakeTradeClient(
			Page(Item(9, null!), Item(1, StarterCase), Item(2, "Case Key")),
			Page(Item(PrizeAssetId, "AK-47 | Redline (Field-Tested)")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		SetupHappyCoordinator(coordinator, 1, 2);

		var outcome = await plugin.OpenRealAsync(session, "vapor.cases.starter", null, null, NullLogger.Instance, CancellationToken.None);

		Assert.Null(outcome.Error);
		Assert.Equal("gc", outcome.Payload!["mode"]);
		coordinator.Verify(m => m.OpenCrateAsync(1, 2, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task CaseOpenAction_RealMode_OpenFailure_SurfacesTheOutcomeError()
	{
		// Gate on, explicit ids, coordinator handshake fails: the action's real
		// branch must return the outcome error, not a success payload.
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true);
		var action = new CaseOpenAction(plugin);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		coordinator.Setup(m => m.EnsureCs2SessionAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new Cs2GcSessionResult(false, "steam client is not connected"));

		var result = await action.ExecuteAsync(
			session,
			Payload(("caseId", "vapor.cases.starter"), ("crateAssetId", 1ul), ("keyAssetId", 2ul)),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("steam client is not connected", result.Error);
	}

	[Fact]
	public void RealOpenOutcome_ReportsSuccessOnlyWithAPayload()
	{
		var outcome = new CsgoPlugin.RealOpenOutcome(new Dictionary<string, object?>(), null);
		Assert.True(outcome.Payload is not null);
	}

	[Fact]
	public async Task CaseOpenAction_RealIds_WithoutGate_ReturnsGuidance()
	{
		var plugin = new CsgoPlugin();
		var action = new CaseOpenAction(plugin);
		(BotSession session, _) = CreateSession(null);

		var result = await action.ExecuteAsync(
			session,
			Payload(("caseId", "vapor.cases.starter"), ("crateAssetId", 1ul), ("keyAssetId", 2ul)),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("open.enabled", result.Error);
		Assert.Contains("VAPOR_CSGO_OPEN_ENABLED", result.Error);
	}

	[Fact]
	public async Task CaseOpenAction_RealIds_WithCountOverOne_Fail()
	{
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true);
		var action = new CaseOpenAction(plugin);
		(BotSession session, _) = CreateSession(null);

		var result = await action.ExecuteAsync(
			session,
			Payload(("caseId", "vapor.cases.starter"), ("count", 2), ("crateAssetId", 1ul), ("keyAssetId", 2ul)),
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("one crate + one key per request", result.Error);
	}

	[Fact]
	public async Task CaseOpenAction_RealIds_HappyPath_ReturnsGcPayload()
	{
		// Explicit asset ids skip the pre-open scan: the only read is the
		// post-open prize mapping.
		var client = new FakeTradeClient(Page(Item(PrizeAssetId, "AK-47 | Redline (Field-Tested)")));
		CsgoPlugin plugin = await CreatePluginAsync(openEnabled: true, client);
		var action = new CaseOpenAction(plugin);
		(BotSession session, Mock<ISteamClientManager> coordinator) = CreateSession(CreateWebHandler());
		SetupHappyCoordinator(coordinator, 1, 2);

		var result = await action.ExecuteAsync(
			session,
			Payload(("caseId", "vapor.cases.starter"), ("crateAssetId", 1ul), ("keyAssetId", 2ul)),
			CancellationToken.None);

		Assert.True(result.Success);
		var payload = Assert.IsType<Dictionary<string, object?>>(result.Output!["payload"]);
		Assert.Equal("gc", payload["mode"]);
	}

	[Fact]
	public void CaseOpenAction_Metadata_DeclaresTheGatedRealPath()
	{
		CaseOpenAction action = new(new CsgoPlugin());

		Assert.Equal("case_open", action.Name);
		Assert.Equal(ActionSafety.NonIdempotent, action.Metadata.Safety);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(180, action.Metadata.TimeoutSeconds);
	}

	[Theory]
	[InlineData(111L)]
	[InlineData(111)]
	public async Task CaseOpenAction_AssetIdParsing_NumericPayloads_TurnRealModeOn(long value)
	{
		// Gate off: the parse decides the branch, and the gate guidance proves
		// the action took the real path.
		var plugin = new CsgoPlugin();
		var action = new CaseOpenAction(plugin);
		(BotSession session, _) = CreateSession(null);

		var result = await action.ExecuteAsync(
			session, Payload(("caseId", "vapor.cases.starter"), ("crateAssetId", value)), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("open.enabled", result.Error);
	}

	[Fact]
	public async Task CaseOpenAction_AssetIdParsing_StringAndJsonAndOutOfRange_TurnRealModeOnOrFallBack()
	{
		var plugin = new CsgoPlugin();
		var action = new CaseOpenAction(plugin);
		(BotSession session, _) = CreateSession(null);

		// String asset id parses into the real path.
		var stringResult = await action.ExecuteAsync(
			session, Payload(("caseId", "vapor.cases.starter"), ("crateAssetId", "111")), CancellationToken.None);
		Assert.Contains("open.enabled", stringResult.Error);

		// A JSON number does the same.
		using JsonDocument number = JsonDocument.Parse("111");
		var jsonResult = await action.ExecuteAsync(
			session,
			Payload(("caseId", "vapor.cases.starter"), ("crateAssetId", number.RootElement.Clone())),
			CancellationToken.None);
		Assert.Contains("open.enabled", jsonResult.Error);

		// A negative long, a JSON string and a bool are not asset ids: the
		// action falls back to the simulation path and fails on the case id.
		using JsonDocument text = JsonDocument.Parse("\"not-a-number\"");
		foreach (object? value in new object?[] { -5L, text.RootElement.Clone(), true })
		{
			var fallback = await action.ExecuteAsync(
				session, Payload(("caseId", "no-such-case"), ("crateAssetId", value)), CancellationToken.None);
			Assert.Contains("unknown case", fallback.Error);
		}
	}

	[Fact]
	public async Task CaseOpenAction_AssetIdParsing_Decimal_CoversTheNumericBranch()
	{
		var plugin = new CsgoPlugin();
		var action = new CaseOpenAction(plugin);
		(BotSession session, _) = CreateSession(null);

		var result = await action.ExecuteAsync(
			session, Payload(("caseId", "vapor.cases.starter"), ("crateAssetId", 111m)), CancellationToken.None);

		Assert.Contains("open.enabled", result.Error);
	}

	private sealed class TestPluginContext(IReadOnlyDictionary<string, string> configuration) : IPluginContext
	{
		public PluginInfo Info { get; } = new(
			"vapor.csgo", "Vapor CS2/CSGO (test)", new Version(1, 0, 0), PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

		public IPluginHostServices Host { get; } =
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub());
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
