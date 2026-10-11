using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Vapor.Plugins.Csgo;
using Vapor.Plugins.Core;

using Xunit;

namespace Vapor.Plugins.Csgo.Tests;

public class CsgoLedgerRoutesTests
{
	private const string StarterCase = "vapor.cases.starter";

	[Fact]
	public async Task InventoryRoute_LedgerFile_SurvivesRestart()
	{
		string ledgerPath = Path.Combine(Path.GetTempPath(), $"vapor-case-ledger-init-{Guid.NewGuid():N}.jsonl");
		var first = new CsgoPlugin();
		await first.InitializeAsync(TestContext(("ledger.path", ledgerPath)), CancellationToken.None);
		first.Ledger.Record(new ConsumeEntry("c1", cases: 1, keys: 1));

		var second = new CsgoPlugin();
		await second.InitializeAsync(TestContext(("ledger.path", ledgerPath)), CancellationToken.None);

		Assert.Equal(1, second.Ledger.Summarize().TotalEntries);
		PluginWebRoute route = second.GetRoutes().Single(candidate => candidate.Path == "/inventory");
		PluginWebResponse response = await route.Handler(Req("/inventory"), CancellationToken.None);
		Assert.Contains("\"entries\":1", response.Body);

		try
		{
			File.Delete(ledgerPath);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void Plugin_ExposesTheBookkeepingSurface()
	{
		var plugin = new CsgoPlugin();

		Assert.Equal(
			["case_open", "case_inventory", "case_consume"],
			[.. plugin.GetActions().Select(action => action.Name)]);

		List<PluginWebRoute> routes = [.. plugin.GetRoutes()];
		Assert.Contains(routes, route => route.Method == "GET" && route.Path == "/inventory");
		Assert.Contains(routes, route => route.Method == "POST" && route.Path == "/consume");
	}

	[Fact]
	public async Task InventoryRoute_ServesLedgerSummary_WithLimitClamping()
	{
		CsgoPlugin plugin = InitializedPlugin();
		plugin.Ledger.Record(new ConsumeEntry("c1", cases: 1, keys: 1));
		plugin.Ledger.Record(new ConsumeEntry("c2", cases: 2, keys: 2));
		plugin.Ledger.Record(new ConsumeEntry("c3", cases: 3, keys: 3));

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/inventory");

		PluginWebResponse all = await route.Handler(Req("/inventory"), CancellationToken.None);
		Assert.Equal(200, all.StatusCode);
		Assert.Contains("\"mode\":\"readonly\"", all.Body);
		Assert.Contains("\"entries\":3", all.Body);
		Assert.Contains("\"cases\":6", all.Body);
		Assert.Contains("\"keys\":6", all.Body);
		Assert.Contains("\"caseId\":\"c1\"", all.Body);
		Assert.Contains("\"caseId\":\"c3\"", all.Body);

		PluginWebResponse limited = await route.Handler(Req("/inventory", query: ("limit", "2")), CancellationToken.None);
		string limitedRecent = limited.Body!.Split("\"recent\":")[1]!;
		Assert.Contains("\"caseId\":\"c2\"", limitedRecent);
		Assert.Contains("\"caseId\":\"c3\"", limitedRecent);
		Assert.DoesNotContain("\"caseId\":\"c1\"", limitedRecent);

		PluginWebResponse clampedLow = await route.Handler(Req("/inventory", query: ("limit", "0")), CancellationToken.None);
		string clampedLowRecent = clampedLow.Body!.Split("\"recent\":")[1]!;
		Assert.Contains("\"caseId\":\"c3\"", clampedLowRecent);
		Assert.DoesNotContain("\"caseId\":\"c2\"", clampedLowRecent);

		PluginWebResponse clampedHigh = await route.Handler(Req("/inventory", query: ("limit", "999999")), CancellationToken.None);
		Assert.Contains("\"caseId\":\"c1\"", clampedHigh.Body);

		PluginWebResponse unparsable = await route.Handler(Req("/inventory", query: ("limit", "abc")), CancellationToken.None);
		Assert.Contains("\"caseId\":\"c1\"", unparsable.Body);
	}

	[Fact]
	public async Task InventoryRoute_EmptyLedger_ServesZeroes()
	{
		CsgoPlugin plugin = InitializedPlugin();

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/inventory");
		PluginWebResponse response = await route.Handler(Req("/inventory"), CancellationToken.None);

		Assert.Equal(200, response.StatusCode);
		Assert.Contains("\"entries\":0", response.Body);
		Assert.Contains("\"perCase\":[]", response.Body);
	}

	[Fact]
	public async Task ConsumeRoute_Returns403_WhileGateIsOff()
	{
		CsgoPlugin plugin = InitializedPlugin();

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/consume");
		PluginWebResponse response = await route.Handler(
			Req("/consume", body: $$"""{"caseId":"{{StarterCase}}"}"""),
			CancellationToken.None);

		Assert.Equal(403, response.StatusCode);
		Assert.Contains("consume.enabled", response.Body);
		Assert.Contains("VAPOR_CSGO_CONSUME_ENABLED", response.Body);
	}

	[Theory]
	[InlineData(null, 400)]
	[InlineData("not json", 400)]
	[InlineData("""{"count":2}""", 400)]
	[InlineData("""{"caseId":"vapor.cases.starter","count":"many"}""", 400)]
	[InlineData("""{"caseId":"vapor.cases.starter","keys":"many"}""", 400)]
	[InlineData("""{"caseId":"vapor.cases.starter","awards":"not-an-array"}""", 400)]
	[InlineData("""{"caseId":"ghost.case"}""", 404)]
	[InlineData("""{"caseId":"vapor.cases.starter","count":2,"keys":null,"awards":null}""", 200)]
	[InlineData("""{"caseId":"vapor.cases.starter","awards":["AK", 7, "M4"]}""", 200)]
	public async Task ConsumeRoute_ValidatesBody_AndRecordsWhenEnabled(string? body, int expectedStatus)
	{
		CsgoPlugin plugin = EnabledPlugin();

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/consume");
		PluginWebResponse response = await route.Handler(Req("/consume", body: body), CancellationToken.None);

		Assert.Equal(expectedStatus, response.StatusCode);
		if (expectedStatus == 200)
		{
			Assert.Contains("\"mode\":\"ledger\"", response.Body);
			Assert.Contains("\"totals\":", response.Body);
		}
	}

	[Fact]
	public async Task ConsumeRoute_ReportsLedgerTotals_AfterRecord()
	{
		CsgoPlugin plugin = EnabledPlugin();
		plugin.Ledger.Record(new ConsumeEntry("c0", cases: 1, keys: 1));

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/consume");
		PluginWebResponse response = await route.Handler(
			Req("/consume", body: $$"""{"caseId":"{{StarterCase}}","count":2,"keys":3,"awards":["AK-47 | Redline"]}"""),
			CancellationToken.None);

		Assert.Equal(200, response.StatusCode);
		Assert.Contains("\"mode\":\"ledger\"", response.Body);
		Assert.Contains("\"cases\":2", response.Body);
		Assert.Contains("\"keys\":3", response.Body);
		Assert.Contains("\"awards\":[\"AK-47 | Redline\"]", response.Body);
		Assert.Contains("\"entries\":2", response.Body);
	}

	[Fact]
	public async Task ConsumeRoute_RejectsUnknownCase_EvenWhenEnabled()
	{
		CsgoPlugin plugin = EnabledPlugin();

		PluginWebRoute route = plugin.GetRoutes().Single(candidate => candidate.Path == "/consume");
		PluginWebResponse response = await route.Handler(
			Req("/consume", body: """{"caseId":"ghost.case"}"""),
			CancellationToken.None);

		Assert.Equal(404, response.StatusCode);
		Assert.Contains("unknown case", response.Body);
	}

	private static PluginWebRequest Req(string path, (string Key, string Value)? query = null, string? body = null) =>
		new(
			Path: path,
			Query: query is null
				? new Dictionary<string, string>()
				: new Dictionary<string, string> { [query.Value.Key] = query.Value.Value },
			Headers: new Dictionary<string, string>(),
			Body: body);

	private static CsgoPlugin InitializedPlugin()
	{
		var plugin = new CsgoPlugin();
		plugin.InitializeAsync(TestContext(), CancellationToken.None).GetAwaiter().GetResult();
		return plugin;
	}

	private static CsgoPlugin EnabledPlugin()
	{
		var plugin = new CsgoPlugin();
		plugin.InitializeAsync(TestContext(("consume.enabled", "true")), CancellationToken.None).GetAwaiter().GetResult();
		return plugin;
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
}
