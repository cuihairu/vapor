using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// End-to-end tests for the §39 P-b warehouse collect orchestration against the real
/// Control Plane process: declaring a storage + farm fleet over the REST API, POSTing
/// a collect run that fans one loot_inventory task per eligible farm into a single
/// job, and observing the storage.collect audit entry plus the snapshot read-back —
/// alongside the guard rails (404 unknown storage; 400 wrong role / missing steam_id /
/// no eligible farms) and the single-account loot endpoint's dispatch + audit on a
/// never-checked account. The E2E agent loads an empty plugin directory, so no agent
/// declares loot_inventory: tasks must settle as terminal Failed through the real
/// scheduler instead of hanging, which is the bounded-window contract these endpoints
/// are built around (200 with per-account results once settled, 202 + job id if the
/// wait window ever closes first).
/// </summary>
[Collection("E2E")]
public sealed class StorageCollectE2ETests
{
	private const string FarmOne = "e2e-collect-farm-1";
	private const string FarmTwo = "e2e-collect-farm-2";
	private const string DisabledFarm = "e2e-collect-farm-disabled";
	private const string OfflineFarm = "e2e-collect-farm-offline";
	private const string StorageAccount = "e2e-collect-storage";
	private const string StorageGuard = "e2e-guard-storage";
	private const string StorageNoSteam = "e2e-guard-storage-nosteam";
	private const string FarmGuard = "e2e-guard-farm";
	private const string LootFarm = "e2e-loot-farm";
	private const string GhostAccount = "e2e-collect-ghost";
	private const string StorageSteamId = "76561198000000099";
	private const string CollectPath = "/v1/orchestration/storage/collect";

	// A region with no agent: the source filter needs a non-Offline desired state,
	// and with nobody to assign to, the orchestrator never spawns login churn for
	// these accounts while the test runs.
	private const string NoAgentRegion = "e2e-collect-noagent";

	private readonly E2EStack _stack;

	public StorageCollectE2ETests(E2EStack stack)
	{
		_stack = stack;
	}

