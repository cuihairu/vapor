using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Covers the Program.cs endpoint branches the happy-path suites don't reach:
/// auth failures on every admin route, per-route validation and not-found
/// responses, SSE streams, the agent WebSocket lifecycle, audit-persistence
/// failure handling and the live (non-JsonElement) task-output shapes.
/// </summary>
// Shares the static AccountTaskRunner.WaitWindow/PollInterval knobs with
// AccountApiTests (see AccountTaskWaitWindowCollection).
[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed partial class ProgramBranchCoverageTests
{
	// ── auth matrix ──────────────────────────────────────────────────────────
	[Theory]
	[InlineData("GET", "/v1/agents")]
	[InlineData("PUT", "/v1/config/global")]
	[InlineData("PUT", "/v1/config/account/alice")]
	[InlineData("GET", "/v1/accounts/alice")]
	[InlineData("DELETE", "/v1/accounts/alice")]
	[InlineData("POST", "/v1/accounts/alice/enable")]
	[InlineData("POST", "/v1/accounts/alice/disable")]
	[InlineData("POST", "/v1/accounts/alice/reconcile")]
	[InlineData("GET", "/v1/accounts/alice/inventory")]
	[InlineData("POST", "/v1/accounts/alice/confirmations/accept-all")]
	[InlineData("POST", "/v1/accounts/alice/loot")]
	[InlineData("GET", "/v1/accounts/alice/duplicates")]
	[InlineData("POST", "/v1/accounts/alice/swap-offers")]
	[InlineData("POST", "/v1/accounts/alice/licenses")]
	[InlineData("POST", "/v1/jobs")]
	[InlineData("POST", "/v1/jobs/secret-payloads/purge")]
	[InlineData("GET", "/v1/jobs")]
	[InlineData("GET", "/v1/jobs/job-1")]
	[InlineData("POST", "/v1/jobs/job-1/cancel")]
	[InlineData("GET", "/v1/jobs/job-1/events")]
	[InlineData("GET", "/v1/jobs/events")]
	[InlineData("GET", "/v1/sessions/events")]
	[InlineData("GET", "/v1/auth/challenges/events")]
	[InlineData("GET", "/v1/auth/challenges")]
	[InlineData("POST", "/v1/auth/challenges/alice/code")]
	[InlineData("GET", "/v1/agents/status")]
	[InlineData("POST", "/v1/sessions/events")]
	[InlineData("GET", "/v1/sessions")]
	[InlineData("GET", "/v1/agent/ws")]
	public async Task AdminRoutes_WithoutToken_Return401(string method, string path)
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await SendAsync(client, method, path, body: new { });

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[InlineData("Bearer admin-token", HttpStatusCode.OK)]
	[InlineData("admin-token", HttpStatusCode.OK)]
	[InlineData("   ", HttpStatusCode.Unauthorized)]
	public async Task AuthorizationQueryParameter_IsAcceptedLikeHeader(string queryValue, HttpStatusCode expected)
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync($"/v1/sessions?authorization={Uri.EscapeDataString(queryValue)}");

		Assert.Equal(expected, response.StatusCode);
	}

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path, object? body)
	{
		return method switch
		{
			"GET" => await client.GetAsync(path),
			"DELETE" => await client.DeleteAsync(path),
			"POST" => await client.PostAsJsonAsync(path, body),
			_ => await client.PutAsJsonAsync(path, body)
		};
	}

	// ── config endpoints ─────────────────────────────────────────────────────
	[Fact]
	public async Task PutAccountConfig_WhitespaceName_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/config/account/%20", new { enabled = true });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("account name is required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task PutGlobalConfig_MixedSettingValueTypes_RoundTrip()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");
		// The audit entry's actor comes from X-Forwarded-For when present.
		client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

		using HttpResponseMessage put = await client.PutAsJsonAsync("/v1/config/global", new
		{
			settings = new Dictionary<string, object?>
			{
				["enabled"] = true,
				["name"] = "prod",
				["missing"] = null,
				["retries"] = 3
			},
			updatedBy = "tester"
		});

		Assert.Equal(HttpStatusCode.OK, put.StatusCode);

		// The audit write above recorded the forwarded actor (the connection IP stays local).
		IReadOnlyList<AuditEntry> audits = await factory.AuditStore!.QueryAsync(new AuditQuery(Action: "config.global.updated"), CancellationToken.None);
		AuditEntry entry = Assert.Single(audits);
		Assert.Equal("203.0.113.7", entry.Actor);

		using HttpResponseMessage get = await client.GetAsync("/v1/config");
		string body = await get.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal(4, doc.RootElement.GetProperty("global").GetProperty("settings").EnumerateObject().Count());
	}

	// ── account endpoints ────────────────────────────────────────────────────
	[Fact]
	public async Task PutAccount_WhitespaceName_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/accounts/%20", new { desiredState = "offline" });

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("account name is required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task AccountMutations_OnUndeclaredAccount_Return404()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage removed = await client.DeleteAsync("/v1/accounts/ghost");
		using HttpResponseMessage enabled = await client.PostAsync("/v1/accounts/ghost/enable", content: null);
		using HttpResponseMessage disabled = await client.PostAsync("/v1/accounts/ghost/disable", content: null);

		Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, enabled.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
	}

	[Fact]
	public void GetAuthorization_QueryKeyWithEmptyValueCollection_YieldsEmpty()
	{
		// Defensive-arm contract: an ASP.NET query collection never yields a key
		// with an empty value bundle, so the `token.Count > 0` guard on the
		// ?authorization= fallback has no natural false side. Drive it directly
		// with that impossible shape so the guard stays pinned (see
		// tests/TESTING.md).
		var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
		ctx.Request.Query = new EmptyValueQueryCollection();

		Microsoft.Extensions.Primitives.StringValues result = EndpointHelpers.GetAuthorization(ctx);

		Assert.True(result == Microsoft.Extensions.Primitives.StringValues.Empty);
	}

	/// <summary>A query collection whose only key maps to no values at all — the
	/// shape the <c>token.Count &gt; 0</c> guard in <c>GetAuthorization</c> exists for.</summary>
	private sealed class EmptyValueQueryCollection : Microsoft.AspNetCore.Http.IQueryCollection
	{
		public Microsoft.Extensions.Primitives.StringValues this[string key] => default;
		public ICollection<string> Keys { get; } = new[] { "authorization" };
		public int Count => 0;
		public bool ContainsKey(string key) => string.Equals(key, "authorization", StringComparison.Ordinal);
		public bool TryGetValue(string key, out Microsoft.Extensions.Primitives.StringValues value)
		{
			value = default;
			return string.Equals(key, "authorization", StringComparison.Ordinal);
		}
		public IEnumerator<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> GetEnumerator()
		{
			yield return new("authorization", default(Microsoft.Extensions.Primitives.StringValues));
		}
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	[Fact]
	public async Task AuthCode_SubmissionVariants()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage noCode = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { type = "email" });
		using HttpResponseMessage defaultType = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { code = "123456" });
		using HttpResponseMessage totp = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { code = "123456", type = "totp" });
		using HttpResponseMessage invalidType = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new { code = "123456", type = "sms" });

		Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
		Assert.Contains("code is required", await noCode.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.OK, defaultType.StatusCode);
		Assert.Equal(HttpStatusCode.OK, totp.StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, invalidType.StatusCode);
		Assert.Contains("type must be one of", await invalidType.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task PutGlobalConfig_ReplacesSettings_AndAudits()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/config/global", new
		{
			settings = new Dictionary<string, object?> { ["farmedAppIds"] = new[] { 730 } },
			updatedBy = "operator"
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(await PollAuditAsync(factory, "config.global.updated"));
	}

	[Fact]
	public async Task PutAccountConfig_ReplacesAccountSettings_AndAudits()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PutAsJsonAsync("/v1/config/account/bob", new
		{
			enabled = false,
			region = "eu-west",
			labels = new[] { "veteran" },
			settings = new Dictionary<string, object?> { ["k"] = "v" },
			updatedBy = "operator"
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		Assert.NotNull(await PollAuditAsync(factory, "config.account.updated"));
	}

	// ── helpers ──────────────────────────────────────────────────────────────
	/// <summary>Polls the audit store until at least one entry with <paramref name="action"/> lands.</summary>
	private static async Task<AuditEntry?> PollAuditAsync(BranchFactory factory, string action)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (DateTimeOffset.UtcNow < deadline)
		{
			IReadOnlyList<AuditEntry> entries = await factory.AuditStore!.QueryAsync(new AuditQuery(Action: action), CancellationToken.None);
			if (entries.Count > 0)
			{
				return entries[^1];
			}

			await Task.Delay(25);
		}

		return null;
	}

}

