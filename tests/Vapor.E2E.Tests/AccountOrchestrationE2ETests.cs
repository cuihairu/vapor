using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// End-to-end tests for the desired-state orchestrator: declaring an account online via
/// the REST API must get it assigned to a capable agent with a dispatched login job, and
/// killing that agent must rebalance the account to a surviving agent.
/// </summary>
[Collection("E2E")]
public sealed class AccountOrchestrationE2ETests
{
	private const string AccountName = "e2e-orch-alice";
	private const string RebalanceRegion = "e2e-rebalance";
	private static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(60);

	private readonly E2EStack _stack;

	public AccountOrchestrationE2ETests(E2EStack stack)
	{
		_stack = stack;
	}

	[Fact]
	public async Task DeclaredOnlineAccount_IsAssignedAndRebalancesAfterAgentLoss()
	{
		// Dedicated region + agents so the orchestrator's deterministic pick (least-loaded,
		// then agent id order) always selects agent-loss-1 first, and the shared stack agent
		// is never a rebalance target.
		using VaporProcess lossAgent = await _stack.StartAgentAsync("agent-loss-1", RebalanceRegion);
		using VaporProcess spareAgent = await _stack.StartAgentAsync("agent-loss-2", RebalanceRegion);

		try
		{
			await AdminJsonAsync(HttpMethod.Put, $"/v1/accounts/{AccountName}", new
			{
				enabled = true,
				desiredState = "online",
				region = RebalanceRegion,
			});

			// The reconciler assigns the account and dispatches an orchestrated login job.
			JsonElement assigned = await WaitForAssignmentAsync("agent-loss-1");

			Assert.Equal("online", assigned.GetProperty("spec").GetProperty("desiredState").GetString());

			JsonElement[] initialLoginJobs = (await AdminJsonAsync(HttpMethod.Get, $"/v1/jobs?account={AccountName}"))
				.GetProperty("jobs").EnumerateArray()
				.Where(j => j.GetProperty("action").GetString() == "login")
				.ToArray();
			Assert.NotEmpty(initialLoginJobs);
			Assert.Equal(
				"desired-state",
				initialLoginJobs[0].GetProperty("meta").GetProperty("orchestrator").GetString(),
				ignoreCase: true);

			// Kill the assigned agent: the account must be rebalanced to the surviving one.
			lossAgent.Dispose();

			await WaitForAssignmentAsync("agent-loss-2");

			// The rebalance dispatched a fresh login job for the account.
			JsonElement afterLoss = await AdminJsonAsync(HttpMethod.Get, $"/v1/jobs?account={AccountName}");
			int loginJobs = afterLoss.GetProperty("jobs").EnumerateArray()
				.Count(j => j.GetProperty("action").GetString() == "login");
			Assert.True(loginJobs >= 2, $"expected at least 2 orchestrated login jobs after rebalance, found {loginJobs}");

			// The reconciliation decisions are auditable.
			JsonElement audit = await _stack.GetAuditLogsAsync("account.reconciled");
			bool rebalanceAudited = audit.GetProperty("logs").EnumerateArray().Any(entry =>
				entry.TryGetProperty("accountName", out var entryAccount) &&
				entryAccount.GetString() == AccountName &&
				entry.TryGetProperty("details", out var details) &&
				details.GetRawText().Contains("rebalanced", StringComparison.Ordinal));
			Assert.True(rebalanceAudited,
				$"No 'rebalanced' audit entry found for {AccountName}. Entries:{Environment.NewLine}{audit.GetRawText()}");
		}
		finally
		{
			await AdminAsync(HttpMethod.Delete, $"/v1/accounts/{AccountName}");
		}
	}

	/// <summary>Polls the account aggregate view until the orchestrator assigns the expected agent.</summary>
	private async Task<JsonElement> WaitForAssignmentAsync(string expectedAgent)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow + ReconcileTimeout;
		string? lastAgent = "<no orchestration view>";

		while (DateTimeOffset.UtcNow < deadline)
		{
			using HttpResponseMessage response = await AdminAsync(HttpMethod.Get, $"/v1/accounts/{AccountName}");
			response.EnsureSuccessStatusCode();
			using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

			if (doc.RootElement.TryGetProperty("orchestration", out var orchestration) &&
				orchestration.ValueKind == JsonValueKind.Object &&
				orchestration.TryGetProperty("assignedAgent", out var assignedAgent))
			{
				lastAgent = assignedAgent.GetString();
				if (string.Equals(lastAgent, expectedAgent, StringComparison.Ordinal))
				{
					return doc.RootElement.Clone();
				}
			}

			await Task.Delay(500);
		}

		throw new TimeoutException(
			$"Account '{AccountName}' was never assigned to '{expectedAgent}' within {ReconcileTimeout.TotalSeconds:s}s (last assignment: {lastAgent}).{Environment.NewLine}{_stack.Diagnostics()}");
	}

	private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, object? body = null)
	{
		using var request = new HttpRequestMessage(method, path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", E2EStack.AdminApiKey);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		HttpResponseMessage response = await _stack.Http.SendAsync(request);
		if (!response.IsSuccessStatusCode && method != HttpMethod.Delete)
		{
			string errorBody = await response.Content.ReadAsStringAsync();
			throw new InvalidOperationException($"{method} {path} failed ({response.StatusCode}): {errorBody}");
		}

		return response;
	}

	private async Task<JsonElement> AdminJsonAsync(HttpMethod method, string path, object? body = null)
	{
		using HttpResponseMessage response = await AdminAsync(method, path, body);
		response.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
		return doc.RootElement.Clone();
	}
}
