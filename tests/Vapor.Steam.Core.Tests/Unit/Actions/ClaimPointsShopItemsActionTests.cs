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
	public void Name_MatchesActionName()
	{
		Assert.Equal("claim_points_shop_items", _action.Name);
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
