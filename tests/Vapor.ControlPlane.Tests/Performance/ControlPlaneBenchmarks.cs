using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.ControlPlane.Tests.Performance;

/// <summary>
/// Control plane performance baselines covering the three tracked dimensions:
/// queue throughput (SQLite job store), concurrent task dispatch (parallel
/// claimers), and SSE fan-out (subscriber count).
///
/// Assertions use generous upper bounds so slow CI machines never flake;
/// the numbers they print are the actual baselines to watch over time.
/// </summary>
public class ControlPlaneBenchmarks {
	private readonly ITestOutputHelper _output;

	public ControlPlaneBenchmarks(ITestOutputHelper output) {
		_output = output;
	}

	[Fact]
	public async Task QueueThroughput_CreateClaimFinishCycle() {
		const int jobCount = 500;
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

		var createSw = Stopwatch.StartNew();
		List<string> jobIds = [];
		for (int i = 0; i < jobCount; i++) {
			JobWithTasks created = await store.CreateJob(
				new CreateJobRequest("ping", "local", [$"acct-{i}"], null, null),
				cts.Token);
			jobIds.Add(created.Job.Id);
		}

		createSw.Stop();

		var cycleSw = Stopwatch.StartNew();
		int claimed = 0;
		while (await store.ClaimNextQueuedTask("local", cts.Token) is { } task) {
			await store.SetTaskResult(
				new TaskResult(task.Id, true, null, null, DateTimeOffset.UtcNow, task.Attempt),
				cts.Token);
			claimed++;
		}

		cycleSw.Stop();

		_output.WriteLine($"create:  {jobCount} jobs in {createSw.ElapsedMilliseconds} ms ({jobCount / Math.Max(createSw.Elapsed.TotalSeconds, 0.001):F0}/s)");
		_output.WriteLine($"cycle:   {claimed} claim+finish in {cycleSw.ElapsedMilliseconds} ms ({claimed / Math.Max(cycleSw.Elapsed.TotalSeconds, 0.001):F0}/s)");

		Assert.Equal(jobCount, claimed);
		foreach (string jobId in jobIds) {
			JobWithTasks job = await store.GetJob(jobId, cts.Token);
			Assert.Equal(JobStatus.Finished, job.Job.Status);
		}

		// Correctness first, generous wall-clock second: a full run takes ~2s
		// locally; 60s catches order-of-magnitude regressions without CI flake.
		Assert.True(createSw.Elapsed.TotalSeconds < 60, $"create took {createSw.Elapsed.TotalSeconds:F1}s");
		Assert.True(cycleSw.Elapsed.TotalSeconds < 60, $"claim+finish cycle took {cycleSw.Elapsed.TotalSeconds:F1}s");
	}

	[Fact]
	public async Task ConcurrentClaimers_EveryTaskDispatchedExactlyOnce() {
		const int jobCount = 200;
		const int claimerCount = 4;
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

		for (int i = 0; i < jobCount; i++) {
			await store.CreateJob(
				new CreateJobRequest("ping", "local", [$"acct-{i}"], null, null),
				cts.Token);
		}

		ConcurrentDictionary<string, int> claimsPerTask = new();
		var sw = Stopwatch.StartNew();

		// All claimers use the jobs' region ("local"), simulating several
		// agents in the same region racing for the oldest queued task.
		IEnumerable<Task> claimers = Enumerable.Range(0, claimerCount).Select(_ => Task.Run(async () => {
			while (await store.ClaimNextQueuedTask("local", cts.Token) is { } task) {
				claimsPerTask.AddOrUpdate(task.Id, 1, (_, n) => n + 1);
				await store.SetTaskResult(
					new TaskResult(task.Id, true, null, null, DateTimeOffset.UtcNow, task.Attempt),
					cts.Token);
			}
		}));

		await Task.WhenAll(claimers);
		sw.Stop();

		int doubleClaims = claimsPerTask.Count(kv => kv.Value != 1);
		_output.WriteLine($"{claimerCount} concurrent claimers finished {claimsPerTask.Count}/{jobCount} tasks in {sw.ElapsedMilliseconds} ms ({claimsPerTask.Count / Math.Max(sw.Elapsed.TotalSeconds, 0.001):F0}/s)");
		_output.WriteLine($"double-claimed tasks: {doubleClaims}");

		Assert.Equal(jobCount, claimsPerTask.Count);
		Assert.Equal(0, doubleClaims);
		Assert.True(sw.Elapsed.TotalSeconds < 60, $"took {sw.Elapsed.TotalSeconds:F1}s");
	}

