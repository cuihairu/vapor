using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

static string RequireEnv(string key) => Environment.GetEnvironmentVariable(key) switch {
	{ Length: > 0 } v => v,
	_ => throw new InvalidOperationException($"{key} is required")
};

string agentId = RequireEnv("AGENT_ID");
string region = RequireEnv("AGENT_REGION");
string wsUrlBase = RequireEnv("AGENT_CONTROLPLANE_WS_URL");
string agentApiKey = RequireEnv("AGENT_API_KEY");

var serviceProvider = new ServiceCollection()
	.AddLogging(configure => configure.AddConsole())
	.AddSingleton<IActionRegistry, ActionRegistry>()
	.AddSingleton<ICredentialStore, FileCredentialStore>()
	.AddSingleton<ISessionManager>(p => new SessionManager(
		p.GetRequiredService<IActionRegistry>(),
		p.GetRequiredService<ILogger<SessionManager>>(),
		p.GetRequiredService<ISteamClientManager>(),
		p.GetRequiredService<ICredentialStore>(),
		p.GetRequiredService<ILoggerFactory>()
	))
	.AddSingleton<SteamClientManager>()
	.AddSingleton<ISteamClientManager>(p => p.GetRequiredService<SteamClientManager>())
	.AddSingleton<PingAction>()
	.AddSingleton<IdleAction>()
	.AddSingleton<EchoAction>()
	.AddSingleton<LoginAction>()
	.AddSingleton<PlayGamesAction>()
	.AddSingleton<RedeemKeyAction>()
	.AddSingleton<GetInventoryAction>()
	.AddSingleton<SendTradeOfferAction>()
	.AddSingleton<AcceptTradeOfferAction>()
	.AddSingleton<DeclineTradeOfferAction>()
	.AddSingleton<CancelTradeOfferAction>()
	.BuildServiceProvider();

var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var actionRegistry = serviceProvider.GetRequiredService<IActionRegistry>();
var sessionManager = serviceProvider.GetRequiredService<ISessionManager>();

// Set up session event callback to publish to Control Plane
sessionManager.SetEventCallback(async (accountName, eventType, state, message) =>
{
	// Will be called when session state changes or auth challenges occur
	logger.LogInformation("Session event: {AccountName} - {EventType} - {State}", accountName, eventType, state);

	// Send session event to Control Plane via HTTP
	await PublishSessionEventAsync(wsUrlBase, agentApiKey, accountName, eventType, state, message, logger);
});

actionRegistry.Register(serviceProvider.GetRequiredService<PingAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<IdleAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<EchoAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<LoginAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<PlayGamesAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<RedeemKeyAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<GetInventoryAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<SendTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<AcceptTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<DeclineTradeOfferAction>());
actionRegistry.Register(serviceProvider.GetRequiredService<CancelTradeOfferAction>());

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Start one background task to listen for auth challenge events via SSE.
_ = Task.Run(() => PollAuthChallengesAsync(agentId, region, wsUrlBase, agentApiKey, sessionManager, logger, cts.Token), cts.Token);

TimeSpan backoff = TimeSpan.FromMilliseconds(500);
while (!cts.IsCancellationRequested) {
	try {
		await RunOnce(cts.Token);
		backoff = TimeSpan.FromMilliseconds(500);
	} catch (OperationCanceledException) when (cts.IsCancellationRequested) {
		break;
	} catch (Exception ex) {
		Console.Error.WriteLine($"agent disconnected: {ex.Message}");
		await Task.Delay(backoff, cts.Token);
		backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 10_000));
	}
}

