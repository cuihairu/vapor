using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Task-family validation and confirmation-chain branch tests (inventory, loot, duplicates, swap offers, licenses, achievements, offers, confirmations).</summary>
public sealed partial class ProgramBranchCoverageTests
{
	[Fact]
	public async Task Inventory_ValidationAndPendingResponses()
	{
		// Window must dwarf scheduler pauses: if the whole window is burned
		// before the first poll, the loop body never runs and its line hits
		// vanish (2026-09-23 CI flake). 2s at 25ms polls is 80 polls for a
		// never-finishing fake — same 202, far less pause-sensitive.
		AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(2);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage undeclared = await client.GetAsync("/v1/accounts/ghost/inventory");
			using HttpResponseMessage emptyList = await client.GetAsync("/v1/accounts/alice/inventory?appIds=,&marketableOnly=true");
			using HttpResponseMessage pending = await client.GetAsync("/v1/accounts/alice/inventory?appIds=753&marketableOnly=true");

			Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
			Assert.Equal(HttpStatusCode.BadRequest, emptyList.StatusCode);
			Assert.Contains("at least one app", await emptyList.Content.ReadAsStringAsync());
			Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Loot_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.PostAsJsonAsync("/v1/accounts/ghost/loot", new { partnerSteamId = "76561198000000001" });
		using HttpResponseMessage badPartner = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "not-a-number" });

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badPartner.StatusCode);
		Assert.Contains("positive 64-bit SteamID", await badPartner.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Loot_WithMessageAndAppIds_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new
			{
				partnerSteamId = "76561198000000001",
				message = "  thanks!  ",
				appIds = new[] { 753, 730 }
			});

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

			// The dispatched payload carries the trimmed message and the app id list.
			IJobStore store = factory.Services.GetRequiredService<IJobStore>();
			JobWithTasks job = await store.GetJob(Assert.Single(await store.ListJobs(10, null, CancellationToken.None)).Id, CancellationToken.None);
			JsonElement payload = (JsonElement)job.Tasks[0].Payload!["message"]!;
			Assert.Equal("thanks!", payload.GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Loot_PlainSuccessWithoutConfirmationFlag_SkipsConfirm()
	{
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(new Dictionary<string, object?> { ["ok"] = true }) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76561198000000001" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.True(doc.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
		Assert.False(doc.RootElement.TryGetProperty("mobile_confirmation", out _));
	}

	[Fact]
	public async Task Loot_RequiresConfirmationWithLiveValueTypes_AutoConfirms()
	{
		// Live bool/string values (not the JsonElement shapes a SQLite round-trip
		// produces) exercise the tolerant output readers on their native branches.
		Dictionary<string, object?> output = new()
		{
			["requires_mobile_confirmation"] = true,
			["trade_offer_id"] = "43591234567890"
		};
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(output, confirmOutput: new Dictionary<string, object?> { ["confirmed"] = true }) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76591198000000001" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.True(confirm.GetProperty("confirmed").GetBoolean());
	}

	[Fact]
	public async Task Loot_RequiresConfirmationWithoutOfferId_SkipsConfirm()
	{
		Dictionary<string, object?> output = new() { ["requires_mobile_confirmation"] = true };
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(output) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76591198000000002" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.False(doc.RootElement.TryGetProperty("mobile_confirmation", out _));
	}

	[Fact]
	public async Task Loot_ConfirmFails_ReportsErrorInMobileConfirmation()
	{
		// A numeric trade_offer_id survives the JSON round-trip as a JsonElement
		// number — the fallback ToString branch of the output reader.
		Dictionary<string, object?> output = new()
		{
			["requires_mobile_confirmation"] = true,
			["trade_offer_id"] = 43591234567890L
		};
		await using BranchFactory factory = new()
		{
			JobStore = MakeScriptedStore(output, confirmError: "no identity secret is stored for 'alice'")
		};
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76591198000000003" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.False(confirm.GetProperty("confirmed").GetBoolean());
		Assert.Contains("identity secret", confirm.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AchievementsUnlock_MainTaskFailsWithoutError_UsesStatusFallback()
	{
		// The achievements write path shares the loot/accept 502 shape: a Failed
		// main task with a null error must surface the status-text fallback.
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(new Dictionary<string, object?> { ["ok"] = true }, failMainWithoutError: true) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/achievements/unlock", new { appId = 440, names = new[] { "PORTAL_BEAT_GAME" } });

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Contains("task ended as Failed", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Loot_MainTaskFailsWithoutError_UsesStatusFallback()
	{
		// A task that ends Failed with a null error must not surface a null in
		// the 502 body: the status-text fallback names the outcome instead.
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(new Dictionary<string, object?> { ["ok"] = true }, failMainWithoutError: true) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/loot", new { partnerSteamId = "76591198000000004" });

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Contains("task ended as Failed", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task AcceptOffer_MainTaskFailsWithoutError_UsesStatusFallback()
	{
		await using BranchFactory factory = new() { JobStore = MakeScriptedStore(new Dictionary<string, object?> { ["ok"] = true }, failMainWithoutError: true) };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/42/accept", new { partnerSteamId = "76561198000000001" });

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Contains("task ended as Failed", doc.RootElement.GetProperty("error").GetString());
	}

	[Fact]
	public async Task AcceptOffer_ConfirmFailsWithoutError_ReportsStatusFallbackInConfirmation()
	{
		// The confirmation task fails with a null error while the accept
		// succeeded and demanded a mobile confirmation: the confirmation block
		// must report the status fallback instead of a null error.
		Dictionary<string, object?> output = new()
		{
			["requires_mobile_confirmation"] = true,
			["trade_offer_id"] = "43591234567890"
		};
		await using BranchFactory factory = new()
		{
			JobStore = MakeScriptedStore(output, failConfirmWithoutError: true)
		};
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/42/accept", new { partnerSteamId = "76561198000000001" });

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.False(confirm.GetProperty("confirmed").GetBoolean());
		Assert.Contains("task ended as Failed", confirm.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Duplicates_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.GetAsync("/v1/accounts/ghost/duplicates");
		using HttpResponseMessage badEntry = await client.GetAsync("/v1/accounts/alice/duplicates?appIds=abc");
		using HttpResponseMessage emptyList = await client.GetAsync("/v1/accounts/alice/duplicates?appIds=,");

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badEntry.StatusCode);
		Assert.Contains("positive app ids", await badEntry.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.BadRequest, emptyList.StatusCode);
		Assert.Contains("at least one app", await emptyList.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task SwapOffers_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.PostAsJsonAsync("/v1/accounts/ghost/swap-offers", new { partnerSteamId = "76561198000000100" });
		using HttpResponseMessage badPartner = await client.PostAsJsonAsync("/v1/accounts/alice/swap-offers", new { partnerSteamId = "0" });

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badPartner.StatusCode);
		Assert.Contains("positive 64-bit SteamID", await badPartner.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task SwapOffers_TradeUrlWithAllOptionalFields_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/swap-offers", new
			{
				tradeUrl = "https://s.team/a/1234567890/abcdefgh",
				message = "swap?",
				appIds = new[] { 753 },
				keep = 1,
				maxSwaps = 5,
				send = true
			});

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

			IJobStore store = factory.Services.GetRequiredService<IJobStore>();
			JobWithTasks job = await store.GetJob(Assert.Single(await store.ListJobs(10, null, CancellationToken.None)).Id, CancellationToken.None);
			JsonElement tradeUrl = (JsonElement)job.Tasks[0].Payload!["trade_url"]!;
			Assert.StartsWith("https://s.team/a/", tradeUrl.GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task SwapOffers_SendConfirmFails_ReportsErrorInMobileConfirmation()
	{
		Dictionary<string, object?> output = new()
		{
			["requires_mobile_confirmation"] = true,
			["trade_offer_id"] = "43591234567891"
		};
		await using BranchFactory factory = new()
		{
			JobStore = MakeScriptedStore(output, confirmError: "confirmation window closed")
		};
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/swap-offers", new
		{
			partnerSteamId = "76561198000000100",
			send = true
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement confirm = doc.RootElement.GetProperty("mobile_confirmation");
		Assert.False(confirm.GetProperty("confirmed").GetBoolean());
		Assert.Equal("confirmation window closed", confirm.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Licenses_ValidationBranches()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage undeclared = await client.PostAsJsonAsync("/v1/accounts/ghost/licenses", new { appIds = new[] { 753 } });
		using HttpResponseMessage badAppId = await client.PostAsJsonAsync("/v1/accounts/alice/licenses", new { appIds = new[] { 0 } });

		Assert.Equal(HttpStatusCode.NotFound, undeclared.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, badAppId.StatusCode);
		Assert.Contains("app_ids must be positive", await badAppId.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task DeclineTradeOffer_InvalidOfferId_Returns400()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/nope/decline", new { });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
	}

	[Fact]
	public async Task DeclineTradeOffer_UndeclaredAccount_Returns404()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/ghost/trade-offers/42/decline", new { });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task ConfirmationsAcceptAll_UndeclaredAccount_Returns404()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/ghost/confirmations/accept-all", new { });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task GetDuplicates_WhenTaskStaysQueued_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(300);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using BranchFactory factory = new();
			using HttpClient client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

			// No agent responds, so the dispatch window closes with the task queued.
			using HttpResponseMessage resp = await client.GetAsync("/v1/accounts/alice/duplicates");

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using JsonDocument doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task AcceptTradeOffer_ConfirmResultAsString_TracksConfirmation()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using CancellationTokenSource cts = new();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
			new (string Action, bool Success, Dictionary<string, object?>? Output, string? Error)[]
			{
				("accept_trade_offer", true, new Dictionary<string, object?> { ["requires_mobile_confirmation"] = true }, null),
				// A JSON string booleans survives the SQLite round-trip as a string
				// JsonElement; the tolerant output reader must still parse it.
				("confirm_trade_offer", true, new Dictionary<string, object?> { ["confirmed"] = "true" }, null),
			}, cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/trade-offers/42/accept", new { partnerSteamId = "76561198000000001" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using JsonDocument doc = JsonDocument.Parse(body);
		Assert.True(doc.RootElement.GetProperty("mobile_confirmation").GetProperty("confirmed").GetBoolean());
	}

	/// <summary>
	/// Simulates an agent working through a sequence of dispatched tasks: claims
	/// each queued task by action and completes it with the scripted outcome.
	/// </summary>
	private static async Task RespondToStepsAsync(
		IJobStore store,
		ReadOnlyMemory<(string Action, bool Success, Dictionary<string, object?>? Output, string? Error)> steps,
		CancellationToken ct)
	{
		for (int i = 0; i < steps.Length; i++)
		{
			(string action, bool success, Dictionary<string, object?>? output, string? error) step = steps.Span[i];
			while (!ct.IsCancellationRequested)
			{
				JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
				if (claimed is not null && claimed.Action == step.action)
				{
					await store.SetTaskResult(
						new TaskResult(claimed.Id, step.success, step.success ? null : step.error, step.success ? step.output : null, DateTimeOffset.UtcNow),
						ct);
					break;
				}

				await Task.Delay(25, ct);
			}
		}
	}

	/// <summary>
	/// A store whose jobs immediately complete with a caller-supplied output
	/// dictionary. Values keep their live .NET types (no SQLite JSON round-trip),
	/// which is the point: the API layer must tolerate both shapes.
	/// </summary>
	private static ScriptedJobStore MakeScriptedStore(
		Dictionary<string, object?> output,
		Dictionary<string, object?>? confirmOutput = null,
		string? confirmError = null,
		bool failMainWithoutError = false,
		bool failConfirmWithoutError = false)
	{
		return new ScriptedJobStore(output, confirmOutput, confirmError, failMainWithoutError, failConfirmWithoutError);
	}

	private sealed class ScriptedJobStore(
		Dictionary<string, object?> taskOutput,
		Dictionary<string, object?>? confirmOutput,
		string? confirmError,
		bool failMainWithoutError = false,
		bool failConfirmWithoutError = false) : IJobStore
	{
		private sealed record ScriptedTask(CreateJobRequest Request, JobTaskStatus Status, Dictionary<string, object?>? Output, string? Error);

		private readonly Dictionary<string, ScriptedTask> _jobs = new();
		private int _counter;

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
		{
			int n = Interlocked.Increment(ref _counter);
			string jobId = $"scripted-job-{n}";
			bool confirm = string.Equals(request.Action, AccountTaskRunner.ConfirmTradeOfferAction, StringComparison.Ordinal);
			bool mainFail = failMainWithoutError;
			bool confirmFail = failConfirmWithoutError || (confirm && confirmError is not null);
			var scripted = new ScriptedTask(
				request,
				confirm ? (confirmFail ? JobTaskStatus.Failed : JobTaskStatus.Finished) : (mainFail ? JobTaskStatus.Failed : JobTaskStatus.Finished),
				confirm ? (confirmFail ? null : confirmOutput) : (mainFail ? null : taskOutput),
				confirm ? (failConfirmWithoutError ? null : confirmError) : null);
			lock (_jobs)
			{
				_jobs[jobId] = scripted;
			}

			return Task.FromResult(BuildJob(jobId, scripted));
		}

		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
		{
			ScriptedTask scripted;
			lock (_jobs)
			{
				if (!_jobs.TryGetValue(jobId, out scripted!))
				{
					throw new NotFoundException("job not found");
				}
			}

			return Task.FromResult(BuildJob(jobId, scripted));
		}

		private static JobWithTasks BuildJob(string jobId, ScriptedTask scripted)
		{
			CreateJobRequest request = scripted.Request;
			var job = new Job(jobId, request.Action, request.Region, request.Targets, request.Meta, JobStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
			var task = new JobTask($"{jobId}-t1", jobId, request.Targets[0], request.Action, request.Region, request.Payload, scripted.Status, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, scripted.Error, scripted.Output);
			return new JobWithTasks(job, [task]);
		}

		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobTask>>([]);
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskCancel>>([]);
		public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<JobTaskStatus, int>>(new Dictionary<JobTaskStatus, int>());
		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => Task.FromResult<Job?>(null);
		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => Task.FromResult<JobTask?>(null);
		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);
		public Task MarkTaskClaimedBy(string taskId, int attempt, string agentId, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<Dictionary<string, int>> CountRunningTasksByAgent(CancellationToken cancellationToken) => Task.FromResult(new Dictionary<string, int>());
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(int Jobs, int Tasks)> PurgeSecretPayloadsAsync(bool dryRun, string? backupPath, CancellationToken cancellationToken) => Task.FromResult((0, 0));
	}

}
