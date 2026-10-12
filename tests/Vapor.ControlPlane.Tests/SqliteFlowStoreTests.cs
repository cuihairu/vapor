using Microsoft.Data.Sqlite;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class SqliteFlowStoreTests : IDisposable
{
	private readonly SqliteFlowStore _store = new(":memory:");

	public void Dispose() => _store.Dispose();

	private static FlowStep SampleStep(
		string scriptId = "script-1",
		string agentId = "agent-a",
		string onFailure = "stop") => new(scriptId, agentId, onFailure);

	private static ScriptFlowRecord SampleFlow(
		string id = "flow-1",
		string name = "maintenance",
		IReadOnlyList<FlowStep>? steps = null) => new(
		Id: id,
		Name: name,
		Description: "",
		Steps: steps ?? [SampleStep()],
		CreatedAtMs: 1000,
		UpdatedAtMs: 2000);

	private static ScriptFlowRunRecord SampleRun(
		string id = "run-1",
		string flowId = "flow-1",
		string status = "completed",
		long startedAtMs = 5000) => new(
		Id: id,
		FlowId: flowId,
		FlowName: "maintenance",
		Status: status,
		Steps: [new FlowRunStep(0, "script-1", "agent-a", "succeeded", "job-1")],
		StartedAtMs: startedAtMs);

	[Fact]
	public void Constructor_WithEmptyDbPath_Throws()
	{
		Assert.Throws<ArgumentException>(() => new SqliteFlowStore(""));
		Assert.Throws<ArgumentException>(() => new SqliteFlowStore("   "));
	}

	[Fact]
	public void Constructor_WithRelativePath_CreatesDirectory()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), "vapor-flow-store-tests");
		string dbPath = Path.Combine(tempDir, "flows.db");
		try
		{
			using SqliteFlowStore store = new(dbPath);
			Assert.True(File.Exists(dbPath));
		}
		finally
		{
			// Windows: ADO.NET keeps a pooled file handle even after Dispose();
			// clearing the pool is what actually releases the file for deletion.
			SqliteConnection.ClearAllPools();
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, recursive: true);
			}
		}
	}

	[Fact]
	public async Task UpsertFlow_InsertsThenUpdatesById()
	{
		ScriptFlowRecord flow = SampleFlow(id: "f1", name: "first");
		await _store.UpsertFlowAsync(flow);
		Assert.Single(await _store.ListFlowsAsync());

		ScriptFlowRecord updated = flow with
		{
			Name = "second",
			Description = "renamed",
			Steps = [SampleStep(scriptId: "s2", agentId: "agent-b", onFailure: "continue")],
			UpdatedAtMs = 3000
		};
		await _store.UpsertFlowAsync(updated);

		List<ScriptFlowRecord> flows = await _store.ListFlowsAsync();
		Assert.Single(flows);
		Assert.Equal("f1", flows[0].Id);
		Assert.Equal("second", flows[0].Name);
		Assert.Equal("renamed", flows[0].Description);
		Assert.Equal(3000, flows[0].UpdatedAtMs);
		FlowStep step = Assert.Single(flows[0].Steps);
		Assert.Equal("s2", step.ScriptId);
		Assert.Equal("agent-b", step.AgentId);
		Assert.Equal("continue", step.OnFailure);
	}

	[Fact]
	public async Task GetFlow_WithUnknownId_ReturnsNull()
	{
		Assert.Null(await _store.GetFlowAsync("missing"));
		Assert.Null(await _store.GetFlowAsync(""));
	}

	[Fact]
	public async Task ListFlows_ReturnsFlowsOrderedByNameThenId()
	{
		await _store.UpsertFlowAsync(SampleFlow(id: "b-id", name: "beta"));
		await _store.UpsertFlowAsync(SampleFlow(id: "a-id", name: "beta"));
		await _store.UpsertFlowAsync(SampleFlow(id: "c-id", name: "alpha"));

		List<ScriptFlowRecord> flows = await _store.ListFlowsAsync();

		var names = flows.Select(f => f.Name).ToList();
		Assert.Equal(new[] { "alpha", "beta", "beta" }, names);
		Assert.Equal("a-id", flows[1].Id);
		Assert.Equal("b-id", flows[2].Id);
	}

	[Fact]
	public async Task DeleteFlow_RemovesExistingAndReportsFalseForUnknown()
	{
		await _store.UpsertFlowAsync(SampleFlow(id: "f1"));

		Assert.True(await _store.DeleteFlowAsync("f1"));
		Assert.Empty(await _store.ListFlowsAsync());

		Assert.False(await _store.DeleteFlowAsync("f1"));
		Assert.False(await _store.DeleteFlowAsync("missing"));
	}

	[Fact]
	public async Task Run_CreateUpdateGet_RoundTripsStatusAndFinishTime()
	{
		ScriptFlowRunRecord run = SampleRun(id: "r1") with { FinishedAtMs = null };
		await _store.CreateRunAsync(run);

		ScriptFlowRunRecord stored = await _store.GetRunAsync("r1") ?? throw new InvalidOperationException("run missing");
		Assert.Null(stored.FinishedAtMs);
		Assert.Equal("completed", stored.Status);

		ScriptFlowRunRecord finished = stored with
		{
			Status = "failed",
			FinishedAtMs = 9000,
			Steps = [new FlowRunStep(0, "script-1", "agent-a", "failed", "job-1", Error: "script exited with code 3")]
		};
		await _store.UpdateRunAsync(finished);

		ScriptFlowRunRecord updated = await _store.GetRunAsync("r1") ?? throw new InvalidOperationException("run missing");
		Assert.Equal("failed", updated.Status);
		Assert.Equal(9000, updated.FinishedAtMs);
		FlowRunStep step = Assert.Single(updated.Steps);
		Assert.Equal("failed", step.Status);
		Assert.Equal("script exited with code 3", step.Error);
		Assert.Equal("job-1", step.JobId);
	}

	[Fact]
	public async Task GetRun_WithUnknownId_ReturnsNull()
	{
		Assert.Null(await _store.GetRunAsync("missing"));
	}

	[Fact]
	public async Task ListRuns_FiltersByFlowAndOrdersMostRecentFirst()
	{
		await _store.CreateRunAsync(SampleRun(id: "r1", flowId: "f1", startedAtMs: 1000));
		await _store.CreateRunAsync(SampleRun(id: "r2", flowId: "f1", startedAtMs: 3000) with { FinishedAtMs = 4000 });
		await _store.CreateRunAsync(SampleRun(id: "r3", flowId: "f2", startedAtMs: 2000));

		List<ScriptFlowRunRecord> runs = await _store.ListRunsAsync("f1");

		Assert.Equal(2, runs.Count);
		Assert.Equal("r2", runs[0].Id);
		Assert.Equal("r1", runs[1].Id);
	}

	[Fact]
	public async Task GetFlow_WithNullStepsJson_FallsBackToEmptyList()
	{
		// A legacy row whose steps column holds the literal JSON null must read
		// back as an empty step list, not a null reference. The store cannot
		// produce such a row, so a second connection writes it directly.
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-flow-null-steps-{Guid.NewGuid():N}.db");
		try
		{
			using (SqliteFlowStore store = new(dbPath))
			{
				await using (SqliteConnection connection = new($"Data Source={dbPath}"))
				{
					await connection.OpenAsync();
					await using SqliteCommand command = connection.CreateCommand();
					command.CommandText = """
						INSERT INTO script_flows (id, name, description, steps, created_at_ms, updated_at_ms)
						VALUES ('f1', 'legacy', '', 'null', 1000, 1000);
						""";
					await command.ExecuteNonQueryAsync();
				}

				ScriptFlowRecord? flow = await store.GetFlowAsync("f1");
				Assert.NotNull(flow);
				Assert.Empty(flow!.Steps);
			}
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			if (File.Exists(dbPath))
			{
				File.Delete(dbPath);
			}
		}
	}

	[Fact]
	public async Task GetRun_WithNullStepsJson_FallsBackToEmptyStepList()
	{
		// Same defensive read for runs: literal JSON null in the steps column
		// reads back as an empty step list, with the finish time intact.
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-flow-null-run-steps-{Guid.NewGuid():N}.db");
		try
		{
			using (SqliteFlowStore store = new(dbPath))
			{
				await using (SqliteConnection connection = new($"Data Source={dbPath}"))
				{
					await connection.OpenAsync();
					await using SqliteCommand command = connection.CreateCommand();
					command.CommandText = """
						INSERT INTO script_flow_runs (id, flow_id, flow_name, status, steps, started_at_ms, finished_at_ms)
						VALUES ('r1', 'f1', 'legacy', 'completed', 'null', 1000, 9000);
						""";
					await command.ExecuteNonQueryAsync();
				}

				ScriptFlowRunRecord? run = await store.GetRunAsync("r1");
				Assert.NotNull(run);
				Assert.Empty(run!.Steps);
				Assert.Equal(9000, run.FinishedAtMs);
			}
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			if (File.Exists(dbPath))
			{
				File.Delete(dbPath);
			}
		}
	}

	[Fact]
	public async Task UpsertAndGet_RoundTripsTheScheduleAndCursor()
	{
		ScriptFlowRecord armed = SampleFlow() with
		{
			Schedule = new JobSchedule(IntervalSeconds: 60, Missed: ScheduleMissedPolicy.RunOnce, Overlap: ScheduleOverlapPolicy.Allow),
			NextRunAtMs = 9999
		};
		await _store.UpsertFlowAsync(armed);

		ScriptFlowRecord? fetched = await _store.GetFlowAsync("flow-1");
		Assert.NotNull(fetched);
		Assert.NotNull(fetched!.Schedule);
		Assert.Equal(60, fetched.Schedule!.IntervalSeconds);
		Assert.Equal(ScheduleMissedPolicy.RunOnce, fetched.Schedule.Missed);
		Assert.Equal(ScheduleOverlapPolicy.Allow, fetched.Schedule.Overlap);
		Assert.Equal(9999, fetched.NextRunAtMs);
	}

	[Fact]
	public async Task ListFlowsAsync_CarriesTheScheduleFields()
	{
		await _store.UpsertFlowAsync(SampleFlow(id: "flow-plain") with { Schedule = new JobSchedule(Cron: "0 0 * * *"), NextRunAtMs = 42 });
		await _store.UpsertFlowAsync(SampleFlow(id: "flow-manual"));

		List<ScriptFlowRecord> flows = await _store.ListFlowsAsync();
		Assert.Equal(2, flows.Count);
		Assert.Equal(42, flows.Single(f => f.Id == "flow-plain").NextRunAtMs);
		Assert.Equal("0 0 * * *", flows.Single(f => f.Id == "flow-plain").Schedule!.Cron);
		Assert.Null(flows.Single(f => f.Id == "flow-manual").Schedule);
		Assert.Null(flows.Single(f => f.Id == "flow-manual").NextRunAtMs);
	}

	[Fact]
	public async Task Upsert_WithNullSchedule_DisarmsAnArmedFlow()
	{
		await _store.UpsertFlowAsync(SampleFlow() with { Schedule = new JobSchedule(IntervalSeconds: 30), NextRunAtMs = 500 });
		await _store.UpsertFlowAsync(SampleFlow());

		ScriptFlowRecord? fetched = await _store.GetFlowAsync("flow-1");
		Assert.NotNull(fetched);
		Assert.Null(fetched!.Schedule);
		Assert.Null(fetched.NextRunAtMs);
	}

	[Fact]
	public async Task ListDueFlowsAsync_ReturnsOnlyArmedDueFlows_OldestFirst()
	{
		// Due and oldest: listed first. Future cursor: not due. Disarmed cursor
		// (schedule set, cursor null): never comes back on its own. Manual flow
		// (no schedule): never scheduled.
		await _store.UpsertFlowAsync(SampleFlow(id: "due-old") with { Schedule = new JobSchedule(IntervalSeconds: 60), NextRunAtMs = 100 });
		await _store.UpsertFlowAsync(SampleFlow(id: "due-new") with { Schedule = new JobSchedule(IntervalSeconds: 60), NextRunAtMs = 200 });
		await _store.UpsertFlowAsync(SampleFlow(id: "future") with { Schedule = new JobSchedule(IntervalSeconds: 60), NextRunAtMs = 900 });
		await _store.UpsertFlowAsync(SampleFlow(id: "disarmed") with { Schedule = new JobSchedule(IntervalSeconds: 60) });
		await _store.UpsertFlowAsync(SampleFlow(id: "manual"));

		List<ScriptFlowRecord> due = await _store.ListDueFlowsAsync(DateTimeOffset.FromUnixTimeMilliseconds(500), 10);
		Assert.Equal(new[] { "due-old", "due-new" }, due.Select(f => f.Id).ToArray());

		List<ScriptFlowRecord> limited = await _store.ListDueFlowsAsync(DateTimeOffset.FromUnixTimeMilliseconds(500), 1);
		Assert.Equal(new[] { "due-old" }, limited.Select(f => f.Id).ToArray());
	}

	[Fact]
	public async Task AdvanceFlowScheduleAsync_MovesOnlyThatCursor_AndReportsUnknownIds()
	{
		await _store.UpsertFlowAsync(SampleFlow(id: "flow-a") with { Schedule = new JobSchedule(IntervalSeconds: 60), NextRunAtMs = 100 });
		await _store.UpsertFlowAsync(SampleFlow(id: "flow-b") with { Schedule = new JobSchedule(IntervalSeconds: 60), NextRunAtMs = 100 });

		Assert.True(await _store.AdvanceFlowScheduleAsync("flow-a", DateTimeOffset.FromUnixTimeMilliseconds(777)));
		Assert.False(await _store.AdvanceFlowScheduleAsync("missing", DateTimeOffset.FromUnixTimeMilliseconds(777)));

		Assert.Equal(777, (await _store.GetFlowAsync("flow-a"))!.NextRunAtMs);
		Assert.Equal(100, (await _store.GetFlowAsync("flow-b"))!.NextRunAtMs);
	}

	[Fact]
	public async Task HasActiveFlowRunAsync_TrueOnlyWhileARunIsRunning()
	{
		Assert.False(await _store.HasActiveFlowRunAsync("flow-1")); // no runs at all

		await _store.CreateRunAsync(SampleRun(id: "run-live", status: "running"));
		Assert.True(await _store.HasActiveFlowRunAsync("flow-1"));

		await _store.UpdateRunAsync(SampleRun(id: "run-live", status: "completed") with { FinishedAtMs = 6000 });
		Assert.False(await _store.HasActiveFlowRunAsync("flow-1"));

		// Terminal and unresolved statuses are not "running": a pending run left
		// pollable via the jobs surface does not hold the schedule back.
		await _store.CreateRunAsync(SampleRun(id: "run-pending", status: "pending"));
		await _store.CreateRunAsync(SampleRun(id: "run-failed", status: "failed"));
		Assert.False(await _store.HasActiveFlowRunAsync("flow-1"));

		Assert.False(await _store.HasActiveFlowRunAsync("other-flow"));
	}

	[Fact]
	public async Task Migration_FromPreScheduleDatabase_AddsTheColumns()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-flows-mig-{Guid.NewGuid():N}.db");
		try
		{
			// A store from before the schedule columns: the flows table exists in
			// its old shape, so the migration must add both columns in place.
			await using (SqliteConnection connection = new($"Data Source={dbPath}"))
			{
				await connection.OpenAsync();
				await using SqliteCommand command = connection.CreateCommand();
				command.CommandText = """
					CREATE TABLE script_flows (
						id TEXT PRIMARY KEY,
						name TEXT NOT NULL,
						description TEXT NOT NULL,
						steps TEXT NOT NULL,
						created_at_ms INTEGER NOT NULL,
						updated_at_ms INTEGER NOT NULL
					);
					INSERT INTO script_flows (id, name, description, steps, created_at_ms, updated_at_ms)
					VALUES ('legacy', 'legacy flow', '[]', '[{"scriptId":"s","agentId":"a","onFailure":"stop"}]', 1, 2);
					""";
				await command.ExecuteNonQueryAsync();
			}

			using (SqliteFlowStore store = new(dbPath))
			{
				ScriptFlowRecord? legacy = await store.GetFlowAsync("legacy");
				Assert.NotNull(legacy);
				Assert.Null(legacy!.Schedule); // old rows stay manual-only
				Assert.Null(legacy.NextRunAtMs);

				// The migrated columns are usable: arm the legacy flow in place.
				await store.UpsertFlowAsync(legacy with { Schedule = new JobSchedule(IntervalSeconds: 60), NextRunAtMs = 123 });
				Assert.Equal(123, (await store.GetFlowAsync("legacy"))!.NextRunAtMs);
			}
		}
		finally
		{
			SqliteConnection.ClearAllPools();
			if (File.Exists(dbPath))
			{
				File.Delete(dbPath);
			}
		}
	}
}
