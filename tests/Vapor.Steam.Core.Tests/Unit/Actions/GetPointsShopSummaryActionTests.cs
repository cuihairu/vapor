using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class GetPointsShopSummaryActionTests
{
	private readonly GetPointsShopSummaryAction _action = new(NullLogger<GetPointsShopSummaryAction>.Instance);

	[Fact]
	public async Task ExecuteAsync_DefinitionIds_StringZero_IsSkippedByGuard()
	{
		// The string "0" parses as a number but fails the > 0 guard — skipped
		// without aborting the lookup of the valid sibling.
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free", 0, true, 0)
			});
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
		Assert.Equal("get_points_shop_summary", _action.Name);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(60, _action.Metadata.TimeoutSeconds);
	}

	// definition_ids arrive as JsonElements after the WS/SQLite JSON round-trip;
	// every value shape the parser accepts must survive to the lookup call.
	[Fact]
	public async Task ExecuteAsync_DefinitionIds_JsonElementArrayParsesMixedValuesAndDedupes()
	{
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free", 0, true, 0),
				new(91001, 753, 3, "free", 0, true, 0)
			});
		BotSession session = CreateSession(clientMock.Object);

		// "91000" string, 91001 number, 0 and "not-a-number" skipped, 91000 de-duped.
		Dictionary<string, object?> payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{ "definition_ids": ["91000", 91001, 0, "not-a-number", 91000] }""")!;

		ActionResult result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success, result.Error);
		Assert.Equal(new uint[] { 91000, 91001 }, captured.Single());
		Assert.Equal(2, result.Output!["items_total"]);
	}

	[Fact]
	public async Task ExecuteAsync_DefinitionIds_DotNetListAcceptsEveryNumericShape()
	{
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
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
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free", 0, true, 0)
			});
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
	public async Task ExecuteAsync_DefinitionIds_SingleScalarWrapsAndAllInvalidValuesSkipLookup()
	{
		var captured = new List<IReadOnlyCollection<uint>>();
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(Capture.In(captured), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<PointsShopItemInfo>)new List<PointsShopItemInfo> { new(91000, 753, 3, "free", 0, true, 0) });
		BotSession session = CreateSession(clientMock.Object);

		ActionResult fromString = await _action.ExecuteAsync(
			session, new Dictionary<string, object?> { ["definition_ids"] = "91000" }, CancellationToken.None);
		// A list with no parseable ids behaves like no ids at all: balance-only output.
		Dictionary<string, object?> allInvalid = JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{ "definition_ids": ["not-a-number", 0] }""")!;
		ActionResult fromInvalid = await _action.ExecuteAsync(session, allInvalid, CancellationToken.None);

		Assert.True(fromString.Success, fromString.Error);
		Assert.True(fromInvalid.Success, fromInvalid.Error);
		Assert.Equal(new uint[] { 91000 }, captured.Single());
		Assert.False(fromInvalid.Output!.ContainsKey("items"));
	}

	[Fact]
	public async Task ExecuteAsync_WithoutClientManager_Fails()
	{
		BotSession session = CreateSession(clientManager: null);

		ActionResult result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam client not available", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_BalanceOnly_ReportsPointsWithoutItems()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1500L, result.Output!["points"]);
		Assert.Equal(2000L, result.Output["points_earned"]);
		Assert.Equal(500L, result.Output["points_spent"]);
		Assert.False(result.Output.ContainsKey("items"));
		clientMock.Verify(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ExecuteAsync_WithDefinitionIds_IncludesItemDefinitions()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(
				It.Is<IReadOnlyCollection<uint>>(ids => ids.Count == 2 && ids.Contains(91000u) && ids.Contains(91001u)),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "sticker pack", 0, true, 1767225600),
				new(91001, 730, 15, "avatar frame", 2000, true, 0)
			});

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000, 91001 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["items_total"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
		Assert.Equal(2, items.Count);
		Assert.Equal(91000u, items[0]["defid"]);
		Assert.Equal(753u, items[0]["app_id"]);
		Assert.Equal(0L, items[0]["point_cost"]);
		Assert.Equal("sticker pack", items[0]["description"]);
		Assert.Equal(1767225600u, items[0]["free_until"]);
		Assert.Equal(2000L, items[1]["point_cost"]);
	}

	[Fact]
	public async Task ExecuteAsync_FreeOnly_FiltersPaidItemsButKeepsTotal()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
		clientMock
			.Setup(m => m.QueryPointsShopItemsAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new List<PointsShopItemInfo>
			{
				new(91000, 753, 3, "free sticker", 0, true, 0),
				new(91001, 753, 3, "paid sticker", 500, true, 0),
				new(91002, 753, 3, "another paid", 3000, true, 0)
			});

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["definition_ids"] = new List<uint> { 91000, 91001, 91002 }, ["free_only"] = true },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(3, result.Output!["items_total"]);
		var items = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["items"]);
		Assert.Single(items);
		Assert.Equal(91000u, items[0]["defid"]);
	}

	[Fact]
	public async Task ExecuteAsync_NoSummaryResponse_Fails()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync((PointsShopSummary?)null);

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no response from Steam", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_ItemQueryFails_KeepsBalanceAndFails()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.GetPointsShopSummaryAsync(It.IsAny<CancellationToken>()))
			.ReturnsAsync(new PointsShopSummary(1500, 2000, 500));
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
		Assert.Equal(1500L, result.Output!["points"]);
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
