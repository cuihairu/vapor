using System.Text.Json;
using Vapor.Plugins.GameAccess;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

/// <summary>
/// Dedicated unit tests for the shared <see cref="AchievementsWritePayload"/> parser and output formatter.
/// These are pure functions with no external dependencies — no mocks, no BotSession, no network.
/// </summary>
public sealed class AchievementsWritePayloadTests
{
	[Theory]
	[InlineData("""{ "app_id": "400", "names": ["ACH_ONE"] }""", false, 400, new[] { "ACH_ONE" })]
	[InlineData("""{ "app_id": "400", "names": ["ACH_ONE", "ACH_TWO"] }""", false, 400, new[] { "ACH_ONE", "ACH_TWO" })]
	[InlineData("""{ "app_id": "400", "names": ["7", "ACH_ONE"] }""", false, 400, new[] { "7", "ACH_ONE" })]
	public void TryParse_Unlock_ValidInputs_ParsesCorrectly(string json, bool requireConfirm, uint expectedAppId, string[] expectedNames)
	{
		var payload = Json(json);

		bool ok = AchievementsWritePayload.TryParse(payload, requireConfirm, out uint appId, out List<string> names, out string? error);

		Assert.True(ok, error);
		Assert.Equal(expectedAppId, appId);
		Assert.Equal(expectedNames, names);
		Assert.Null(error);
	}

	[Theory]
	[InlineData("""{ "app_id": "400", "names": ["ACH_ONE"], "confirm": true }""", 400, new[] { "ACH_ONE" })]
	[InlineData("""{ "app_id": "400", "names": ["ACH_ONE", "ACH_TWO"], "confirm": true }""", 400, new[] { "ACH_ONE", "ACH_TWO" })]
	public void TryParse_Reset_ValidInputs_ParsesCorrectly(string json, uint expectedAppId, string[] expectedNames)
	{
		var payload = Json(json);

		bool ok = AchievementsWritePayload.TryParse(payload, requireConfirm: true, out uint appId, out List<string> names, out string? error);

		Assert.True(ok, error);
		Assert.Equal(expectedAppId, appId);
		Assert.Equal(expectedNames, names);
		Assert.Null(error);
	}

	[Theory]
	[InlineData("""{ "names": ["ACH_ONE"] }""", "app_id parameter is required")]
	[InlineData("""{ "app_id": "", "names": ["ACH_ONE"] }""", "app_id parameter is required")]
	[InlineData("""{ "app_id": "0", "names": ["ACH_ONE"] }""", "app_id parameter is required")]
	[InlineData("""{ "app_id": "abc", "names": ["ACH_ONE"] }""", "app_id parameter is required")]
	[InlineData("""{ "app_id": "400" }""", "explicit non-empty list")]
	[InlineData("""{ "app_id": "400", "names": [] }""", "explicit non-empty list")]
	[InlineData("""{ "app_id": "400", "names": null }""", "explicit non-empty list")]
	[InlineData("""{ "app_id": "400", "names": 42 }""", "explicit non-empty list")]
	public void TryParse_InvalidInputs_ReturnsFalseWithError(string json, string expectedErrorFragment)
	{
		var payload = Json(json);

		bool ok = AchievementsWritePayload.TryParse(payload, requireConfirm: false, out _, out _, out string? error);

		Assert.False(ok);
		Assert.NotNull(error);
		Assert.Contains(expectedErrorFragment, error, StringComparison.Ordinal);
	}

	[Fact]
	public void TryParse_Reset_MissingConfirm_ReturnsFalse()
	{
		var payload = Json("""{ "app_id": "400", "names": ["ACH_ONE"] }""");

		bool ok = AchievementsWritePayload.TryParse(payload, requireConfirm: true, out _, out _, out string? error);

		Assert.False(ok);
		Assert.Contains("confirm must be explicitly true", error, StringComparison.Ordinal);
	}

