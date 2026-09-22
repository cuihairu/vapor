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

	[Fact]
	public void PluginLoadContext_UnresolvedManagedAssembly_ReturnsNullToHostFallback()
	{
		// An assembly outside the plugin's dependency graph resolves to no path:
		// the context must yield null so the host fallback takes over.
		var loadContext = new PluginLoadContext("managed-probe-test", PluginStaging.TestPluginAssemblyPath);
		var method = typeof(AssemblyLoadContext).GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);

		Assert.Null(method!.Invoke(loadContext, [new AssemblyName("vapor_no_such_managed_dependency")]));

		loadContext.Unload();
	}

	[Fact]
	public void PluginLoadContext_DependencyListedInDepsJson_LoadsFromPluginDirectory()
	{
		// With a deps.json beside the entry assembly the resolver maps a listed
		// dependency to a real path, so the load context serves the assembly itself
		// instead of falling back to the host. The staging helper copies only the
		// DLL, so the dependency graph is authored here — minimal but shaped exactly
		// like the emitted one (runtimeTarget + targets + libraries).
		var dir = Path.Combine(Path.GetTempPath(), "vapor-plc-deps-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			const string pluginDll = "Vapor.Plugins.TestPlugin.dll";
			File.Copy(PluginStaging.TestPluginAssemblyPath, Path.Combine(dir, pluginDll), overwrite: true);
			File.WriteAllText(Path.Combine(dir, "Vapor.Plugins.TestPlugin.deps.json"), """
				{
				  "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
				  "targets": {
				    ".NETCoreApp,Version=v10.0": {
				      "Vapor.Plugins.TestPlugin/1.0.0": {
				        "runtime": { "Vapor.Plugins.TestPlugin.dll": {} }
				      }
				    }
				  },
				  "libraries": {
				    "Vapor.Plugins.TestPlugin/1.0.0": { "type": "project", "serviceable": false, "sha512": "" }
				  }
				}
				""");

			var loadContext = new PluginLoadContext(
				"managed-resolve-test", Path.Combine(dir, pluginDll));
			var method = typeof(AssemblyLoadContext).GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(method);

			var loaded = (Assembly?)method!.Invoke(loadContext, [new AssemblyName("Vapor.Plugins.TestPlugin")]);
			Assert.NotNull(loaded);
			Assert.Equal("Vapor.Plugins.TestPlugin", loaded!.GetName().Name);
			loadContext.Unload();
		}
		finally
		{
			// The collectible context may still hold the file mapping; cleanup is
			// best-effort by design (day-one rule — never fail a green test over a
			// temp file the OS will reclaim).
			try { Directory.Delete(dir, recursive: true); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
	}

	[Fact]
	public void PluginLoadContext_DependencyNativeLibraryListedInDepsJson_LoadsFromPath()
	{
		// The non-null arm of LoadUnmanagedDll: a native library declared in the
		// plugin's deps.json runtimeTargets resolves to a real file, so the
		// context loads it via LoadUnmanagedDllFromPath instead of yielding a
		// zero handle. A copy of a runtime-owned library is the only dlopen-able
		// file we can rely on being present (Linux only — the rid is linux-*).
		if (!OperatingSystem.IsLinux())
		{
			return;
		}

		var dir = Path.Combine(Path.GetTempPath(), "vapor-plc-native-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			const string pluginDll = "Vapor.Plugins.TestPlugin.dll";
			const string nativeRel = "runtimes/linux-x64/native/libvapor_probe_native.so";
			string runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
			string sourceNative = Path.Combine(runtimeDir, "libSystem.Native.so");
			Assert.True(File.Exists(sourceNative), $"runtime native library not found: {sourceNative}");
			Directory.CreateDirectory(Path.Combine(dir, "runtimes", "linux-x64", "native"));
			File.Copy(sourceNative, Path.Combine(dir, nativeRel), overwrite: true);
			File.Copy(PluginStaging.TestPluginAssemblyPath, Path.Combine(dir, pluginDll), overwrite: true);
			File.WriteAllText(Path.Combine(dir, "Vapor.Plugins.TestPlugin.deps.json"), """
				{
				  "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
				  "targets": {
				    ".NETCoreApp,Version=v10.0": {
				      "Vapor.Plugins.TestPlugin/1.0.0": {
				        "runtime": { "Vapor.Plugins.TestPlugin.dll": {} },
				        "runtimeTargets": {
				          "runtimes/linux-x64/native/libvapor_probe_native.so": { "rid": "linux-x64", "assetType": "native" }
				        }
				      }
				    }
				  },
				  "libraries": {
				    "Vapor.Plugins.TestPlugin/1.0.0": { "type": "project", "serviceable": false, "sha512": "" }
				  }
				}
				""");

			var loadContext = new PluginLoadContext(
				"native-real-test", Path.Combine(dir, pluginDll));
			var method = typeof(AssemblyLoadContext).GetMethod("LoadUnmanagedDll", BindingFlags.Instance | BindingFlags.NonPublic);
			Assert.NotNull(method);

			var handle = (IntPtr)method!.Invoke(loadContext, ["libvapor_probe_native.so"])!;

			Assert.NotEqual(IntPtr.Zero, handle);
			loadContext.Unload();
		}
		finally
		{
			// Day-one rule: ALC-locked files make cleanup best-effort by design.
			try { Directory.Delete(dir, recursive: true); }
			catch (IOException) { }
			catch (UnauthorizedAccessException) { }
		}
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
