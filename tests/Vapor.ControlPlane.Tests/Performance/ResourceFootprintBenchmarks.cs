using System.Diagnostics;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.ControlPlane.Tests.Performance;

/// <summary>
/// Resource-footprint baseline: managed allocations per job-store operation,
/// measured with <see cref="GC.GetTotalAllocatedBytes"/> around a full
/// create/claim/finish cycle. The absolute number includes test-harness noise;
/// its value is as a stable per-operation baseline to catch allocation
/// regressions (an order of magnitude, not a few percent).
///
/// Assertions use generous bounds so GC/runtime differences never flake.
/// </summary>
[Collection(AllocationBenchmarkCollection.Name)]
public class ResourceFootprintBenchmarks
{
	private const int OperationCount = 200;

	private readonly ITestOutputHelper _output;

	public ResourceFootprintBenchmarks(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public async Task JobStoreCycle_AllocationPerOperation()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

		// Warm-up cycle: JIT, connection pool, and schema migration allocate
		// heavily on first use and would poison the measurement.
		await RunCycleAsync(store, operationCount: 10, cts.Token);

		long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
		var sw = Stopwatch.StartNew();
		await RunCycleAsync(store, OperationCount, cts.Token);
		sw.Stop();
		long allocatedPerOperation = (GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore) / OperationCount;

		_output.WriteLine(
			$"job store create+claim+finish: {OperationCount} cycles in {sw.ElapsedMilliseconds} ms, {allocatedPerOperation:N0} bytes allocated per cycle");

		// Correctness first; allocation bound generous (local runs land in the
		// tens of KB per cycle) so runtime-version drift never flakes CI.
		Assert.True(allocatedPerOperation > 0, "allocation counter must advance");
		Assert.True(allocatedPerOperation < 200_000, $"allocations grew to {allocatedPerOperation:N0} bytes per cycle");
		Assert.True(sw.Elapsed.TotalSeconds < 60, $"took {sw.Elapsed.TotalSeconds:F1}s");
	}

	private static async Task RunCycleAsync(SqliteJobStore store, int operationCount, CancellationToken cancellationToken)
	{
		for (int i = 0; i < operationCount; i++)
		{
			await store.CreateJob(
				new CreateJobRequest("ping", "local", [$"acct-{i}"], null, null),
				cancellationToken);
		}

		while (await store.ClaimNextQueuedTask("local", cancellationToken) is { } task)
		{
			await store.SetTaskResult(
				new TaskResult(task.Id, true, null, null, DateTimeOffset.UtcNow, task.Attempt),
				cancellationToken);
		}
	}
}
