namespace Vapor.Plugins.Core;

/// <summary>
/// Builds the plugin dependency graph at enablement time: every plugin whose dependencies
/// cannot be satisfied (missing plugin, circular dependency, apiVersion mismatch, or a
/// dependency that itself failed) is excluded with a failure message, and the remaining
/// plugins are ordered so each loads after its dependencies. Plugins without dependencies
/// keep their discovery order.
/// </summary>
public static class PluginDependencyGraph
{
	private enum State
	{
		Pending,
		Ok,
		Failed
	}

	/// <summary>
	/// Resolves <paramref name="descriptors"/> into a load order plus the failure messages
	/// for every excluded plugin.
	/// </summary>
	public static (IReadOnlyList<PluginDescriptor> Ordered, IReadOnlyList<string> Failures) Resolve(
		IReadOnlyList<PluginDescriptor> descriptors)
	{
		ArgumentNullException.ThrowIfNull(descriptors);

		var failures = new List<string>();
		var byId = new Dictionary<string, PluginDescriptor>(StringComparer.OrdinalIgnoreCase);
		foreach (var descriptor in descriptors)
		{
			byId[descriptor.Manifest.Id] = descriptor;
		}

		var state = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
		foreach (var descriptor in descriptors)
		{
			state[descriptor.Manifest.Id] = State.Pending;
		}

		// Alternate two fixpoints until nothing changes: prune (failures cascade to every
		// plugin that transitively depends on them; satisfied plugins become Ok) and, once
		// pruning is stable, anything still Pending can only be waiting on another Pending
		// plugin — the nodes that can reach themselves are cycle members. Repeat because
		// failing a cycle unblocks the prune of outsiders that merely depend on it.
		bool changed = true;
		while (changed)
		{
			changed = Prune(descriptors, byId, state, failures);
			changed |= FailCycles(descriptors, byId, state, failures);
		}

		// Topological order over the survivors: repeatedly take the first plugin (in
		// discovery order) whose dependencies are all already placed. Cycles are fully
		// pruned above, so FindIndex always finds a ready plugin on a well-formed graph —
		// a future edit breaking that invariant fails loudly on the negative index.
		var survivors = descriptors.Where(d => state[d.Manifest.Id] == State.Ok).ToList();
		var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var ordered = new List<PluginDescriptor>(survivors.Count);
		while (ordered.Count < survivors.Count)
		{
			var index = survivors.FindIndex(d =>
				!placed.Contains(d.Manifest.Id)
				&& (d.Manifest.Dependencies ?? []).All(dep => placed.Contains(dep.PluginId!)));
			var next = survivors[index];

			placed.Add(next.Manifest.Id);
			ordered.Add(next);
		}

		return (ordered, failures);
	}

	/// <summary>
	/// One pass of failure propagation and satisfaction: pending plugins with a missing,
	/// failed or version-mismatched dependency fail; pending plugins whose dependencies are
	/// all Ok become Ok themselves. Returns whether any state changed.
	/// </summary>
	private static bool Prune(
		IReadOnlyList<PluginDescriptor> descriptors,
		IReadOnlyDictionary<string, PluginDescriptor> byId,
		Dictionary<string, State> state,
		List<string> failures)
	{
		bool changed = false;
		foreach (var descriptor in descriptors)
		{
			var id = descriptor.Manifest.Id;
			if (state[id] != State.Pending)
			{
				continue;
			}

			var dependencies = descriptor.Manifest.Dependencies;
			var satisfied = true;
			foreach (var dependency in dependencies ?? [])
			{
				if (dependency.PluginId is null || !byId.TryGetValue(dependency.PluginId, out var provider))
				{
					state[id] = State.Failed;
					failures.Add($"Plugin '{id}' depends on missing plugin '{dependency.PluginId}'");
					changed = true;
					satisfied = false;
					break;
				}

				if (state[dependency.PluginId] == State.Failed)
				{
					state[id] = State.Failed;
					failures.Add($"Plugin '{id}' depends on failed plugin '{dependency.PluginId}'");
					changed = true;
					satisfied = false;
					break;
				}

				if (dependency.ApiVersion is not null
					&& !Satisfies(provider.Manifest.ApiVersion, dependency.ApiVersion, dependency.PluginId, out var reason))
				{
					state[id] = State.Failed;
					failures.Add($"Plugin '{id}' dependency '{dependency.PluginId}' is not satisfied: {reason}");
					changed = true;
					satisfied = false;
					break;
				}

				if (state[dependency.PluginId] != State.Ok)
				{
					satisfied = false;
				}
			}

			if (satisfied)
			{
				state[id] = State.Ok;
				changed = true;
			}
		}

		return changed;
	}

