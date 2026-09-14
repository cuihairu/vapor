using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

/// <summary>
/// Plugin contract types and lifecycle plumbing: web request/response records, command
/// results, descriptor/info accessors, LoadedPlugin double-unload safety and the load
/// context's native-library resolution fallback.
/// </summary>
public sealed class PluginCapabilitiesTests
{
	[Fact]
	public void PluginWebRequest_ExposesConstructorValues()
	{
		var query = new Dictionary<string, string> { ["q"] = "1" };
		var headers = new Dictionary<string, string> { ["x-test"] = "yes" };

		var request = new PluginWebRequest("/hello", query, headers, "payload");

		Assert.Equal("/hello", request.Path);
		Assert.Equal("1", request.Query["q"]);
		Assert.Equal("yes", request.Headers["x-test"]);
		Assert.Equal("payload", request.Body);
	}

	[Fact]
	public void PluginWebResponse_Error_SerializesErrorMessage()
	{
		var response = PluginWebResponse.Error(404, "missing route");

		Assert.Equal(404, response.StatusCode);
		Assert.Contains("missing route", response.Body);
	}

	[Fact]
	public void PluginCommandResult_Fail_CarriesMessageAndSuccessFlag()
	{
		var result = PluginCommandResult.Fail("denied");

		Assert.False(result.Success);
		Assert.Equal("denied", result.Message);
	}

	[Fact]
	public void PluginDescriptor_ExposesManifestPath()
	{
		var manifest = new PluginManifest
		{
			Id = "x",
			Name = "X",
			Version = "1.0.0",
			ApiVersion = "1.0",
			EntryAssembly = "x.dll"
		};

		var descriptor = new PluginDescriptor(manifest, "/plugins/x", "/plugins/x/plugin.json", "/plugins/x/x.dll");

		Assert.Equal("/plugins/x/plugin.json", descriptor.ManifestPath);
	}

	[Fact]
	public void PluginInfo_CarriesOptionalDescription()
	{
		var withoutDescription = new PluginInfo("x", "X", new Version(1, 0, 0), PluginApi.Current);
		var withDescription = new PluginInfo("x", "X", new Version(1, 0, 0), PluginApi.Current, "a description");

		Assert.Null(withoutDescription.Description);
		Assert.Equal("a description", withDescription.Description);
	}

	[Fact]
	public void MarkUnloaded_Twice_SecondCallIsNoOp()
	{
		// After the first unload the context reference is released; a second call must not
		// touch it again (the released context only survives through UnloadTracker).
		var descriptor = CreateDescriptor("vapor.mark-unloaded");
		var loadContext = new PluginLoadContext("mark-unloaded-test", PluginStaging.TestPluginAssemblyPath);
		var plugin = new LoadedPlugin(descriptor, new NoOpPlugin(), loadContext, [], [], [], []);

		plugin.MarkUnloaded();
		plugin.MarkUnloaded();

		Assert.NotNull(plugin.UnloadTracker);
		Assert.Null(plugin.LoadContext);
	}

	[Fact]
	public void PluginLoadContext_UnresolvedNativeLibrary_ReturnsZeroPointer()
	{
		// An arbitrary native library name has no entry in the plugin's dependency graph,
		// so the unmanaged probe must yield a zero handle instead of throwing.
		var loadContext = new PluginLoadContext("native-probe-test", PluginStaging.TestPluginAssemblyPath);
		var method = typeof(AssemblyLoadContext).GetMethod("LoadUnmanagedDll", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		var result = (IntPtr)method!.Invoke(loadContext, ["vapor_no_such_native_library"])!;

		Assert.Equal(IntPtr.Zero, result);
		loadContext.Unload();
	}

	private static PluginDescriptor CreateDescriptor(string pluginId)
	{
		var manifest = new PluginManifest
		{
			Id = pluginId,
			Name = "Mark Unloaded Test",
			Version = "1.0.0",
			ApiVersion = "1.0",
			EntryAssembly = "Vapor.Plugins.TestPlugin.dll"
		};

		return new PluginDescriptor(
			manifest,
			AppContext.BaseDirectory,
			Path.Combine(AppContext.BaseDirectory, PluginManifest.ManifestFileName),
			PluginStaging.TestPluginAssemblyPath);
	}

	private sealed class NoOpPlugin : IPlugin
	{
		public PluginInfo Info { get; } = new(
			Id: "vapor.no-op",
			Name: "No Op",
			Version: new Version(1, 0, 0),
			ApiVersion: PluginApi.Current);

		public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
