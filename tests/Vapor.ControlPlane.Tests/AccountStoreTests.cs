using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class AccountStoreTests
{
	[Fact]
	public void Upsert_NewAccount_AssignsVersionOne()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert("alice", enabled: true, AccountDesiredState.Idle, ["730", "570"], "us-east", "agent-1", "farm bot");

		Assert.Equal("alice", spec.AccountName);
		Assert.True(spec.Enabled);
		Assert.Equal(AccountDesiredState.Idle, spec.DesiredState);
		Assert.Equal(new[] { "730", "570" }, spec.IdleApps);
		Assert.Equal("us-east", spec.Region);
		Assert.Equal("agent-1", spec.AgentId);
		Assert.Equal("farm bot", spec.Note);
		Assert.Equal(1, spec.Version!.Version);
	}

	[Fact]
	public void Upsert_ExistingAccount_IncrementsVersionAndReplacesFields()
	{
		var store = new AccountStore();
		store.Upsert("alice", enabled: true, AccountDesiredState.Online, null, "us-east", null, null);

		AccountSpec updated = store.Upsert("ALICE", enabled: true, AccountDesiredState.Idle, ["730"], null, "agent-2", null);

		Assert.Equal(2, updated.Version!.Version);
		Assert.Equal(AccountDesiredState.Idle, updated.DesiredState);
		Assert.Equal(new[] { "730" }, updated.IdleApps);
		Assert.Null(updated.Region);
		Assert.Equal("agent-2", updated.AgentId);
		Assert.Single(store.List());
	}

	[Fact]
	public void Upsert_TrimsAccountName()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert("  alice  ", enabled: true, AccountDesiredState.Offline, null, null, null, null);

		Assert.Equal("alice", spec.AccountName);
	}

	[Fact]
	public void Upsert_NormalizesIdleApps_TrimsDropsDuplicatesAndEmpty()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert("alice", enabled: true, AccountDesiredState.Idle, [" 730", "730", "", "570 "], null, null, null);

		Assert.Equal(new[] { "730", "570" }, spec.IdleApps);
	}

	[Fact]
	public void Upsert_EmptyIdleAppsList_BecomesNull()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert("alice", enabled: true, AccountDesiredState.Idle, Array.Empty<string>(), null, null, null);

		Assert.Null(spec.IdleApps);
	}

	[Theory]
	[InlineData("abc")]
	[InlineData("73.0")]
	[InlineData("id/730")]
	public void Upsert_NonNumericIdleApp_Throws(string appId)
	{
		var store = new AccountStore();

		Assert.Throws<ArgumentException>(() => store.Upsert("alice", enabled: true, AccountDesiredState.Idle, [appId], null, null, null));
	}

	[Fact]
	public void Upsert_WhitespaceOptionalFields_BecomeNull()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert("alice", enabled: true, AccountDesiredState.Online, null, "  ", " ", "\t", "bob");

		Assert.Null(spec.Region);
		Assert.Null(spec.AgentId);
		Assert.Null(spec.Note);
		Assert.Equal("bob", spec.Version!.UpdatedBy);
	}

	[Fact]
	public void Get_IsCaseInsensitiveAndTrimsInput()
	{
		var store = new AccountStore();
		store.Upsert("alice", enabled: true, AccountDesiredState.Online, null, null, null, null);

		Assert.NotNull(store.Get("  ALICE "));
		Assert.Null(store.Get("bob"));
		Assert.Null(store.Get(""));
	}

	[Fact]
	public void SetEnabled_TogglesFlagWithoutTouchingDesiredStateAndIncrementsVersion()
	{
		var store = new AccountStore();
		store.Upsert("alice", enabled: true, AccountDesiredState.Idle, ["730"], null, null, null);

		AccountSpec? disabled = store.SetEnabled("alice", enabled: false);

		Assert.NotNull(disabled);
		Assert.False(disabled!.Enabled);
		Assert.Equal(AccountDesiredState.Idle, disabled.DesiredState);
		Assert.Equal(new[] { "730" }, disabled.IdleApps);
		Assert.Equal(2, disabled.Version!.Version);

		Assert.Equal(3, store.SetEnabled("ALICE", enabled: true)!.Version!.Version);
	}

	[Fact]
	public void SetEnabled_MissingAccount_ReturnsNull()
	{
		var store = new AccountStore();

		Assert.Null(store.SetEnabled("ghost", enabled: false));
	}

	[Fact]
	public void Remove_DeletesAndReturnsSpec()
	{
		var store = new AccountStore();
		store.Upsert("alice", enabled: true, AccountDesiredState.Online, null, null, null, null);

		AccountSpec? removed = store.Remove("alice");

		Assert.NotNull(removed);
		Assert.Equal("alice", removed!.AccountName);
		Assert.Null(store.Get("alice"));
		Assert.Null(store.Remove("alice"));
		Assert.Empty(store.List());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void SetEnabled_WhitespaceAccount_ReturnsNull(string? accountName)
	{
		var store = new AccountStore();
		store.Upsert("alice", enabled: true, AccountDesiredState.Online, null, null, null, null);

		Assert.Null(store.SetEnabled(accountName!, enabled: true));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Remove_WhitespaceAccount_ReturnsNull(string? accountName)
	{
		var store = new AccountStore();
		store.Upsert("alice", enabled: true, AccountDesiredState.Online, null, null, null, null);

		Assert.Null(store.Remove(accountName!));
		Assert.NotNull(store.Get("alice")); // untouched
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Upsert_WhitespaceAccount_Throws(string? accountName)
	{
		var store = new AccountStore();

		Assert.Throws<ArgumentException>(
			() => store.Upsert(accountName!, enabled: true, AccountDesiredState.Online, null, null, null, null));
	}

	[Fact]
	public void Upsert_BoostState_WithoutTargets_Throws()
	{
		var store = new AccountStore();

		Assert.Throws<ArgumentException>(
			() => store.Upsert("alice", enabled: true, AccountDesiredState.Boost, null, null, null, null));
	}

	[Fact]
	public void Upsert_BoostTargets_NormalizesSortsAndKeepsUsableEntries()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert(
			"alice", enabled: true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(620u, 36.7), new BoostTarget(0u, 5), new BoostTarget(220u, 1234.5)]);

		// Zero app ids carry no playable identity; survivors sort by app id
		// so the spec is independent of input order.
		Assert.Equal(
			new[] { new BoostTarget(220u, 1234.5), new BoostTarget(620u, 36.7) },
			spec.BoostTargets);
	}

	[Fact]
	public void Upsert_DuplicateIdenticalBoostTargets_Collapse()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert(
			"alice", enabled: true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(220u, 10), new BoostTarget(220u, 10)]);

		var only = Assert.Single(spec.BoostTargets!);
		Assert.Equal(new BoostTarget(220u, 10), only);
	}

	[Fact]
	public void Upsert_ConflictingBoostTargets_Throws()
	{
		var store = new AccountStore();

		Assert.Throws<ArgumentException>(
			() => store.Upsert(
				"alice", enabled: true, AccountDesiredState.Boost, null, null, null, null,
				boostTargets: [new BoostTarget(220u, 10), new BoostTarget(220u, 20)]));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-5)]
	public void Upsert_NonPositiveBoostTargetHours_Throws(double hours)
	{
		var store = new AccountStore();

		Assert.Throws<ArgumentException>(
			() => store.Upsert(
				"alice", enabled: true, AccountDesiredState.Boost, null, null, null, null,
				boostTargets: [new BoostTarget(220u, hours)]));
	}

	[Fact]
	public void Upsert_NonFiniteBoostTargetHours_Throws()
	{
		var store = new AccountStore();

		Assert.Throws<ArgumentException>(
			() => store.Upsert(
				"alice", enabled: true, AccountDesiredState.Boost, null, null, null, null,
				boostTargets: [new BoostTarget(220u, double.NaN)]));
		Assert.Throws<ArgumentException>(
			() => store.Upsert(
				"alice", enabled: true, AccountDesiredState.Boost, null, null, null, null,
				boostTargets: [new BoostTarget(220u, double.PositiveInfinity)]));
	}

	[Fact]
	public void Upsert_AllTargetsFilteredOut_BecomesNull()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert(
			"alice", enabled: true, AccountDesiredState.Online, null, null, null, null,
			boostTargets: [new BoostTarget(0u, 5)]);

		Assert.Null(spec.BoostTargets);
	}

	[Fact]
	public void Upsert_BoostTargets_PreservedAcrossOtherStates()
	{
		// Targets may be pre-configured before switching to the Boost state.
		var store = new AccountStore();

		AccountSpec online = store.Upsert(
			"alice", enabled: true, AccountDesiredState.Online, null, null, null, null,
			boostTargets: [new BoostTarget(220u, 10)]);

		AccountSpec updated = store.Upsert(
			"ALICE", enabled: true, AccountDesiredState.Boost, null, null, null, null,
			boostTargets: [new BoostTarget(220u, 10)]);

		Assert.NotNull(online.BoostTargets);
		Assert.Equal(2, updated.Version!.Version);
		Assert.Equal(
			new[] { new BoostTarget(220u, 10) },
			updated.BoostTargets);
	}

	[Fact]
	public void Upsert_TradePolicyWhitelist_NormalizesSortsAndDeduplicates()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert(
			"alice", enabled: true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(
				AutoAcceptGifts: false,
				PartnerWhitelist: [76561198000000000ul, 76561197960265728ul, 76561198000000000ul, 0ul]));

		// Zero ids carry no usable Steam identity and are dropped; survivors
		// sort ascending so the spec is independent of input order.
		Assert.NotNull(spec.TradePolicy);
		Assert.False(spec.TradePolicy!.AutoAcceptGifts);
		Assert.Equal(
			new ulong[] { 76561197960265728, 76561198000000000 },
			spec.TradePolicy.PartnerWhitelist!.ToArray());
	}

	[Fact]
	public void Upsert_AutoAcceptGifts_WithEmptyWhitelist_Throws()
	{
		var store = new AccountStore();

		// Red line ② of todo §32: an empty whitelist means the policy is off —
		// the declaration-time interlock must reject the contradictory combo
		// instead of letting the reconciler silently skip everything.
		Assert.Throws<ArgumentException>(
			() => store.Upsert(
				"alice", enabled: true, AccountDesiredState.Online, null, null, null, null,
				tradePolicy: new TradePolicy(AutoAcceptGifts: true)));
		Assert.Throws<ArgumentException>(
			() => store.Upsert(
				"alice", enabled: true, AccountDesiredState.Online, null, null, null, null,
				tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [0ul])));
	}

	[Fact]
	public void Upsert_TradePolicyWithNothingActive_NormalizesToNull()
	{
		var store = new AccountStore();

		AccountSpec spec = store.Upsert(
			"alice", enabled: true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: false, PartnerWhitelist: []));

		Assert.Null(spec.TradePolicy);
	}

	[Fact]
	public void Upsert_OmittedTradePolicy_ClearsPreviousOne()
	{
		// Full replace semantics, same direction as the rest of the spec: an
		// update that forgets the policy lands on the safe side — no
		// auto-accept — never on silently keeping it enabled.
		var store = new AccountStore();
		store.Upsert(
			"alice", enabled: true, AccountDesiredState.Online, null, null, null, null,
			tradePolicy: new TradePolicy(AutoAcceptGifts: true, PartnerWhitelist: [76561197960265728ul]));

		AccountSpec updated = store.Upsert("alice", enabled: true, AccountDesiredState.Online, null, null, null, null);

		Assert.Null(updated.TradePolicy);
	}

	[Fact]
	public void List_IsOrderedByAccountName()
	{
		var store = new AccountStore();
		store.Upsert("carol", enabled: true, AccountDesiredState.Offline, null, null, null, null);
		store.Upsert("alice", enabled: true, AccountDesiredState.Offline, null, null, null, null);
		store.Upsert("bob", enabled: true, AccountDesiredState.Offline, null, null, null, null);

		Assert.Equal(new[] { "alice", "bob", "carol" }, store.List().Select(a => a.AccountName));
	}
}
