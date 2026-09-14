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
}
