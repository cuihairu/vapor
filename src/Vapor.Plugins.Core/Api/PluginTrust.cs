namespace Vapor.Plugins.Core;

/// <summary>
/// Trust level declared by a plugin manifest. The host can refuse to load plugins
/// below a configured minimum via <see cref="PluginManagerOptions.MinimumTrust"/>.
/// </summary>
public enum PluginTrust
{
	/// <summary>No trust declared (the default when the manifest omits "trust").</summary>
	Unknown = 0,

	/// <summary>Community plugin: reviewed by a third party but not part of the distribution.</summary>
	Community = 1,

	/// <summary>Official plugin: shipped and maintained with the host itself.</summary>
	Official = 2
}

/// <summary>
/// Well-known permission names for the manifest "permissions" field. A plugin must
/// declare a permission to be granted the matching capability interface; capabilities
/// implemented without a declaration are stripped (or rejected in strict host policy).
/// </summary>
public static class PluginPermissions
{
	/// <summary>Contributes actions via <see cref="IActionPlugin"/>.</summary>
	public const string Actions = "actions";

	/// <summary>Contributes console commands via <see cref="ICommandPlugin"/>.</summary>
	public const string Commands = "commands";

	/// <summary>Contributes web routes via <see cref="IWebApiPlugin"/>.</summary>
	public const string Web = "web";

	/// <summary>Subscribes to host session events via <see cref="IEventPlugin"/>.</summary>
	public const string Events = "events";

	/// <summary>All known permission names.</summary>
	public static IReadOnlyList<string> All { get; } = [Actions, Commands, Web, Events];
}
