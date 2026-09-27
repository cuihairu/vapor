using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Unit tests for the declared-state persistence sink: payload round-trip
/// fidelity (the store is the only writer, the reader must give back exactly
/// what went in), the case-insensitive primary key (mirroring the stores'
/// OrdinalIgnoreCase dictionaries), fail-closed loading, and the constructor
/// path arms shared with the other Sqlite stores.
/// </summary>
public sealed class SqliteConfigStoreTests
{
	private static AccountSpec SampleSpec(
		string name = "alice",
		AccountDesiredState state = AccountDesiredState.Idle,
		string? region = "us-east") => new(
		AccountName: name,
		Enabled: true,
		DesiredState: state,
		IdleApps: new List<string> { "730", "570" },
		Region: region,
		AgentId: "agent-1",
		Note: "farm bot",
		Version: new ConfigVersion(3, DateTimeOffset.FromUnixTimeSeconds(1000), "operator"),
		MarketListingsEnabled: true,
		BoostTargets: new List<BoostTarget> { new(480u, 2.5) },
		TradePolicy: new TradePolicy(true, new ulong[] { 123 }),
		FarmPolicy: new FarmPolicy(60, FarmPriorityOrder.CardsDescending, new uint[] { 730 }));

	[Fact]
	public void Spec_RoundTripsEveryField()
	{
		using var store = new SqliteConfigStore(":memory:");

		store.SaveAccountSpec(SampleSpec());

		AccountSpec loaded = Assert.Single(store.LoadAccountSpecs());
		Assert.Equal("alice", loaded.AccountName);
		Assert.True(loaded.Enabled);
		Assert.Equal(AccountDesiredState.Idle, loaded.DesiredState);
		Assert.Equal(new[] { "730", "570" }, loaded.IdleApps);
		Assert.Equal("us-east", loaded.Region);
		Assert.Equal("agent-1", loaded.AgentId);
		Assert.Equal("farm bot", loaded.Note);
		Assert.Equal(3, loaded.Version!.Version);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1000), loaded.Version.UpdatedAt);
		Assert.Equal("operator", loaded.Version.UpdatedBy);
		Assert.True(loaded.MarketListingsEnabled);
		BoostTarget boost = Assert.Single(loaded.BoostTargets!);
		Assert.Equal(480u, boost.AppId);
		Assert.Equal(2.5, boost.TargetHours);
		Assert.True(loaded.TradePolicy!.AutoAcceptGifts);
		Assert.Equal(new ulong[] { 123 }, loaded.TradePolicy.PartnerWhitelist);
		Assert.Equal(60, loaded.FarmPolicy!.PerGameHourBudget);
		Assert.Equal(FarmPriorityOrder.CardsDescending, loaded.FarmPolicy.PriorityOrder);
		Assert.Equal(new uint[] { 730 }, loaded.FarmPolicy.PriorityApps);
	}

	[Fact]
	public void Specs_LoadSortedByName_MultipleRows()
	{
		using var store = new SqliteConfigStore(":memory:");

		store.SaveAccountSpec(SampleSpec("zed"));
		store.SaveAccountSpec(SampleSpec("amy"));

		Assert.Equal(new[] { "amy", "zed" }, store.LoadAccountSpecs().Select(s => s.AccountName));
	}

	[Fact]
	public void SaveSpec_SameNameDifferentCase_OverwritesSingleRow()
	{
		using var store = new SqliteConfigStore(":memory:");

		store.SaveAccountSpec(SampleSpec("Foo", region: "old"));
		store.SaveAccountSpec(SampleSpec("foo", region: "new"));

		// The NOCASE primary key mirrors the OrdinalIgnoreCase dictionary: one
		// entry wins, carrying the latest payload (with its own name casing).
		AccountSpec loaded = Assert.Single(store.LoadAccountSpecs());
		Assert.Equal("new", loaded.Region);
	}

	[Fact]
	public void DeleteSpec_RemovesRow_AndUnknownNameIsNoOp()
	{
		using var store = new SqliteConfigStore(":memory:");
		store.SaveAccountSpec(SampleSpec("alice"));

		store.DeleteAccountSpec("ALICE");
		store.DeleteAccountSpec("never-there");

		Assert.Empty(store.LoadAccountSpecs());
	}

	[Fact]
	public void Global_RoundTripsThroughPayload_AndAbsentLoadsNull()
	{
		using var store = new SqliteConfigStore(":memory:");
		Assert.Null(store.LoadGlobalConfig());

		var config = new GlobalConfig(
			Version: new ConfigVersion(2, DateTimeOffset.FromUnixTimeSeconds(500), "operator"),
			Settings: new Dictionary<string, object?> { ["theme"] = JsonSerializer.SerializeToElement("dark"), ["retries"] = JsonSerializer.SerializeToElement(3), ["off"] = null });

		store.SaveGlobalConfig(config);

		// Save is an upsert: the second write replaces, never duplicates.
		store.SaveGlobalConfig(config with { Version = config.Version with { Version = 3 } });
		GlobalConfig? loaded = store.LoadGlobalConfig();

		Assert.NotNull(loaded);
		Assert.Equal(3, loaded!.Version.Version);
		Assert.Equal("operator", loaded.Version.UpdatedBy);
		Assert.Equal("dark", ((JsonElement)loaded.Settings!["theme"]!).GetString());
		Assert.Equal(3, ((JsonElement)loaded.Settings!["retries"]!).GetInt32());
		Assert.Null(loaded.Settings["off"]);
	}

	[Fact]
	public void AccountConfig_RoundTripsAndOverwritesCaseInsensitively()
	{
		using var store = new SqliteConfigStore(":memory:");
		Assert.Empty(store.LoadAccountConfigs());

		store.SaveAccountConfig(new AccountConfig("Bob", Enabled: true, Region: "eu", Labels: new List<string> { "tag" }, Version: new ConfigVersion(1, DateTimeOffset.FromUnixTimeSeconds(1))));
		store.SaveAccountConfig(new AccountConfig("BOB", Enabled: false, Region: "eu2", Version: new ConfigVersion(2, DateTimeOffset.FromUnixTimeSeconds(2))));

		AccountConfig loaded = Assert.Single(store.LoadAccountConfigs());
		Assert.False(loaded.Enabled);
		Assert.Equal("eu2", loaded.Region);
		Assert.Null(loaded.Labels);
	}

	[Fact]
	public void Ctor_BlankPath_Throws()
	{
		Assert.Throws<ArgumentException>(() => new SqliteConfigStore("   "));
	}

	[Fact]
	public async Task Ctor_CreatesMissingDirectory_AndReopensSurvives()
	{
		string root = Path.Combine(Path.GetTempPath(), $"vapor-cfg-{Guid.NewGuid():N}");
		string dbPath = Path.Combine(root, "nested", "config.db");
		try
		{
			using (var first = new SqliteConfigStore(dbPath))
			{
				first.SaveAccountSpec(SampleSpec());
			}

			using var reopened = new SqliteConfigStore(dbPath);
			Assert.Single(reopened.LoadAccountSpecs());
		}
		finally
		{
			await TryDeleteWithRetryAsync(root);
		}
	}

	[Fact]
	public async Task Ctor_RelativePathWithoutDirectory_Opens()
	{
		// Path.GetDirectoryName("x.db") is "" — the no-directory arm of the ctor.
		string name = $"vapor-cfg-relative-{Guid.NewGuid():N}.db";
		try
		{
			using var store = new SqliteConfigStore(name);
			Assert.Empty(store.LoadAccountSpecs());
		}
		finally
		{
			await TryDeleteWithRetryAsync(name);
		}
	}

	[Fact]
	public async Task CorruptPayloads_FailClosedOnLoad()
	{
		string dbPath = Path.Combine(Path.GetTempPath(), $"vapor-cfg-{Guid.NewGuid():N}.db");
		try
		{
			using (var seed = new SqliteConfigStore(dbPath))
			{
				seed.SaveAccountSpec(SampleSpec("good"));
			}

			// Poison the row from the outside: a JSON "null" deserializes to null,
			// which must abort the load instead of silently shrinking the set.
			using (var conn = new SqliteConnection($"Data Source={dbPath}"))
			{
				conn.Open();
				using var cmd = conn.CreateCommand();
				cmd.CommandText = "UPDATE account_specs SET payload = 'null';";
				cmd.ExecuteNonQuery();
			}

			using var reopened = new SqliteConfigStore(dbPath);
			Assert.Throws<InvalidDataException>(() => reopened.LoadAccountSpecs());
		}
		finally
		{
			await TryDeleteWithRetryAsync(dbPath);
		}
	}

	[Fact]
	public void NullArguments_Throw()
	{
		using var store = new SqliteConfigStore(":memory:");

		Assert.Throws<ArgumentNullException>(() => store.SaveAccountSpec(null!));
		Assert.Throws<ArgumentNullException>(() => store.SaveGlobalConfig(null!));
		Assert.Throws<ArgumentNullException>(() => store.SaveAccountConfig(null!));
	}

	private static async Task TryDeleteWithRetryAsync(string path)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			try
			{
				if (Directory.Exists(path))
				{
					Directory.Delete(path, recursive: true);
				}
				else
				{
					File.Delete(path);
				}

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