	[Fact]
	public void TryParse_Reset_ConfirmFalse_ReturnsFalse()
	{
		var payload = Json("""{ "app_id": "400", "names": ["ACH_ONE"], "confirm": false }""");

		bool ok = AchievementsWritePayload.TryParse(payload, requireConfirm: true, out _, out _, out string? error);

		Assert.False(ok);
		Assert.Contains("confirm must be explicitly true", error, StringComparison.Ordinal);
	}

	[Fact]
	public void TryParse_Reset_ConfirmTrue_ParsesCorrectly()
	{
		var payload = Json("""{ "app_id": "400", "names": ["ACH_ONE"], "confirm": true }""");

		bool ok = AchievementsWritePayload.TryParse(payload, requireConfirm: true, out uint appId, out List<string> names, out string? error);

		Assert.True(ok, error);
		Assert.Equal(400u, appId);
		Assert.Equal(new[] { "ACH_ONE" }, names);
	}

	[Fact]
	public void TryParse_DuplicateNames_PreservesDuplicates_TransportResponsible()
	{
		// The parser does NOT deduplicate — the transport layer is responsible for that.
		// This is by design (see UnlockAchievementsActionTests.ExecuteAsync_Success_PassesNamesAndShapesPerItemResults).
		var payload = Json("""{ "app_id": "400", "names": ["ach_one", "ACH_ONE", "ACH_TWO"] }""");

		bool ok = AchievementsWritePayload.TryParse(payload, requireConfirm: false, out _, out List<string> names, out _);

		Assert.True(ok);
		Assert.Equal(new[] { "ach_one", "ACH_ONE", "ACH_TWO" }, names);
	}

	[Fact]
	public void ToOutput_ShapesResultCorrectly()
	{
		var result = new AchievementWriteResult(false, SteamResult.OK,
			new[]
			{
				new AchievementWriteEntry("ACH_ONE", true, null),
				new AchievementWriteEntry("ACH_TWO", false, "unknown achievement name"),
				new AchievementWriteEntry("ACH_THREE", true, null),
			},
			Verified: true);

		var output = AchievementsWritePayload.ToOutput(400, unlock: true, requestedCount: 3, result);

		Assert.Equal(400u, output["app_id"]);
		Assert.True((bool)output["unlock"]!);
		Assert.Equal(3, output["requested_count"]);
		Assert.Equal(2, output["succeeded_count"]);
		Assert.Equal(1, output["failed_count"]);
		Assert.True((bool)output["verified"]!);

		var results = Assert.IsType<List<Dictionary<string, object?>>>(output["results"]);
		Assert.Equal(3, results.Count);
		Assert.Equal("ACH_ONE", results[0]["name"]);
		Assert.True((bool)results[0]["success"]!);
		Assert.Null(results[0]["detail"]);
		Assert.Equal("ACH_TWO", results[1]["name"]);
		Assert.False((bool)results[1]["success"]!);
		Assert.Equal("unknown achievement name", results[1]["detail"]);
		Assert.Equal("ACH_THREE", results[2]["name"]);
		Assert.True((bool)results[2]["success"]!);
	}

	[Fact]
	public void ToOutput_AllFailed_ReportsZeroSucceeded()
	{
		var result = new AchievementWriteResult(false, SteamResult.OK,
			new[]
			{
				new AchievementWriteEntry("ACH_ONE", false, "error 1"),
				new AchievementWriteEntry("ACH_TWO", false, "error 2"),
			},
			Verified: false);

		var output = AchievementsWritePayload.ToOutput(400, unlock: false, requestedCount: 2, result);

		Assert.False((bool)output["unlock"]!);
		Assert.Equal(2, output["requested_count"]);
		Assert.Equal(0, output["succeeded_count"]);
		Assert.Equal(2, output["failed_count"]);
		Assert.False((bool)output["verified"]!);
	}

	private static Dictionary<string, object?> Json(string json)
		=> JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
}
