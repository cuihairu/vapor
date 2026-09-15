using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.ControlPlane.Tests.Performance;

/// <summary>
/// Read-REST latency baselines: representative read endpoints under concurrent
/// load, plus the job-creation endpoint as the task-dispatch write path for
/// comparison. Measurements run through WebApplicationFactory's in-memory
/// handler, so they cover the framework pipeline + serialization + storage
/// cost but NOT real network stack latency — the recorded numbers are a
/// regression baseline for the code under test, not end-to-end wall latency.
///
/// Assertions use generous upper bounds so slow CI machines never flake;
/// the printed percentiles are the actual baselines to watch over time.
/// </summary>
public class ApiLatencyBenchmarks
{
	private const int Concurrency = 8;
	private const int RequestsPerEndpoint = 200;

	private readonly ITestOutputHelper _output;

	public ApiLatencyBenchmarks(ITestOutputHelper output)
	{
		_output = output;
	}

	public static TheoryData<string> ReadEndpoints => new()
	{
		"/healthz",
		"/v1/accounts",
		"/v1/jobs?limit=100",
		"/v1/audit/logs?limit=100",
		"/v1/crawl/plans",
		"/v1/crawl/results?limit=100",
	};

	[Theory]
	[MemberData(nameof(ReadEndpoints))]
	public async Task ReadEndpoints_LatencyUnderConcurrentLoad(string path)
	{
		await using BenchmarkApiFactory factory = new();
		await SeedAsync(factory);
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Warm the endpoint once so JIT/first-hit costs don't skew percentiles.
		using (HttpRequestMessage warmup = new(HttpMethod.Get, path))
		{
			using HttpResponseMessage warm = await client.SendAsync(warmup);
			Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
		}

		var latencies = new List<long>(RequestsPerEndpoint);
		var gate = new TaskCompletionSource();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

		async Task WorkerAsync()
		{
			await gate.Task.WaitAsync(cts.Token);
			for (int i = 0; i < RequestsPerEndpoint / Concurrency; i++)
			{
				var sw = Stopwatch.StartNew();
				using HttpRequestMessage request = new(HttpMethod.Get, path);
				using HttpResponseMessage response = await client.SendAsync(request, cts.Token);
				sw.Stop();

				Assert.Equal(HttpStatusCode.OK, response.StatusCode);
				lock (latencies)
				{
					latencies.Add(sw.ElapsedMilliseconds);
				}
			}
		}

		List<Task> workers = Enumerable.Range(0, Concurrency).Select(_ => WorkerAsync()).ToList();
		gate.SetResult();
		var total = Stopwatch.StartNew();
		await Task.WhenAll(workers);
		total.Stop();

		double seconds = Math.Max(total.Elapsed.TotalSeconds, 0.001);
		_output.WriteLine($"{path}: {latencies.Count} requests, p50 {Percentile(latencies, 0.50)} ms, p95 {Percentile(latencies, 0.95)} ms, max {latencies.Max()} ms, {latencies.Count / seconds:F0} req/s");

		// Correctness first, generous wall-clock second: local runs land in the
		// low milliseconds; 5s per-request catches order-of-magnitude regressions.
		Assert.Equal(RequestsPerEndpoint, latencies.Count);
		Assert.True(Percentile(latencies, 0.95) < 5000, $"p95 {Percentile(latencies, 0.95)} ms");
	}