async Task RunOnce(CancellationToken cancellationToken) {
	Uri uri = BuildUri(wsUrlBase, agentId, region);

	using ClientWebSocket ws = new();
	ws.Options.SetRequestHeader("Authorization", $"Bearer {agentApiKey}");

	Console.WriteLine($"connecting: {uri}");
	await ws.ConnectAsync(uri, cancellationToken);

	using SemaphoreSlim sendGate = new(1, 1);
	var tasks = Channel.CreateUnbounded<JobTask>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

	var executionGate = new object();
	CancellationTokenSource? currentTaskCts = null;
	string? currentTaskId = null;
	int currentAttempt = 0;

	var capabilities = actionRegistry.ListNames().ToDictionary(name => name, _ => true, StringComparer.OrdinalIgnoreCase);
	var hello = new AgentHello(agentId, region, capabilities, null);
	await SendLocked(ws, sendGate, new WSMessage("hello", hello, null, null), cancellationToken);

	var receiver = Task.Run(async () => {
		try {
			while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open) {
				WSMessage msg = await Receive<WSMessage>(ws, cancellationToken);
				if (string.Equals(msg.Type, "task", StringComparison.Ordinal) && msg.Task != null) {
					await tasks.Writer.WriteAsync(msg.Task, cancellationToken);
					continue;
				}

				if (string.Equals(msg.Type, "task_cancel", StringComparison.Ordinal) && msg.TaskCancel != null) {
					bool matches;
					lock (executionGate) {
						matches =
							currentTaskCts != null &&
							string.Equals(currentTaskId, msg.TaskCancel.TaskId, StringComparison.Ordinal) &&
							currentAttempt == msg.TaskCancel.Attempt;
					}

					if (matches) {
						try {
							currentTaskCts!.Cancel();
						} catch {
						}
					}
				}
			}
		} catch {
			// Receiver loop stops; outer loop will reconnect.
		} finally {
			tasks.Writer.TryComplete();
		}
	}, cancellationToken);

	try {
		while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open) {
			JobTask task = await tasks.Reader.ReadAsync(cancellationToken);
			Console.WriteLine($"task received: id={task.Id} action={task.Action} target={task.Target}");

			using var executeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, executeCts.Token);

			lock (executionGate) {
				currentTaskCts = executeCts;
				currentTaskId = task.Id;
				currentAttempt = task.Attempt;
			}

			var heartbeatTask = Task.Run(
				() => HeartbeatLoop(ws, sendGate, task, heartbeatCts.Token),
				heartbeatCts.Token
			);

			bool success;
			string? error;
			IReadOnlyDictionary<string, object?>? output;
			try {
				(success, error, output) = await Execute(
					task,
					sessionManager,
					logger,
					executeCts.Token
				);
			} finally {
				lock (executionGate) {
					currentTaskCts = null;
					currentTaskId = null;
					currentAttempt = 0;
				}
			}

			TaskResult result = new(
				TaskId: task.Id,
				Success: success,
				Error: error,
				Output: output,
				FinishedAt: DateTimeOffset.UtcNow,
				Attempt: task.Attempt
			);

			heartbeatCts.Cancel();
			try {
				await heartbeatTask;
			} catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested) {
			}

			if (!executeCts.IsCancellationRequested) {
				await SendLocked(ws, sendGate, new WSMessage("task_result", null, null, result), cancellationToken);
			}
		}
	} finally {
		try {
			await receiver;
		} catch {
		}
	}
}

