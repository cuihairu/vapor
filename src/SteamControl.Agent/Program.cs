using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamControl.Protocol;
using SteamControl.Steam.Core;
using SteamControl.Steam.Core.Actions;
using SteamControl.Steam.Core.Steam;

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
	.AddSingleton<ISessionManager, SessionManager>()
	.AddSingleton<SteamClientManager>()
	.AddSingleton<ISteamClientManager>(p => p.GetRequiredService<SteamClientManager>())
	.AddSingleton<PingAction>()
	.AddSingleton<IdleAction>()
	.AddSingleton<EchoAction>()
	.AddSingleton<LoginAction>()
	.AddSingleton<RedeemKeyAction>()
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
actionRegistry.Register(serviceProvider.GetRequiredService<RedeemKeyAction>());

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

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

	var capabilities = actionRegistry.ListNames().ToDictionary(name => name, _ => true, StringComparer.OrdinalIgnoreCase);
	var hello = new AgentHello(agentId, region, capabilities, null);
	await Send(ws, new WSMessage("hello", hello, null, null), cancellationToken);

	// Start background task to listen for auth challenge events via HTTP polling
_ = Task.Run(() => PollAuthChallengesAsync(agentId, region, wsUrlBase, agentApiKey, sessionManager, logger, cts.Token), cts.Token);

while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open) {
		WSMessage msg = await Receive<WSMessage>(ws, cancellationToken);
		if (!string.Equals(msg.Type, "task", StringComparison.Ordinal) || msg.Task == null) {
			continue;
		}

		JobTask task = msg.Task;
		Console.WriteLine($"task received: id={task.Id} action={task.Action} target={task.Target}");

		(bool success, string? error, IReadOnlyDictionary<string, object?>? output) = await Execute(
			task,
			sessionManager,
			logger,
			cancellationToken
		);

		TaskResult result = new(
			TaskId: task.Id,
			Success: success,
			Error: error,
			Output: output,
			FinishedAt: DateTimeOffset.UtcNow
		);

		await Send(ws, new WSMessage("task_result", null, null, result), cancellationToken);
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
	
	try {
		var payload = task.Payload ?? new Dictionary<string, object?>();

		string password =
			PayloadReader.GetString(payload, "password") ??
			PayloadReader.GetString(payload, "pass") ??
			"stub_password";

		var credentials = new AccountCredentials(
			AccountName: accountName,
			Password: password,
			AuthCode: PayloadReader.GetString(payload, "authCode") ?? PayloadReader.GetString(payload, "auth_code"),
			TwoFactorCode: PayloadReader.GetString(payload, "twoFactorCode") ?? PayloadReader.GetString(payload, "two_factor_code"),
			RefreshToken: PayloadReader.GetString(payload, "refreshToken") ?? PayloadReader.GetString(payload, "refresh_token"),
			AccessToken: PayloadReader.GetString(payload, "accessToken") ?? PayloadReader.GetString(payload, "access_token")
		);

		var session = await sessionManager.GetOrCreateSessionAsync(
			accountName,
			credentials,
			cancellationToken
		);

		var result = await session.ExecuteActionAsync(
			action,
			payload,
			cancellationToken
		);

		return (result.Success, result.Error, result.Output);
	} catch (Exception ex) {
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

					// Parse SSE format: "event: <type>" or "data: <json>"
					if (line.StartsWith("event: "))
					{
						var eventType = line["event: ".Length..].Trim();
						var dataLine = await reader.ReadLineAsync();
						if (dataLine?.StartsWith("data: ") == true)
						{
							var jsonData = dataLine["data: ".Length..];
							try
							{
								using var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonData);
								var root = jsonDoc.RootElement;
								var accountName = root.GetProperty("accountName").GetString();

								if (!string.IsNullOrWhiteSpace(accountName))
								{
									// Handle different auth challenge events
									if (eventType == "auth.code_provided_email")
									{
										logger.LogInformation("Auth code provided for {AccountName}", accountName);
										// The code will be picked up by the session automatically from the payload
									}
									else if (eventType == "auth.code_provided_totp")
									{
										logger.LogInformation("2FA code provided for {AccountName}", accountName);
									}
								}
							}
							catch (System.Text.Json.JsonException ex)
							{
								logger.LogWarning(ex, "Failed to parse auth challenge event: {Data}", jsonData);
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
