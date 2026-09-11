namespace Vapor.Agent;

/// <summary>
/// Builds the Control Plane WebSocket URL, merging any pre-existing query string
/// with the agent identity parameters.
/// </summary>
public static class AgentWebSocketUri
{
	public static Uri Build(string baseUrl, string agentId, string region)
	{
		var baseUri = new Uri(baseUrl);
		var ub = new UriBuilder(baseUri);

		string qs = ub.Query;
		if (qs.StartsWith('?'))
		{
			qs = qs[1..];
		}

		var parts = new List<string>();
		if (!string.IsNullOrEmpty(qs))
		{
			parts.Add(qs);
		}

		parts.Add($"agentId={Uri.EscapeDataString(agentId)}");
		parts.Add($"region={Uri.EscapeDataString(region)}");

		ub.Query = string.Join('&', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
		return ub.Uri;
	}
}
