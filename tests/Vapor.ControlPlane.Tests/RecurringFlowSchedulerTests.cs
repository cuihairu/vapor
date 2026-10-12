using System.Net.WebSockets;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// Shares the AccountTaskWaitWindow collection: these tests tune the static
// AccountTaskRunner.WaitWindow / PollInterval knobs while flows dispatch.
[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed class RecurringFlowSchedulerTests : IDisposable
{
	private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vapor-flow-sched-{Guid.NewGuid():N}.db");
	private readonly SqliteScriptStore _scripts;
	private readonly SqliteFlowStore _flows;
	private readonly SqliteJobStore _store;
	private readonly AgentRegistry _agents;
	private readonly ScriptFlowRunner _runner;
	private readonly RecurringFlowScheduler _scheduler;
	private readonly RecordingAuditStore _runnerAudit = new();
	private readonly RecordingAuditStore _schedulerAudit = new();

	public RecurringFlowSchedulerTests()
	{
		// Scripts and flows share one file database so the runner's pre-flight
		// finds scripts created directly through the store.
		_scripts = new SqliteScriptStore(_dbPath);
		_flows = new SqliteFlowStore(_dbPath);
		_store = new SqliteJobStore(":memory:");
		_agents = new AgentRegistry();
		_runner = new ScriptFlowRunner(_scripts, _flows, _store, _agents, _runnerAudit, NullLogger<ScriptFlowRunner>.Instance);
		_scheduler = new RecurringFlowScheduler(_flows, _runner, _schedulerAudit, NullLogger<RecurringFlowScheduler>.Instance);
	}

	public void Dispose()
	{
		_flows.Dispose();
		_scripts.Dispose();
		_store.Dispose();
		SqliteConnection.ClearAllPools();
		try
		{
			File.Delete(_dbPath);
		}
		catch (IOException)
		{
			// Windows pooling may still hold the file; best-effort cleanup.
		}
	}

	/// <summary>Forwards to an in-memory list so tests can assert the audit trail.</summary>
	private sealed class RecordingAuditStore : IAuditStore
	{
		public List<AuditEntry> Entries { get; } = [];

		/// <summary>When set, every record throws: the audit callers must swallow it.</summary>
		public Exception? ThrowOnRecord { get; set; }

		public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
		{
			if (ThrowOnRecord is not null)
			{
				return Task.FromException(ThrowOnRecord);
			}

			Entries.Add(entry);
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
			=> Task.FromResult<IReadOnlyList<AuditEntry>>(Entries);

		public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult(Entries.Count);
	}

	[Fact]
	public async Task ExecuteAsync_PreCanceledToken_UnwindsThroughTheTimerLoop()
	{
		var execute = typeof(RecurringFlowScheduler).GetMethod(
			"ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Assert.NotNull(execute);

		Task task = (Task)execute.Invoke(_scheduler, [new CancellationToken(canceled: true)])!;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
	}

	/// <summary>Arms a one-step flow (script-1 on agent-1) due at the given instant.</summary>
	private async Task<ScriptFlowRecord> ArmFlowAsync(JobSchedule schedule, DateTimeOffset dueAt, string flowId = "flow-1")
	{
		await _scripts.UpsertAsync(new ScriptRecord(
			Id: "script-1",
			Name: "step",
			Description: "",
			Language: "shell",
			Content: "echo",
			CreatedAtMs: 1,
			UpdatedAtMs: 1));
		RegisterAgent(_agents, "agent-1");

		var flow = new ScriptFlowRecord(
			Id: flowId,
			Name: "scheduled flow",
			Description: "",
			Steps: [new FlowStep("script-1", "agent-1", "stop")],
			CreatedAtMs: 1,
			UpdatedAtMs: 1,
			Schedule: schedule,
			NextRunAtMs: dueAt.ToUnixTimeMilliseconds());
		await _flows.UpsertFlowAsync(flow);
		return flow;
	}

	/// <summary>Plays the agent side: finishes every queued script_exec with a scripted outcome.</summary>
	private async Task RespondToScriptExecTasksAsync(CancellationToken ct)
	{
		int index = 0;
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await _store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == "script_exec")
			{
				// Every step succeeds; step outputs carry the step index so the
				// completed run can be told apart from an empty one.
				await _store.SetTaskResult(
					new TaskResult(
						claimed.Id,
						true,
						null,
						new Dictionary<string, object?> { ["exitCode"] = 0, ["step"] = index++ },
						DateTimeOffset.UtcNow),
					ct);
				continue;
			}

			await Task.Delay(10, ct);
		}
	}

	/// <summary>Shrinks the dispatch wait knobs and runs one tick with a live agent responder.</summary>
	private async Task TickWithResponderAsync()
	{
		TimeSpan savedWindow = AccountTaskRunner.WaitWindow;
		TimeSpan savedInterval = AccountTaskRunner.PollInterval;
		try
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(5);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);

			using var cts = new CancellationTokenSource();
			Task responder = Task.Run(() => RespondToScriptExecTasksAsync(cts.Token), cts.Token);
			await _scheduler.TickAsync(CancellationToken.None);
			await cts.CancelAsync();
		}
		finally
		{
			AccountTaskRunner.WaitWindow = savedWindow;
			AccountTaskRunner.PollInterval = savedInterval;
		}
	}

	private static void RegisterAgent(AgentRegistry registry, string agentId) =>
		registry.Register(
			new AgentHello(agentId, "us-east", new Dictionary<string, bool> { ["script_exec"] = true }, null),
			new NoopWebSocket(),
			new CancellationTokenSource().Token);

	private sealed class NoopWebSocket : WebSocket
	{
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override void Dispose()
		{
		}
		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			Task.FromCanceled<WebSocketReceiveResult>(cancellationToken);
		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	[Fact]
	public async Task TickAsync_DueFlow_FiresCompletesAndAdvances()
	{
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);

		await TickWithResponderAsync();

		Assert.Equal(1, _scheduler.TriggeredRuns);
		List<ScriptFlowRunRecord> runs = await _flows.ListRunsAsync(flow.Id);
		ScriptFlowRunRecord run = Assert.Single(runs);
		Assert.Equal("completed", run.Status);
		Assert.Equal("succeeded", Assert.Single(run.Steps).Status);
		Assert.NotNull(run.FinishedAtMs);

		// Cursor moved to the next interval point; the schedule stays armed.
		ScriptFlowRecord reloaded = (await _flows.GetFlowAsync(flow.Id))!;
		Assert.Equal(61_000, reloaded.NextRunAtMs);

		// The run's audit trail is written under the scheduler actor.
		AuditEntry started = Assert.Single(_runnerAudit.Entries, e => e.Action == "flow_run_started");
		Assert.Equal("scheduler", started.Actor);
	}

	[Fact]
	public async Task TickAsync_NotDueFlow_IsLeftAlone()
	{
		await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(10_000));
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(5_000);

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Empty(await _flows.ListRunsAsync("flow-1"));
		Assert.Empty(_runnerAudit.Entries);
	}

	[Fact]
	public async Task TickAsync_MissedSkip_DropsTheGapAndStaysOnSchedule()
	{
		// interval=60s; jump 5 minutes past the due point: 5 extra trigger points
		// beyond the due one are dropped by the missed=skip policy.
		ScriptFlowRecord flow = await ArmFlowAsync(
			new JobSchedule(IntervalSeconds: 60, Missed: ScheduleMissedPolicy.Skip), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1000 + 5 * 60_000);

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Equal(5, _scheduler.MissedDropped);
		Assert.Empty(await _flows.ListRunsAsync(flow.Id));
		Assert.Equal(1000 + 6 * 60_000, (await _flows.GetFlowAsync(flow.Id))!.NextRunAtMs);

		AuditEntry skipped = Assert.Single(_schedulerAudit.Entries, e => e.Action == "flow_schedule_skipped");
		Assert.Equal("missed", skipped.Details!["reason"]);
		Assert.Equal(5, skipped.Details!["missedCount"]);

		// The next trigger point fires normally again.
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1000 + 6 * 60_000 + 1);
		await TickWithResponderAsync();
		Assert.Equal(1, _scheduler.TriggeredRuns);
	}

	[Fact]
	public async Task TickAsync_MissedRunOnce_CatchesUpWithOneRun()
	{
		ScriptFlowRecord flow = await ArmFlowAsync(
			new JobSchedule(IntervalSeconds: 60, Missed: ScheduleMissedPolicy.RunOnce), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1000 + 5 * 60_000);

		await TickWithResponderAsync();

		// The catch-up is counted on the miss path, not as an on-time trigger.
		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Equal(1, _scheduler.MissedCatchUps);
		ScriptFlowRunRecord run = Assert.Single(await _flows.ListRunsAsync(flow.Id));
		Assert.Equal("completed", run.Status);
		Assert.Equal(1000 + 6 * 60_000, (await _flows.GetFlowAsync(flow.Id))!.NextRunAtMs);
	}

	[Fact]
	public async Task TickAsync_OverlapSkip_DefersWhileARunIsActive()
	{
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		// A previous run still walking: created directly, the scheduler sees it
		// through HasActiveFlowRunAsync.
		await _flows.CreateRunAsync(new ScriptFlowRunRecord(
			Id: "run-live",
			FlowId: flow.Id,
			FlowName: flow.Name,
			Status: "running",
			Steps: [new FlowRunStep(0, "script-1", "agent-1", "pending")],
			StartedAtMs: 900));

		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);
		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Equal(1, _scheduler.SkippedOverlaps);
		Assert.Single(await _flows.ListRunsAsync(flow.Id)); // No new run was created.
		Assert.Equal(61_000, (await _flows.GetFlowAsync(flow.Id))!.NextRunAtMs); // Advanced, not lost.

		AuditEntry skipped = Assert.Single(_schedulerAudit.Entries, e => e.Action == "flow_schedule_skipped");
		Assert.Equal("overlap", skipped.Details!["reason"]);
	}

	[Fact]
	public async Task TickAsync_OverlapAllow_RunsDespiteActiveRun()
	{
		ScriptFlowRecord flow = await ArmFlowAsync(
			new JobSchedule(IntervalSeconds: 60, Overlap: ScheduleOverlapPolicy.Allow), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		await _flows.CreateRunAsync(new ScriptFlowRunRecord(
			Id: "run-live",
			FlowId: flow.Id,
			FlowName: flow.Name,
			Status: "running",
			Steps: [new FlowRunStep(0, "script-1", "agent-1", "pending")],
			StartedAtMs: 900));

		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);
		await TickWithResponderAsync();

		Assert.Equal(1, _scheduler.TriggeredRuns);
		Assert.Equal(0, _scheduler.SkippedOverlaps);
		Assert.Equal(2, (await _flows.ListRunsAsync(flow.Id)).Count);
	}

	[Fact]
	public async Task TickAsync_PreflightRefusal_AuditsSkipWithoutCountingARun()
	{
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		await _scripts.DeleteAsync("script-1"); // Script vanished after arming.
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Empty(await _flows.ListRunsAsync(flow.Id));
		// The cursor still advanced past the dead point: the schedule must not
		// hot-loop on a refusal it can never satisfy.
		Assert.Equal(61_000, (await _flows.GetFlowAsync(flow.Id))!.NextRunAtMs);

		AuditEntry skipped = Assert.Single(_schedulerAudit.Entries, e => e.Action == "flow_schedule_skipped");
		Assert.Equal("preflight", skipped.Details!["reason"]);
		Assert.Contains("does not exist", (string)skipped.Details!["error"]!);
	}

	[Fact]
	public async Task TickAsync_ExhaustedCronSchedule_DisarmsTheFlow()
	{
		// A due flow whose cron never matches again (Feb 30) has no future trigger
		// point: the pass disarms it back to manual-only instead of hot-looping on
		// a dead cursor. The schedule is rewritten after arming because validation
		// refuses never-matching crons up front — the state a bounded schedule
		// eventually reaches.
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(Cron: "* * * * *"), DateTimeOffset.UtcNow.AddSeconds(-30));
		await _flows.AdvanceFlowScheduleAsync(flow.Id, DateTimeOffset.UtcNow.AddSeconds(-30));
		RewriteScheduleCron(_flows, flow.Id, "0 0 30 2 *");

		await _scheduler.TickAsync(CancellationToken.None);

		ScriptFlowRecord disarmed = (await _flows.GetFlowAsync(flow.Id))!;
		Assert.Null(disarmed.Schedule);
		Assert.Null(disarmed.NextRunAtMs);
		Assert.Equal(0, _scheduler.TriggeredRuns);

		AuditEntry completed = Assert.Single(_schedulerAudit.Entries, e => e.Action == "flow_schedule_completed");
		Assert.Equal("schedule_exhausted", completed.Details!["reason"]);
	}

	[Fact]
	public async Task TickAsync_AuditFailureOnTheSkipPath_DoesNotBreakTheTick()
	{
		// The scheduler's audit persistence is best-effort: with the store down,
		// the overlap skip still advances the cursor and the tick still returns.
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		await _flows.CreateRunAsync(new ScriptFlowRunRecord(
			Id: "run-live",
			FlowId: flow.Id,
			FlowName: flow.Name,
			Status: "running",
			Steps: [new FlowRunStep(0, "script-1", "agent-1", "pending")],
			StartedAtMs: 900));
		_schedulerAudit.ThrowOnRecord = new InvalidOperationException("audit store is down");
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(1, _scheduler.SkippedOverlaps);
		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Equal(61_000, (await _flows.GetFlowAsync(flow.Id))!.NextRunAtMs);
	}

	[Fact]
	public async Task TickAsync_AuditFailureInsideTheRun_CompletesAnyway()
	{
		// The runner shares the best-effort guarantee: every audit entry of the
		// scheduled run fails, yet the run walks to completion and counts.
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		_runnerAudit.ThrowOnRecord = new InvalidOperationException("audit store is down");
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);

		await TickWithResponderAsync();

		Assert.Equal(1, _scheduler.TriggeredRuns);
		ScriptFlowRunRecord run = Assert.Single(await _flows.ListRunsAsync(flow.Id));
		Assert.Equal("completed", run.Status);
		Assert.NotNull(run.FinishedAtMs);
	}

	[Fact]
	public async Task TickAsync_AuditCancellation_Propagates()
	{
		// Cancellation is not persistence failure: the OCE rethrow keeps the
		// shutdown path responsive instead of logging a cancelled audit away.
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		await _flows.CreateRunAsync(new ScriptFlowRunRecord(
			Id: "run-live",
			FlowId: flow.Id,
			FlowName: flow.Name,
			Status: "running",
			Steps: [new FlowRunStep(0, "script-1", "agent-1", "pending")],
			StartedAtMs: 900));
		_schedulerAudit.ThrowOnRecord = new OperationCanceledException();
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scheduler.TickAsync(CancellationToken.None));
	}

	[Fact]
	public async Task RunAuditCancellation_PropagatesThroughTheTick()
	{
		ScriptFlowRecord flow = await ArmFlowAsync(new JobSchedule(IntervalSeconds: 60), DateTimeOffset.FromUnixTimeMilliseconds(1000));
		_runnerAudit.ThrowOnRecord = new OperationCanceledException();
		_scheduler.Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1001);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _scheduler.TickAsync(CancellationToken.None));
	}

	private static void RewriteScheduleCron(SqliteFlowStore store, string flowId, string cron)
	{
		FieldInfo field = typeof(SqliteFlowStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Missing store connection field.");
		var connection = (SqliteConnection?)field.GetValue(store);
		Assert.NotNull(connection);

		using var cmd = connection!.CreateCommand();
		cmd.CommandText = "UPDATE script_flows SET schedule = $json WHERE id = $id;";
		cmd.Parameters.AddWithValue(
			"$json", System.Text.Json.JsonSerializer.Serialize(new JobSchedule(Cron: cron), JsonDefaults.Options));
		cmd.Parameters.AddWithValue("$id", flowId);
		Assert.Equal(1, cmd.ExecuteNonQuery());
	}

	[Fact]
	public async Task StartStop_RunsAtLeastOneSchedulerTick()
	{
		// The scheduler's PeriodicTimer fires every second; the tick's clock read
		// is the deterministic signal that the ExecuteAsync loop body actually ran
		// (a fixed delay would race the first tick on a loaded CI runner).
		int ticks = 0;
		_scheduler.Clock = () =>
		{
			Interlocked.Increment(ref ticks);
			return DateTimeOffset.UtcNow;
		};

		await _scheduler.StartAsync(CancellationToken.None);
		Stopwatch deadline = Stopwatch.StartNew();
		while (Volatile.Read(ref ticks) == 0 && deadline.Elapsed < TimeSpan.FromSeconds(30))
		{
			await Task.Delay(25);
		}

		await _scheduler.StopAsync(CancellationToken.None);

		Assert.True(Volatile.Read(ref ticks) >= 1);
		Assert.True(_scheduler.ExecuteTask?.IsCompleted); // clean stop, no fault
	}
}
