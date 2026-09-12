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
