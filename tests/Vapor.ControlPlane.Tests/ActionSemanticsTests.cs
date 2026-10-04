using Xunit;
using Vapor.Protocol;

namespace Vapor.ControlPlane.Tests;

public class ActionSemanticsTests
{
	[Fact]
	public void ClassificationTable_CoversAll57InTreeActions_WithNoUnknownEntries()
	{
		var names = ActionSemantics.ActionNames;

		Assert.Equal(57, names.Count);
		Assert.Equal(57, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
		foreach (string name in names)
		{
			Assert.NotEqual(ActionSafety.Unknown, ActionSemantics.SafetyOf(name));
		}
	}

	[Fact]
	public void ClassificationTable_MatchesDocumentedDistribution()
	{
		// The per-action decision log lives in todo.md 轮五十; the distribution is
		// asserted here so an annotation change requires a deliberate review.
		var byClass = ActionSemantics.ActionNames
			.Select(ActionSemantics.SafetyOf)
			.GroupBy(s => s)
			.ToDictionary(g => g.Key, g => g.Count());

		Assert.Equal(29, byClass[ActionSafety.ReadOnly]);
		Assert.Equal(11, byClass[ActionSafety.Idempotent]);
		Assert.Equal(12, byClass[ActionSafety.GuardedWrite]);
		Assert.Equal(5, byClass[ActionSafety.NonIdempotent]);
	}

	[Theory]
	[InlineData("send_trade_offer")] // NonIdempotent
	[InlineData("create_market_listing")] // NonIdempotent
	[InlineData("accept_trade_offer")] // GuardedWrite
	[InlineData("redeem_key")] // GuardedWrite
	public void MaxDispatchAttempts_UnsafeClasses_CapAtConservativeCeiling(string action)
	{
		Assert.Equal(ActionSemantics.ConservativeMaxDispatchAttempts, ActionSemantics.MaxDispatchAttempts(action, 10));
	}

	[Fact]
	public void MaxDispatchAttempts_UnknownAction_CappedConservatively()
	{
		// A future/plugin action absent from the table must never spin at the configured ceiling.
		Assert.Equal(ActionSemantics.ConservativeMaxDispatchAttempts, ActionSemantics.MaxDispatchAttempts("no_such_action", 10));
		Assert.Equal(ActionSafety.Unknown, ActionSemantics.SafetyOf("no_such_action"));
	}

	[Theory]
	[InlineData("login")] // Idempotent
	[InlineData("get_inventory")] // ReadOnly
	[InlineData("echo")] // ReadOnly
	public void MaxDispatchAttempts_SafeClasses_UseConfiguredCeiling(string action)
	{
		Assert.Equal(10, ActionSemantics.MaxDispatchAttempts(action, 10));
	}

	[Fact]
	public void MaxDispatchAttempts_RespectsLowerConfiguredCeiling()
	{
		// A ceiling below the conservative cap wins for unsafe classes...
		Assert.Equal(1, ActionSemantics.MaxDispatchAttempts("send_trade_offer", 1));
		// ...and a zero ceiling keeps today's "unlimited" semantics.
		Assert.Equal(0, ActionSemantics.MaxDispatchAttempts("send_trade_offer", 0));
		Assert.Equal(0, ActionSemantics.MaxDispatchAttempts("login", 0));
	}
}