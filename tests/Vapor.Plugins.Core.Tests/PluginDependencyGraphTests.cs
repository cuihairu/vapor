using Xunit;
using Vapor.Plugins.Core;

namespace Vapor.Plugins.Core.Tests;

public class PluginDependencyGraphTests
{
	private static PluginDescriptor Descriptor(string id, params PluginDependency[] dependencies) =>
		new(
			new PluginManifest
			{
				Id = id,
				Name = id,
				Version = "1.0.0",
				ApiVersion = "1.0",
				EntryAssembly = $"{id}.dll",
				Dependencies = dependencies.Length == 0 ? null : dependencies
			},
			id,
			$"{id}/{PluginManifest.ManifestFileName}",
			$"{id}/{id}.dll");

	private static string[] OrderOf(IReadOnlyList<PluginDescriptor> descriptors) =>
		descriptors.Select(d => d.Manifest.Id).ToArray();

	[Fact]
	public void Resolve_EmptyInput_ReturnsEmpty()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve([]);

		Assert.Empty(ordered);
		Assert.Empty(failures);
	}

	[Fact]
	public void Resolve_WithoutDependencies_KeepsDiscoveryOrder()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a"), Descriptor("b"), Descriptor("c")]);

		Assert.Equal(["a", "b", "c"], OrderOf(ordered));
		Assert.Empty(failures);
	}

	[Fact]
	public void Resolve_DependencyChain_LoadsInTopologicalOrder()
	{
		// The dependent sits first in discovery order; the dependency must load first.
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a", new PluginDependency { PluginId = "b" }), Descriptor("b")]);

		Assert.Equal(["b", "a"], OrderOf(ordered));
		Assert.Empty(failures);
	}

	[Fact]
	public void Resolve_MissingDependency_ExcludesDependent()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a", new PluginDependency { PluginId = "ghost" }), Descriptor("b")]);

		Assert.Equal(["b"], OrderOf(ordered));
		var failure = Assert.Single(failures);
		Assert.Equal("Plugin 'a' depends on missing plugin 'ghost'", failure);
	}

	[Fact]
	public void Resolve_NullDependencyId_TreatedAsMissing()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a", new PluginDependency { PluginId = null }), Descriptor("b")]);

		Assert.Equal(["b"], OrderOf(ordered));
		var failure = Assert.Single(failures);
		Assert.Equal("Plugin 'a' depends on missing plugin ''", failure);
	}

	[Fact]
	public void Resolve_TransitiveFailure_CascadesToDependents()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[
				Descriptor("c", new PluginDependency { PluginId = "b" }),
				Descriptor("b", new PluginDependency { PluginId = "ghost" }),
				Descriptor("a")
			]);

		Assert.Equal(["a"], OrderOf(ordered));
		Assert.Contains(failures, f => f == "Plugin 'b' depends on missing plugin 'ghost'");
		Assert.Contains(failures, f => f == "Plugin 'c' depends on failed plugin 'b'");
		Assert.Equal(2, failures.Count);
	}

	[Fact]
	public void Resolve_TwoNodeCycle_ExcludesBothKeepsIndependent()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[
				Descriptor("a", new PluginDependency { PluginId = "b" }),
				Descriptor("b", new PluginDependency { PluginId = "a" }),
				Descriptor("standalone")
			]);

		Assert.Equal(["standalone"], OrderOf(ordered));
		Assert.Equal(2, failures.Count);
		Assert.All(
			failures,
			f => Assert.Contains("participates in a dependency cycle involving: 'a', 'b'", f));
	}

	[Fact]
	public void Resolve_SelfDependency_ReportsCycle()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("self", new PluginDependency { PluginId = "self" }), Descriptor("other")]);

		Assert.Equal(["other"], OrderOf(ordered));
		var failure = Assert.Single(failures);
		Assert.Contains("participates in a dependency cycle", failure);
	}

	[Fact]
	public void Resolve_ApiConstraintSatisfied_Loads()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[
				Descriptor("a", new PluginDependency { PluginId = "b", ApiVersion = "1.0" }),
				Descriptor("b", new PluginDependency { PluginId = "c" }),
				Descriptor("c")
			]);

		Assert.Empty(failures);
		Assert.Equal(["c", "b", "a"], OrderOf(ordered));
	}

	[Fact]
	public void Resolve_ApiConstraintTooNew_ExcludesDependent()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a", new PluginDependency { PluginId = "b", ApiVersion = "1.2" }), Descriptor("b")]);

		Assert.Equal(["b"], OrderOf(ordered));
		var failure = Assert.Single(failures);
		Assert.Contains("is not satisfied: dependency 'b' implements API 1.0 but the constraint requires 1.2 or later", failure);
	}

	[Fact]
	public void Resolve_ApiConstraintMajorMismatch_ExcludesDependent()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a", new PluginDependency { PluginId = "b", ApiVersion = "2.0" }), Descriptor("b")]);

		Assert.Equal(["b"], OrderOf(ordered));
		var failure = Assert.Single(failures);
		Assert.Contains("implements API major 1 but the constraint requires major 2", failure);
	}

	[Fact]
	public void Resolve_ProviderDeclaresInvalidApiVersion_ExcludesDependent()
	{
		var provider = Descriptor("b");
		provider = provider with { Manifest = provider.Manifest with { ApiVersion = "nope" } };
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a", new PluginDependency { PluginId = "b", ApiVersion = "1.0" }), provider]);

		Assert.Equal(["b"], OrderOf(ordered));
		var failure = Assert.Single(failures);
		Assert.Contains("is not satisfied: dependency 'b' declares invalid apiVersion 'nope'", failure);
	}

	[Fact]
	public void Resolve_DisjointCycles_FailWithOwnMemberLists()
	{
		// a↔b and c↔d are separate cycles; x merely waits on the second one. Each cycle
		// must fail with its own two members — not one merged list — and x cascades with
		// the normal "depends on failed" message rather than being called a member.
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[
				Descriptor("a", new PluginDependency { PluginId = "b" }, new PluginDependency { PluginId = "x" }),
				Descriptor("b", new PluginDependency { PluginId = "a" }),
				Descriptor("c", new PluginDependency { PluginId = "d" }),
				Descriptor("d", new PluginDependency { PluginId = "c" }),
				Descriptor("x", new PluginDependency { PluginId = "c" })
			]);

		Assert.Empty(ordered);
		Assert.Equal(5, failures.Count);
		Assert.Contains("Plugin 'a' participates in a dependency cycle involving: 'a', 'b'", failures);
		Assert.Contains("Plugin 'b' participates in a dependency cycle involving: 'a', 'b'", failures);
		Assert.Contains("Plugin 'c' participates in a dependency cycle involving: 'c', 'd'", failures);
		Assert.Contains("Plugin 'd' participates in a dependency cycle involving: 'c', 'd'", failures);
		Assert.Contains("Plugin 'x' depends on failed plugin 'c'", failures);
	}

	[Fact]
	public void Resolve_InvalidApiConstraint_ExcludesDependent()
	{
		var (ordered, failures) = PluginDependencyGraph.Resolve(
			[Descriptor("a", new PluginDependency { PluginId = "b", ApiVersion = "garbage" }), Descriptor("b")]);

		Assert.Equal(["b"], OrderOf(ordered));
		var failure = Assert.Single(failures);
		Assert.Contains("is not satisfied: constraint 'garbage' is not a valid SemVer version", failure);
	}

	[Fact]
	public void Resolve_NullDescriptors_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => PluginDependencyGraph.Resolve(null!));
	}
}
