namespace Vapor.ControlPlane;

/// <summary>
/// Target convention for tasks that must run on one specific machine (plugin
/// lifecycle): the target is "agent:{agentId}" instead of an account name. The
/// scheduler resolves the id directly from the agent registry rather than picking
/// deterministically within the region.
/// </summary>
public static class HostTaskTarget
{
	public const string Prefix = "agent:";

	public static string For(string agentId) => Prefix + agentId;

	public static bool TryParseAgentId(string? target, out string agentId)
	{
		if (target is not null && target.StartsWith(Prefix, StringComparison.Ordinal) && target.Length > Prefix.Length)
		{
			agentId = target[Prefix.Length..];
			return true;
		}

		agentId = string.Empty;
		return false;
	}
}
