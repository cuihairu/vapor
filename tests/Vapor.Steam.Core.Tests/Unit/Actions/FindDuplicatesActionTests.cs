using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class FindDuplicatesActionTests : IDisposable
{
	private const ulong OwnSteamId = 76561198000000042UL;

	private readonly Mock<ILogger<FindDuplicatesAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		var action = new FindDuplicatesAction(_loggerMock.Object);
		Assert.Equal("find_duplicates", action.Name);
	}

	[Fact]
	public void Metadata_RequiresLogin()
	{
		var action = new FindDuplicatesAction(_loggerMock.Object);
		Assert.True(action.Metadata.RequiresLogin);
	}

	[Fact]
	public async Task ExecuteAsync_InvalidKeep_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var zero = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["keep"] = "0" },
			CancellationToken.None);
		var huge = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["keep"] = "101" },
			CancellationToken.None);
		var junk = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["keep"] = "many" },
			CancellationToken.None);

		Assert.All(new[] { zero, huge, junk }, r =>
		{
			Assert.False(r.Success);
			Assert.Contains("keep", r.Error, StringComparison.Ordinal);
		});
	}

	[Fact]
	public async Task ExecuteAsync_TooManyApps_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new object[] { 730, 440, 570, 252490, 2183900, 232090 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("limited to 5", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_WhenInventoryLoadCanceled_Rethrows()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OperationCanceledException());
		var session = CreateSession(CreateWebHandler());

		// Cancellation must not be swallowed into an error result.
		await Assert.ThrowsAsync<OperationCanceledException>(() =>
			action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None));
	}

	[Fact]
	public async Task ExecuteAsync_GroupsDuplicates_WithExcessAssetIds()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items =
				[
					new InventoryItem { AssetId = 1, AppId = 753, ClassId = 100, InstanceId = 150, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 2, AppId = 753, ClassId = 100, InstanceId = 150, Tradable = true, MarketHashName = "Card A" },
					new InventoryItem { AssetId = 3, AppId = 753, ClassId = 200, InstanceId = 250, Tradable = true, MarketHashName = "Card B" },
					new InventoryItem { AssetId = 4, AppId = 753, ClassId = 200, InstanceId = 250, Tradable = false, MarketHashName = "Card B" }
				]
			});
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(OwnSteamId.ToString(), result.Output!["steam_id"]);
		Assert.Equal(1, result.Output["keep"]);
		Assert.Equal(1, result.Output["excess_count"]);

		var duplicates = Assert.IsType<Dictionary<string, object?>[]>(result.Output["duplicates"]);
		var group = Assert.Single(duplicates);
		Assert.Equal(753u, group["app_id"]);
		Assert.Equal(100UL, group["class_id"]);
		Assert.Equal("Card A", group["name"]);
		Assert.Equal(2, group["total"]);
		Assert.Equal(1, group["excess_count"]);
		Assert.Equal([2UL], Assert.IsType<ulong[]>(group["excess_asset_ids"]));

		// Card B has one tradable copy (== keep): the untradable duplicate does not count.
		var scanned = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["apps_scanned"]);
		var app = Assert.Single(scanned);
		Assert.Equal("6", app["context_id"]);
		Assert.Equal(4, app["scanned_items"]);
	}

	[Fact]
	public async Task ExecuteAsync_NoDuplicates_SucceedsWithEmptyList()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse
			{
				Success = true,
				Items = [new InventoryItem { AssetId = 1, AppId = 753, ClassId = 100, Tradable = true }]
			});
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(0, result.Output!["excess_count"]);
		Assert.Empty(Assert.IsType<Dictionary<string, object?>[]>(result.Output["duplicates"]));
	}

	[Fact]
	public async Task ExecuteAsync_InventoryFailure_ReturnsError()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = false, Error = "inventory is private" });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Failed to load inventory for app 753", result.Error, StringComparison.Ordinal);
		Assert.Contains("inventory is private", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_NoOwnSteamId_Fails()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns((ulong?)null);
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("own SteamID", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_NoWebHandler_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(webHandler: null);

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_InventoryThrows_ReturnsError()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, 753, 6, null, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("connection reset"));
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("connection reset", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsAsSingleUint_IsAccepted()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, It.IsAny<uint>(), It.IsAny<ulong>(), null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = 753u },
			CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsMixedValueShapes_ParseKnownAndSkipRest()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		var scannedApps = new List<uint>();
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, It.IsAny<uint>(), It.IsAny<ulong>(), null, It.IsAny<CancellationToken>()))
			.Callback<ulong, uint, ulong, ulong?, CancellationToken>((_, app, _, _, _) => scannedApps.Add(app))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });
		var session = CreateSession(CreateWebHandler());

		// object[] with every supported value shape plus junk that must be skipped:
		// uint, long, JsonElement number, JsonElement numeric string, plain string,
		// and two unusable entries (bool, zero) that are silently dropped.
		Dictionary<string, object?> payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(
			"""{"app_ids":[753, 730, 570, "252490", "440", true, 0]}""")!;

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(new List<uint> { 753u, 730u, 570u, 252490u, 440u }, scannedApps);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsInMemoryValueShapes_ParseKnownAndSkipRest()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		var scannedApps = new List<uint>();
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, It.IsAny<uint>(), It.IsAny<ulong>(), null, It.IsAny<CancellationToken>()))
			.Callback<ulong, uint, ulong, ulong?, CancellationToken>((_, app, _, _, _) => scannedApps.Add(app))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });
		var session = CreateSession(CreateWebHandler());

		// In-memory dispatch skips the JSON round-trip, so values keep their .NET
		// types: int, long, uint and plain string all parse; fractional doubles,
		// bools and null are silently dropped.
		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?>
			{
				["app_ids"] = new object?[] { 753, 730L, 252490u, "570", 570.5, true, null }
			},
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(new List<uint> { 753u, 730u, 252490u, 570u }, scannedApps);
	}

	[Fact]
	public async Task ExecuteAsync_KeepAsInt_IsAccepted()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, It.IsAny<uint>(), It.IsAny<ulong>(), null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["keep"] = 2 }, CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_KeepAsLongAndJsonElement_IsAccepted()
	{
		var (action, clientMock) = CreateActionWithMock();
		clientMock
			.Setup(c => c.GetOwnSteamId())
			.Returns(OwnSteamId);
		clientMock
			.Setup(c => c.GetInventoryAsync(OwnSteamId, It.IsAny<uint>(), It.IsAny<ulong>(), null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new InventoryResponse { Success = true, Items = [] });
		var session = CreateSession(CreateWebHandler());

		var asLong = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["keep"] = 2L }, CancellationToken.None);
		var asElement = await action.ExecuteAsync(
			session,
			System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"keep":2}""")!,
			CancellationToken.None);
		var asElementString = await action.ExecuteAsync(
			session,
			System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"keep":"2"}""")!,
			CancellationToken.None);

		Assert.All(new[] { asLong, asElement, asElementString }, r => Assert.True(r.Success));
	}

	[Fact]
	public async Task ExecuteAsync_KeepAsUnsupportedShape_Fails()
	{
		var (action, _) = CreateActionWithMock();
		var session = CreateSession(CreateWebHandler());

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["keep"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("keep", result.Error, StringComparison.Ordinal);
	}

	private (FindDuplicatesAction Action, Mock<ISteamTradeClient> ClientMock) CreateActionWithMock()
	{
		var clientMock = new Mock<ISteamTradeClient>(MockBehavior.Loose);
		var action = new FindDuplicatesAction(
			_loggerMock.Object,
			_ => clientMock.Object);
		return (action, clientMock);
	}

	private BotSession CreateSession(SteamWebHandler? webHandler)
	{
		var credentials = new AccountCredentials("test_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession("test_account", credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	private static SteamWebHandler CreateWebHandler() =>
		new(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance);

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
