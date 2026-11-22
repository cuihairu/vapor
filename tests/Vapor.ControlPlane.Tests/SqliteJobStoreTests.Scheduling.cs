using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Scheduled-template tests: null/blank region collapse, unknown template, advance and post-schedule transitions.</summary>
public sealed partial class SqliteJobStoreTests
{
	[Fact]
	public async Task TriggerScheduledJob_NullJsonColumns_ChildCollapsesToEmptyCollections()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		JobWithTasks template = await store.CreateJob(
			new CreateJobRequest("ping", "us-east", ["alice"], null, null, new JobSchedule(IntervalSeconds: 60)),
			cts.Token);

		// JSON 'null' in the template row drives every Deserialize to null — the store
		// must fall back to empty collections instead of handing nulls to Job.
		RawExec(store,
			"UPDATE jobs SET targets_json = $t, meta_json = $m, payload_json = $p WHERE id = $id",
			("$t", "null"), ("$m", "null"), ("$p", "null"), ("$id", template.Job.Id));

		Job? child = await store.TriggerScheduledJob(
			template.Job.Id, DateTimeOffset.UtcNow.AddSeconds(60), extraMeta: null, cts.Token);

		Assert.NotNull(child);
		Assert.NotNull(child!.Targets);
		Assert.Empty(child.Targets);
		// The null template meta falls back to empty, then the trigger merges in its own marker.
		Assert.NotNull(child.Meta);
		Assert.Equal(new[] { "scheduledFrom" }, child.Meta!.Keys.ToArray());
		Assert.Equal("us-east", child.Region);
	}

	[Fact]
	public async Task TriggerScheduledJob_BlankTemplateRegion_ChildRegionIsNull()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		JobWithTasks template = await store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null, new JobSchedule(IntervalSeconds: 60)),
			cts.Token);

		Job? child = await store.TriggerScheduledJob(
			template.Job.Id, DateTimeOffset.UtcNow.AddSeconds(60), extraMeta: null, cts.Token);

		Assert.NotNull(child);
		Assert.Null(child!.Region);
		Assert.Equal(new[] { "alice" }, child.Targets);
	}

	[Fact]
	public async Task TriggerScheduledJob_UnknownTemplate_ReturnsNull()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Job? triggered = await store.TriggerScheduledJob(
			"does-not-exist",
			DateTimeOffset.UtcNow.AddMinutes(1),
			extraMeta: null,
			cts.Token);

		Assert.Null(triggered);
	}

	[Fact]
	public async Task TriggerScheduledJob_AdvanceTouchesNothing_ReturnsNull()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-jobs-{Guid.NewGuid():N}.db");
		try
		{
			using var store = new SqliteJobStore(dbPath);
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			JobWithTasks template = await store.CreateJob(
				new CreateJobRequest("ping", null, ["alice"], null, null, new JobSchedule(IntervalSeconds: 60)),
				cts.Token);

			using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				raw.Open();
				using var cmd = raw.CreateCommand();
				cmd.CommandText =
					$"CREATE TRIGGER advance_sabotage BEFORE UPDATE ON jobs WHEN NEW.id = '{template.Job.Id}' " +
					"BEGIN SELECT RAISE(IGNORE); END;";
				cmd.ExecuteNonQuery();
			}

			Job? triggered = await store.TriggerScheduledJob(
				template.Job.Id, DateTimeOffset.UtcNow.AddSeconds(60), extraMeta: null, cts.Token);

			Assert.Null(triggered);
		}
		finally
		{
			try
			{
				File.Delete(dbPath);
			}
			catch (IOException)
			{
				// Microsoft.Data.Sqlite pooling may still hold the file on Windows; best-effort cleanup.
			}
		}
	}

	[Fact]
	public async Task AdvanceSchedule_AfterTemplateLeftScheduledState_ReturnsFalse()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks template = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null, new JobSchedule(IntervalSeconds: 60)),
			cts.Token);
		await store.CancelJob(template.Job.Id, cts.Token);

		// The guard clause only advances templates still in the Scheduled state; a
		// concurrently canceled template must report the lost race instead of writing.
		bool advanced = await store.AdvanceSchedule(
			template.Job.Id, DateTimeOffset.UtcNow.AddMinutes(1), cts.Token);

		Assert.False(advanced);
	}

	[Fact]
	public async Task TriggerScheduledJob_AfterTemplateLeftScheduledState_ReturnsNull()
	{
		// The guarded advance inside TriggerScheduledJob only fires for templates still
		// Scheduled; a canceled template must come back null so the scheduler stays
		// silent instead of spawning a child for a dead template.
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		JobWithTasks template = await store.CreateJob(
			new CreateJobRequest("ping", "local", ["acct-1"], null, null, new JobSchedule(IntervalSeconds: 60)),
			cts.Token);
		await store.CancelJob(template.Job.Id, cts.Token);

		Job? triggered = await store.TriggerScheduledJob(
			template.Job.Id, DateTimeOffset.UtcNow.AddSeconds(-1), null, cts.Token);

		Assert.Null(triggered);
	}

}
