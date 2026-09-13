using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// Boots a real Control Plane process plus one Agent process against throwaway SQLite
/// databases and an empty plugin directory. Shared by all tests in the E2E collection.
/// </summary>
public sealed class E2EStack : IAsyncLifetime
{
	public const string AdminApiKey = "admin-e2e-secret";
	public const string AgentApiKey = "agent-e2e-secret";
	public const string AgentId = "e2e-agent-1";
	public const string AgentRegion = "e2e-region";

	public string BaseUrl { get; private set; } = string.Empty;
	public HttpClient Http { get; private set; } = null!;

	private VaporProcess? _controlPlane;
	private VaporProcess? _agent;
	private string _workDir = string.Empty;

	public async Task InitializeAsync()
	{
		int port = TestInfrastructure.FindFreePort();
		_workDir = TestInfrastructure.CreateTempWorkDir();
		BaseUrl = $"http://127.0.0.1:{port}";

		string controlPlaneDll = TestInfrastructure.FindAppDll("Vapor.ControlPlane", "Vapor.ControlPlane.dll");

		_controlPlane = VaporProcess.Start(
			controlPlaneDll,
			new Dictionary<string, string>
			{
				["ASPNETCORE_URLS"] = BaseUrl,
				["Vapor_ADMIN_API_KEY"] = AdminApiKey,
				["Vapor_AGENT_API_KEYS"] = AgentApiKey,
				["Vapor_DB_PATH"] = Path.Combine(_workDir, "controlplane.db"),
				["Vapor_AUDIT_DB_PATH"] = Path.Combine(_workDir, "audit.db"),
				// Fail undispatchable tasks quickly so the negative-path test observes the
				// terminal state within seconds instead of the production default (10 × 2s).
				["Vapor_TASK_MAX_DISPATCH_ATTEMPTS"] = "3",
				["Vapor_TASK_DISPATCH_RETRY_DELAY_MS"] = "200",
				// Account orchestration: aggressive timing so orchestration tests observe
				// reconciliation within seconds instead of the production default (15s interval).
				["Vapor_RECONCILE_INTERVAL_SECONDS"] = "2",
				["Vapor_RECONCILE_LOGIN_COOLDOWN_SECONDS"] = "3",
				["Vapor_RECONCILE_MAX_LOGIN_ATTEMPTS"] = "50",
			},
			Path.Combine(_workDir, "controlplane.log"));

		try
		{
			Http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
			await WaitForControlPlaneAsync();
			_agent = await StartAgentAsync(AgentId, AgentRegion);
		}
		catch
		{
			await DisposeAsync();
			throw;
		}
	}

	private async Task WaitForControlPlaneAsync()
	{
		Exception? lastError = null;
		for (int attempt = 0; attempt < 150; attempt++)
		{
			if (_controlPlane!.HasExited)
			{
				throw new InvalidOperationException(
					$"Control plane exited early (code {_controlPlane.ExitCode}). Log:{Environment.NewLine}{_controlPlane.ReadLogTail()}");
			}

			try
			{
				using var response = await Http.GetAsync("/healthz");
				if (response.IsSuccessStatusCode)
				{
					return;
				}
			}
			catch (Exception ex)
			{
				lastError = ex;
			}

			await Task.Delay(200);
		}

		throw new TimeoutException(
			$"Control plane did not become healthy within 30s. Last error: {lastError}. Log:{Environment.NewLine}{_controlPlane!.ReadLogTail()}");
	}

	/// <summary>
	/// Starts an additional agent process against the shared control plane for tests that
	/// need multi-agent scenarios (e.g. orchestration rebalancing). The caller owns disposal.
	/// </summary>
	internal async Task<VaporProcess> StartAgentAsync(string agentId, string region)
	{
		string agentDll = TestInfrastructure.FindAppDll("Vapor.Agent", "Vapor.Agent.dll");
		string homeDir = Path.Combine(_workDir, $"home-{agentId}");
		Directory.CreateDirectory(homeDir);

		VaporProcess agent = VaporProcess.Start(
			agentDll,
			new Dictionary<string, string>
			{
				["AGENT_ID"] = agentId,
				["AGENT_REGION"] = region,
				["AGENT_CONTROLPLANE_WS_URL"] = $"{BaseUrl.Replace("http://", "ws://")}/v1/agent/ws",
				["AGENT_API_KEY"] = AgentApiKey,
				["VAPOR_PLUGINS_DIR"] = Path.Combine(_workDir, "plugins-empty"),
				// Isolate FileCredentialStore (~/.vapor) and any user-profile writes from the developer machine.
				["HOME"] = homeDir,
				["USERPROFILE"] = homeDir,
			},
			Path.Combine(_workDir, $"{agentId}.log"));

		try
		{
			await WaitForAgentOnlineAsync(agent, agentId);
		}
		catch
		{
			agent.Dispose();
			throw;
		}

		return agent;
	}