	[Fact]
	public async Task Collect_FansOutToEligibleFarms_ReportsAuditAndSnapshot()
	{
		await PutAccountAsync(FarmOne, new { enabled = true, desiredState = "online", region = NoAgentRegion });
		await PutAccountAsync(FarmTwo, new { enabled = true, desiredState = "online", region = NoAgentRegion });
		await PutAccountAsync(DisabledFarm, new { enabled = false, desiredState = "online", region = NoAgentRegion });
		await PutAccountAsync(OfflineFarm, new { enabled = true, desiredState = "offline" });
		await PutAccountAsync(StorageAccount, new { enabled = true, desiredState = "offline", role = "storage", steamId = StorageSteamId });

		try
		{
			// The collect endpoint blocks on its real wait window (the CP child process
			// owns that static, unreachable from here), so dispatching calls bring a
			// client whose timeout covers it — the shared stack client gives up at 10s.
			using HttpClient dispatch = CreateDispatchClient();
			using HttpResponseMessage resp = await SendAsync(dispatch, HttpMethod.Post, CollectPath, new { storage = StorageAccount });
			string raw = await resp.Content.ReadAsStringAsync();
			Assert.True(
				resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
				$"collect failed ({resp.StatusCode}): {raw}{Environment.NewLine}{_stack.Diagnostics()}");

			JsonElement body;
			using (JsonDocument doc = JsonDocument.Parse(raw))
			{
				body = doc.RootElement.Clone();
			}

			Assert.Equal(StorageAccount, body.GetProperty("storage").GetString());
			Assert.Equal(StorageSteamId, body.GetProperty("storage_steam_id").GetString());
			string jobId = body.GetProperty("job_id").GetString()
				?? throw new InvalidOperationException($"job_id missing from collect response: {raw}");

			// The source set is exactly the enabled, non-Offline, role=farm accounts —
			// the declared disabled and offline farms and the storage itself are out.
			string[] sources = body.GetProperty("farm_accounts").EnumerateArray().Select(e => e.GetString()!).ToArray();
			Assert.Equal(2, sources.Length);
			Assert.Contains(FarmOne, sources);
			Assert.Contains(FarmTwo, sources);
			Assert.DoesNotContain(DisabledFarm, sources);
			Assert.DoesNotContain(OfflineFarm, sources);
			Assert.DoesNotContain(StorageAccount, sources);
			Assert.Empty(body.GetProperty("skipped_quarantined").EnumerateArray());

			// 200 already carries the settled per-account results; 202 means the window
			// closed while tasks were still in flight — poll the job id it hands back.
			if (body.TryGetProperty("results", out JsonElement results))
			{
				AssertFailedPerAccountResults(results);
			}
			else
			{
				Assert.Equal("pending", body.GetProperty("status").GetString());
				JsonElement settled = await WaitForJobTasksTerminalAsync(jobId, TimeSpan.FromSeconds(120));
				AssertFailedTasks(settled.GetProperty("tasks"));
			}

			// The run is audited under the storage account with its fan-out facts: how
			// many farms were eligible, what was skipped, and that nothing was forced.
			JsonElement audit = await _stack.GetAuditLogsAsync("storage.collect");
			JsonElement entry = FindAuditEntry(audit, StorageAccount, jobId);
			JsonElement details = entry.GetProperty("details");
			Assert.Equal(JsonValueKind.Object, details.ValueKind);
			Assert.Equal(2, details.GetProperty("farmCount").GetInt32());
			Assert.False(details.GetProperty("forcedQuarantine").GetBoolean());
			Assert.Empty(details.GetProperty("skippedQuarantined").EnumerateArray());

			// The snapshot rebuilds per-source last-collect stats from the collect job
			// itself (meta origin=storage-collect + storage name) — both sources show up
			// with the run's job id and the failed loot outcome.
			JsonElement snapshot = await AdminJsonAsync(HttpMethod.Get, "/v1/orchestration/storage/snapshot");
			JsonElement accounts = snapshot.GetProperty("accounts");
			foreach (string farm in new[] { FarmOne, FarmTwo })
			{
				JsonElement entryForFarm = FindSnapshotEntry(accounts, farm);
				Assert.Equal(StorageAccount, entryForFarm.GetProperty("storage").GetString());
				Assert.Equal(jobId, entryForFarm.GetProperty("jobId").GetString());
				Assert.Equal("failed", entryForFarm.GetProperty("status").GetString());
				Assert.False(string.IsNullOrEmpty(entryForFarm.GetProperty("error").GetString()));
			}

			// A farm that was never part of the fan-out has no snapshot row.
			Assert.DoesNotContain(
				accounts.EnumerateArray(),
				a => a.GetProperty("account").GetString() == DisabledFarm);
			Assert.DoesNotContain(
				accounts.EnumerateArray(),
				a => a.GetProperty("account").GetString() == OfflineFarm);
		}
		finally
		{
			await DeleteAccountsAsync(FarmOne, FarmTwo, DisabledFarm, OfflineFarm, StorageAccount);
		}
	}

