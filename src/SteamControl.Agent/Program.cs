using System.Net.WebSockets;
using System.Text.Json;
using SteamControl.Protocol;

static string RequireEnv(string key) => Environment.GetEnvironmentVariable(key) switch {
	{ Length: > 0 } v => v,
	_ => throw new InvalidOperationException($"{key} is required")
};

string agentId = RequireEnv("AGENT_ID");
string region = RequireEnv("AGENT_REGION");
string wsUrlBase = RequireEnv("AGENT_CONTROLPLANE_WS_URL");
string agentApiKey = RequireEnv("AGENT_API_KEY");

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

	var hello = new AgentHello(agentId, region, new Dictionary<string, bool> { ["stub"] = true }, null);
	await Send(ws, new WSMessage("hello", hello, null, null), cancellationToken);

	while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open) {
		WSMessage msg = await Receive<WSMessage>(ws, cancellationToken);
		if (!string.Equals(msg.Type, "task", StringComparison.Ordinal) || msg.Task == null) {
			continue;
		}

		JobTask task = msg.Task;
		Console.WriteLine($"task received: id={task.Id} action={task.Action} target={task.Target}");

		(bool success, string? error, IReadOnlyDictionary<string, object?>? output) = await Execute(task, cancellationToken);

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

static async Task<(bool Success, string? Error, IReadOnlyDictionary<string, object?>? Output)> Execute(JobTask task, CancellationToken cancellationToken) {
	string action = task.Action.Trim().ToLowerInvariant();
	try {
		return action switch {
			"ping" => (true, null, new Dictionary<string, object?> { ["pong"] = true }),
			"echo" => (true, null, new Dictionary<string, object?> { ["echo"] = task.Payload }),
			_ => (false, $"unknown action: {task.Action}", null)
		};
	} catch (Exception ex) {
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
