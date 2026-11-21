using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// Shares the static AccountTaskRunner.WaitWindow/PollInterval knobs with
// ProgramBranchCoverageTests (see AccountTaskWaitWindowCollection).
[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed partial class AccountApiTests
{
	[Fact]
	public async Task Accounts_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage list = await client.GetAsync("/v1/accounts");
		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });

		Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
	}

	[Fact]
	public async Task PutAccount_CreatesSpecAndReturnsIt()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "idle",
			idleApps = new[] { "730", "570" },
			region = "us-east",
			agentId = "agent-1",
			note = "farm bot"
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		Assert.Equal("alice", spec.GetProperty("accountName").GetString());
		Assert.Equal("idle", spec.GetProperty("desiredState").GetString());
		Assert.Equal(2, spec.GetProperty("idleApps").GetArrayLength());
		Assert.Equal("us-east", spec.GetProperty("region").GetString());
		Assert.Equal("agent-1", spec.GetProperty("agentId").GetString());
		Assert.Equal(1, spec.GetProperty("version").GetProperty("version").GetInt32());
	}

	[Fact]
	public async Task PutAccount_InvalidIdleApp_ReturnsBadRequest()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			desiredState = "idle",
			idleApps = new[] { "not-a-number" }
		});

		Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
	}

	[Fact]
	public async Task PutAccount_FarmState_AcceptsIdleAppsAsExclusionList()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "farm",
			idleApps = new[] { "730" }
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		Assert.Equal("farm", spec.GetProperty("desiredState").GetString());
		Assert.Equal(1, spec.GetProperty("idleApps").GetArrayLength());
	}

	[Fact]
	public async Task PutAccount_BoostState_WithTargets_ReturnsThemSorted()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "boost",
			boostTargets = new[]
			{
				new { appId = 620u, targetHours = 36.7 },
				new { appId = 220u, targetHours = 1234.5 }
			}
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		Assert.Equal("boost", spec.GetProperty("desiredState").GetString());
		JsonElement targets = spec.GetProperty("boostTargets");
		var enumerated = targets.EnumerateArray().ToList();
		Assert.Equal(2, enumerated.Count);
		Assert.Equal(220u, enumerated[0].GetProperty("appId").GetUInt32());
		Assert.Equal(1234.5, enumerated[0].GetProperty("targetHours").GetDouble());
		Assert.Equal(620u, enumerated[1].GetProperty("appId").GetUInt32());
	}

	[Fact]
	public async Task PutAccount_BoostState_WithoutTargets_ReturnsBadRequest()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "boost"
		});

		Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
	}

	[Fact]
	public async Task PutAccount_ConflictingBoostTargets_ReturnsBadRequest()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "boost",
			boostTargets = new[]
			{
				new { appId = 220u, targetHours = 10.0 },
				new { appId = 220u, targetHours = 20.0 }
			}
		});

		Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
	}

	[Fact]
	public async Task PutAccount_TradePolicy_RoundTripsWhitelist()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "online",
			tradePolicy = new
			{
				autoAcceptGifts = true,
				partnerWhitelist = new ulong[] { 76561198000000000, 76561197960265728 }
			}
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		JsonElement policy = spec.GetProperty("tradePolicy");
		Assert.True(policy.GetProperty("autoAcceptGifts").GetBoolean());
		JsonElement whitelist = policy.GetProperty("partnerWhitelist");
		Assert.Equal(2, whitelist.GetArrayLength());
		Assert.Equal(76561197960265728ul, whitelist[0].GetUInt64()); // sorted ascending
	}

	[Fact]
	public async Task PutAccount_AutoAcceptGifts_WithoutWhitelist_ReturnsBadRequest()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			enabled = true,
			desiredState = "online",
			tradePolicy = new { autoAcceptGifts = true }
		});

		Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
	}

	[Fact]
	public async Task PutAccount_ReplacesExistingSpec()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });
		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline", region = "eu-west" });

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);
		string body = await put.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");

		Assert.Equal(2, spec.GetProperty("version").GetProperty("version").GetInt32());
		Assert.Equal("offline", spec.GetProperty("desiredState").GetString());
		Assert.Equal("eu-west", spec.GetProperty("region").GetString());

		using HttpResponseMessage list = await client.GetAsync("/v1/accounts");
		string listBody = await list.Content.ReadAsStringAsync();
		Assert.Contains("\"accountName\":\"alice\"", listBody);
	}

	[Fact]
	public async Task GetAccount_ReturnsSpecAndMissingReturns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });

		using HttpResponseMessage found = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.OK, found.StatusCode);
		string body = await found.Content.ReadAsStringAsync();
		Assert.Contains("\"accountName\":\"alice\"", body);

		using HttpResponseMessage missing = await client.GetAsync("/v1/accounts/ghost");
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task ListAccounts_FiltersByStateRegionAndAgent()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "idle", idleApps = new[] { "730" }, region = "us-east", agentId = "agent-1" });
		await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "online", region = "us-east" });
		await client.PutAsJsonAsync("/v1/accounts/carol", new { desiredState = "offline", region = "eu-west", agentId = "agent-1" });

		using HttpResponseMessage byState = await client.GetAsync("/v1/accounts?state=idle");
		string byStateBody = await byState.Content.ReadAsStringAsync();
		Assert.Contains("alice", byStateBody);
		Assert.DoesNotContain("bob", byStateBody);
		Assert.DoesNotContain("carol", byStateBody);

		using HttpResponseMessage byRegion = await client.GetAsync("/v1/accounts?region=us-east");
		string byRegionBody = await byRegion.Content.ReadAsStringAsync();
		Assert.Contains("alice", byRegionBody);
		Assert.Contains("bob", byRegionBody);
		Assert.DoesNotContain("carol", byRegionBody);

		using HttpResponseMessage byAgent = await client.GetAsync("/v1/accounts?agent=agent-1");
		string byAgentBody = await byAgent.Content.ReadAsStringAsync();
		Assert.Contains("alice", byAgentBody);
		Assert.DoesNotContain("bob", byAgentBody);
		Assert.Contains("carol", byAgentBody);

		using HttpResponseMessage invalidState = await client.GetAsync("/v1/accounts?state=hopping");
		Assert.Equal(HttpStatusCode.BadRequest, invalidState.StatusCode);
	}

	[Fact]
	public async Task DisableAndEnable_ToggleWithoutTouchingDesiredState()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "idle", idleApps = new[] { "730" } });

		using HttpResponseMessage disabled = await client.PostAsJsonAsync("/v1/accounts/alice/disable", new { });
		Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
		using var disabledDoc = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());
		Assert.False(disabledDoc.RootElement.GetProperty("spec").GetProperty("enabled").GetBoolean());
		Assert.Equal("idle", disabledDoc.RootElement.GetProperty("spec").GetProperty("desiredState").GetString());

		using HttpResponseMessage enabled = await client.PostAsJsonAsync("/v1/accounts/alice/enable", new { });
		Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
		using var enabledDoc = JsonDocument.Parse(await enabled.Content.ReadAsStringAsync());
		Assert.True(enabledDoc.RootElement.GetProperty("spec").GetProperty("enabled").GetBoolean());

		using HttpResponseMessage missing = await client.PostAsJsonAsync("/v1/accounts/ghost/disable", new { });
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task StandingEndpoints_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage list = await client.GetAsync("/v1/orchestration/standing");
		using HttpResponseMessage force = await client.PostAsync("/v1/accounts/alice/standing-check", null);

		Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, force.StatusCode);
	}

	[Fact]
	public async Task StandingCheck_AccountMissing_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsync("/v1/accounts/ghost/standing-check", null);

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task StandingEndpoints_ListAndScheduleForcedCheck()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		// One reconcile pass so the orchestrator tracks alice.
		DesiredStateReconciler reconciler = factory.Services.GetRequiredService<DesiredStateReconciler>();
		await reconciler.ReconcileOnce(CancellationToken.None);

		using HttpResponseMessage list = await client.GetAsync("/v1/orchestration/standing");
		Assert.Equal(HttpStatusCode.OK, list.StatusCode);
		string listBody = await list.Content.ReadAsStringAsync();
		using var listDoc = JsonDocument.Parse(listBody);
		JsonElement entries = listDoc.RootElement.GetProperty("standing");
		Assert.Equal(1, entries.GetArrayLength());
		Assert.Equal("alice", entries[0].GetProperty("accountName").GetString());
		Assert.False(entries[0].GetProperty("quarantined").GetBoolean());
		// WhenWritingNull drops "standing": null — absent means "never checked".
		Assert.False(entries[0].TryGetProperty("standing", out JsonElement standingValue)
			&& standingValue.ValueKind != JsonValueKind.Null);

		using HttpResponseMessage forced = await client.PostAsync("/v1/accounts/alice/standing-check", null);
		Assert.Equal(HttpStatusCode.Accepted, forced.StatusCode);
	}

	[Fact]
	public async Task FarmEndpoints_RequireAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage snapshot = await client.GetAsync("/v1/orchestration/farm");

		Assert.Equal(HttpStatusCode.Unauthorized, snapshot.StatusCode);
	}

	[Fact]
	public async Task FarmPolicy_PutValidationSnapshotAndClearing()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// An invalid budget is refused at declaration time (fail-fast, same as boost targets).
		using HttpResponseMessage invalid = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			desiredState = "farm",
			farmPolicy = new { perGameHourBudget = -1 }
		});
		Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

		// A valid policy is carried into the spec.
		using HttpResponseMessage declared = await client.PutAsJsonAsync("/v1/accounts/alice", new
		{
			desiredState = "farm",
			farmPolicy = new { perGameHourBudget = 5, priorityOrder = "cardsAscending", priorityApps = new[] { 730, 570 } }
		});
		Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
		string specBody = await declared.Content.ReadAsStringAsync();
		using var specDoc = JsonDocument.Parse(specBody);
		JsonElement policy = specDoc.RootElement.GetProperty("spec").GetProperty("farmPolicy");
		Assert.Equal(5, policy.GetProperty("perGameHourBudget").GetDouble());
		Assert.Equal("cardsAscending", policy.GetProperty("priorityOrder").GetString());
		Assert.Equal(2, policy.GetProperty("priorityApps").GetArrayLength());

		// One reconcile pass so the orchestrator tracks alice, then the farm
		// snapshot lists her with the efficiency counters present (0 collected).
		DesiredStateReconciler reconciler = factory.Services.GetRequiredService<DesiredStateReconciler>();
		await reconciler.ReconcileOnce(CancellationToken.None);
		using HttpResponseMessage snapshot = await client.GetAsync("/v1/orchestration/farm");
		Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
		string snapshotBody = await snapshot.Content.ReadAsStringAsync();
		using var snapshotDoc = JsonDocument.Parse(snapshotBody);
		JsonElement farm = snapshotDoc.RootElement.GetProperty("farm");
		Assert.Equal(1, farm.GetArrayLength());
		Assert.Equal("alice", farm[0].GetProperty("accountName").GetString());
		Assert.Equal(0, farm[0].GetProperty("cardsCollected").GetInt32());

		// Full replace: an update that omits the policy clears it.
		using HttpResponseMessage cleared = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "farm" });
		Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
		string clearedBody = await cleared.Content.ReadAsStringAsync();
		using var clearedDoc = JsonDocument.Parse(clearedBody);
		// WhenWritingNull drops "farmPolicy": null — absence means cleared.
		Assert.False(clearedDoc.RootElement.GetProperty("spec").TryGetProperty("farmPolicy", out JsonElement clearedPolicy)
			&& clearedPolicy.ValueKind != JsonValueKind.Null);
	}

	[Fact]
	public async Task DeleteAccount_RemovesSpecAndIsAudited()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online" });

		using HttpResponseMessage deleted = await client.DeleteAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

		using HttpResponseMessage missing = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

		using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=account.spec.updated");
		Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
		string auditBody = await audit.Content.ReadAsStringAsync();
		using var auditDoc = JsonDocument.Parse(auditBody);
		Assert.True(auditDoc.RootElement.GetProperty("total").GetInt32() >= 1);

		using HttpResponseMessage deletedAudit = await client.GetAsync("/v1/audit/logs?action=account.spec.removed");
		string deletedBody = await deletedAudit.Content.ReadAsStringAsync();
		using var deletedDoc = JsonDocument.Parse(deletedBody);
		Assert.Equal(1, deletedDoc.RootElement.GetProperty("total").GetInt32());
	}

	[Fact]
	public async Task GetAccount_ReturnsAggregateViewWithSessionChallengeAndTasks()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "idle", idleApps = new[] { "730" }, region = "us-east" });

		// A session snapshot + a pending 2FA challenge via the normal event endpoint.
		using HttpResponseMessage evt = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "state_changed",
			state = "ConnectingWait2FA",
			message = "need 2fa"
		});
		Assert.Equal(HttpStatusCode.OK, evt.StatusCode);

		// A job targeting alice so the aggregate view has recent tasks.
		using HttpResponseMessage job = await client.PostAsJsonAsync("/v1/jobs", new { action = "login", targets = new[] { "alice" } });
		Assert.Equal(HttpStatusCode.Accepted, job.StatusCode);

		using HttpResponseMessage view = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.OK, view.StatusCode);

		string body = await view.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement root = doc.RootElement;

		Assert.Equal("idle", root.GetProperty("spec").GetProperty("desiredState").GetString());
		Assert.Equal("ConnectingWait2FA", root.GetProperty("session").GetProperty("state").GetString());
		Assert.Equal("2fa_required", root.GetProperty("pendingChallenge").GetProperty("challengeType").GetString());
		Assert.Equal(1, root.GetProperty("recentTasks").GetArrayLength());
		Assert.Equal("login", root.GetProperty("recentTasks")[0].GetProperty("action").GetString());
	}

	[Fact]
	public async Task GetAccount_ExposesObservedAndReasonProjection()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Online in a region no agent serves: a reconcile pass records the
		// deviation the projection must surface.
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online", region = "us-east" });

		using HttpResponseMessage before = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.OK, before.StatusCode);
		string beforeBody = await before.Content.ReadAsStringAsync();
		using var beforeDoc = JsonDocument.Parse(beforeBody);
		// WhenWritingNull drops "observed"/"reason": absence means "no session
		// snapshot" / "no reconcile decision recorded yet".
		Assert.False(beforeDoc.RootElement.TryGetProperty("observed", out _));
		Assert.False(beforeDoc.RootElement.TryGetProperty("reason", out _));

		// One reconcile pass: no capable agent in us-east → the pass records the
		// reason on the account's runtime, which the projection reads.
		DesiredStateReconciler reconciler = factory.Services.GetRequiredService<DesiredStateReconciler>();
		await reconciler.ReconcileOnce(CancellationToken.None);

		using HttpResponseMessage after = await client.GetAsync("/v1/accounts/alice");
		Assert.Equal(HttpStatusCode.OK, after.StatusCode);
		string afterBody = await after.Content.ReadAsStringAsync();
		using var afterDoc = JsonDocument.Parse(afterBody);
		JsonElement root = afterDoc.RootElement;
		Assert.Equal("no capable agent available", root.GetProperty("reason").GetString());
		Assert.False(root.TryGetProperty("observed", out _));

		// A session snapshot makes the observed state appear at the top level.
		using HttpResponseMessage evt = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "state_changed",
			state = "Connected",
			message = "logged in"
		});
		Assert.Equal(HttpStatusCode.OK, evt.StatusCode);

		using HttpResponseMessage withSession = await client.GetAsync("/v1/accounts/alice");
		string withSessionBody = await withSession.Content.ReadAsStringAsync();
		using var withSessionDoc = JsonDocument.Parse(withSessionBody);
		Assert.Equal("Connected", withSessionDoc.RootElement.GetProperty("observed").GetString());
		Assert.Equal("no capable agent available", withSessionDoc.RootElement.GetProperty("reason").GetString());
	}

	[Fact]
	public async Task ReconcileAccount_RunsPassWritesAuditAndValidatesTarget()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();

		// No auth header → 401 before anything else.
		using HttpResponseMessage anonymous = await client.PostAsync("/v1/accounts/alice/reconcile", null);
		Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Unknown account → 404.
		using HttpResponseMessage ghost = await client.PostAsync("/v1/accounts/ghost/reconcile", null);
		Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "online", region = "us-east" });

		// Declared account → the endpoint runs one pass synchronously and answers.
		using HttpResponseMessage reconciled = await client.PostAsync("/v1/accounts/alice/reconcile", null);
		Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
		string body = await reconciled.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("reconciled", doc.RootElement.GetProperty("status").GetString());

		// The forced pass is auditable under its own action name.
		using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=orchestration.reconcile");
		Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
		string auditBody = await audit.Content.ReadAsStringAsync();
		using var auditDoc = JsonDocument.Parse(auditBody);
		Assert.Equal(1, auditDoc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal("alice", auditDoc.RootElement.GetProperty("logs")[0].GetProperty("accountName").GetString());
	}

	[Fact]
	public async Task GetAccount_MissingReturns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage missing = await client.GetAsync("/v1/accounts/ghost");
		Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
	}

	[Fact]
	public async Task ListJobs_SupportsAccountFilter()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PostAsJsonAsync("/v1/jobs", new { action = "login", targets = new[] { "alice" } });
		await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", targets = new[] { "bob" } });

		using HttpResponseMessage filtered = await client.GetAsync("/v1/jobs?account=alice");
		Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
		string body = await filtered.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.Equal(1, doc.RootElement.GetProperty("jobs").GetArrayLength());
		Assert.Equal("login", doc.RootElement.GetProperty("jobs")[0].GetProperty("action").GetString());
	}

	[Fact]
	public async Task ListSessions_SupportsAccountFilter()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PostAsJsonAsync("/v1/sessions/events", new { accountName = "alice", eventType = "state_changed", state = "Connected" });
		await client.PostAsJsonAsync("/v1/sessions/events", new { accountName = "bob", eventType = "state_changed", state = "Disconnected" });

		using HttpResponseMessage filtered = await client.GetAsync("/v1/sessions?account=alice");
		Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
		string body = await filtered.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);

		Assert.Equal(1, doc.RootElement.GetProperty("sessions").GetArrayLength());
		Assert.Equal("alice", doc.RootElement.GetProperty("sessions")[0].GetProperty("accountName").GetString());
	}

	[Fact]
	public async Task PutAccount_MarketListingsEnabled_SurvivesPutWithoutField()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline", marketListingsEnabled = true });
		// A later PUT that omits the flag must keep it (opt-in is not silently reset).
		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.True(doc.RootElement.GetProperty("spec").GetProperty("marketListingsEnabled").GetBoolean());
	}

	/// <summary>Normalizes a task payload value (may be a JsonElement after the SQLite round-trip) to a string.</summary>
	private static string PayloadValue(IReadOnlyDictionary<string, object?>? payload, string key)
	{
		if (payload is null || !payload.TryGetValue(key, out object? raw))
		{
			return string.Empty;
		}

		return raw switch
		{
			JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
			JsonElement n => n.GetRawText(),
			string s => s,
			_ => string.Empty
		};
	}

	[Fact]
	public async Task PutAccount_StorageRoleAndSteamId_PersistThroughSpec()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync(
			"/v1/accounts/warehouse",
			new { desiredState = "offline", role = "storage", steamId = " 76561197960265728 " });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement spec = doc.RootElement.GetProperty("spec");
		Assert.Equal("storage", spec.GetProperty("role").GetString());
		// Whitespace is trimmed and the id is stored as canonical digits.
		Assert.Equal("76561197960265728", spec.GetProperty("steamId").GetString());

		// Farm stays the default: an update that omits the role keeps the
		// account a plain loot source.
		await client.PutAsJsonAsync("/v1/accounts/plain", new { desiredState = "online" });
		using HttpResponseMessage plain = await client.GetAsync("/v1/accounts/plain");
		string plainBody = await plain.Content.ReadAsStringAsync();
		using var plainDoc = JsonDocument.Parse(plainBody);
		Assert.Equal("farm", plainDoc.RootElement.GetProperty("spec").GetProperty("role").GetString());
	}

	[Fact]
	public async Task PutAccount_RoleOutOfRange_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// A numeric role binds past the string converter — the store's
		// validation is what keeps garbage roles out of the spec.
		using HttpResponseMessage resp = await client.PutAsJsonAsync(
			"/v1/accounts/alice", new { desiredState = "online", role = 99 });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("account role must be farm or storage", body, StringComparison.Ordinal);
	}

	[Fact]
	public async Task PutAccount_UnusableSteamId_Returns400()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync(
			"/v1/accounts/alice", new { desiredState = "online", steamId = "not-a-number" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("steam_id must be a positive 64-bit SteamID", body, StringComparison.Ordinal);
	}

	private static async Task<string> PayloadValueFromTask(IJobStore store, string jobId, int taskIndex, string key)
	{
		JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
		JobTask task = job.Tasks[taskIndex];
		return PayloadValue(task.Payload, key);
	}

	/// <summary>Plays the agent side: claims and completes a fixed sequence of tasks, in order.</summary>
	private sealed record FakeAgentStep(string Action, bool Success, Dictionary<string, object?>? Output, string? Error);

	private static async Task RespondToStepsAsync(IJobStore store, FakeAgentStep[] steps, CancellationToken ct)
	{
		int index = 0;
		while (index < steps.Length && !ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == steps[index].Action)
			{
				FakeAgentStep step = steps[index];
				await store.SetTaskResult(
					new TaskResult(claimed.Id, step.Success, step.Success ? null : step.Error, step.Success ? step.Output : null, DateTimeOffset.UtcNow),
					ct);
				index++;
				continue;
			}

			await Task.Delay(25, ct);
		}
	}

	/// <summary>Renders a dispatched payload value as a plain string — every value
	/// arrives as a JsonElement after the SQLite job store round-trip.</summary>
	private static string Scalar(object? value) => value switch
	{
		JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "null" : e.GetRawText(),
		null => "null",
		_ => value.ToString() ?? "null"
	};

	/// <summary>Plays the agent side: claims the first queued task for <paramref name="action"/>,
	/// reports a result and returns the dispatched payload.</summary>
	private static async Task<Dictionary<string, object?>?> RespondToFirstTaskAsync(
		IJobStore store,
		string action,
		bool success,
		Dictionary<string, object?>? output,
		string? error,
		CancellationToken ct)
	{
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is not null && claimed.Action == action)
			{
				Dictionary<string, object?>? payload = claimed.Payload is null
					? null
					: new Dictionary<string, object?>(claimed.Payload);
				await store.SetTaskResult(
					new TaskResult(claimed.Id, success, success ? null : error, success ? output : null, DateTimeOffset.UtcNow),
					ct);
				return payload;
			}

			await Task.Delay(25, ct);
		}

		return null;
	}

	private static TestFactory CreateFactory(bool removeHosted = false)
	{
		return new TestFactory(removeHosted);
	}

	private sealed class TestFactory(bool removeHosted) : WebApplicationFactory<Program>
	{
		protected override void ConfigureWebHost(IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IAuditStore>();
				services.RemoveAll<AccountStore>();
				services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:", AgentKeyAgentIds: new Dictionary<string, string> { ["agent-token"] = "agent-9" }));
				services.AddSingleton<IJobStore>(sp => new SqliteJobStore(":memory:"));
				services.AddSingleton<IAuditStore>(sp => new SqliteAuditStore(":memory:"));
				services.AddSingleton<AccountStore>();
				if (removeHosted)
				{
					// The synchronous trade-offers endpoint races its own fake agent
					// responder; background dispatchers would claim/fail the task first.
					services.RemoveAll<IHostedService>();
					services.RemoveAll<IHostedLifecycleService>();
				}
			});
		}
	}
}
