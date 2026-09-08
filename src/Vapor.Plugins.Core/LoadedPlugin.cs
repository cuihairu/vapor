using Vapor.Steam.Core;

namespace Vapor.Plugins.Core;

/// <summary>
/// A plugin that has been loaded and initialized. Holds the isolated load context so the
/// plugin can be unloaded later.
/// </summary>
public sealed class LoadedPlugin
{
	internal LoadedPlugin(
		PluginDescriptor descriptor,
		IPlugin instance,
		PluginLoadContext loadContext,
		IReadOnlyList<IAction> actions,
		IReadOnlyList<IPluginCommand> commands,
		IReadOnlyList<PluginWebRoute> routes)
	{
		Descriptor = descriptor;
		Instance = instance;
		LoadContext = loadContext;
		Actions = actions;
		Commands = commands;
		Routes = routes;
	}

	public PluginDescriptor Descriptor { get; }

	public IPlugin Instance { get; }

	/// <summary>Actions contributed by this plugin (empty when it is not an <see cref="IActionPlugin"/>).</summary>
	public IReadOnlyList<IAction> Actions { get; }

	/// <summary>Commands contributed by this plugin (empty when it is not an <see cref="ICommandPlugin"/>).</summary>
	public IReadOnlyList<IPluginCommand> Commands { get; }

	/// <summary>Web routes contributed by this plugin (empty when it is not an <see cref="IWebApiPlugin"/>).</summary>
	public IReadOnlyList<PluginWebRoute> Routes { get; }

	internal PluginLoadContext? LoadContext { get; private set; }

	public PluginInfo Info => Instance.Info;

	/// <summary>
	/// Set when the plugin has been unloaded. Tracks the released load context so hosts and
	/// tests can verify that the context is eventually collected.
	/// </summary>
	public WeakReference? UnloadTracker { get; private set; }

	/// <summary>
	/// Releases the strong reference to the load context and unloads it. After this call,
	/// <see cref="UnloadTracker"/> is the only reference left.
	/// </summary>
	internal void MarkUnloaded()
	{
		if (LoadContext is null)
		{
			return;
		}

		UnloadTracker = new WeakReference(LoadContext);
		LoadContext.Unload();
		LoadContext = null;
	}
}
