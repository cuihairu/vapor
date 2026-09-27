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
	// ── Persistence through a SqliteConfigStore sink (settings survive
	// ── restarts — the claim consistency.md has always made for this store).

	[Fact]
	public async Task Persistence_SettingsSurviveRestart_AndVersionsContinue()
	{
		string root = Path.Combine(Path.GetTempPath(), $"vapor-cfgstore-{Guid.NewGuid():N}");
		string dbPath = Path.Combine(root, "config.db");
		try
		{
			var persist1 = new SqliteConfigStore(dbPath);
			var store1 = new ConfigStore(persist1);
			// An empty database still seeds the default global config.
			Assert.Equal(1, store1.GetGlobal().Version.Version);
			store1.SetGlobal(new Dictionary<string, object?> { ["theme"] = "dark" }, "operator");
			store1.SetAccount("alice", enabled: true, region: "us", labels: ["l"], settings: null, updatedBy: "op");
			persist1.Dispose();

			var persist2 = new SqliteConfigStore(dbPath);
			var store2 = new ConfigStore(persist2);

			Assert.Equal(2, store2.GetGlobal().Version.Version);
			Assert.Equal("operator", store2.GetGlobal().Version.UpdatedBy);
			AccountConfig alice = Assert.Single(store2.ListAccounts());
			Assert.Equal("us", alice.Region);
			Assert.Equal(new[] { "l" }, alice.Labels);

			AccountConfig updated = store2.SetAccount("alice", enabled: false, region: "eu", labels: null, settings: null, updatedBy: null);
			Assert.Equal(2, updated.Version!.Version);
			persist2.Dispose();

			var persist3 = new SqliteConfigStore(dbPath);
			var store3 = new ConfigStore(persist3);
			Assert.Equal("eu", Assert.Single(store3.ListAccounts()).Region);
			// Account settings live on their own rows: global stays at 2.
			Assert.Equal(2, store3.GetGlobal().Version.Version);
			persist3.Dispose();
		}
		finally
		{
			await CleanupRootAsync(root);
		}
	}

	[Fact]
	public void Persistence_WriteThroughFailure_LeavesMemoryUntouched()
	{
		var persist = new SqliteConfigStore(":memory:");
		var store = new ConfigStore(persist);
		GlobalConfig before = store.GetGlobal();

		persist.Dispose();

		Assert.ThrowsAny<Exception>(() => store.SetGlobal(new Dictionary<string, object?>(), "op"));
		Assert.Same(before, store.GetGlobal());
		Assert.ThrowsAny<Exception>(() => store.SetAccount("alice", enabled: true, region: null, labels: null, settings: null, updatedBy: null));
		Assert.Empty(store.ListAccounts());
	}

	private static async Task CleanupRootAsync(string root)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				Directory.Delete(root, recursive: true);
				return;
			}
			catch (IOException)
			{
				// The connection pool may still hold the file briefly after dispose.
				await Task.Delay(50);
			}
		}
	}
}
