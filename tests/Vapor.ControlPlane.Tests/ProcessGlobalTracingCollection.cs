using Xunit;

namespace Vapor.ControlPlane.Tests;

// xUnit parallelizes distinct test classes. Two of them touch process-global
// state that must never interleave: CompositionRootSmokeTests boots the real
// composition root with OTEL_EXPORTER_OTLP_ENDPOINT set, which registers a
// process-wide OpenTelemetry listener for the Vapor.ControlPlane ActivitySource
// (and mutates process-level environment variables), while TracingTests asserts
// the documented no-listener behavior — that a dispatch without an OTel
// registration carries no traceparent. When these ran concurrently, a dispatch
// landing inside the host's liveness window observed the global listener and the
// Assert.Null on TraceHeaders failed (CI windows/macos Release, 2026-09-16).
// Marking the collection DisableParallelization pins both classes to run after
// every parallel collection, so the listener state is quiescent either way.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalTracingCollection
{
	public const string Name = "ProcessGlobalTracing";
}