internal sealed class BranchFactory : WebApplicationFactory<Program>
{
	public ProgramBranchCoverageTests.BranchEventBroker Events { get; } = new();

	/// <summary>Optional store override; a fresh :memory: SQLite store is used when unset.</summary>
	public IJobStore? JobStore { get; init; }

	/// <summary>Optional audit store override (exposed for assertions and fault injection).</summary>
	public IAuditStore? AuditStore { get; internal set; }

	/// <summary>Optional agent registry override; a fresh registry is used when unset.</summary>
	public AgentRegistry? AgentRegistry { get; init; }

	/// <summary>
	/// Handle onto the agent WebSocket request's abort signal: the middleware
	/// below replaces the endpoint's RequestAborted with a linked source a test
	/// can cancel deterministically from inside a store call. TestServer's
	/// client-side <c>WebSocket.Abort()</c> races the socket teardown and can
	/// surface as a read exception instead of a graceful condition exit. Null
	/// until the first /v1/agent/ws request has passed the pipeline.
	/// </summary>
	public CancellationTokenSource? RequestAbortControl { get; private set; }

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		BranchFactory self = this;
		builder.UseEnvironment("Development");
		builder.ConfigureServices(services =>
		{
			services.RemoveAll<IJobStore>();
			services.RemoveAll<IEventBroker>();
			services.RemoveAll<IAuditStore>();
			services.RemoveAll<AgentRegistry>();
			services.RemoveAll<IHostedService>();
			services.RemoveAll<IHostedLifecycleService>();
			services.AddSingleton(new Config("admin-token", new Dictionary<string, DateTimeOffset?> { ["agent-token"] = null }, ":memory:", 300, false, ":memory:", CrawlDbPath: ":memory:", ConfigDbPath: ":memory:", AgentKeyAgentIds: new Dictionary<string, string> { ["agent-token"] = "agent-9" }));
			services.AddSingleton<IJobStore>(sp => self.JobStore ?? new SqliteJobStore(":memory:"));
			services.AddSingleton<IAuditStore>(sp => self.AuditStore ??= new SqliteAuditStore(":memory:"));
			services.AddSingleton<IEventBroker>(self.Events);
			services.AddSingleton<AgentRegistry>(sp => self.AgentRegistry ?? new AgentRegistry());
			services.AddSingleton<IStartupFilter>(new WsRequestAbortCapture(this));
		});
	}

	/// <summary>
	/// Swaps the agent WebSocket request's RequestAborted for a linked,
	/// test-controlled source (captured on <see cref="BranchFactory"/>).
	/// Injected as a startup filter so it wraps Program's pipeline instead of
	/// replacing it — a <c>builder.Configure</c> middleware branch loses the
	/// mapped endpoints (every WS upgrade 404s).
	/// </summary>
	private sealed class WsRequestAbortCapture(BranchFactory factory) : IStartupFilter
	{
		public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
		{
			app.Use(async (ctx, nextMiddleware) =>
			{
				if (ctx.Request.Path.StartsWithSegments("/v1/agent/ws"))
				{
					CancellationTokenSource control = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
					factory.RequestAbortControl = control;
					ctx.RequestAborted = control.Token;
				}

				await nextMiddleware();
			});
			next(app);
		};
	}

}

/// <summary>WebSocket that never drains the send channel (keeps the queue full).</summary>
internal sealed class CancelNoopWebSocket : System.Net.WebSockets.WebSocket
{
	public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
	public override string? CloseStatusDescription => null;
	public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
	public override string SubProtocol => string.Empty;
	public override void Abort() { }
	public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
	public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
	public override void Dispose() { }
	public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => Task.FromResult(new System.Net.WebSockets.WebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true));
	public override ValueTask<System.Net.WebSockets.ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) => ValueTask.FromResult(new System.Net.WebSockets.ValueWebSocketReceiveResult(0, System.Net.WebSockets.WebSocketMessageType.Close, true));
	public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
	public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
