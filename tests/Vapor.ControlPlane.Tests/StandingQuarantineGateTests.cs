using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// Joins the wait-window collection: the API tests retune the static
// StorageCollectRunner/AccountTaskRunner WaitWindow/PollInterval knobs exactly
// like AccountApiTests and StorageCollectRunnerTests do (see the collection's
// note — interleaved classes can read each other's shrunk windows mid-wait).
[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed class StandingQuarantineGateTests
{
	private const string StorageSteamId = "76561197960265728";

	// ── PartitionByStanding (pure) ──

	[Fact]
	public void PartitionByStanding_SplitsQuarantinedFromEligible_PreservingOrder()
	{
		List<AccountSpec> farms =
		[
			new("farm-a", Enabled: true, AccountDesiredState.Online),
			new("farm-b", Enabled: true, AccountDesiredState.Farm),
			new("farm-c", Enabled: true, AccountDesiredState.Online),
		];

		(List<AccountSpec> eligible, List<string> skipped) = StorageCollectRunner.PartitionByStanding(
			farms, name => name == "farm-b");

		Assert.Equal(["farm-a", "farm-c"], eligible.Select(f => f.AccountName));
		Assert.Equal(["farm-b"], skipped);
	}

	[Fact]
	public void PartitionByStanding_AllQuarantined_YieldsEmptyEligible()
	{
		List<AccountSpec> farms =
		[
			new("farm-a", Enabled: true, AccountDesiredState.Online),
			new("farm-b", Enabled: true, AccountDesiredState.Farm),
		];

		(List<AccountSpec> eligible, List<string> skipped) = StorageCollectRunner.PartitionByStanding(
			farms, _ => true);

		Assert.Empty(eligible);
		Assert.Equal(["farm-a", "farm-b"], skipped);
	}

	[Fact]
	public void PartitionByStanding_HealthyFleet_SkipsNothing()
	{
		List<AccountSpec> farms = [new("farm-a", Enabled: true, AccountDesiredState.Online)];

		(List<AccountSpec> eligible, List<string> skipped) = StorageCollectRunner.PartitionByStanding(
			farms, _ => false);

		Assert.Equal(["farm-a"], eligible.Select(f => f.AccountName));
		Assert.Empty(skipped);
	}

	// ── Reconciler standing view + seam ──

	[Fact]
	public async Task GetStandingView_UntrackedIsNull_AndSeamSeedsQuarantine()
	{
		await using var factory = CreateFactory();
		DesiredStateReconciler reconciler = factory.Services.GetRequiredService<DesiredStateReconciler>();

		Assert.Null(reconciler.GetStandingView("ghost"));

		// Default seed keeps the never-checked shape: no summary-adjacent
		// timestamp, so the view reports CheckedAt as null.
		reconciler.SetStandingForTests("ghost", quarantined: true, "banned");
		AccountStandingView? view = reconciler.GetStandingView("ghost");

		Assert.NotNull(view);
		Assert.True(view.Quarantined);
		Assert.Equal("banned", view.Standing);
		Assert.Null(view.CheckedAt);

		// A checkedAt seed flows through as a real timestamp (the shape a
		// post-reconcile account has).
		DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
		reconciler.SetStandingForTests("ghost", quarantined: true, "banned", checkedAt);
		view = reconciler.GetStandingView("ghost");

		Assert.NotNull(view);
		Assert.Equal(checkedAt, view.CheckedAt);
	}

	// ── POST /v1/orchestration/storage/collect ──

	[Fact]
	public async Task Collect_SkipsQuarantinedFarmSource_AndReportsIt()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory();
			SeedAccounts(factory, ("farm-a", AccountDesiredState.Online), ("farm-b", AccountDesiredState.Farm));
			Quarantine(factory, "farm-a");
			HttpClient client = CreateClient(factory);

			HttpResponseMessage response = await client.PostAsJsonAsync("/v1/orchestration/storage/collect",
				new { storage = "warehouse" });

			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
			JsonElement body = await ReadJson(response);
			Assert.Equal(["farm-b"], body.GetProperty("farm_accounts").EnumerateArray().Select(e => e.GetString()));
			Assert.Equal(["farm-a"], body.GetProperty("skipped_quarantined").EnumerateArray().Select(e => e.GetString()));
		}
		finally
		{
			StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(150);
			StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(500);
		}
	}

	[Fact]
	public async Task Collect_AllFarmsQuarantined_Returns400()
	{
		await using var factory = CreateFactory();
		SeedAccounts(factory, ("farm-a", AccountDesiredState.Online));
		Quarantine(factory, "farm-a");
		HttpClient client = CreateClient(factory);

		HttpResponseMessage response = await client.PostAsJsonAsync("/v1/orchestration/storage/collect",
			new { storage = "warehouse" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("quarantined", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Collect_QuarantinedWarehouse_Blocks400_UnlessForced()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory();
			SeedAccounts(factory, ("farm-a", AccountDesiredState.Online));
			Quarantine(factory, "warehouse");
			HttpClient client = CreateClient(factory);

			HttpResponseMessage blocked = await client.PostAsJsonAsync("/v1/orchestration/storage/collect",
				new { storage = "warehouse" });
			Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
			Assert.Contains("storage account 'warehouse' is quarantined", await blocked.Content.ReadAsStringAsync());

			HttpResponseMessage forced = await client.PostAsJsonAsync("/v1/orchestration/storage/collect",
				new { storage = "warehouse", force = true });
			Assert.Equal(HttpStatusCode.Accepted, forced.StatusCode);
			JsonElement body = await ReadJson(forced);
			Assert.Equal(["farm-a"], body.GetProperty("farm_accounts").EnumerateArray().Select(e => e.GetString()));
			Assert.Empty(body.GetProperty("skipped_quarantined").EnumerateArray());
		}
		finally
		{
			StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(150);
			StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(500);
		}
	}

	[Fact]
	public async Task Collect_ForceAppliesOnlyToWarehouse_FarmsStillSkipped()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory();
			SeedAccounts(factory, ("farm-a", AccountDesiredState.Online), ("farm-b", AccountDesiredState.Farm));
			Quarantine(factory, "warehouse");
			Quarantine(factory, "farm-b");
			HttpClient client = CreateClient(factory);

			HttpResponseMessage response = await client.PostAsJsonAsync("/v1/orchestration/storage/collect",
				new { storage = "warehouse", force = true });

			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
			JsonElement body = await ReadJson(response);
			Assert.Equal(["farm-a"], body.GetProperty("farm_accounts").EnumerateArray().Select(e => e.GetString()));
			Assert.Equal(["farm-b"], body.GetProperty("skipped_quarantined").EnumerateArray().Select(e => e.GetString()));
		}
		finally
		{
			StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(150);
			StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(500);
		}
	}

	// ── POST /v1/accounts/{name}/loot ──

	[Fact]
	public async Task Loot_QuarantinedAccount_Blocks400_WithStandingInMessage()
	{
		await using var factory = CreateFactory();
		SeedAccounts(factory, ("farm-a", AccountDesiredState.Online));
		Quarantine(factory, "farm-a");
		HttpClient client = CreateClient(factory);

		HttpResponseMessage response = await client.PostAsJsonAsync("/v1/accounts/farm-a/loot",
			new { partnerSteamId = StorageSteamId });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("account 'farm-a' is quarantined by the standing check", body);
		Assert.Contains("force=true", body);
	}

	[Fact]
	public async Task Loot_QuarantinedAccount_ForceTrue_Dispatches_AndAuditsTheForcing()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory();
			SeedAccounts(factory, ("farm-a", AccountDesiredState.Online));
			Quarantine(factory, "farm-a");
			HttpClient client = CreateClient(factory);

			HttpResponseMessage response = await client.PostAsJsonAsync("/v1/accounts/farm-a/loot",
				new { partnerSteamId = StorageSteamId, force = true });

			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
			JsonElement body = await ReadJson(response);
			Assert.False(string.IsNullOrEmpty(body.GetProperty("job_id").GetString()));

			SqliteAuditStore audit = factory.Services.GetRequiredService<IAuditStore>() as SqliteAuditStore
				?? throw new InvalidOperationException("audit store fake missing");
			IReadOnlyList<AuditEntry> entries = await audit.QueryAsync(new AuditQuery(Action: "account.loot"), CancellationToken.None);
			AuditEntry entry = Assert.Single(entries);
			// The audit store round-trips detail values as JSON elements.
			Assert.Equal(JsonValueKind.True, Assert.IsType<JsonElement>(entry.Details?["forced_quarantine"]).ValueKind);
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Loot_NeverCheckedAccount_Proceeds()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory();
			SeedAccounts(factory, ("farm-a", AccountDesiredState.Online));
			HttpClient client = CreateClient(factory);

			HttpResponseMessage response = await client.PostAsJsonAsync("/v1/accounts/farm-a/loot",
				new { partnerSteamId = StorageSteamId });

			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Collect_QuarantinedWarehouseWithoutSummary_ShowsUnknownStanding()
	{
		await using var factory = CreateFactory();
		SeedAccounts(factory, ("farm-a", AccountDesiredState.Online));
		factory.Services.GetRequiredService<DesiredStateReconciler>().SetStandingForTests("warehouse", quarantined: true, summary: null);
		HttpClient client = CreateClient(factory);

		HttpResponseMessage response = await client.PostAsJsonAsync("/v1/orchestration/storage/collect",
			new { storage = "warehouse" });

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Contains("standing=unknown", await response.Content.ReadAsStringAsync());
	}

	// ── scaffolding ──

	private static void SeedAccounts(TestFactory factory, params (string Name, AccountDesiredState State)[] farms)
	{
		AccountStore accounts = factory.Services.GetRequiredService<AccountStore>();
		foreach ((string name, AccountDesiredState state) in farms)
		{
			accounts.Upsert(name, enabled: true, state, idleApps: null, region: null, agentId: null, note: null);
		}

		accounts.Upsert("warehouse", enabled: true, AccountDesiredState.Offline, idleApps: null, region: null,
			agentId: null, note: null, role: AccountRole.Storage, steamId: StorageSteamId);
	}

	private static void Quarantine(TestFactory factory, string accountName) =>
		factory.Services.GetRequiredService<DesiredStateReconciler>().SetStandingForTests(accountName, quarantined: true, "banned");

	private static HttpClient CreateClient(TestFactory factory)
	{
		HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		return client;
	}

	private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
		JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

	private static TestFactory CreateFactory() => new();

	private sealed class TestFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				// The quarantine gates are pure dispatch-time checks — background
				// reconciler/scheduler loops would only race the seeded state.
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}
}
