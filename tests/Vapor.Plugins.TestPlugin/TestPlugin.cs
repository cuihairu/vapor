using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Plugins.TestPlugin;

/// <summary>
/// Test plugin used by Vapor.Plugins.Core.Tests. It exercises the full plugin surface:
/// actions, commands, web routes, configuration and lifecycle callbacks (via marker files
/// written into the configured marker directory).
/// </summary>
public sealed class TestPlugin : IActionPlugin, ICommandPlugin, IWebApiPlugin
{
	private string? _markerDir;

	public PluginInfo Info { get; } = new(
		Id: "vapor.test-plugin",
		Name: "Vapor Test Plugin",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current,
		Description: "Plugin used by plugin infrastructure tests");

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		_markerDir = context.Configuration.TryGetValue("markerDir", out var dir) ? dir : null;
		WriteMarker("initialized");
		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		WriteMarker("shutdown");
		return Task.CompletedTask;
	}

	private void WriteMarker(string name)
	{
		if (_markerDir is null)
		{
			return;
		}

		Directory.CreateDirectory(_markerDir);
		File.WriteAllText(Path.Combine(_markerDir, $"{name}.marker"), Info.Id);
	}

	public IEnumerable<IAction> GetActions()
	{
		yield return new TestPluginAction();
	}

	public IEnumerable<IPluginCommand> GetCommands()
	{
		yield return new TestPluginCommand();
	}

	public IEnumerable<PluginWebRoute> GetRoutes()
	{
		yield return new PluginWebRoute(
			"GET",
			"/hello",
			(_, _) => Task.FromResult(PluginWebResponse.Json("{\"hello\":\"plugin\"}")));
	}

	private sealed class TestPluginAction : IAction
	{
		public string Name => "plugin_echo";

		public ActionMetadata Metadata { get; } = new(
			Name: "plugin_echo",
			Description: "Echoes the payload back (contributed by the test plugin)");

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken)
		{
			var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
			{
				["echoed"] = payload.Count,
				["plugin"] = "vapor.test-plugin"
			};

			return Task.FromResult(new ActionResult(true, null, output));
		}
	}

	private sealed class TestPluginCommand : IPluginCommand
	{
		public string Name => "plugin-ping";

		public string Description => "Responds with pong (contributed by the test plugin)";

		public Task<PluginCommandResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
		{
			return Task.FromResult(PluginCommandResult.Ok("pong"));
		}
	}
}
