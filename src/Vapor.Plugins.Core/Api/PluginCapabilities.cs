using Vapor.Steam.Core;

namespace Vapor.Plugins.Core;

/// <summary>
/// Capability interface: a plugin implementing this contributes <see cref="IAction"/>
/// implementations to the Agent's action registry.
/// </summary>
public interface IActionPlugin : IPlugin
{
	IEnumerable<IAction> GetActions();
}

/// <summary>Result of executing a plugin command.</summary>
public sealed record PluginCommandResult(bool Success, string? Message = null)
{
	public static PluginCommandResult Ok(string? message = null) => new(true, message);
	public static PluginCommandResult Fail(string message) => new(false, message);
}

/// <summary>
/// A host console/CLI command contributed by a plugin.
/// </summary>
public interface IPluginCommand
{
	string Name { get; }

	string Description { get; }

	Task<PluginCommandResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);
}

/// <summary>
/// Capability interface: a plugin implementing this contributes host console commands.
/// </summary>
public interface ICommandPlugin : IPlugin
{
	IEnumerable<IPluginCommand> GetCommands();
}

/// <summary>
/// A host-agnostic web request handed to plugin route handlers. Hosts (e.g. the Control
/// Plane) adapt their native request type to this shape.
/// </summary>
public sealed record PluginWebRequest(
	string Path,
	IReadOnlyDictionary<string, string> Query,
	IReadOnlyDictionary<string, string> Headers,
	string? Body
);

/// <summary>A host-agnostic web response returned by plugin route handlers.</summary>
public sealed record PluginWebResponse(int StatusCode, string? Body = null, string ContentType = "application/json")
{
	public static PluginWebResponse Json(string body, int statusCode = 200) => new(statusCode, body);

	public static PluginWebResponse Error(int statusCode, string message) =>
		new(statusCode, System.Text.Json.JsonSerializer.Serialize(new { error = message }));
}

/// <summary>
/// A web API route contributed by a plugin. Paths are relative to the host's plugin route
/// prefix (e.g. <c>/v1/plugins/{pluginId}/...</c>).
/// </summary>
public sealed record PluginWebRoute(
	string Method,
	string Path,
	Func<PluginWebRequest, CancellationToken, Task<PluginWebResponse>> Handler
);

/// <summary>
/// Capability interface: a plugin implementing this contributes web API routes that hosts
/// can mount under a plugin route prefix.
/// </summary>
public interface IWebApiPlugin : IPlugin
{
	IEnumerable<PluginWebRoute> GetRoutes();
}

/// <summary>
/// Capability interface: a plugin implementing this receives session events observed by
/// the host. Handlers must return promptly (they are awaited inline on the event pump)
/// and are isolated: an exception thrown by one plugin's handler never affects other
/// subscribers or the event source. Hosts stop delivering while the plugin unloads.
/// </summary>
public interface IEventPlugin : IPlugin
{
	/// <summary>Called for every session event the host observes.</summary>
	Task OnSessionEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);
}
