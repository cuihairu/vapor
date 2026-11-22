using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.ControlPlane;

namespace Vapor.ControlPlane.Tests;

public sealed partial class CrawlRunWorkerTests : IDisposable
{
	private readonly SqliteCrawlStore _crawl = new(":memory:");

	private readonly AccountStore _accounts = new();

	private readonly FakeCrawlJobStore _jobs = new();

	private readonly FakeCrawlEventBroker _events = new();

	private readonly FakeCrawlAuditStore _audit = new();

	public void Dispose() => _crawl.Dispose();

	private CrawlRunWorker CreateWorker(
		int runTimeoutSeconds = 1800,
		int keepRuns = 10,
		int maxAppsPerTask = 200,
		int tickSeconds = 5) =>
		new(
			_crawl,
			_accounts,
			_jobs,
			_events,
			_audit,
			new Config(
				"admin",
				new Dictionary<string, DateTimeOffset?>(),
				":memory:",
				TaskLeaseSeconds: 300,
				EnableSwagger: false,
				AuditDbPath: ":memory:",
				CrawlWorkerTickSeconds: tickSeconds,
				CrawlKeepRuns: keepRuns,
				CrawlRunTimeoutSeconds: runTimeoutSeconds,
				CrawlMaxAppsPerTask: maxAppsPerTask),
			NullLogger<CrawlRunWorker>.Instance);

	private async Task<CrawlPlan> SeedOneShotPlanAsync(
		string id = "plan-1",
		uint[]? appIds = null,
		string? accounts = null,
		int intervalSeconds = 0,
		string? cron = null,
		int shardSize = 50)
	{
		var plan = new CrawlPlan(
			Id: id,
			Name: "test plan",
			AppIds: appIds ?? new uint[] { 570, 730, 400, 620, 440 },
			Accounts: accounts == null ? null : accounts.Split(','),
			Overrides: null,
			ShardSize: shardSize,
			IntervalMs: 100,
			Cc: "us",
			Cron: cron,
			IntervalSeconds: intervalSeconds,
			Enabled: true,
			CreatedAt: DateTimeOffset.UtcNow,
			UpdatedAt: DateTimeOffset.UtcNow,
			NextRunAt: DateTimeOffset.UtcNow.AddMilliseconds(-10)); // due

		await _crawl.UpsertPlanAsync(plan);
		return plan;
	}

	private static JsonElement BatchOutput(uint[] okApps, params (uint AppId, string Error)[] errors)
	{
		var output = new Dictionary<string, object?>
		{
			["games"] = JsonSerializer.SerializeToElement(okApps.Select(id => new { app_id = id, name = $"game-{id}" }).ToArray()),
			["errors"] = JsonSerializer.SerializeToElement(errors.Select(e => new { app_id = e.AppId, error = e.Error }).ToArray())
		};
		return JsonSerializer.SerializeToElement(output);
	}

	private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? budget = null)
	{
		// Ticks come from a real PeriodicTimer (1s), so wait for observable state
		// instead of guessing timing; the budget only covers timer drift on slow CI.
		var deadline = DateTimeOffset.UtcNow + (budget ?? TimeSpan.FromSeconds(30));
		while (!condition())
		{
			if (DateTimeOffset.UtcNow > deadline)
			{
				throw new TimeoutException("Condition not reached within budget");
			}

			await Task.Delay(10);
		}
	}

	private static string _latestRunId(FakeCrawlJobStore jobs) =>
		jobs.Created.Select(j => j.Meta!["crawl_run_id"]).Last();

	private int RunEventsPublished() =>
		_events.Published.Count(e => e.Type == "crawl.run_triggered");

}
