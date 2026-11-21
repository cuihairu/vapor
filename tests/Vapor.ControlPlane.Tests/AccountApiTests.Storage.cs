using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Storage collect/snapshot endpoint tests: fleet declaration, per-account reports.</summary>
public sealed partial class AccountApiTests
{
	[Fact]
	public async Task Storage_Collect_RequiresAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage collect = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "warehouse" });
		using HttpResponseMessage snapshot = await client.GetAsync("/v1/orchestration/storage/snapshot");

		Assert.Equal(HttpStatusCode.Unauthorized, collect.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, snapshot.StatusCode);
	}

	[Fact]
	public async Task Storage_Collect_MissingStorageName_Returns400()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage withoutField = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { message = "batch move" });
		using HttpResponseMessage blank = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "   " });
		using HttpResponseMessage withoutBody = await client.PostAsync("/v1/orchestration/storage/collect", null);

		Assert.Equal(HttpStatusCode.BadRequest, withoutField.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, withoutBody.StatusCode);
		Assert.Contains("storage (target account name) is required", await withoutBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Storage_Collect_UnknownStorageAccount_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "ghost" });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
		Assert.Contains("account 'ghost' is not declared", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Storage_Collect_FarmRoleTarget_Returns400()
	{
		// Collecting into a plain farm account would move items between two loot
		// sources; the role is what makes the destination a warehouse.
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline", steamId = "76561198000000001" });
		await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "alice" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("not declared as a storage account (role=storage)", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Storage_Collect_StorageWithoutSteamId_Returns400()
	{
		// The role can be declared ahead of the id (the declaration is not
		// blocked), but a collect with no partner address is refused at the door.
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/warehouse", new { desiredState = "offline", role = "storage" });
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "warehouse" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("has no steam_id declared", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Storage_Collect_WithoutFarmSources_Returns400()
	{
		// A declared-offline farm is deliberately logged out — there is nothing
		// to loot from it, so it never counts as a source.
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/warehouse", new { desiredState = "offline", role = "storage", steamId = "76561198000000009" });
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });
		await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "idle", enabled = false });
		await client.PutAsJsonAsync("/v1/accounts/carol", new { desiredState = "offline", enabled = true });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "warehouse" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("no enabled farm accounts to collect from", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Storage_Collect_ReportsPerAccountAndRecordsAudit()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await DeclareStorageFleetAsync(client);

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondCollectByTargetAsync(
			store,
			new Dictionary<string, LootStep>
			{
				["alice"] = LootStep.Ok(3, "offer-alice"),
				["bob"] = LootStep.Ok(2, "offer-bob", requiresConfirmation: false),
				["dave"] = LootStep.Failed("no tradable items to loot in the scanned apps")
			},
			cts.Token),
			CancellationToken.None);

		using HttpResponseMessage resp = await client.PostAsJsonAsync(
			"/v1/orchestration/storage/collect",
			new { storage = "warehouse", message = " batch move ", appIds = new[] { 753, 1091500 } });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("warehouse", doc.RootElement.GetProperty("storage").GetString());
		Assert.Equal("76561198000000009", doc.RootElement.GetProperty("storage_steam_id").GetString());

		// The source set is every enabled farm account that is not declared offline.
		List<string> sources = doc.RootElement.GetProperty("farm_accounts").EnumerateArray().Select(a => a.GetString()!).ToList();
		Assert.Equal(["alice", "bob", "dave"], sources);

		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		JsonElement results = doc.RootElement.GetProperty("results");
		Assert.Equal(3, results.GetArrayLength());

		JsonElement alice = SingleResult(results, "alice");
		Assert.Equal("finished", alice.GetProperty("status").GetString());
		Assert.Equal(3, alice.GetProperty("itemCount").GetInt32());
		Assert.Equal("offer-alice", alice.GetProperty("tradeOfferId").GetString());
		Assert.True(alice.GetProperty("confirmed").GetBoolean());
		Assert.NotEmpty(alice.GetProperty("confirmJobId").GetString()!);

		// Nothing to confirm means no confirm job at all — never a false "pending".
		JsonElement bob = SingleResult(results, "bob");
		Assert.Equal("finished", bob.GetProperty("status").GetString());
		AssertOmitted(bob, "confirmed");
		AssertOmitted(bob, "confirmJobId");

		// One source failing is a per-account result, not a blanket failure.
		JsonElement dave = SingleResult(results, "dave");
		Assert.Equal("failed", dave.GetProperty("status").GetString());
		Assert.Contains("no tradable items", dave.GetProperty("error").GetString(), StringComparison.Ordinal);
		AssertOmitted(dave, "itemCount");

		// The loot task carried the storage partner address plus the caller's
		// message and app filter.
		JobWithTasks lootJob = await store.GetJob(jobId, CancellationToken.None);
		Assert.Equal(3, lootJob.Tasks.Count);
		Assert.Equal("76561198000000009", await PayloadValueFromTask(store, jobId, 0, "partner_steam_id"));
		Assert.Equal("batch move", await PayloadValueFromTask(store, jobId, 0, "message"));
		Assert.Contains("753", await PayloadValueFromTask(store, jobId, 0, "app_ids"), StringComparison.Ordinal);
		Assert.Contains("1091500", await PayloadValueFromTask(store, jobId, 0, "app_ids"), StringComparison.Ordinal);
		JobWithTasks confirmJob = await store.GetJob(alice.GetProperty("confirmJobId").GetString()!, CancellationToken.None);
		Assert.Equal(jobId, confirmJob.Job.Meta!["parent_job"]);
		Assert.Equal("offer-alice", await PayloadValueFromTask(store, confirmJob.Job.Id, 0, "trade_offer_id"));

		// The run is audited with its source count and per-status totals.
		using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=storage.collect");
		string auditBody = await audit.Content.ReadAsStringAsync();
		Assert.Contains("\"farmCount\":3", auditBody, StringComparison.Ordinal);
		Assert.Contains("\"completed\":true", auditBody, StringComparison.Ordinal);
		Assert.Contains("\"succeeded\":2", auditBody, StringComparison.Ordinal);
		Assert.Contains("\"failed\":1", auditBody, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Storage_Collect_StillPending_Returns202()
	{
		// No agent ever answers: the run stays observable through its job id
		// (its SSE stream is the progress channel) and the next collect simply
		// re-loots — unaccepted offers expire and the items stay in place.
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await DeclareStorageFleetAsync(client);

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "warehouse" });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
			Assert.NotEmpty(doc.RootElement.GetProperty("job_id").GetString()!);
			Assert.False(doc.RootElement.TryGetProperty("results", out _));
			Assert.Equal(3, doc.RootElement.GetProperty("farm_accounts").GetArrayLength());

			// A pending run is still audited with the count it is waiting on.
			using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=storage.collect");
			string auditBody = await audit.Content.ReadAsStringAsync();
			Assert.Contains("\"completed\":false", auditBody, StringComparison.Ordinal);
			Assert.Contains("\"succeeded\":0", auditBody, StringComparison.Ordinal);
		}
		finally
		{
			StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(150);
			StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(500);
		}
	}

	[Fact]
	public async Task Storage_Snapshot_EmptyWithoutCollectJobs()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/orchestration/storage/snapshot");

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Empty(JsonElement.Parse(body).GetProperty("accounts").EnumerateArray());
	}

	[Fact]
	public async Task Storage_Snapshot_ReportsLastCollectPerAccount()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await DeclareStorageFleetAsync(client);

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using (var cts = new CancellationTokenSource())
		{
			Task responder = Task.Run(() => RespondCollectByTargetAsync(
				store,
				new Dictionary<string, LootStep>
				{
					["alice"] = LootStep.Ok(3, "offer-alice"),
					["bob"] = LootStep.Ok(2, "offer-bob", requiresConfirmation: false),
					["dave"] = LootStep.Failed("no tradable items to loot in the scanned apps")
				},
				cts.Token),
				CancellationToken.None);

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/orchestration/storage/collect", new { storage = "warehouse" });
			cts.Cancel();
			Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		}

		// An unrelated loot (the per-account endpoint, not a collect) must not
		// leak into the warehouse snapshot.
		using (var cts = new CancellationTokenSource())
		{
			Task responder = Task.Run(() => RespondToFirstTaskAsync(
				store, AccountTaskRunner.LootInventoryAction, success: true,
				new Dictionary<string, object?> { ["trade_offer_id"] = "manual-1", ["item_count"] = 1 },
				null, cts.Token),
				CancellationToken.None);
			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76561198000000009" });
			cts.Cancel();
			Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		}

		using HttpResponseMessage snapshot = await client.GetAsync("/v1/orchestration/storage/snapshot");
		Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
		string body = await snapshot.Content.ReadAsStringAsync();
		JsonElement accounts = JsonElement.Parse(body).GetProperty("accounts");
		Assert.Equal(3, accounts.GetArrayLength());

		JsonElement alice = SingleResult(accounts, "alice");
		Assert.Equal("warehouse", alice.GetProperty("storage").GetString());
		Assert.Equal("finished", alice.GetProperty("status").GetString());
		Assert.Equal(3, alice.GetProperty("itemCount").GetInt32());
		Assert.Equal("offer-alice", alice.GetProperty("tradeOfferId").GetString());
		Assert.True(alice.GetProperty("confirmed").GetBoolean());
		Assert.NotEmpty(alice.GetProperty("confirmJobId").GetString()!);
		Assert.NotEmpty(alice.GetProperty("jobId").GetString()!);

		JsonElement bob = SingleResult(accounts, "bob");
		AssertOmitted(bob, "confirmed");

		JsonElement dave = SingleResult(accounts, "dave");
		Assert.Equal("failed", dave.GetProperty("status").GetString());
		Assert.Contains("no tradable items", dave.GetProperty("error").GetString(), StringComparison.Ordinal);
	}

	/// <summary>Declares the storage warehouse plus the three farm sources the collect tests fan out to.</summary>
	private static async Task DeclareStorageFleetAsync(HttpClient client)
	{
		await client.PutAsJsonAsync("/v1/accounts/warehouse", new
		{
			desiredState = "offline",
			role = "storage",
			steamId = "76561198000000009"
		});
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "idle" });
		await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "idle" });
		await client.PutAsJsonAsync("/v1/accounts/dave", new { desiredState = "idle" });
	}

	/// <summary>One farm source's staged loot answer.</summary>
	private sealed record LootStep(bool Success, Dictionary<string, object?>? Output, string? Error)
	{
		internal static LootStep Ok(int itemCount, string offerId, bool requiresConfirmation = true) => new(
			true,
			new Dictionary<string, object?>
			{
				["item_count"] = itemCount,
				["trade_offer_id"] = offerId,
				["requires_mobile_confirmation"] = requiresConfirmation
			},
			null);

		internal static LootStep Failed(string error) => new(false, null, error);
	}

	/// <summary>
	/// Plays the agent side of a collect run keyed by <em>target</em> (claim
	/// order is the store's business, not the test's): answers each
	/// <c>loot_inventory</c> task from its source's staged outcome, then
	/// confirms every chained confirmation task.
	/// </summary>
	private static async Task RespondCollectByTargetAsync(
		IJobStore store,
		IReadOnlyDictionary<string, LootStep> lootSteps,
		CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is null)
			{
				await Task.Delay(25, ct);
				continue;
			}

			if (claimed.Action == AccountTaskRunner.LootInventoryAction)
			{
				LootStep step = lootSteps[claimed.Target];
				await store.SetTaskResult(
					new TaskResult(claimed.Id, step.Success, step.Error, step.Output, DateTimeOffset.UtcNow),
					ct);
				continue;
			}

			if (claimed.Action == AccountTaskRunner.ConfirmTradeOfferAction)
			{
				await store.SetTaskResult(
					new TaskResult(
						claimed.Id, true, null,
						new Dictionary<string, object?> { ["confirmed"] = true },
						DateTimeOffset.UtcNow),
					ct);
			}
		}
	}

	/// <summary>
	/// Null record fields are omitted from the wire (WhenWritingNull), so an
	/// absent key is how "no value" reads on the client.
	/// </summary>
	private static void AssertOmitted(JsonElement obj, string property) =>
		Assert.False(
			obj.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.Null,
			$"'{property}' should be absent when it has no value");

	private static JsonElement SingleResult(JsonElement array, string account)
	{
		List<JsonElement> matches = array.EnumerateArray()
			.Where(a => a.GetProperty("account").GetString() == account)
			.ToList();
		return Assert.Single(matches);
	}
}
