using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Failure-path tests: run timeout cancellation, stop-phase cancellation propagation, read/dispatch/audit failures and broken-tick resilience.</summary>
public sealed partial class CrawlRunWorkerTests
{
	[Fact]
	public async Task TimedOutRun_CancelsOutstandingJobsAndRecordsFailures()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730 });

		var worker = CreateWorker(runTimeoutSeconds: 0); // deadline already lapsed on the next tick
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		string jobId = _jobs.CreatedJobs.Single().Job.Id;

		await worker.RunTickAsync(CancellationToken.None); // poll → timeout path

		Assert.Contains(jobId, _jobs.Cancelled);
		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		Assert.All(rows, r => { Assert.False(r.Ok); Assert.Equal("crawl run timed out", r.Error); });
		Assert.Equal(1, worker.RunsTimedOut);

		var completed = Assert.Single(_events.Published, e => e.Type == "crawl.run_completed");
		Assert.Equal("timeout", completed.Payload!["status"]);
	}

	[Fact]
	public async Task ReadJobCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.GetJobExceptions.Enqueue(new OperationCanceledException());
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// The poll path rethrows cancellation so the hosting loop can shut down.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(cts.Token));
	}

	[Fact]
	public async Task ReadJobFailure_IsSwallowedAndRunStaysInFlight()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.GetJobExceptions.Enqueue(new IOException("store hiccup"));

		await worker.RunTickAsync(CancellationToken.None); // poll → unreadable job stays in flight

		Assert.Equal(0, worker.RunsCompleted);
		Assert.Empty(await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1")));

		// The run settles normally once the job store is readable again.
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 });
		await worker.RunTickAsync(CancellationToken.None);
		Assert.Equal(1, worker.RunsCompleted);
	}

	[Fact]
	public async Task TimedOutRun_WhenCancelItselfFails_StillRecordsShardFailures()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570, 730 });

		var worker = CreateWorker(runTimeoutSeconds: 0);
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.CancelThrows = true;

		await worker.RunTickAsync(CancellationToken.None); // poll → timeout path, CancelJob throws

		// The cancel failure is logged and swallowed; the shard still records failures.
		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		Assert.All(rows, r => Assert.Equal("crawl run timed out", r.Error));
		Assert.Equal(1, worker.RunsTimedOut);
	}

	[Fact]
	public async Task TimedOutRun_WhenCancelIsCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker(runTimeoutSeconds: 0);
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		_jobs.CancelCanceledThrows = true;
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Cancellation escaping CancelJob must not be mistaken for a store
		// failure (which would swallow it) — it rethrows so the loop can stop.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(cts.Token));
	}

	[Fact]
	public async Task DispatchCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		_jobs.ThrowCanceledOnCreate = true;
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var worker = CreateWorker();

		// Dispatch cancellation must escape the generic dispatch-failure handler
		// so the hosting loop breaks instead of bookkeeping a failed run.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(cts.Token));
	}

	[Fact]
	public async Task AuditFailure_NeverBlocksTheRun()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		_audit.ThrowOnRecord = true;

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);

		// Dispatch succeeded even though the audit write failed.
		Assert.Equal(1, worker.RunsTriggered);
		Assert.Single(_jobs.Created);
	}

	[Fact]
	public async Task AuditCanceledWhileStopping_PropagatesCancellation()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });
		_audit.ThrowCanceledOnRecord = true;

		var worker = CreateWorker();

		// Cancellation escaping the audit write must not be mistaken for an audit
		// failure (which would swallow it) — it rethrows so the loop can stop.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunTickAsync(CancellationToken.None));
	}

	[Fact]
	public async Task BrokenTick_LogsErrorAndKeepsServing()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		// Real hosting loop: a tick that throws must be logged, not kill the service.
		var worker = CreateWorker(tickSeconds: 1);
		await worker.StartAsync(CancellationToken.None);
		try
		{
			// Wait for the *published event*, not the created job: the job exists before
			// its run event is published, and arming ThrowOnPublishRuns while tick 1's
			// publish is still in flight would break the wrong tick's event instead.
			await WaitUntilAsync(() => RunEventsPublished() == 1); // tick 1: dispatch

			string jobId = _jobs.CreatedJobs[0].Job.Id;
			_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
			_jobs.Outputs[jobId] = BatchOutput(new uint[] { 570 });
			_events.ThrowOnPublishRuns = 1; // tick 2: completion event blows up mid-tick

			// Tick 2 fails inside CompleteRunAsync (publish throws); the cursor was never
			// settled, so tick 3 claims the still-due plan again — proof the loop survived.
			// Waiting on the second run event (not Created.Count) is what makes the
			// Contains assertion below race-free: the job is created before its event
			// is published, so Created.Count == 2 can fire mid-publish (CI flake,
			// macos Debug 2026-09-17: the assertion ran against a one-event list).
			await WaitUntilAsync(() => RunEventsPublished() == 2);

			Assert.Equal(2, worker.RunsTriggered);
			Assert.Contains(_events.Published, e => e.Type == "crawl.run_triggered"
				&& Equals(e.Payload!["run_id"], _jobs.Created[1].Meta!["crawl_run_id"]));
		}
		finally
		{
			await worker.StopAsync(CancellationToken.None);
		}

		// Reaching tick 3 already proves the loop survived the broken tick (an escaping
		// tick failure would have faulted ExecuteTask and stopped all further ticks).
		Assert.NotNull(worker.ExecuteTask);
		Assert.True(worker.ExecuteTask!.IsCompleted);
	}

	[Fact]
	public async Task StopRequestedWhileDispatchInFlight_BreaksLoopQuietly()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		// The dispatch call parks in the gate; stop is requested, then the gate opens
		// so CreateJob throws cancellation on an already-cancelled stopping token —
		// the loop must break through the OperationCanceledException arm, not the
		// generic one (which would keep the service running past shutdown).
		var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var openGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_jobs.CreateGate = _ =>
		{
			createEntered.TrySetResult();
			return openGate.Task;
		};
		_jobs.ThrowCanceledOnCreate = true;

		var worker = CreateWorker(tickSeconds: 1);
		await worker.StartAsync(CancellationToken.None);
		await createEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // tick 1 parked in dispatch

		Task stopTask = worker.StopAsync(CancellationToken.None); // cancels the stopping token
		openGate.TrySetResult();

		await stopTask.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.NotNull(worker.ExecuteTask);
		Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully); // broke out, did not fault
		Assert.Equal(0, worker.RunsCompleted); // the aborted run never settled
	}

}