static Uri BuildUri(string baseUrl, string agentId, string region) {
	var baseUri = new Uri(baseUrl);
	var ub = new UriBuilder(baseUri);

	string qs = ub.Query;
	if (qs.StartsWith('?')) {
		qs = qs[1..];
	}

	var parts = new List<string>();
	if (!string.IsNullOrEmpty(qs)) {
		parts.Add(qs);
	}

	parts.Add($"agentId={Uri.EscapeDataString(agentId)}");
	parts.Add($"region={Uri.EscapeDataString(region)}");

	ub.Query = string.Join('&', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
	return ub.Uri;
}

static async Task<(bool Success, string? Error, IReadOnlyDictionary<string, object?>? Output)> Execute(
	JobTask task,
	ISessionManager sessionManager,
	ILogger logger,
	CancellationToken cancellationToken)
{
	string action = task.Action.Trim().ToLowerInvariant();
	string accountName = task.Target;

	try
	{
		var payload = task.Payload ?? new Dictionary<string, object?>();

		string password =
			PayloadReader.GetString(payload, "password") ??
			PayloadReader.GetString(payload, "pass") ??
			string.Empty;

		string? accessToken = PayloadReader.GetString(payload, "accessToken") ?? PayloadReader.GetString(payload, "access_token");
		string? refreshToken = PayloadReader.GetString(payload, "refreshToken") ?? PayloadReader.GetString(payload, "refresh_token");

		BotSession session;

		// If only tokens are provided (no password), try to restore from stored credentials
		if (string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(refreshToken))
		{
			logger.LogInformation("Attempting to restore session for {AccountName} using tokens", accountName);

			var credentials = new AccountCredentials(
				AccountName: accountName,
				Password: string.Empty,
				AccessToken: accessToken,
				RefreshToken: refreshToken
			);

			session = await sessionManager.GetOrCreateSessionAsync(
				accountName,
				credentials,
				cancellationToken
			);
		}
		else if (string.IsNullOrEmpty(password))
		{
			// No credentials provided, try to restore from stored credentials
			logger.LogInformation("No credentials provided, attempting to restore session for {AccountName}", accountName);
			var restoredSession = await ((Vapor.Steam.Core.SessionManager)sessionManager).TryRestoreSessionAsync(accountName, cancellationToken);

			if (restoredSession == null)
			{
				return (false, "No credentials provided and no stored session found", null);
			}

			session = restoredSession;
		}
		else
		{
			// Password provided, create new session
			var credentials = new AccountCredentials(
				AccountName: accountName,
				Password: password,
				AuthCode: PayloadReader.GetString(payload, "authCode") ?? PayloadReader.GetString(payload, "auth_code"),
				TwoFactorCode: PayloadReader.GetString(payload, "twoFactorCode") ?? PayloadReader.GetString(payload, "two_factor_code"),
				RefreshToken: refreshToken,
				AccessToken: accessToken
			);

			session = await sessionManager.GetOrCreateSessionAsync(
				accountName,
				credentials,
				cancellationToken
			);
		}

		var result = await session.ExecuteActionAsync(
			action,
			payload,
			cancellationToken
		);

		return (result.Success, result.Error, result.Output);
	}
	catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
	{
		return (false, "canceled", null);
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Execute failed for task {TaskId}", task.Id);
		return (false, ex.Message, null);
	}
}

static async Task<T> Receive<T>(ClientWebSocket ws, CancellationToken cancellationToken) {
	ArraySegment<byte> chunk = new(new byte[16 * 1024]);
	using var ms = new MemoryStream();

	while (true) {
		WebSocketReceiveResult r = await ws.ReceiveAsync(chunk, cancellationToken);
		if (r.MessageType == WebSocketMessageType.Close) {
			throw new IOException("websocket closed");
		}

		ms.Write(chunk.Array!, chunk.Offset, r.Count);
		if (r.EndOfMessage) {
			break;
		}
	}

	return JsonSerializer.Deserialize<T>(ms.ToArray(), JsonDefaults.Options) ?? throw new InvalidOperationException("invalid json");
}

static async Task Send<T>(ClientWebSocket ws, T value, CancellationToken cancellationToken) {
	byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
	await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
}

static async Task SendLocked<T>(ClientWebSocket ws, SemaphoreSlim sendGate, T value, CancellationToken cancellationToken) {
	await sendGate.WaitAsync(cancellationToken);
	try {
		await Send(ws, value, cancellationToken);
	} finally {
		sendGate.Release();
	}
}

static async Task HeartbeatLoop(ClientWebSocket ws, SemaphoreSlim sendGate, JobTask task, CancellationToken cancellationToken) {
	static async Task SendHeartbeat(ClientWebSocket ws, SemaphoreSlim sendGate, JobTask task, CancellationToken cancellationToken) {
		if (ws.State != WebSocketState.Open) {
			return;
		}

		var hb = new TaskHeartbeat(TaskId: task.Id, Attempt: task.Attempt, Ts: DateTimeOffset.UtcNow);
		var msg = new WSMessage(Type: "task_heartbeat", Hello: null, Task: null, TaskResult: null, TaskHeartbeat: hb);
		await SendLocked(ws, sendGate, msg, cancellationToken);
	}

	try {
		await SendHeartbeat(ws, sendGate, task, cancellationToken);

		using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
		while (await timer.WaitForNextTickAsync(cancellationToken)) {
			await SendHeartbeat(ws, sendGate, task, cancellationToken);
		}
	} catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
	} catch {
		// Best-effort: if the websocket is disconnected or errors, don't fail the task itself.
	}
}

	static async Task PollAuthChallengesAsync(
		string agentId,
		string region,
		string wsUrlBase,
		string agentApiKey,
		ISessionManager sessionManager,
		ILogger logger,
		CancellationToken cancellationToken)
	{
	try
	{
		// Build HTTP base URL from WebSocket URL
		var wsUri = new Uri(wsUrlBase);
		var httpScheme = wsUri.Scheme == "wss" ? "https" : "http";
		var httpBaseUrl = $"{httpScheme}://{wsUri.Host}:{wsUri.Port}";

		using var httpClient = new System.Net.Http.HttpClient();
		httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {agentApiKey}");
		httpClient.Timeout = TimeSpan.FromMinutes(5);

		logger.LogInformation("Starting auth challenge polling for agent {AgentId}", agentId);

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				// Connect to auth challenge events stream
				var url = $"{httpBaseUrl}/v1/auth/challenges/events";
				using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
				using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

				response.EnsureSuccessStatusCode();

				using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
				using var reader = new System.IO.StreamReader(stream);

					while (!cancellationToken.IsCancellationRequested && !reader.EndOfStream)
					{
						var line = await reader.ReadLineAsync(cancellationToken);
						if (string.IsNullOrWhiteSpace(line)) continue;

						// Parse SSE format: "event: <type>" then "data: <json>"
						if (line.StartsWith("event: ", StringComparison.Ordinal))
						{
							var eventType = line["event: ".Length..].Trim();
							var dataLine = await reader.ReadLineAsync(cancellationToken);
							if (dataLine?.StartsWith("data: ", StringComparison.Ordinal) == true)
							{
								var jsonData = dataLine["data: ".Length..];
								try
								{
									if (eventType is not ("auth.code_provided_email" or "auth.code_provided_totp" or "auth.code_provided_2fa"))
									{
										continue;
									}

									using var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonData);
									var root = jsonDoc.RootElement;

									if (!root.TryGetProperty("accountName", out var accountNameProp))
									{
										continue;
									}

									var accountName = accountNameProp.GetString();
									if (string.IsNullOrWhiteSpace(accountName))
									{
										continue;
									}

									if (!root.TryGetProperty("code", out var codeProp))
									{
										logger.LogWarning("Auth code event missing code for {AccountName}", accountName);
										continue;
									}

									var code = codeProp.GetString();
									if (string.IsNullOrWhiteSpace(code))
									{
										logger.LogWarning("Auth code event has empty code for {AccountName}", accountName);
										continue;
									}

									var session = await sessionManager.GetSessionAsync(accountName, cancellationToken);
									if (session == null)
									{
										logger.LogWarning("Auth code received but no active session for {AccountName}", accountName);
										continue;
									}

									if (eventType == "auth.code_provided_email")
									{
										logger.LogInformation("Applying email auth code for {AccountName}", accountName);
										session.ProvideAuthCode(code);
									}
									else
									{
										logger.LogInformation("Applying 2FA code for {AccountName}", accountName);
										session.Provide2FACode(code);
									}
								}
								catch (System.Text.Json.JsonException ex)
								{
									logger.LogWarning(ex, "Failed to parse auth challenge event: {Data}", jsonData);
								}
								catch (Exception ex)
								{
									logger.LogWarning(ex, "Failed to handle auth challenge event: {Data}", jsonData);
								}
							}
						}
					}
				}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Auth challenge polling error, will retry");
				await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
			}
		}
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "Auth challenge polling task failed");
	}
}

static async Task PublishSessionEventAsync(string wsUrlBase, string agentApiKey, string accountName, string eventType, string state, string? message, ILogger logger)
{
	try
	{
		// Build HTTP base URL from WebSocket URL
		var wsUri = new Uri(wsUrlBase);
		var httpScheme = wsUri.Scheme == "wss" ? "https" : "http";
		var httpBaseUrl = $"{httpScheme}://{wsUri.Host}:{wsUri.Port}";

		using var httpClient = new System.Net.Http.HttpClient();
		httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {agentApiKey}");
		httpClient.Timeout = TimeSpan.FromSeconds(10);

		var payload = new
		{
			accountName,
			eventType,
			state,
			message,
			timestamp = DateTimeOffset.UtcNow
		};

		var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
		var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

		// POST to a new endpoint that will publish the event
		var response = await httpClient.PostAsync($"{httpBaseUrl}/v1/sessions/events", content);

		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("Failed to publish session event: {StatusCode}", response.StatusCode);
		}
	}
	catch (Exception ex)
	{
		logger.LogWarning(ex, "Failed to publish session event for {AccountName}", accountName);
	}
}