	[Fact]
	public async Task JobCreateEndpoint_Latency()
	{
		const int requestCount = 200;
		await using BenchmarkApiFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Warm-up create (also its own correctness check).
		using (HttpResponseMessage warm = await client.PostAsJsonAsync("/v1/jobs", new
		{
			action = "ping",
			region = "local",
			targets = new[] { "bench-warmup" }
		}))
		{
			Assert.Equal(HttpStatusCode.Accepted, warm.StatusCode);
		}

		var latencies = new List<long>(requestCount);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
		var total = Stopwatch.StartNew();

		for (int i = 0; i < requestCount; i++)
		{
			var sw = Stopwatch.StartNew();
			using HttpResponseMessage response = await client.PostAsJsonAsync(
				"/v1/jobs",
				new { action = "ping", region = "local", targets = new[] { $"bench-{i}" } },
				cts.Token);
			sw.Stop();

			Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
			latencies.Add(sw.ElapsedMilliseconds);
		}

		total.Stop();
		double seconds = Math.Max(total.Elapsed.TotalSeconds, 0.001);
		_output.WriteLine($"POST /v1/jobs: {requestCount} sequential creates, p50 {Percentile(latencies, 0.50)} ms, p95 {Percentile(latencies, 0.95)} ms, max {latencies.Max()} ms, {requestCount / seconds:F0} req/s");

		Assert.Equal(requestCount, latencies.Count);
		Assert.True(Percentile(latencies, 0.95) < 5000, $"p95 {Percentile(latencies, 0.95)} ms");
	}

	/// <summary>Sorted-index percentile (p50 → 0.50); latency lists hold whole milliseconds.</summary>
	private static long Percentile(List<long> sortedSource, double fraction)
	{
		long[] sorted = [.. sortedSource.OrderBy(static v => v)];
		int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
		return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
	}

	private static async Task SeedAsync(BenchmarkApiFactory factory)
	{
		AccountStore accounts = factory.Services.GetRequiredService<AccountStore>();
		for (int i = 0; i < 50; i++)
		{
			accounts.Upsert($"acct-{i:D2}", enabled: true, AccountDesiredState.Idle, null, "local", null, null);
		}

		IJobStore jobs = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		for (int i = 0; i < 200; i++)
		{
			await jobs.CreateJob(
				new CreateJobRequest("ping", "local", [$"acct-{i % 50:D2}"], null, null),
				cts.Token);
		}

		IAuditStore audit = factory.Services.GetRequiredService<IAuditStore>();
		for (int i = 0; i < 100; i++)
		{
			await audit.RecordAsync(AuditStoreExtensions.CreateEntry("bench.event", "bench"), cts.Token);
		}

		SqliteCrawlStore crawl = factory.Services.GetRequiredService<SqliteCrawlStore>();
		DateTimeOffset now = DateTimeOffset.UtcNow;
		for (int p = 0; p < 5; p++)
		{
			await crawl.UpsertPlanAsync(new CrawlPlan(
				Id: $"plan-{p}",
				Name: $"bench plan {p}",
				AppIds: Enumerable.Range(0, 40).Select(a => (uint)(570 + a)).ToList(),
				Accounts: null,
				Overrides: null,
				ShardSize: 50,
				IntervalMs: 500,
				Cc: "us",
				Cron: null,
				IntervalSeconds: 0,
				Enabled: true,
				CreatedAt: now,
				UpdatedAt: now,
				NextRunAt: null), cts.Token);
			for (int r = 0; r < 40; r++)
			{
				await crawl.AddResultAsync(new CrawlResultRow(
					0, $"plan-{p}", $"run-{p}", (uint)(570 + r), $"acct-{r % 50:D2}", $"job-{p}-{r}",
					Ok: r % 10 != 0, Error: r % 10 == 0 ? "bench failure" : null, Data: null,
					FetchedAt: now), cts.Token);
			}
		}
	}

	/// <summary>
	/// Real stores on :memory: databases with every hosted service removed, so
	/// background workers cannot poll during latency measurements. The shared
	/// <see cref="ControlPlaneApiTests.TestFactory"/> swaps in stub stores that
	/// reject writes, which these benchmarks cannot use.
	/// </summary>
	private sealed class BenchmarkApiFactory : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			// Per-request console logging would both skew the measurements and
			// drown the benchmark output; benchmarks only need status codes.
			builder.ConfigureLogging(logging => logging.ClearProviders());
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.RemoveAll<SqliteCrawlStore>();
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:"));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				services.AddSingleton<SqliteCrawlStore>(sp => new SqliteCrawlStore(":memory:"));
				services.RemoveAll<IHostedService>();
				services.RemoveAll<IHostedLifecycleService>();
			});
		}
	}
}
