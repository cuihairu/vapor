using Microsoft.Data.Sqlite;
using Vapor.ControlPlane;
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
}
