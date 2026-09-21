using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class RecurringJobSchedulerTests : IDisposable
{
	private readonly SqliteJobStore _store;
	private readonly ControlPlaneApiTests.RecordingEventBroker _events;
	private readonly RecurringJobScheduler _scheduler;

	public RecurringJobSchedulerTests()
	{
		_store = new SqliteJobStore(":memory:");
		_events = new ControlPlaneApiTests.RecordingEventBroker();
		_scheduler = new RecurringJobScheduler(_store, _events, NullLogger<RecurringJobScheduler>.Instance);
	}

	public void Dispose() => _store.Dispose();

	[Fact]
	public async Task ExecuteAsync_PreCanceledToken_UnwindsThroughTheTimerLoop()
	{
		// The host calls ExecuteAsync with the application lifetime token; a
		// pre-cancelled token drives the same unwind deterministically — control
		// reaches the loop call, the 1s timer's await throws immediately, and the
		// service exits with the cancellation.
		var execute = typeof(RecurringJobScheduler).GetMethod(
			"ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Assert.NotNull(execute);

		Task task = (Task)execute.Invoke(_scheduler, [new CancellationToken(canceled: true)])!;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
	}

	/// <summary>Truncates to millisecond precision — what SQLite persists.</summary>
	private static DateTimeOffset Ms(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

	private JobWithTasks CreateTemplate(JobSchedule schedule, string action = "ping", string[]? targets = null) =>
		_store.CreateJob(new CreateJobRequest(
			Action: action,
			Region: "us-east",
			Targets: targets ?? ["alice", "bob"],
			Payload: new Dictionary<string, object?> { ["minutes"] = 30 },
			Meta: new Dictionary<string, string> { ["owner"] = "ops" },
			Schedule: schedule), CancellationToken.None).GetAwaiter().GetResult();

	[Fact]
	public async Task CreateTemplate_HasScheduledStatusAndNoTasks()
	{
		JobWithTasks created = CreateTemplate(new JobSchedule(IntervalSeconds: 60));

		Assert.Equal(JobStatus.Scheduled, created.Job.Status);
		Assert.Empty(created.Tasks);
		Assert.Equal(60, created.Job.Schedule!.IntervalSeconds);
		Assert.NotNull(created.Job.NextRunAt);
		Assert.True(created.Job.NextRunAt > DateTimeOffset.UtcNow);

		JobWithTasks fetched = await _store.GetJob(created.Job.Id, CancellationToken.None);
		Assert.Equal(JobStatus.Scheduled, fetched.Job.Status);
		Assert.Empty(fetched.Tasks);
		Assert.Equal(60, fetched.Job.Schedule!.IntervalSeconds);
		Assert.Equal(Ms(created.Job.NextRunAt!.Value), fetched.Job.NextRunAt);
	}

	[Fact]
	public async Task CreateTemplate_RejectsInvalidSchedules()
	{
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null, new JobSchedule()), CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null, new JobSchedule(IntervalSeconds: 1)), CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null, new JobSchedule(IntervalSeconds: 60, Cron: "* * * * *")), CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null, new JobSchedule(Cron: "not a cron")), CancellationToken.None));
		// Feb 30 never exists — no future occurrence to schedule.
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null, new JobSchedule(Cron: "0 0 30 2 *")), CancellationToken.None));
	}

	[Fact]
	public async Task TickAsync_DueRowWithoutSchedule_SkipsTheCorruptRow()
	{
		// A due row whose schedule_json is gone (hand-edited / migrated DB) must be
		// left for manual inspection, never hot-looped into a child job. The shape
		// is unreachable through the store API, so the row is poked directly.
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			Job job;
			using (var store = new SqliteJobStore(dbPath))
			{
				JobWithTasks created = await store.CreateJob(
					new CreateJobRequest("ping", null, ["alice"], null, null), CancellationToken.None);
				job = created.Job;
			}

			long pastMs = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
			using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				raw.Open();
				using var cmd = raw.CreateCommand();
				cmd.CommandText = "UPDATE jobs SET status = 'Scheduled', schedule_next_run_ms = $due WHERE id = $id;";
				cmd.Parameters.AddWithValue("$due", pastMs);
				cmd.Parameters.AddWithValue("$id", job.Id);
				Assert.Equal(1, cmd.ExecuteNonQuery());
			}

			var events = new ControlPlaneApiTests.RecordingEventBroker();
			DateTimeOffset now = DateTimeOffset.UtcNow;
			using var schedulerStore = new SqliteJobStore(dbPath);
			using var scheduler = new RecurringJobScheduler(schedulerStore, events, NullLogger<RecurringJobScheduler>.Instance);
			scheduler.Clock = () => now;

			await scheduler.TickAsync(CancellationToken.None);

			Assert.Equal(0, scheduler.TriggeredRuns);
			Assert.Single(await schedulerStore.ListJobs(10, null, CancellationToken.None));
		}
		finally
		{
			File.Delete(dbPath);
		}
	}

	[Fact]
	public async Task DueTemplate_TriggersChildJobWithTemplateDefinition()
	{
		JobWithTasks template = CreateTemplate(new JobSchedule(IntervalSeconds: 60));
		DateTimeOffset firstDue = Ms(template.Job.NextRunAt!.Value);
		_scheduler.Clock = () => firstDue.AddSeconds(1);

		await _scheduler.TickAsync(CancellationToken.None);

		IReadOnlyList<Job> jobs = await _store.ListJobs(10, null, CancellationToken.None);
		Job child = Assert.Single(jobs, j => j.Id != template.Job.Id);
		Assert.Equal(JobStatus.Queued, child.Status);
		Assert.Equal("ping", child.Action);
		Assert.Equal("us-east", child.Region);
		Assert.Equal("ops", child.Meta!["owner"]);
		Assert.Equal(template.Job.Id, child.Meta["scheduledFrom"]);

		JobWithTasks withTasks = await _store.GetJob(child.Id, CancellationToken.None);
		Assert.Equal(new[] { "alice", "bob" }, withTasks.Tasks.Select(t => t.Target).ToArray());
		Assert.All(withTasks.Tasks, t => Assert.Equal(30, int.Parse(t.Payload!["minutes"]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture)));
		Assert.All(withTasks.Tasks, t => Assert.Equal(JobTaskStatus.Queued, t.Status));

		// Template advanced to the next trigger point and stays scheduled.
		Job reloaded = (await _store.GetJob(template.Job.Id, CancellationToken.None)).Job;
		Assert.Equal(JobStatus.Scheduled, reloaded.Status);
		Assert.Equal(firstDue.AddSeconds(60), reloaded.NextRunAt);

		Assert.Equal(1, _scheduler.TriggeredRuns);
		Event evt = Assert.Single(_events.Events, e => e.Type == "job.scheduled_triggered");
		Assert.Equal(template.Job.Id, evt.JobId);
		Assert.Equal(child.Id, evt.Payload!["jobId"]);
	}

	[Fact]
	public async Task NotDueTemplate_IsLeftAlone()
	{
		JobWithTasks template = CreateTemplate(new JobSchedule(IntervalSeconds: 3600));
		_scheduler.Clock = () => template.Job.CreatedAt;

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Empty(_events.Events);
	}

	[Fact]
	public async Task OverlapSkip_HoldsBackWhilePreviousRunIsActive()
	{
		JobWithTasks template = CreateTemplate(new JobSchedule(IntervalSeconds: 60));
		DateTimeOffset firstDue = Ms(template.Job.NextRunAt!.Value);
		_scheduler.Clock = () => firstDue.AddSeconds(1);
		await _scheduler.TickAsync(CancellationToken.None); // First run created, still queued.

		DateTimeOffset secondDue = firstDue.AddSeconds(60);
		_scheduler.Clock = () => secondDue.AddSeconds(1);
		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(1, _scheduler.TriggeredRuns);
		Assert.Equal(1, _scheduler.SkippedOverlaps);

		Job reloaded = (await _store.GetJob(template.Job.Id, CancellationToken.None)).Job;
		Assert.Equal(secondDue.AddSeconds(60), reloaded.NextRunAt); // Advanced, not lost.

		Event skipped = Assert.Single(_events.Events, e => e.Type == "job.scheduled_skipped");
		Assert.Equal("overlap", skipped.Payload!["reason"]);
	}

	[Fact]
	public async Task OverlapAllow_RunsInParallel()
	{
		JobWithTasks template = CreateTemplate(new JobSchedule(IntervalSeconds: 60, Overlap: ScheduleOverlapPolicy.Allow));
		DateTimeOffset firstDue = Ms(template.Job.NextRunAt!.Value);
		_scheduler.Clock = () => firstDue.AddSeconds(1);
		await _scheduler.TickAsync(CancellationToken.None);

		_scheduler.Clock = () => firstDue.AddSeconds(61);
		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(2, _scheduler.TriggeredRuns);
		Assert.Equal(0, _scheduler.SkippedOverlaps);
	}

	[Fact]
	public async Task MissedSkip_DropsMissedPointsAndStaysOnSchedule()
	{
		// interval=60s; jump 5 minutes ahead: 5 extra trigger points beyond the first due one.
		JobWithTasks template = CreateTemplate(new JobSchedule(IntervalSeconds: 60, Missed: ScheduleMissedPolicy.Skip));
		DateTimeOffset firstDue = Ms(template.Job.NextRunAt!.Value);
		_scheduler.Clock = () => firstDue.AddMinutes(5);

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, _scheduler.TriggeredRuns);
		Assert.Equal(5, _scheduler.MissedDropped);

		Job reloaded = (await _store.GetJob(template.Job.Id, CancellationToken.None)).Job;
		Assert.Equal(firstDue.AddMinutes(6), reloaded.NextRunAt);
		Assert.Equal(JobStatus.Scheduled, reloaded.Status); // Still armed for future points.

		Event skipped = Assert.Single(_events.Events, e => e.Type == "job.scheduled_skipped");
		Assert.Equal("missed", skipped.Payload!["reason"]);

		// The next trigger point fires normally again.
		_scheduler.Clock = () => firstDue.AddMinutes(6).AddSeconds(1);
		await _scheduler.TickAsync(CancellationToken.None);
		Assert.Equal(1, _scheduler.TriggeredRuns);
	}

	[Fact]
	public async Task MissedRunOnce_CatchesUpWithMarker()
	{
		JobWithTasks template = CreateTemplate(new JobSchedule(IntervalSeconds: 60, Missed: ScheduleMissedPolicy.RunOnce));
		DateTimeOffset firstDue = Ms(template.Job.NextRunAt!.Value);
		_scheduler.Clock = () => firstDue.AddMinutes(5);

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(1, _scheduler.TriggeredRuns);
		Assert.Equal(1, _scheduler.MissedCatchUps);

		IReadOnlyList<Job> jobs = await _store.ListJobs(10, null, CancellationToken.None);
		Job child = Assert.Single(jobs, j => j.Id != template.Job.Id);
		Assert.Equal("5", child.Meta!["scheduledMissedCount"]); // Five extra points beyond the current one.

		Job reloaded = (await _store.GetJob(template.Job.Id, CancellationToken.None)).Job;
		Assert.Equal(firstDue.AddMinutes(6), reloaded.NextRunAt);
	}

	[Fact]
	public async Task CanceledTemplate_StopsRecurring()
	{
		JobWithTasks template = CreateTemplate(new JobSchedule(IntervalSeconds: 60));
		await _store.CancelJob(template.Job.Id, CancellationToken.None);
		_scheduler.Clock = () => Ms(template.Job.NextRunAt!.Value).AddSeconds(1);

		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, _scheduler.TriggeredRuns);
		IReadOnlyList<Job> jobs = await _store.ListJobs(10, null, CancellationToken.None);
		Assert.Single(jobs); // Only the template itself remains.
		Assert.Equal(JobStatus.Canceled, jobs[0].Status);
	}

	[Fact]
	public async Task CronTemplate_TriggersAtExpressionPoints()
	{
		// Every minute at second zero (UTC).
		JobWithTasks template = CreateTemplate(new JobSchedule(Cron: "* * * * *"), targets: ["carol"]);
		DateTimeOffset firstPoint = Ms(template.Job.NextRunAt!.Value);
		Assert.Equal(0, firstPoint.Second);

		_scheduler.Clock = () => firstPoint.AddSeconds(1);
		await _scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(1, _scheduler.TriggeredRuns);
		Job reloaded = (await _store.GetJob(template.Job.Id, CancellationToken.None)).Job;
		Assert.Equal(firstPoint.AddMinutes(1), reloaded.NextRunAt);

		IReadOnlyList<Job> jobs = await _store.ListJobs(10, null, CancellationToken.None);
		Job child = Assert.Single(jobs, j => j.Id != template.Job.Id);
		Assert.Equal(new[] { "carol" }, (await _store.GetJob(child.Id, CancellationToken.None)).Tasks.Select(t => t.Target).ToArray());
	}
}