	/// <summary>
	/// After pruning is stable every still-pending plugin waits on another pending plugin;
	/// the ones that can reach themselves are cycle members and fail with the member list.
	/// Returns whether any state changed.
	/// </summary>
	private static bool FailCycles(
		IReadOnlyList<PluginDescriptor> descriptors,
		IReadOnlyDictionary<string, PluginDescriptor> byId,
		Dictionary<string, State> state,
		List<string> failures)
	{
		var pending = descriptors.Where(d => state[d.Manifest.Id] == State.Pending).ToList();
		if (pending.Count == 0)
		{
			return false;
		}

		var pendingIds = pending.Select(d => d.Manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
		bool changed = false;
		foreach (var descriptor in pending)
		{
			if (state[descriptor.Manifest.Id] != State.Pending
				|| !Reachable(descriptor.Manifest.Id, descriptor.Manifest.Id, byId, pendingIds))
			{
				continue;
			}

			// The member list is this node's cycle component: reachable from it and able
			// to reach it back — so two disjoint cycles fail with their own members only.
			var members = pending
				.Where(p => Reachable(descriptor.Manifest.Id, p.Manifest.Id, byId, pendingIds)
					&& Reachable(p.Manifest.Id, descriptor.Manifest.Id, byId, pendingIds))
				.Select(p => $"'{p.Manifest.Id}'")
				.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
			var memberList = string.Join(", ", members);
			foreach (var member in pending)
			{
				if (state[member.Manifest.Id] != State.Pending
					|| !Reachable(descriptor.Manifest.Id, member.Manifest.Id, byId, pendingIds)
					|| !Reachable(member.Manifest.Id, descriptor.Manifest.Id, byId, pendingIds))
				{
					continue;
				}

				state[member.Manifest.Id] = State.Failed;
				failures.Add($"Plugin '{member.Manifest.Id}' participates in a dependency cycle involving: {memberList}");
				changed = true;
			}
		}

		return changed;
	}

	/// <summary>Whether <paramref name="to"/> is reachable from <paramref name="from"/> by
	/// following dependencies that are themselves pending.</summary>
	private static bool Reachable(
		string from,
		string to,
		IReadOnlyDictionary<string, PluginDescriptor> byId,
		HashSet<string> pendingIds)
	{
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
		var stack = new Stack<string>([from]);
		while (stack.Count > 0)
		{
			var current = stack.Pop();
			// Only called on pending nodes, and Prune marks every dependency-free plugin
			// Ok — so a pending plugin always has a non-empty, non-null dependency list.
			foreach (var dependency in byId[current].Manifest.Dependencies!)
			{
				var dependencyId = dependency.PluginId;
				if (dependencyId is null || !pendingIds.Contains(dependencyId))
				{
					continue;
				}

				if (string.Equals(dependencyId, to, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}

				if (visited.Add(dependencyId))
				{
					stack.Push(dependencyId);
				}
			}
		}

		return false;
	}

	/// <summary>
	/// Whether the apiVersion a dependency declares satisfies a dependent's constraint —
	/// the host rule mirrored: major must match exactly and the provider's minor must be
	/// at least the requested one (a provider of 1.3 satisfies a constraint of 1.2).
	/// </summary>
	private static bool Satisfies(string? providerText, string requestedText, string providerId, out string reason)
	{
		if (!PluginApi.TryParseVersion(providerText, out var provider))
		{
			reason = $"dependency '{providerId}' declares invalid apiVersion '{providerText}'";
			return false;
		}

		if (!PluginApi.TryParseVersion(requestedText, out var requested))
		{
			reason = $"constraint '{requestedText}' is not a valid SemVer version";
			return false;
		}

		if (provider.Major != requested.Major)
		{
			reason = $"dependency '{providerId}' implements API major {provider.Major} but the constraint requires major {requested.Major}";
			return false;
		}

		if (provider.Minor < requested.Minor)
		{
			reason = $"dependency '{providerId}' implements API {provider.Major}.{provider.Minor} but the constraint requires {requested.Major}.{requested.Minor} or later";
			return false;
		}

		reason = string.Empty;
		return true;
	}
}
