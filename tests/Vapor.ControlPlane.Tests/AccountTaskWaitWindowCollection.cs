using Xunit;

namespace Vapor.ControlPlane.Tests;

// xUnit parallelizes distinct test classes. AccountApiTests and
// ProgramBranchCoverageTests both tune the static AccountTaskRunner.WaitWindow /
// PollInterval knobs to keep API wait loops fast, restoring them in finally.
// When the classes interleave, a test that reads the default 30s window (e.g.
// GetMarketListings_AgentReportsFailure_Returns502) can observe the other
// class's 300ms window mid-wait and return 202 instead of 502 — the loop times
// out in 300ms while the responder is still claiming the task (seen locally,
// 2026-09-19, full-solution coverage run). One shared collection serializes
// every writer/reader of the knob.
[CollectionDefinition(Name)]
public sealed class AccountTaskWaitWindowCollection
{
	public const string Name = "AccountTaskWaitWindow";
}