/// <summary>
/// Retire/edge passes that need store states the real <see cref="SqliteJobStore"/> cannot
/// express: a trigger rejected mid-flight (concurrent cancel) and a due template whose
/// cron never matches again. The template rows are injected through a minimal fake store.
/// </summary>
public sealed class RecurringJobSchedulerRetireTests
{
	private readonly RetiringJobStore _store = new();
	private readonly ControlPlaneApiTests.RecordingEventBroker _events = new();

	[Fact]
	public async Task TickAsync_TriggerRejectedMidFlight_SkipsPublishWithoutCountingRun()
	{
		// TriggerScheduledJob returns null when the template is canceled concurrently
		// between listing and triggering: the pass must stay silent (no event, no count).
		var scheduler = new RecurringJobScheduler(_store, _events, NullLogger<RecurringJobScheduler>.Instance);
		DateTimeOffset due = DateTimeOffset.UtcNow.AddSeconds(-1);
		_store.Templates.Add(MakeTemplate(new JobSchedule(IntervalSeconds: 60), due));

		await scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, scheduler.TriggeredRuns);
		Assert.Empty(_events.Events);
		Assert.Empty(_store.CancelledJobIds); // Not a retirement — nothing was cancelled.
	}

	[Fact]
	public async Task TickAsync_CronNeverMatchesAgain_RetiresTemplateWithCompletedEvent()
	{
		// A due template whose cron can never match again (Feb 30) has no future trigger
		// point and no missed chain: the pass retires it (cancel + schedule_exhausted event).
		var scheduler = new RecurringJobScheduler(_store, _events, NullLogger<RecurringJobScheduler>.Instance);
		DateTimeOffset due = DateTimeOffset.UtcNow.AddSeconds(-1);
		Job template = MakeTemplate(new JobSchedule(Cron: "0 0 30 2 *"), due);
		_store.Templates.Add(template);

		await scheduler.TickAsync(CancellationToken.None);

		Assert.Equal(0, scheduler.TriggeredRuns);
		Assert.Equal(new[] { template.Id }, _store.CancelledJobIds);
		Event completed = Assert.Single(_events.Events);
		Assert.Equal("job.scheduled_completed", completed.Type);
		Assert.Equal(template.Id, completed.JobId);
		Assert.Equal("schedule_exhausted", completed.Payload!["reason"]);
	}

	private static Job MakeTemplate(JobSchedule schedule, DateTimeOffset nextRunAt) =>
		new(
			Id: "template-1",
			Action: "ping",
			Region: "us-east",
			Targets: ["alice"],
			Meta: null,
			Status: JobStatus.Scheduled,
			CreatedAt: nextRunAt.AddSeconds(-60),
			UpdatedAt: nextRunAt.AddSeconds(-60),
			Schedule: schedule,
			NextRunAt: nextRunAt);

	/// <summary>Minimal store: lists injected due templates, records cancels, rejects triggers.</summary>
	private sealed class RetiringJobStore : IJobStore
	{
		public List<Job> Templates { get; } = [];
		public List<string> CancelledJobIds { get; } = [];
		/// <summary>Set on the first due-listing call — the hosting loop's deterministic tick signal.</summary>
		public TaskCompletionSource ListedDue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int DueListCalls;

		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref DueListCalls);
			ListedDue.TrySetResult();
			return Task.FromResult<IReadOnlyList<Job>>(Templates);
		}

		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken)
		{
			CancelledJobIds.Add(jobId);
			return Task.FromResult<IReadOnlyList<TaskCancel>>([]);
		}

		/// <summary>Mirrors a concurrent cancel: the guarded advance finds nothing to trigger.</summary>
		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) =>
			Task.FromResult<Job?>(null);

		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(true);

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	[Fact]
	public async Task StartStop_RunsAtLeastOneSchedulerTick()
	{
		// The scheduler's PeriodicTimer fires every second; the tick's store read is
		// the deterministic signal that the ExecuteAsync loop body actually ran (a
		// fixed delay would race the first tick on a loaded CI runner).
		var scheduler = new RecurringJobScheduler(_store, _events, NullLogger<RecurringJobScheduler>.Instance);

		await scheduler.StartAsync(CancellationToken.None);
		await _store.ListedDue.Task.WaitAsync(TimeSpan.FromSeconds(30));
		await scheduler.StopAsync(CancellationToken.None);

		Assert.True(_store.DueListCalls >= 1);
		Assert.True(scheduler.ExecuteTask?.IsCompleted); // clean stop, no fault
	}

	[Fact]
	public async Task TickAsync_ExhaustedCronSchedule_RetiresTemplate()
	{
		// A due template whose cron never matches again (Feb 30) has no future trigger
		// point and no missed chain: the pass must retire (cancel) the template instead
		// of hot-looping on it. CreateJob validation refuses never-matching crons up
		// front, so the row starts valid and its schedule is rewritten through the
		// store's own connection — the state a bounded schedule eventually reaches.
		using var store = new SqliteJobStore(":memory:");
		var scheduler = new RecurringJobScheduler(
			store, new ControlPlaneApiTests.RecordingEventBroker(), NullLogger<RecurringJobScheduler>.Instance);

		JobWithTasks template = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null, new JobSchedule(Cron: "* * * * *")),
			CancellationToken.None);
		await store.AdvanceSchedule(
			template.Job.Id, DateTimeOffset.UtcNow.AddSeconds(-30), CancellationToken.None);
		RewriteScheduleCron(store, template.Job.Id, "0 0 30 2 *");

		await scheduler.TickAsync(CancellationToken.None);

		JobWithTasks retired = await store.GetJob(template.Job.Id, CancellationToken.None);
		Assert.Equal(JobStatus.Canceled, retired.Job.Status);
	}

	private static void RewriteScheduleCron(SqliteJobStore store, string jobId, string cron)
	{
		FieldInfo field = typeof(SqliteJobStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("Missing store connection field.");
		var connection = (Microsoft.Data.Sqlite.SqliteConnection?)field.GetValue(store);
		Assert.NotNull(connection);

		using var cmd = connection!.CreateCommand();
		cmd.CommandText = "UPDATE jobs SET schedule_json = $json WHERE id = $id;";
		cmd.Parameters.AddWithValue(
			"$json", System.Text.Json.JsonSerializer.Serialize(new JobSchedule(Cron: cron), JsonDefaults.Options));
		cmd.Parameters.AddWithValue("$id", jobId);
		Assert.Equal(1, cmd.ExecuteNonQuery());
	}
}
