using System.Text.Json;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>BatchOutput parsing tests: missing keys, dot-net objects, mixed lists and entries without an app id.</summary>
public sealed partial class CrawlRunWorkerTests
{
	[Fact]
	public async Task BatchOutputWithoutGamesOrErrorsKeys_SettlesWithZeroRows()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.Outputs[jobId] = JsonSerializer.SerializeToElement(new { fetched = 0 });

		await worker.RunTickAsync(CancellationToken.None);

		Assert.Equal(1, worker.RunsCompleted);
		Assert.Empty(await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1")));
	}

	[Fact]
	public async Task BatchOutputAsDotNetObjects_ParsesGamesList()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None);
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		// In-memory output (no JSON round-trip): the games list stays a .NET list
		// of plain objects and must parse the same as the JsonElement variant.
		_jobs.RawOutputs[jobId] = new Dictionary<string, object?>
		{
			["games"] = new List<object?> { new Dictionary<string, object?> { ["app_id"] = 570, ["name"] = "game-570" } }
		};

		await worker.RunTickAsync(CancellationToken.None);

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		var okRow = Assert.Single(rows);
		Assert.True(okRow.Ok);
		Assert.Equal(570U, okRow.AppId);
		Assert.Equal("game-570", okRow.Data!.Value.GetProperty("name").GetString());
		Assert.Equal(1, worker.AppsSucceeded);
	}

	[Fact]
	public async Task BatchOutputMixedList_PassesJsonElementsThroughUntouched()
	{
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		// A mixed list — pre-serialized JsonElements next to plain .NET objects —
		// must pass the elements through without a second serialization.
		_jobs.RawOutputs[jobId] = new Dictionary<string, object?>
		{
			["games"] = new List<object?>
			{
				JsonSerializer.SerializeToElement(new { app_id = 570, name = "game-570" }),
				new Dictionary<string, object?> { ["app_id"] = 730, ["name"] = "game-730" }
			}
		};

		await worker.RunTickAsync(CancellationToken.None);

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		Assert.Equal(2, rows.Count);
		Assert.Equal("game-570", rows.Single(r => r.AppId == 570).Data!.Value.GetProperty("name").GetString());
		Assert.Equal("game-730", rows.Single(r => r.AppId == 730).Data!.Value.GetProperty("name").GetString());
		Assert.Equal(2, worker.AppsSucceeded);
	}

	[Fact]
	public async Task BatchOutputEntriesWithoutAppId_AreSkipped()
	{
		// Agent actions are third-party code: a batch row with a zero or missing
		// app_id must be dropped rather than recorded as "app 0".
		_accounts.Upsert("alice", true, AccountDesiredState.Online, null, null, null, null);
		await SeedOneShotPlanAsync(appIds: new uint[] { 570 });

		var worker = CreateWorker();
		await worker.RunTickAsync(CancellationToken.None); // dispatch
		string jobId = _jobs.CreatedJobs.Single().Job.Id;
		_jobs.Outcomes[jobId] = (JobTaskStatus.Finished, null);
		_jobs.RawOutputs[jobId] = new Dictionary<string, object?>
		{
			["games"] = new List<object?>
			{
				new Dictionary<string, object?> { ["app_id"] = 0, ["name"] = "zero-id" },
				new Dictionary<string, object?> { ["name"] = "missing-id" },
				new Dictionary<string, object?> { ["app_id"] = 570, ["name"] = "game-570" }
			},
			["errors"] = new List<object?>
			{
				new Dictionary<string, object?> { ["app_id"] = 0, ["error"] = "boom" },
				new Dictionary<string, object?> { ["error"] = "also boom" }
			}
		};

		await worker.RunTickAsync(CancellationToken.None); // collect

		var rows = await _crawl.QueryResultsAsync(new CrawlResultQuery(PlanId: "plan-1"));
		var okRow = Assert.Single(rows);
		Assert.True(okRow.Ok);
		Assert.Equal(570U, okRow.AppId);
		Assert.Equal(1, worker.AppsSucceeded);
	}

}
