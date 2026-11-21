using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Proxy endpoint tests: pinning, verification chaining, audit masking.</summary>
public sealed partial class AccountApiTests
{
	[Fact]
	public async Task Proxy_RequiresAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://gw.example.com:1080" });

		Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
	}

	[Fact]
	public async Task Proxy_UnknownAccount_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/ghost/proxy", new { proxy = "socks5://gw.example.com:1080" });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
	}

	[Fact]
	public async Task Proxy_MissingProxy_Returns400()
	{
		// A body-less or null-field call must never silently strip an account's
		// pinned exit: only an explicit empty string means clear.
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage withoutField = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { });
		using HttpResponseMessage withoutBody = await client.PostAsync("/v1/accounts/alice/proxy", null);

		Assert.Equal(HttpStatusCode.BadRequest, withoutField.StatusCode);
		Assert.Contains("proxy is required", await withoutField.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		Assert.Equal(HttpStatusCode.BadRequest, withoutBody.StatusCode);
	}

	[Fact]
	public async Task Proxy_WithoutPinnedAgent_Returns400()
	{
		// The assignment is refused for unpinned accounts: the proxy would
		// otherwise land in whichever agent claims the task, splitting the
		// account's exit identity across machines.
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://gw.example.com:1080" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("no pinned agent", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Proxy_PinnedAgentNotConnected_Returns404()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline", agentId = "agent-1" });

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://gw.example.com:1080" });

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
		Assert.Contains("not connected", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Proxy_AgentReportsFinished_ChainsCheckProxyVerification()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await RegisterPinnedAgentAsync(client, factory);

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("set_proxy", true, new Dictionary<string, object?>
				{
					["account"] = "alice",
					["proxy"] = "socks5://alice:<redacted>@10.0.0.9:1080",
					["cleared"] = false,
					["sessionRestarted"] = true
				}, null),
			new FakeAgentStep("check_proxy", true, new Dictionary<string, object?>
				{
					["proxyEnabled"] = true,
					["proxy"] = "socks5://alice:<redacted>@10.0.0.9:1080",
					["account"] = "alice",
					["exitIp"] = "203.0.113.7",
					["steamReachable"] = true,
					["latencyMs"] = 42L
				}, null)
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://alice:s3cret@10.0.0.9:1080" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		JsonElement result = doc.RootElement.GetProperty("result");
		Assert.Equal("socks5://alice:<redacted>@10.0.0.9:1080", result.GetProperty("proxy").GetString());
		Assert.True(result.GetProperty("sessionRestarted").GetBoolean());

		// The verification is its own job chaining onto the same account.
		JsonElement verification = doc.RootElement.GetProperty("verification");
		Assert.Equal("finished", verification.GetProperty("status").GetString());
		Assert.Equal("203.0.113.7", verification.GetProperty("output").GetProperty("exitIp").GetString());
		Assert.NotEqual(doc.RootElement.GetProperty("job_id").GetString(), verification.GetProperty("job_id").GetString());

		// The set_proxy task must be host-targeted at the pinned agent and carry the
		// proxy endpoint so the agent can persist it in its local credential store
		// (the credential never lingers in the control-plane task queue afterwards).
		string jobId = doc.RootElement.GetProperty("job_id").GetString()!;
		Assert.Equal("socks5://alice:s3cret@10.0.0.9:1080", await PayloadValueFromTask(store, jobId, 0, "proxy"));
		JobWithTasks setJob = await store.GetJob(jobId, CancellationToken.None);
		Assert.Equal("agent:agent-1", setJob.Tasks[0].Target);
		JobWithTasks checkJob = await store.GetJob(verification.GetProperty("job_id").GetString()!, CancellationToken.None);
		Assert.True(checkJob.Tasks[0].Payload is null or { Count: 0 }, "check_proxy must be dispatched payload-free");
	}

	[Fact]
	public async Task Proxy_AgentFailure_Returns502WithoutVerificationChain()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await RegisterPinnedAgentAsync(client, factory);

		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		using var cts = new CancellationTokenSource();
		Task responder = Task.Run(() => RespondToFirstTaskAsync(
			store, "set_proxy", success: false, null,
			"invalid proxy endpoint: expected scheme://host:port (http, https or socks5)", cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://gw.example.com:1080" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		Assert.Contains("scheme://host:port", body, StringComparison.Ordinal);

		// A failed assignment must not fire the check_proxy follow-up.
		string jobId = JsonElement.Parse(body).GetProperty("job_id").GetString()!;
		JobWithTasks job = await store.GetJob(jobId, CancellationToken.None);
		Assert.Single(job.Tasks);
	}

	[Fact]
	public async Task Proxy_VerificationFailure_StillReturns200WithFailedVerification()
	{
		// The assignment landed; a failed connectivity check is reported, not
		// rolled back — the operator decides whether to re-pin.
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await RegisterPinnedAgentAsync(client, factory);

		using var cts = new CancellationTokenSource();
		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("set_proxy", true, new Dictionary<string, object?>
				{
					["account"] = "alice",
					["proxy"] = "socks5://gw.example.com:1080",
					["cleared"] = false,
					["sessionRestarted"] = false
				}, null),
			new FakeAgentStep("check_proxy", false, new Dictionary<string, object?>
				{
					["proxyEnabled"] = true,
					["exitIp"] = null,
					["steamReachable"] = false
				}, "exit-ip probe failed: no route to host")
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://gw.example.com:1080" });
		cts.Cancel();

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		string body = await resp.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.False(doc.RootElement.GetProperty("result").GetProperty("sessionRestarted").GetBoolean());
		JsonElement verification = doc.RootElement.GetProperty("verification");
		Assert.Equal("failed", verification.GetProperty("status").GetString());
		Assert.Contains("no route to host", verification.GetProperty("error").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Proxy_VerificationStillPending_Returns200WithPendingVerification()
	{
		// set_proxy lands but no agent ever answers check_proxy: the endpoint
		// must report the verification as pending, not block past the window
		// and not fail the assignment that already landed.
		AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(2);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await RegisterPinnedAgentAsync(client, factory);

			using var cts = new CancellationTokenSource();
			IJobStore store = factory.Services.GetRequiredService<IJobStore>();
			Task responder = Task.Run(() => RespondToStepsAsync(store,
			[
				new FakeAgentStep("set_proxy", true, new Dictionary<string, object?>
					{
						["account"] = "alice",
						["proxy"] = "socks5://gw.example.com:1080",
						["cleared"] = false,
						["sessionRestarted"] = true
					}, null)
			], cts.Token));

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://gw.example.com:1080" });
			cts.Cancel();

			Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.True(doc.RootElement.GetProperty("result").GetProperty("sessionRestarted").GetBoolean());
			JsonElement verification = doc.RootElement.GetProperty("verification");
			Assert.Equal("pending", verification.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Proxy_StillPending_Returns202()
	{
		AccountTaskRunner.WaitWindow = TimeSpan.FromMilliseconds(400);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await RegisterPinnedAgentAsync(client, factory);

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "" });

			Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.Equal("pending", doc.RootElement.GetProperty("status").GetString());
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Proxy_VerificationStillPending_ReportsPendingVerification()
	{
		// The assignment finished but its check_proxy follow-up is still queued
		// when the bounded window closes: 200 with the assignment result and a
		// pending verification — the operator polls the verification job id.
		AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(2);
		AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			await using var factory = CreateFactory(removeHosted: true);
			using var client = factory.CreateClient();
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
			await RegisterPinnedAgentAsync(client, factory);

			IJobStore store = factory.Services.GetRequiredService<IJobStore>();
			using var cts = new CancellationTokenSource();
			Task responder = Task.Run(() => RespondToFirstTaskAsync(
				store, "set_proxy", success: true,
				new Dictionary<string, object?>
				{
					["account"] = "alice",
					["proxy"] = "socks5://gw.example.com:1080",
					["cleared"] = false,
					["sessionRestarted"] = true
				},
				null, cts.Token));

			using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://gw.example.com:1080" });
			cts.Cancel();

			Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
			string body = await resp.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(body);
			Assert.False(doc.RootElement.GetProperty("result").GetProperty("cleared").GetBoolean());
			JsonElement verification = doc.RootElement.GetProperty("verification");
			Assert.Equal("pending", verification.GetProperty("status").GetString());
			Assert.NotEmpty(verification.GetProperty("job_id").GetString()!);
		}
		finally
		{
			AccountTaskRunner.WaitWindow = TimeSpan.FromSeconds(30);
			AccountTaskRunner.PollInterval = TimeSpan.FromMilliseconds(200);
		}
	}

	[Fact]
	public async Task Proxy_AuditTrail_RecordsMaskedEndpointOnly()
	{
		await using var factory = CreateFactory(removeHosted: true);
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		await RegisterPinnedAgentAsync(client, factory);

		using var cts = new CancellationTokenSource();
		IJobStore store = factory.Services.GetRequiredService<IJobStore>();
		Task responder = Task.Run(() => RespondToStepsAsync(store,
		[
			new FakeAgentStep("set_proxy", true, new Dictionary<string, object?>
				{
					["account"] = "alice",
					["proxy"] = "socks5://alice:<redacted>@10.0.0.9:1080",
					["cleared"] = false,
					["sessionRestarted"] = true
				}, null),
			new FakeAgentStep("check_proxy", true, new Dictionary<string, object?>
				{
					["proxyEnabled"] = true,
					["exitIp"] = "203.0.113.7",
					["steamReachable"] = true
				}, null)
		], cts.Token));

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/accounts/alice/proxy", new { proxy = "socks5://alice:s3cret@10.0.0.9:1080" });
		cts.Cancel();
		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

		using HttpResponseMessage audit = await client.GetAsync("/v1/audit/logs?action=account.proxy_set");
		string auditBody = await audit.Content.ReadAsStringAsync();
		Assert.Contains("<redacted>", auditBody, StringComparison.Ordinal);
		Assert.DoesNotContain("s3cret", auditBody, StringComparison.Ordinal);

		// The verification leg is audited in its own right.
		using HttpResponseMessage checkAudit = await client.GetAsync("/v1/audit/logs?action=account.proxy_check");
		Assert.True(JsonElement.Parse(await checkAudit.Content.ReadAsStringAsync()).GetProperty("total").GetInt32() >= 1);
	}

	/// <summary>Reads one payload value from the n-th task of a job (normalized to string).</summary>
	/// <summary>
	/// Pins alice to agent-1 and registers that agent as connected: the proxy
	/// endpoint refuses assignments for accounts without a pinned agent and for
	/// pins whose agent is not connected — the secret persists in that agent's
	/// local credential store, so both must exist before the task is created.
	/// </summary>
	private static async Task RegisterPinnedAgentAsync(HttpClient client, TestFactory factory)
	{
		await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline", agentId = "agent-1" });
		AgentRegistry registry = factory.Services.GetRequiredService<AgentRegistry>();
		registry.Register(
			new AgentHello("agent-1", "local", new Dictionary<string, bool> { ["set_proxy"] = true }, null),
			new ProxyTestWebSocket(),
			new CancellationTokenSource().Token);
	}

	/// <summary>Stand-in transport for a registered agent; nothing is ever sent over it.</summary>
	private sealed class ProxyTestWebSocket : WebSocket
	{
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override WebSocketState State => WebSocketState.Open;
		public override string SubProtocol => string.Empty;

		public override void Abort()
		{
		}

		public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
		public override void Dispose()
		{
		}
		public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
			Task.FromCanceled<WebSocketReceiveResult>(cancellationToken);
		public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
