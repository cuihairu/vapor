using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Worker-control tests: tick kill-switch completion and zero-tick config disabling the worker.</summary>
public sealed partial class CrawlRunWorkerTests
{
	[Fact]
	public async Task Worker_WithTickKillSwitch_CompletesWithoutScheduling()
	{
		// 0 or below is the documented kill switch: ExecuteAsync returns instead
		// of entering the tick loop, so the hosted task settles almost instantly.
		CrawlRunWorker worker = CreateWorker(tickSeconds: 0);

		await worker.StartAsync(CancellationToken.None);
		if (worker.ExecuteTask is not null)
		{
			await worker.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
		}

		Assert.Equal(0, worker.RunsTriggered);
		Assert.Equal(0, worker.TasksDispatched);
	}

	[Fact]
	public async Task ZeroTickConfig_DisablesTheWorker()
	{
		var worker = CreateWorker(tickSeconds: 0);
		await SeedOneShotPlanAsync();

		await worker.StartAsync(CancellationToken.None);
		await worker.StopAsync(CancellationToken.None);

		Assert.Empty(_jobs.Created); // loop never ran
	}

}