	[Fact]
	public async Task EventBroker_HundredsOfSubscribers_AllReceiveEveryEvent() {
		const int jobSubscribers = 500;
		const int globalSubscribers = 100;
		const int eventCount = 100;
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

		async Task<int> Consume(string jobId, CancellationToken token) {
			int seen = 0;
			try {
				await foreach (Event _ in broker.Subscribe(token, jobId)) {
					seen++;
					if (seen >= eventCount) {
						break;
					}
				}
			} catch (OperationCanceledException) {
				// Quiet-window timeout for job-scoped consumers.
			}

			return seen;
		}

		// Start all subscribers before publishing. Global consumers collect
		// until they have seen every event; job-scoped consumers listen on
		// unique job ids the publish never touches, so they observe a 2s
		// quiet window instead of waiting forever.
		List<Task<int>> jobConsumers = [];
		for (int i = 0; i < jobSubscribers; i++) {
			// Deliberately not disposed: the 2s quiet-window token must stay
			// alive while the consumer task runs.
			var quietCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
			jobConsumers.Add(Consume($"job-{Guid.NewGuid():N}", quietCts.Token));
		}

		List<Task<int>> globalConsumers = [.. Enumerable.Range(0, globalSubscribers).Select(_ => Consume("*", cts.Token))];

		// Let subscription registration settle before the publish burst.
		await Task.Delay(200);

		var sw = Stopwatch.StartNew();
		for (int i = 0; i < eventCount; i++) {
			broker.Publish(null, "bench.event", null);
		}

		// Publishing returns immediately (channel writes); the real cost is
		// observed when consumers drain their queues.
		int[] globalCounts = await Task.WhenAll(globalConsumers);
		sw.Stop();

		int[] jobCounts = await Task.WhenAll(jobConsumers);

		_output.WriteLine($"{jobSubscribers} job + {globalSubscribers} global subscribers x {eventCount} events drained in {sw.ElapsedMilliseconds} ms");
		_output.WriteLine($"global subscriber counts min/max: {globalCounts.Min()}/{globalCounts.Max()}");
		_output.WriteLine($"job subscriber counts (must be 0, quiet window): max seen {jobCounts.Max()}");

		// Job-scoped subscribers listen on unique job ids: the publish above
		// must not reach them.
		Assert.All(jobCounts, c => Assert.Equal(0, c));
		// Global subscribers ("*") must see every event.
		Assert.All(globalCounts, c => Assert.Equal(eventCount, c));
		Assert.True(sw.Elapsed.TotalSeconds < 30, $"fan-out drain took {sw.Elapsed.TotalSeconds:F1}s");
	}

	[Fact]
	public async Task SseEndpoint_ConcurrentConnections_AllReceiveEvents() {
		const int connectionCount = 50;
		await using ControlPlaneApiTests.TestFactory factory = CreateApiFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

		var readers = new List<Task<bool>>();
		var swTotal = Stopwatch.StartNew();
		for (int i = 0; i < connectionCount; i++) {
			readers.Add(ReadFirstJobCreatedEventAsync(client, cts.Token));
		}

		// Wait until every stream has registered its subscription (mirrors the
		// real broker), then publish one event that all of them should forward.
		var swAttach = Stopwatch.StartNew();
		while (factory.Events.ActiveJobSubscriptions < connectionCount) {
			if (swAttach.Elapsed > TimeSpan.FromSeconds(10)) {
				throw new TimeoutException(
					$"only {factory.Events.ActiveJobSubscriptions}/{connectionCount} SSE subscriptions attached after 10s");
			}

			await Task.Delay(25, cts.Token);
		}

		factory.Events.Publish("bench-job", "job.created", null);

		bool[] received = await Task.WhenAll(readers);
		swTotal.Stop();

		int receivedCount = received.Count(r => r);
		_output.WriteLine($"{connectionCount} concurrent SSE connections, {receivedCount} received the event in {swTotal.ElapsedMilliseconds} ms");

		Assert.Equal(connectionCount, receivedCount);
		Assert.True(swTotal.Elapsed.TotalSeconds < 30, $"took {swTotal.Elapsed.TotalSeconds:F1}s");
	}

	private static ControlPlaneApiTests.TestFactory CreateApiFactory() => new();

	private static async Task<bool> ReadFirstJobCreatedEventAsync(HttpClient client, CancellationToken cancellationToken) {
		try {
			using HttpRequestMessage request = new(HttpMethod.Get, "/v1/jobs/events");
			using HttpResponseMessage response = await client.SendAsync(
				request,
				HttpCompletionOption.ResponseHeadersRead,
				cancellationToken);

			if (response.StatusCode != HttpStatusCode.OK) {
				return false;
			}

			await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			using var reader = new StreamReader(stream, Encoding.UTF8);

			while (await reader.ReadLineAsync(cancellationToken) is { } line) {
				if (line.StartsWith("event: job.created", StringComparison.Ordinal)) {
					return true;
				}
			}

			return false;
		} catch (OperationCanceledException) {
			return false;
		}
	}
}

