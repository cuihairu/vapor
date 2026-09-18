using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

// Payloads go through the WS/SQLite JSON round-trip, so every value arrives as a
// JsonElement — the tests deserialize dictionaries the same way the transport does.
public sealed class UnlockAchievementsActionTests
{
	private readonly UnlockAchievementsAction _action = new();

	[Fact]
	public void Metadata_RequiresLoginAndLongTimeout()
	{
		Assert.Equal("unlock_achievements", _action.Name);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(180, _action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_MissingAppId_IsRejected()
	{
		BotSession session = CreateSession(out var clientMock);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "names": ["ACH_ONE"] }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("app_id", result.Error, StringComparison.Ordinal);
		clientMock.Verify(m => m.SetAchievementStatesAsync(
			It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_MissingNames_IsRejectedWithoutCallingTheTransport()
	{
		BotSession session = CreateSession(out var clientMock);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400" }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("explicit non-empty list", result.Error, StringComparison.Ordinal);
		clientMock.Verify(m => m.SetAchievementStatesAsync(
			It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_EmptyNamesList_IsRejected()
	{
		BotSession session = CreateSession(out _);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": [] }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("explicit non-empty list", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_Success_PassesNamesAndShapesPerItemResults()
	{
		BotSession session = CreateSession(out var clientMock);
		var capturedNames = new List<IReadOnlyList<string>>();
		var capturedUnlock = new List<bool>();
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(400, Capture.In(capturedNames), Capture.In(capturedUnlock), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AchievementWriteResult(false, SteamResult.OK,
				new[]
				{
					new AchievementWriteEntry("ACH_ONE", true, null),
					new AchievementWriteEntry("ACH_TWO", false, "unknown achievement name")
				},
				Verified: true));

		ActionResult result = await _action.ExecuteAsync(
			session,
			Json("""{ "app_id": "400", "names": ["ach_one", "ACH_ONE", "ACH_TWO"] }"""),
			CancellationToken.None);

		Assert.False(result.Success); // one entry failed — the aggregate is honest about it
		Assert.Contains("failed to unlock", result.Error!, StringComparison.Ordinal);
		Assert.Equal(new[] { "ach_one", "ACH_ONE", "ACH_TWO" }, capturedNames.Single()); // passed through raw — dedup/trim is the transport's job
		Assert.True(capturedUnlock.Single());
		Assert.Equal(400u, result.Output!["app_id"]);
		Assert.True((bool)result.Output["unlock"]!);
		Assert.Equal(3, result.Output["requested_count"]);
		Assert.Equal(1, result.Output["succeeded_count"]);
		Assert.Equal(1, result.Output["failed_count"]);
		Assert.True((bool)result.Output["verified"]!);
		var results = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["results"]);
		Assert.Equal(2, results.Count);
		Assert.Equal("ACH_ONE", results[0]["name"]);
		Assert.True((bool)results[0]["success"]!);
		Assert.Equal("ACH_TWO", results[1]["name"]);
		Assert.Equal("unknown achievement name", results[1]["detail"]);
	}

	[Fact]
	public async Task ExecuteAsync_NoResponseFromSteam_ReportsFailure()
	{
		BotSession session = CreateSession(out var clientMock);
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((AchievementWriteResult?)null);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": ["ACH_ONE"] }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no response from Steam", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutSteamClient_IsRejected()
	{
		BotSession session = CreateSession(out _, withClient: false);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": ["ACH_ONE"] }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam client not available", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WhenTransportThrows_ReportsFailure()
	{
		BotSession session = CreateSession(out var clientMock);
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("connection lost"));

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": ["ACH_ONE"] }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to unlock achievements", result.Error, StringComparison.Ordinal);
		Assert.Contains("connection lost", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_AcceptsASingleNameAndPlainDotnetLists()
	{
		BotSession session = CreateSession(out var clientMock);
		var capturedNames = new List<IReadOnlyList<string>>();
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(400, Capture.In(capturedNames), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AchievementWriteResult(true, SteamResult.OK,
				new[] { new AchievementWriteEntry("ACH_ONE", true, null) },
				Verified: true));

		// A bare string is a one-element batch; a plain .NET list goes down the
		// IEnumerable arm — both are explicit name inputs, just not JSON.
		ActionResult single = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_id"] = "400", ["names"] = "ACH_ONE" },
			CancellationToken.None);
		ActionResult dotnetList = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_id"] = "400", ["names"] = new List<string> { "ACH_ONE", "ACH_TWO" } },
			CancellationToken.None);

		Assert.True(single.Success, single.Error);
		Assert.True(dotnetList.Success, dotnetList.Error);
		Assert.Equal(new[] { "ACH_ONE" }, capturedNames[0]);
		Assert.Equal(new[] { "ACH_ONE", "ACH_TWO" }, capturedNames[1]);
	}

	[Fact]
	public async Task ExecuteAsync_NamesOfUnusableShapes_AreRejectedAsEmpty()
	{
		BotSession session = CreateSession(out var clientMock);

		// null and non-list scalars parse to nothing — the explicit-names gate
		// rejects them instead of guessing a batch.
		ActionResult nullNames = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_id"] = "400", ["names"] = null },
			CancellationToken.None);
		ActionResult scalar = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_id"] = "400", ["names"] = 42L },
			CancellationToken.None);

		Assert.False(nullNames.Success);
		Assert.False(scalar.Success);
		Assert.Contains("explicit non-empty list", nullNames.Error!, StringComparison.Ordinal);
		Assert.Contains("explicit non-empty list", scalar.Error!, StringComparison.Ordinal);
		clientMock.Verify(m => m.SetAchievementStatesAsync(
			It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_JsonArrayWithNonStringElements_UsesTheElementText()
	{
		// The WS/SQLite round-trip hands values back as a JsonElement array;
		// non-string elements contribute their element text verbatim.
		BotSession session = CreateSession(out var clientMock);
		var capturedNames = new List<IReadOnlyList<string>>();
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(400, Capture.In(capturedNames), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AchievementWriteResult(true, SteamResult.OK,
				new[] { new AchievementWriteEntry("ACH_ONE", true, null) },
				Verified: true));

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": [7, "ACH_ONE"] }"""), CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.Equal(new[] { "7", "ACH_ONE" }, capturedNames.Single());
	}

	private static Dictionary<string, object?> Json(string json)
		=> JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

	private static BotSession CreateSession(out Mock<ISteamClientManager> clientMock, bool withClient = true)
	{
		clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		var registryMock = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(
			"test_account",
			new AccountCredentials("test_account", "test_password"),
			registryMock.Object,
			NullLogger<BotSession>.Instance,
			withClient ? clientMock.Object : null,
			null);
	}
}

public sealed class ResetAchievementsActionTests
{
	private readonly ResetAchievementsAction _action = new();

	[Fact]
	public void Metadata_RequiresLoginAndLongTimeout()
	{
		Assert.Equal("reset_achievements", _action.Name);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(180, _action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_MissingConfirm_IsRejectedBeforeTheTransportIsTouched()
	{
		BotSession session = CreateSession(out var clientMock);

		foreach (string body in new[] { """{ "app_id": "400", "names": ["ACH_ONE"] }""", """{ "app_id": "400", "names": ["ACH_ONE"], "confirm": false }""" })
		{
			ActionResult result = await _action.ExecuteAsync(session, Json(body), CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("confirm must be explicitly true", result.Error, StringComparison.Ordinal);
		}

		clientMock.Verify(m => m.SetAchievementStatesAsync(
			It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_MissingNames_IsRejected()
	{
		BotSession session = CreateSession(out _);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "confirm": true }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("explicit non-empty list", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_ConfirmedReset_PassesUnlockFalseAndReportsResults()
	{
		BotSession session = CreateSession(out var clientMock);
		var capturedUnlock = new List<bool>();
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(400, It.IsAny<IReadOnlyList<string>>(), Capture.In(capturedUnlock), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new AchievementWriteResult(true, SteamResult.OK,
				new[] { new AchievementWriteEntry("ACH_ONE", true, null) },
				Verified: true));

		ActionResult result = await _action.ExecuteAsync(
			session,
			Json("""{ "app_id": "400", "names": ["ACH_ONE"], "confirm": true }"""),
			CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.False(capturedUnlock.Single());
		Assert.Equal(1, result.Output!["succeeded_count"]);
		Assert.Equal(0, result.Output["failed_count"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutSteamClient_IsRejected()
	{
		BotSession session = CreateSession(out _, withClient: false);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": ["ACH_ONE"], "confirm": true }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam client not available", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NoResponseFromSteam_ReportsFailure()
	{
		BotSession session = CreateSession(out var clientMock);
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((AchievementWriteResult?)null);

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": ["ACH_ONE"], "confirm": true }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no response from Steam", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WhenTransportThrows_ReportsFailure()
	{
		BotSession session = CreateSession(out var clientMock);
		clientMock
			.Setup(m => m.SetAchievementStatesAsync(It.IsAny<uint>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("connection lost"));

		ActionResult result = await _action.ExecuteAsync(
			session, Json("""{ "app_id": "400", "names": ["ACH_ONE"], "confirm": true }"""), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to reset achievements", result.Error, StringComparison.Ordinal);
		Assert.Contains("connection lost", result.Error, StringComparison.Ordinal);
	}

	private static Dictionary<string, object?> Json(string json)
		=> JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

	private static BotSession CreateSession(out Mock<ISteamClientManager> clientMock, bool withClient = true)
	{
		clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		var registryMock = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(
			"test_account",
			new AccountCredentials("test_account", "test_password"),
			registryMock.Object,
			NullLogger<BotSession>.Instance,
			withClient ? clientMock.Object : null,
			null);
	}
}
