using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Vapor.Plugins.CaseOpening;
using Vapor.Protocol;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

using Xunit;

namespace Vapor.Plugins.CaseOpening.Tests;

public class CaseConsumeActionTests
{
	private const string StarterCase = "vapor.cases.starter";

	[Fact]
	public void Metadata_DeclaresNonIdempotent_NoLogin()
	{
		CaseConsumeAction action = new(new CaseOpeningPlugin());

		Assert.Equal("case_consume", action.Name);
		Assert.Equal("case_consume", action.Metadata.Name);
		Assert.Equal(ActionSafety.NonIdempotent, action.Metadata.Safety);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public void Constructor_RejectsNullPlugin()
	{
		Assert.Throws<ArgumentNullException>(() => new CaseConsumeAction(null!));
	}

	[Fact]
	public async Task ExecuteAsync_RejectsNullPayload()
	{
		CaseConsumeAction action = new(EnabledPlugin());

		await Assert.ThrowsAsync<ArgumentNullException>(() =>
			action.ExecuteAsync(null!, null!, CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_HonorsCancellation()
	{
		CaseConsumeAction action = new(EnabledPlugin());
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			action.ExecuteAsync(null!, new Dictionary<string, object?>(), cancelled.Token));
	}

	[Fact]
	public async Task ExecuteAsync_RefusesToRun_WhileGateIsOff()
	{
		var plugin = new CaseOpeningPlugin();
		await plugin.InitializeAsync(TestContext(), CancellationToken.None);
		CaseConsumeAction action = new(plugin);

		ActionResult result = await action.ExecuteAsync(
			null!,
			new Dictionary<string, object?> { ["caseId"] = StarterCase },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("consume.enabled", result.Error);
		Assert.Contains("VAPOR_CASEOPENING_CONSUME_ENABLED", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_RecordsDefaultEntry_AndReportsLedgerTotals()
	{
		CaseOpeningPlugin plugin = EnabledPlugin();
		CaseConsumeAction action = new(plugin);

		ActionResult result = await action.ExecuteAsync(
			null!,
			new Dictionary<string, object?> { ["caseId"] = StarterCase },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("ledger", result.Output!["mode"]);
		Assert.Equal(StarterCase, result.Output["caseId"]);
		Assert.Equal(1, result.Output["cases"]);
		Assert.Equal(1, result.Output["keys"]);
		Assert.Empty((IReadOnlyList<string>)result.Output["awards"]!);

		var ledger = Assert.IsType<Dictionary<string, object?>>(result.Output["ledger"]);
		Assert.Equal(1, ledger["entries"]);
		Assert.Equal(1, ledger["cases"]);
		Assert.Equal(1, ledger["keys"]);
		Assert.Equal(1, plugin.Ledger.Summarize().TotalEntries);
	}

	[Fact]
	public async Task ExecuteAsync_CoercesCountAndKeysShapes_AndClamps()
	{
		CaseOpeningPlugin plugin = EnabledPlugin();
		CaseConsumeAction action = new(plugin);
		using JsonDocument countElement = JsonDocument.Parse("2");
		using JsonDocument fractionElement = JsonDocument.Parse("2.5");

		Dictionary<string, object?>[] payloads =
		[
			new() { ["caseId"] = StarterCase, ["count"] = 100_000L },   // clamps to 100, keys follow
			new() { ["caseId"] = StarterCase, ["count"] = 2, ["keys"] = "5" }, // string keys win
			new() { ["caseId"] = StarterCase, ["count"] = 0 },          // clamps up to 1
			new() { ["caseId"] = StarterCase, ["count"] = countElement.RootElement.Clone() },
			new() { ["caseId"] = StarterCase, ["count"] = 3, ["keys"] = 5m },
			new() { ["caseId"] = StarterCase, ["count"] = fractionElement.RootElement.Clone() }, // not an int32: keeps 1
		];

		foreach (Dictionary<string, object?> payload in payloads)
		{
			ActionResult result = await action.ExecuteAsync(null!, payload, CancellationToken.None);
			Assert.True(result.Success, JsonSerializer.Serialize(payload));
		}

		CaseLedgerSummary summary = plugin.Ledger.Summarize();
		Assert.Equal(6, summary.TotalEntries);
		// 100 (clamped) + 2 + 1 (clamped up) + 2 (json) + 3 + 1 (json fraction kept at 1)
		Assert.Equal(109, summary.TotalCases);
		// keys follow count except where overridden: 100 + 5 + 1 + 2 + 5 + 1
		Assert.Equal(114, summary.TotalKeys);
	}

	[Fact]
	public async Task ExecuteAsync_ReadsAwards_InEveryPayloadShape()
	{
		CaseOpeningPlugin plugin = EnabledPlugin();
		CaseConsumeAction action = new(plugin);
		using JsonDocument mixed = JsonDocument.Parse("""["From Json", 7, "Second"]""");

		Dictionary<string, object?>[] payloads =
		[
			new() { ["caseId"] = StarterCase, ["awards"] = mixed.RootElement.Clone() },
			new() { ["caseId"] = StarterCase, ["awards"] = new List<object?> { "From List", 9, "Also" } },
			new() { ["caseId"] = StarterCase, ["awards"] = "Solo Award" },
			new() { ["caseId"] = StarterCase, ["awards"] = null },
			new() { ["caseId"] = StarterCase, ["awards"] = 42 },
		];

		foreach (Dictionary<string, object?> payload in payloads)
		{
			ActionResult result = await action.ExecuteAsync(null!, payload, CancellationToken.None);
			Assert.True(result.Success, JsonSerializer.Serialize(payload));
		}

		IReadOnlyList<ConsumeEntry> entries = plugin.Ledger.RecentEntries();
		Assert.Equal(5, entries.Count);
		Assert.Equal(["From Json", "Second"], entries[0].Awards);
		Assert.Equal(["From List", "Also"], entries[1].Awards);
		Assert.Equal(["Solo Award"], entries[2].Awards);
		Assert.Empty(entries[3].Awards);
		Assert.Empty(entries[4].Awards);
	}

	[Fact]
	public async Task ExecuteAsync_RejectsMissingOrWrongCaseIds()
	{
		CaseOpeningPlugin plugin = EnabledPlugin();
		CaseConsumeAction action = new(plugin);
		using JsonDocument boxed = JsonDocument.Parse(JsonSerializer.Serialize(StarterCase));

		ActionResult missing = await action.ExecuteAsync(null!, new Dictionary<string, object?>(), CancellationToken.None);
		Assert.False(missing.Success);
		Assert.Contains("'caseId'", missing.Error);

		ActionResult wrongType = await action.ExecuteAsync(
			null!, new Dictionary<string, object?> { ["caseId"] = 42 }, CancellationToken.None);
		Assert.False(wrongType.Success);
		Assert.Contains("'caseId'", wrongType.Error);

		ActionResult blank = await action.ExecuteAsync(
			null!, new Dictionary<string, object?> { ["caseId"] = "  " }, CancellationToken.None);
		Assert.False(blank.Success);

		ActionResult unknown = await action.ExecuteAsync(
			null!, new Dictionary<string, object?> { ["caseId"] = "ghost.case" }, CancellationToken.None);
		Assert.False(unknown.Success);
		Assert.Contains("unknown case 'ghost.case'", unknown.Error);

		ActionResult boxedString = await action.ExecuteAsync(
			null!, new Dictionary<string, object?> { ["caseId"] = boxed.RootElement.Clone() }, CancellationToken.None);
		Assert.True(boxedString.Success);
	}

	[Fact]
	public async Task CaseConsumeAction_RecordsThroughTheSameLedgerSurface()
	{
		// The action and the plugin share one ledger instance — totals stay
		// consistent no matter which surface recorded.
		CaseOpeningPlugin plugin = EnabledPlugin();
		plugin.Consume(StarterCase, 2, 2, null);
		CaseConsumeAction action = new(plugin);

		await action.ExecuteAsync(
			null!,
			new Dictionary<string, object?> { ["caseId"] = StarterCase, ["count"] = 3 },
			CancellationToken.None);

		CaseLedgerSummary summary = plugin.Ledger.Summarize();
		Assert.Equal(2, summary.TotalEntries);
		Assert.Equal(5, summary.TotalCases);
		Assert.Equal(5, summary.TotalKeys);
	}

	private static CaseOpeningPlugin EnabledPlugin()
	{
		var plugin = new CaseOpeningPlugin();
		plugin.InitializeAsync(TestContext(("consume.enabled", "true")), CancellationToken.None)
			.GetAwaiter().GetResult();
		return plugin;
	}

	private static IPluginContext TestContext(params (string Key, string Value)[] configuration) => new TestPluginContext(
		configuration.ToDictionary(entry => entry.Key, entry => entry.Value));

	private sealed class TestPluginContext(IReadOnlyDictionary<string, string> configuration) : IPluginContext
	{
		public PluginInfo Info { get; } = new(
			"vapor.caseopening", "Vapor Case Opening (test)", new Version(1, 0, 0), PluginApi.Current);

		public IReadOnlyDictionary<string, string> Configuration { get; } = configuration;

		public IPluginHostServices Host { get; } =
			new DefaultPluginHostServices(NullLoggerFactory.Instance, new ServiceProviderStub());
	}

	private sealed class ServiceProviderStub : IServiceProvider
	{
		public object? GetService(Type serviceType) => null;
	}
}
