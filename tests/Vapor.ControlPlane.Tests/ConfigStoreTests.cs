using System.Reflection;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class ConfigStoreTests
{
	[Fact]
	public void SetGlobal_AcceptsNullSettingsAndAnonymousUpdater()
	{
		var store = new ConfigStore();

		GlobalConfig updated = store.SetGlobal(settings: null, updatedBy: "  ");

		// A blank updater is normalized to null and null settings fall back to an empty map.
		Assert.Equal(2, updated.Version.Version);
		Assert.Null(updated.Version.UpdatedBy);
		Assert.NotNull(updated.Settings);
		Assert.Empty(updated.Settings);
	}

	[Fact]
	public void SetAccount_BlankAccountName_Throws()
	{
		var store = new ConfigStore();

		Assert.Throws<ArgumentException>(() => store.SetAccount("   ", enabled: true, region: null, labels: null, settings: null, updatedBy: null));
	}

	[Fact]
	public void ListAccounts_OrdersCaseInsensitivelyByName()
	{
		// The OrderBy chain in ListAccounts only executes against a non-empty store;
		// a case-insensitive sort must not let "bob" (lowercase) jump ahead of "Alice".
		var store = new ConfigStore();
		store.SetAccount("bob", enabled: true, region: null, labels: null, settings: null, updatedBy: null);
		store.SetAccount("ALICE", enabled: true, region: null, labels: null, settings: null, updatedBy: null);

		IReadOnlyList<AccountConfig> accounts = store.ListAccounts();

		Assert.Equal(new[] { "ALICE", "bob" }, accounts.Select(a => a.AccountName).ToArray());
	}

	[Fact]
	public void SetAccount_ExistingAccount_IncrementsVersionInPlace()
	{
		var store = new ConfigStore();
		store.SetAccount("alice", enabled: true, region: null, labels: null, settings: null, updatedBy: null);

		AccountConfig updated = store.SetAccount("ALICE", enabled: false, region: "us-east", labels: null, settings: null, updatedBy: "bob");

		Assert.Equal(2, updated.Version!.Version);
		Assert.False(updated.Enabled);
		Assert.Equal("us-east", updated.Region);
		Assert.Equal("bob", updated.Version.UpdatedBy);
		Assert.Single(store.ListAccounts());
	}

	[Fact]
	public void SetAccount_ExistingConfigWithoutVersion_RestartsVersionAtOne()
	{
		// Defensive-arm contract: SetAccount is the only writer and always stamps
		// a Version, so the `existing?.Version?.Version ?? 0` fallback is only
		// reachable for a record that entered the dictionary without one —
		// inject it directly so the guard behavior stays pinned.
		var store = new ConfigStore();
		Dictionary<string, AccountConfig> accounts = (Dictionary<string, AccountConfig>)typeof(ConfigStore)
			.GetField("_accounts", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(store)!;
		accounts["legacy"] = new AccountConfig("legacy", Enabled: true, Version: null);

		AccountConfig updated = store.SetAccount("legacy", enabled: false, region: null, labels: null, settings: null, updatedBy: "op");

		Assert.Equal(1, updated.Version!.Version);
		Assert.False(updated.Enabled);
		Assert.Single(store.ListAccounts());
	}
}
