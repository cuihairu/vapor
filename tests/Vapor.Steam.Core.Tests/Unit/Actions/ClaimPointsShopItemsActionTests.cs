using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class ClaimPointsShopItemsActionTests
{
	private readonly ClaimPointsShopItemsAction _action = new(NullLogger<ClaimPointsShopItemsAction>.Instance);

	[Fact]
	public async Task ExecuteAsync_DefinitionIds_StringZero_IsSkippedByGuard()
	{
		// The string "0" parses as a number but fails the > 0 guard — skipped
		// without aborting the redemption of the valid sibling.
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free", 0, true, 0)
			});
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 1));
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1000, 1500, 500));
		BotSession session = CreateSession(clientMock.Object);

		Dictionary<string, object?> payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{ "definition_ids": ["0", "91000"] }""")!;

		ActionResult result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.Equal(new uint[] { 91000 }, captured.Single());
	}

	[Fact]
	public void Name_MatchesActionName()
	{
		Assert.Equal("claim_points_shop_items", _action.Name);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(120, _action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_DefinitionLookupFails_FailsBeforeRedeeming()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>?)null);

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("definition lookup", result.Error, StringComparison.Ordinal);
		clientMock.Verify(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// Payload values arrive as JsonElements after the WS/SQLite JSON round-trip;
	// every value shape the parser accepts must survive to the lookup call.
	[Fact]
	public async Task ExecuteAsync_DefinitionIds_JsonElementArrayParsesStringsNumbersAndSkipsInvalid()
	{
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free", 0, true, 0),
				new(91001, 753, 3, "free", 0, true, 0)
			});
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 1));
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1000, 1500, 500));
		BotSession session = CreateSession(clientMock.Object);

		// "91000" string, 91001 number, 0 and "not-a-number" skipped, 91000 de-duped.
		Dictionary<string, object?> payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{ "definition_ids": ["91000", 91001, 0, "not-a-number", 91000] }""")!;

		ActionResult result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.Equal(new uint[] { 91000, 91001 }, captured.Single());
		Assert.Equal(2, result.Output!["requested"]);
	}

	[Fact]
	public async Task ExecuteAsync_DefinitionIds_DotNetListAcceptsEveryNumericShape()
	{
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo>
			{
				new(91003, 753, 3, "free", 0, true, 0),
				new(91004, 753, 3, "free", 0, true, 0),
				new(91005, 753, 3, "free", 0, true, 0),
				new(91006, 753, 3, "free", 0, true, 0),
				new(91007, 753, 3, "free", 0, true, 0)
			});
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 1));
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1000, 1500, 500));
		BotSession session = CreateSession(clientMock.Object);

		// int / long / double / uint / string parse; null and bool are skipped.
		var payload = new Dictionary<string, object?>
		{
			["definition_ids"] = new List<object?> { 91003, 91004L, 91005.0, 91007u, "91006", null, true }
		};

		ActionResult result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.Equal(new uint[] { 91003, 91004, 91005, 91007, 91006 }, captured.Single());
	}

	[Fact]
	public async Task ExecuteAsync_DefinitionIds_NonNumericClrString_IsSkippedByGuard()
	{
		// A CLR string that fails uint.TryParse itself (the first guard condition,
		// not just the > 0 check) is skipped without aborting the valid sibling.
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free", 0, true, 0)
			});
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 1));
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1000, 1500, 500));
		BotSession session = CreateSession(clientMock.Object);

		var payload = new Dictionary<string, object?>
		{
			["definition_ids"] = new List<object?> { "abc", 91000 }
		};

		ActionResult result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.Equal(new uint[] { 91000 }, captured.Single());
	}

	[Fact]
	public async Task ExecuteAsync_DefinitionIds_SingleScalarValueWrapsIntoOneId()
	{
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo> { new(91000, 753, 3, "free", 0, true, 0) });
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 1));
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1000, 1500, 500));
		BotSession session = CreateSession(clientMock.Object);

		ActionResult fromString = await _action.ExecuteAsync(
			session, new Dictionary<string, object?> { ["definition_ids"] = "91000" }, CancellationToken.None);
		ActionResult fromInt = await _action.ExecuteAsync(
			session, new Dictionary<string, object?> { ["definition_ids"] = 91000 }, CancellationToken.None);

		Assert.True(fromString.Success, fromString.Error);
		Assert.True(fromInt.Success, fromInt.Error);
		Assert.All(captured, ids => Assert.Equal(new uint[] { 91000 }, ids));
		Assert.Equal(2, captured.Count);
	}

	[Fact]
	public async Task ExecuteAsync_MissingDefinitionIds_Fails()
	{
		BotSession session = CreateSession(new Mock<ISteamClientManager>(MockBehavior.Loose).Object);

		ActionResult result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("definition_ids is required", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WithoutClientManager_Fails()
	{
		BotSession session = CreateSession(clientManager: null);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam client not available", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_FreeDefinition_RedeemsAndReportsBalance()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<PointsShopItemInfo> { new(91000, 753, 3, "free sticker", 0, true, 0) });
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(91000u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 12345678901234567ul));
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1000, 1500, 500));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Equal(1, result.Output!["succeeded"]);
		Assert.Equal(0, result.Output["failed"]);
		Assert.Equal(1000L, result.Output["points_after"]);
		var results = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["results"]);
		Assert.Single(results);
		Assert.Equal(true, results[0]["success"]);
		// 64-bit ids must come back as strings so JSON consumers keep precision.
		Assert.Equal("12345678901234567", results[0]["community_item_id"]);
	}

	[Fact]
	public async Task ExecuteAsync_PaidDefinitionWithoutForce_RejectsWholeBatchBeforeRedeeming()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free sticker", 0, true, 0),
				new(91001, 753, 3, "paid sticker", 500, true, 0)
			});

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000, 91001 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("91001", result.Error, StringComparison.Ordinal);
		Assert.Contains("500 points", result.Error, StringComparison.Ordinal);
		Assert.Contains("force=true", result.Error, StringComparison.Ordinal);
		// The batch must be validated before the first redemption goes out.
		clientMock.Verify(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_UnknownDefinition_RejectsWholeBatchBeforeRedeeming()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			// Steam returns only 91000 — 99999 does not exist.
			.ReturnsAsync(new List<PointsShopItemInfo> { new(91000, 753, 3, "free sticker", 0, true, 0) });

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000, 99999 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("99999 not found", result.Error, StringComparison.Ordinal);
		clientMock.Verify(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_Force_RedeemsPaidWithoutLookup()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(91001u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 42ul));
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(0, 1500, 1500));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91001 }, ["force"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		clientMock.Verify(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()), Times.Never);
		var results = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["results"]);
		Assert.Equal(true, results[0]["success"]);
		Assert.Equal(true, result.Output["force"]);
	}

	[Fact]
	public async Task ExecuteAsync_SingleFailure_DoesNotAbortRest()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free a", 0, true, 0),
				new(91001, 753, 3, "free b", 0, true, 0)
			});
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(91000u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.OK, 77ul));
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(91001u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new RedeemPointsResult(SteamResult.AlreadyOwned, 0ul));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000, 91001 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("one or more point shop redemptions failed", result.Error, StringComparison.Ordinal);
		Assert.Equal(1, result.Output!["succeeded"]);
		Assert.Equal(1, result.Output["failed"]);
		var results = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["results"]);
		Assert.Equal(2, results.Count);
		Assert.Equal(true, results[0]["success"]);
		Assert.Equal("AlreadyOwned", results[1]["result"]);
	}

	[Fact]
	public async Task ExecuteAsync_NoRedeemResponse_ReportsFailure()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.RedeemPointsShopItemAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((RedeemPointsResult?)null);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1000, 1500, 500));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000 }, ["force"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		var results = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["results"]);
		Assert.Equal(false, results[0]["success"]);
		Assert.Equal("no response from Steam", results[0]["result"]);
	}

	private static BotSession CreateSession(ISteamClientManager? clientManager)
	{
		var registryMock = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(
			"test_account",
			new AccountCredentials("test_account", "test_password"),
			registryMock.Object,
			NullLogger<BotSession>.Instance,
			clientManager,
			null);
	}
}
