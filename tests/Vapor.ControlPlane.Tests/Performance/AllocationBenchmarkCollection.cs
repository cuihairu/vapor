using Xunit;

namespace Vapor.ControlPlane.Tests.Performance;

// ResourceFootprintBenchmarks asserts a per-operation allocation budget read
// from GC.GetTotalAllocatedBytes — a process-wide counter. Under xUnit's
// default parallelization every other test collection allocates inside the
// measurement window, so the sampled number is baseline-plus-noise: a local
// full run measured 209 KB/cycle against a ~29 KB clean baseline (2026-09-18),
// tripping a bound meant to catch order-of-magnitude regressions. Marking the
// collection DisableParallelization pins the benchmark to run after every
// parallel collection, so the counter measures only its own work.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationBenchmarkCollection
{
	public const string Name = "AllocationBenchmark";
}
