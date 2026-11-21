using Microsoft.Extensions.Logging;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Utilities;

internal partial class Program
{
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

			logger.LogInformation("Starting auth challenge polling for agent {AgentId}", agentId);

			while (!cancellationToken.IsCancellationRequested)
			{
				// Per-iteration token: a faulting stream must tear down every sibling
				// subscription before the loop re-subscribes. Otherwise one stream's
				// 5-minute HttpClient timeout (or server close) faults Task.WhenAll
				// while the other streams keep running, and re-subscribing over a live
				// stream stacks duplicate SSE connections — an auth code is then
				// delivered twice and the second application of a consumed code fails.
				using var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				try
				{
					List<string> accountNames = sessionManager.ListSessions()
						.Select(session => session.AccountName)
						.Distinct(StringComparer.Ordinal)
						.ToList();
					if (accountNames.Count == 0)
					{
						await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
						continue;
					}

					List<Task> subscriptions = accountNames
						.Select(accountName => SubscribeToAccountChallengesAsync(
							httpBaseUrl,
							accountName,
							agentApiKey,
							sessionManager,
							logger,
							iterationCts.Token))
						.ToList();
					await Task.WhenAll(subscriptions).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Auth challenge polling error, will retry");
					iterationCts.Cancel();
					await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
				}
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Auth challenge polling task failed");
		}
	}

	static async Task SubscribeToAccountChallengesAsync(
		string httpBaseUrl,
		string accountName,
		string agentApiKey,
		ISessionManager sessionManager,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		using var accountHttpClient = new System.Net.Http.HttpClient();
		accountHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {agentApiKey}");
		accountHttpClient.Timeout = TimeSpan.FromMinutes(5);

		var url = $"{httpBaseUrl}/v1/auth/challenges/events?accountName={Uri.EscapeDataString(accountName)}";
		using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
		using var response = await accountHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();

		using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var reader = new System.IO.StreamReader(stream);
		while (!cancellationToken.IsCancellationRequested)
		{
			var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (line is null)
			{
				return;
			}

			if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("event: ", StringComparison.Ordinal))
			{
				continue;
			}

			var eventType = line["event: ".Length..].Trim();
			var dataLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
			if (dataLine?.StartsWith("data: ", StringComparison.Ordinal) != true)
			{
				continue;
			}

			var jsonData = dataLine["data: ".Length..];
			try
			{
				if (eventType is not ("auth.code_provided_email" or "auth.code_provided_totp" or "auth.code_provided_2fa"))
				{
					continue;
				}

				using var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonData);
				if (!jsonDoc.RootElement.TryGetProperty("code", out var codeProp))
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

				var session = await sessionManager.GetSessionAsync(accountName, cancellationToken).ConfigureAwait(false);
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
				logger.LogWarning(ex, "Failed to parse auth challenge event: {Data}", SensitiveDataRedactor.Redact(jsonData));
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to handle auth challenge event: {Data}", SensitiveDataRedactor.Redact(jsonData));
			}
		}
	}
}
