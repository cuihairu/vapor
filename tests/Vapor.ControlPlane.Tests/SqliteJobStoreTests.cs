using System.Reflection;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed partial class SqliteJobStoreTests
{
	// ── guard branches (defensive NOT-FOUND / recompute paths) ──
	[Fact]
	public void Constructor_BlankDbPath_Throws()
	{
		Assert.Throws<ArgumentException>(() => new SqliteJobStore("   "));
	}

	/// <summary>
	/// Runs raw SQL on the store's own connection to inject or mutate rows the public
	/// API cannot produce (corrupt/canceled states the guard branches must survive).
	/// </summary>
	private static void RawExec(SqliteJobStore store, string sql, params (string Name, object Value)[] parameters)
	{
		var connectionField = typeof(SqliteJobStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(connectionField);
		var connection = (Microsoft.Data.Sqlite.SqliteConnection?)connectionField!.GetValue(store);
		Assert.NotNull(connection);

		using var cmd = connection!.CreateCommand();
		cmd.CommandText = sql;
		foreach ((string name, object value) in parameters)
		{
			cmd.Parameters.AddWithValue(name, value);
		}

		cmd.ExecuteNonQuery();
	}

	private static string RawScalar(SqliteJobStore store, string sql, params (string Name, object Value)[] parameters)
	{
		var connectionField = typeof(SqliteJobStore).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(connectionField);
		var connection = (Microsoft.Data.Sqlite.SqliteConnection?)connectionField!.GetValue(store);
		Assert.NotNull(connection);

		using var cmd = connection!.CreateCommand();
		cmd.CommandText = sql;
		foreach ((string name, object value) in parameters)
		{
			cmd.Parameters.AddWithValue(name, value);
		}

		return Assert.IsType<string>(cmd.ExecuteScalar());
	}

}
