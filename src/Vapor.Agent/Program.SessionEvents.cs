using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Protocol;

internal partial class Program
{
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
			// CA2000 suppressed: HttpClient.PostAsync disposes the StringContent via
			// the HttpRequestMessage it builds around the URI overload.
#pragma warning disable CA2000
			var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
#pragma warning restore CA2000

			// POST to a new endpoint that will publish the event
			var response = await httpClient.PostAsync(new Uri($"{httpBaseUrl}/v1/sessions/events"), content);

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
}