	[Fact]
	public async Task Collect_RejectsBadStorageTargets_BeforeDispatch()
	{
		await PutAccountAsync(StorageGuard, new { enabled = true, desiredState = "offline", role = "storage", steamId = StorageSteamId });

		try
		{
			// A missing storage field fails before any lookup (an absent body normalizes
			// to the same 400).
			using (HttpResponseMessage missing = await SendAsync(_stack.Http, HttpMethod.Post, CollectPath, new { }))
			{
				Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
				Assert.Contains("storage", await missing.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
			}

			// With only the storage declared there is no enabled farm to collect from —
			// checked after the storage guards, so this 400 is about the fleet, not the
			// target.
			using (HttpResponseMessage noFarms = await SendAsync(_stack.Http, HttpMethod.Post, CollectPath, new { storage = StorageGuard }))
			{
				Assert.Equal(HttpStatusCode.BadRequest, noFarms.StatusCode);
				Assert.Contains("no enabled farm accounts", await noFarms.Content.ReadAsStringAsync(), StringComparison.Ordinal);
			}

			using (HttpResponseMessage ghost = await SendAsync(_stack.Http, HttpMethod.Post, CollectPath, new { storage = GhostAccount }))
			{
				Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
				Assert.Contains("not declared", await ghost.Content.ReadAsStringAsync(), StringComparison.Ordinal);
			}

			await PutAccountAsync(FarmGuard, new { enabled = true, desiredState = "online", region = NoAgentRegion });
			await PutAccountAsync(StorageNoSteam, new { enabled = true, desiredState = "offline", role = "storage" });

			// Role and steam_id guards run before farm selection, so neither of these
			// dispatches anything even though an eligible farm now exists.
			using (HttpResponseMessage notStorage = await SendAsync(_stack.Http, HttpMethod.Post, CollectPath, new { storage = FarmGuard }))
			{
				Assert.Equal(HttpStatusCode.BadRequest, notStorage.StatusCode);
				Assert.Contains("not declared as a storage", await notStorage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
			}

			using (HttpResponseMessage noSteam = await SendAsync(_stack.Http, HttpMethod.Post, CollectPath, new { storage = StorageNoSteam }))
			{
				Assert.Equal(HttpStatusCode.BadRequest, noSteam.StatusCode);
				Assert.Contains("steam_id", await noSteam.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
			}
		}
		finally
		{
			await DeleteAccountsAsync(StorageGuard, FarmGuard, StorageNoSteam);
		}
	}

	[Fact]
	public async Task LootEndpoint_DispatchesAndAudits_NeverCheckedAccount()
	{
		await PutAccountAsync(LootFarm, new { enabled = true, desiredState = "online", region = NoAgentRegion });

		try
		{
			using (HttpResponseMessage missingPartner = await SendAsync(_stack.Http, HttpMethod.Post, $"/v1/accounts/{LootFarm}/loot", new { }))
			{
				Assert.Equal(HttpStatusCode.BadRequest, missingPartner.StatusCode);
				Assert.Contains("partner_steam_id", await missingPartner.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
			}

			using (HttpResponseMessage ghost = await SendAsync(_stack.Http, HttpMethod.Post, $"/v1/accounts/{GhostAccount}/loot", new { partnerSteamId = StorageSteamId }))
			{
				Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
				Assert.Contains("not declared", await ghost.Content.ReadAsStringAsync(), StringComparison.Ordinal);
			}

			// Real dispatch: the agent declares no loot_inventory capability (empty
			// plugin directory), so the task must settle as Failed through the scheduler
			// — 502 once it settles inside the runner's wait window, or 202 with the job
			// id if the window closes first.
			using HttpClient dispatch = CreateDispatchClient();
			using HttpResponseMessage resp = await SendAsync(dispatch, HttpMethod.Post, $"/v1/accounts/{LootFarm}/loot", new { partnerSteamId = StorageSteamId });
			string raw = await resp.Content.ReadAsStringAsync();
			Assert.True(
				resp.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.BadGateway,
				$"unexpected loot status ({resp.StatusCode}): {raw}{Environment.NewLine}{_stack.Diagnostics()}");

			JsonElement lootBody;
			using (JsonDocument doc = JsonDocument.Parse(raw))
			{
				lootBody = doc.RootElement.Clone();
			}

			string jobId = lootBody.GetProperty("job_id").GetString()
				?? throw new InvalidOperationException($"job_id missing from loot response: {raw}");

			JsonElement settled = await WaitForJobTasksTerminalAsync(jobId, TimeSpan.FromSeconds(60));
			AssertFailedTasks(settled.GetProperty("tasks"));

			// The standing isolation gate's auditable arm on a real dispatch: the account
			// was never checked (unknown, not bad), so the loot went through with
			// forced_quarantine=false recorded — never-checked must not read as forced.
			JsonElement audit = await _stack.GetAuditLogsAsync("account.loot");
			JsonElement entry = FindAuditEntry(audit, LootFarm, jobId);
			JsonElement details = entry.GetProperty("details");
			Assert.Equal(JsonValueKind.Object, details.ValueKind);
			Assert.False(details.GetProperty("forced_quarantine").GetBoolean());
			Assert.Equal(StorageSteamId, details.GetProperty("partner").GetString());
		}
		finally
		{
			await DeleteAccountsAsync(LootFarm);
		}
	}

	/// <summary>The 200-body slice: one entry per farm source with account/status/error.</summary>
	private static void AssertFailedPerAccountResults(JsonElement results)
	{
		Assert.Equal(2, results.GetArrayLength());
		foreach (JsonElement result in results.EnumerateArray())
		{
			Assert.False(string.IsNullOrEmpty(result.GetProperty("account").GetString()));
			Assert.Equal("failed", result.GetProperty("status").GetString());
			Assert.False(string.IsNullOrEmpty(result.GetProperty("error").GetString()));
		}
	}

	/// <summary>The GET /v1/jobs/{id} slice: every task terminal and failed with an error.</summary>
	private static void AssertFailedTasks(JsonElement tasks)
	{
		Assert.True(tasks.GetArrayLength() > 0, "job has no tasks");
		foreach (JsonElement task in tasks.EnumerateArray())
		{
			Assert.Equal("failed", task.GetProperty("status").GetString());
			Assert.False(string.IsNullOrEmpty(task.GetProperty("error").GetString()));
		}
	}

	private static JsonElement FindAuditEntry(JsonElement audit, string accountName, string jobId)
	{
		foreach (JsonElement entry in audit.GetProperty("logs").EnumerateArray())
		{
			if (entry.TryGetProperty("jobId", out JsonElement entryJobId) &&
				entryJobId.GetString() == jobId &&
				entry.TryGetProperty("accountName", out JsonElement entryAccount) &&
				entryAccount.GetString() == accountName)
			{
				return entry;
			}
		}

		throw new InvalidOperationException(
			$"No '{accountName}' audit entry for job {jobId}.{Environment.NewLine}{audit.GetRawText()}");
	}

	private static JsonElement FindSnapshotEntry(JsonElement accounts, string accountName)
	{
		foreach (JsonElement entry in accounts.EnumerateArray())
		{
			if (entry.GetProperty("account").GetString() == accountName)
			{
				return entry;
			}
		}

		throw new InvalidOperationException(
			$"No snapshot entry for '{accountName}'. Accounts:{Environment.NewLine}{accounts.GetRawText()}");
	}

	/// <summary>
	/// Polls GET /v1/jobs/{id} until every task reports a terminal status — used for the
	/// 202/pending arms where the endpoint handed the job id back instead of waiting.
	/// </summary>
	private async Task<JsonElement> WaitForJobTasksTerminalAsync(string jobId, TimeSpan timeout)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
		string lastBody = "<no response>";

		while (DateTimeOffset.UtcNow < deadline)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/jobs/{jobId}");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", E2EStack.AdminApiKey);
			using HttpResponseMessage response = await _stack.Http.SendAsync(request);
			response.EnsureSuccessStatusCode();

			lastBody = await response.Content.ReadAsStringAsync();
			using JsonDocument doc = JsonDocument.Parse(lastBody);
			JsonElement tasks = doc.RootElement.GetProperty("tasks");
			if (tasks.GetArrayLength() > 0 && tasks.EnumerateArray().All(IsTerminal))
			{
				return doc.RootElement.Clone();
			}

			await Task.Delay(250);
		}

		throw new TimeoutException(
			$"Job {jobId} tasks did not reach a terminal state within {timeout.TotalSeconds:s}s " +
			$"(last body: {lastBody}).{Environment.NewLine}{_stack.Diagnostics()}");

		static bool IsTerminal(JsonElement task)
		{
			string? status = task.GetProperty("status").GetString();
			return status is not null && (status.Equals("finished", StringComparison.OrdinalIgnoreCase)
				|| status.Equals("failed", StringComparison.OrdinalIgnoreCase)
				|| status.Equals("canceled", StringComparison.OrdinalIgnoreCase));
		}
	}

	/// <summary>
	/// The shared stack client times out at 10s; dispatching endpoints block on their
	/// real wait windows (30s loot / 150s collect — process statics owned by the CP
	/// child, unreachable from here), so these calls bring a client that outlives them.
	/// </summary>
	private HttpClient CreateDispatchClient() =>
		new()
		{
			BaseAddress = new Uri(_stack.BaseUrl),
			Timeout = TimeSpan.FromSeconds(200),
		};

	private async Task PutAccountAsync(string name, object spec)
	{
		using HttpResponseMessage response = await SendAsync(_stack.Http, HttpMethod.Put, $"/v1/accounts/{name}", spec);
		string body = await response.Content.ReadAsStringAsync();
		Assert.True(
			response.IsSuccessStatusCode,
			$"PUT /v1/accounts/{name} failed ({response.StatusCode}): {body}{Environment.NewLine}{_stack.Diagnostics()}");
	}

	private async Task DeleteAccountsAsync(params string[] names)
	{
		foreach (string name in names)
		{
			// Best-effort cleanup: a 404 on an already-removed account is fine.
			using HttpResponseMessage response = await SendAsync(_stack.Http, HttpMethod.Delete, $"/v1/accounts/{name}");
		}
	}

	private async Task<JsonElement> AdminJsonAsync(HttpMethod method, string path, object? body = null)
	{
		using HttpResponseMessage response = await SendAsync(_stack.Http, method, path, body);
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
		return doc.RootElement.Clone();
	}

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
	{
		using var request = new HttpRequestMessage(method, path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", E2EStack.AdminApiKey);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		return await client.SendAsync(request);
	}
}