	private async Task WaitForAgentOnlineAsync(VaporProcess process, string agentId)
	{
		for (int attempt = 0; attempt < 150; attempt++)
		{
			if (process.HasExited)
			{
				throw new InvalidOperationException(
					$"Agent '{agentId}' exited early (code {process.ExitCode}). Log:{Environment.NewLine}{process.ReadLogTail()}");
			}

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/agents");
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminApiKey);
				using var response = await Http.SendAsync(request);
				response.EnsureSuccessStatusCode();

				using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
				if (doc.RootElement.TryGetProperty("agents", out var agents))
				{
					foreach (var agent in agents.EnumerateArray())
					{
						if (agent.TryGetProperty("agentId", out var id) && id.GetString() == agentId)
						{
							// The scheduler only routes actions the agent declared in its hello;
							// require the built-in echo action before tests start creating jobs.
							bool supportsEcho = agent.TryGetProperty("capabilities", out var capabilities) &&
								capabilities.EnumerateObject().Any(c =>
									c.Name.Equals("echo", StringComparison.OrdinalIgnoreCase) &&
									c.Value.ValueKind == JsonValueKind.True);

							if (supportsEcho)
							{
								return;
							}

							throw new InvalidOperationException(
								$"Agent '{agentId}' registered without 'echo' capability: {agent.GetRawText()}");
						}
					}
				}
			}
			catch (Exception ex) when (ex is HttpRequestException or JsonException)
			{
				// Agent may not be registered yet; keep polling.
			}

			await Task.Delay(200);
		}

		throw new TimeoutException(
			$"Agent '{agentId}' never registered with the control plane. Agent log:{Environment.NewLine}{process.ReadLogTail()}");
	}

	public async Task<JsonElement> CreateJobAsync(object request)
	{
		using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs") { Content = JsonContent.Create(request) };
		msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminApiKey);
		using var response = await Http.SendAsync(msg);

		if (!response.IsSuccessStatusCode)
		{
			string body = await response.Content.ReadAsStringAsync();
			throw new InvalidOperationException($"POST /v1/jobs failed ({response.StatusCode}): {body}");
		}

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
		return doc.RootElement.Clone();
	}

	public async Task<(string Status, JsonElement Payload)> WaitForJobCompletionAsync(
		string jobId,
		string[] terminalStatuses,
		TimeSpan? timeout = null)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));
		string status = "<unknown>";
		string lastBody = "<no response>";

		while (DateTimeOffset.UtcNow < deadline)
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/jobs/{jobId}");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminApiKey);
			using var response = await Http.SendAsync(request);
			response.EnsureSuccessStatusCode();

			lastBody = await response.Content.ReadAsStringAsync();
			using var doc = JsonDocument.Parse(lastBody);
			var tasks = doc.RootElement.GetProperty("tasks");

			if (tasks.GetArrayLength() > 0)
			{
				var task = tasks[0];
				status = task.GetProperty("status").GetString() ?? "<null>";

				// The API serializes enum values in camelCase ("finished"); compare insensitively.
				if (terminalStatuses.Any(t => t.Equals(status, StringComparison.OrdinalIgnoreCase)))
				{
					return (status, task.Clone());
				}
			}

			await Task.Delay(250);
		}

		throw new TimeoutException(
			$"Job {jobId} did not reach [{string.Join('/', terminalStatuses)}] within the deadline (last status: {status}). Body:{Environment.NewLine}{lastBody}{Environment.NewLine}{Diagnostics()}");
	}

	public async Task<(string JobStatus, JsonElement Job, string? TaskStatus, int TaskAttempt)> GetJobStateAsync(string jobId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/jobs/{jobId}");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminApiKey);
		using var response = await Http.SendAsync(request);
		response.EnsureSuccessStatusCode();

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
		JsonElement root = doc.RootElement.Clone();
		string jobStatus = root.GetProperty("job").GetProperty("status").GetString() ?? "<null>";

		string? taskStatus = null;
		int taskAttempt = 0;
		var tasks = root.GetProperty("tasks");
		if (tasks.GetArrayLength() > 0)
		{
			taskStatus = tasks[0].GetProperty("status").GetString();
			taskAttempt = tasks[0].GetProperty("attempt").GetInt32();
		}

		return (jobStatus, root, taskStatus, taskAttempt);
	}

	public async Task CancelJobAsync(string jobId)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/{jobId}/cancel");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminApiKey);
		using var response = await Http.SendAsync(request);
		response.EnsureSuccessStatusCode();
	}

	public async Task<JsonElement> GetAuditLogsAsync(string action)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/audit/logs?action={Uri.EscapeDataString(action)}&limit=200");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminApiKey);
		using var response = await Http.SendAsync(request);
		response.EnsureSuccessStatusCode();

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
		return doc.RootElement.Clone();
	}

	public string Diagnostics() =>
		$"ControlPlane log:{Environment.NewLine}{_controlPlane?.ReadLogTail()}{Environment.NewLine}" +
		$"Agent log:{Environment.NewLine}{_agent?.ReadLogTail()}";

	public async Task DisposeAsync()
	{
		Http?.Dispose();
		_agent?.Dispose();
		_controlPlane?.Dispose();

		try
		{
			if (Directory.Exists(_workDir))
			{
				Directory.Delete(_workDir, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best-effort cleanup; temp dirs are cleaned by the OS eventually.
			// Windows raises UnauthorizedAccessException for directories with open files.
		}
	}
}

[CollectionDefinition("E2E")]
public sealed class E2ECollection : ICollectionFixture<E2EStack>;
