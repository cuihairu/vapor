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
	public void List_IsOrderedByAccountName()
	{
		var store = new AccountStore();
		store.Upsert("carol", enabled: true, AccountDesiredState.Offline, null, null, null, null);
		store.Upsert("alice", enabled: true, AccountDesiredState.Offline, null, null, null, null);
		store.Upsert("bob", enabled: true, AccountDesiredState.Offline, null, null, null, null);

		Assert.Equal(new[] { "alice", "bob", "carol" }, store.List().Select(a => a.AccountName));
	}
}
