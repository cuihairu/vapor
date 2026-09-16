using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Caching;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class GetGameInfoBatchActionTests : IDisposable
{
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}

	private BotSession CreateSession(bool withWebHandler = true)
	{
		var credentials = new AccountCredentials("batch_account", "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var webHandler = withWebHandler
			? new SteamWebHandler(new SteamWebHandlerConfig(), new Mock<ILogger<SteamWebHandler>>(MockBehavior.Loose).Object)
			: null;
		var session = new BotSession("batch_account", credentials, registry.Object, _sessionLoggerMock.Object, null, webHandler, null);
		_sessions.Add(session);
		return session;
	}

	private static GameInfo SampleGame(uint appId, string name) => new()
	{
		AppId = appId,
		Name = name,
		Type = "game",
		IsFree = false
	};

	private static Mock<ISteamStoreApiClient> ClientReturning(params (uint AppId, string? Name)[] games)
	{
		var mock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		foreach (var (appId, name) in games)
		{
			mock
				.Setup(c => c.GetGameInfoAsync(appId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
				.ReturnsAsync(name == null ? null : SampleGame(appId, name));
		}

		return mock;
	}

	private GetGameInfoBatchAction CreateAction(Mock<ISteamStoreApiClient> client, IVaporCache? cache = null, List<TimeSpan>? delays = null) =>
		new(
			NullLogger<GetGameInfoBatchAction>.Instance,
			_ => client.Object,
			cache,
			delays == null
				? null
				: span => { delays.Add(span); return Task.CompletedTask; });

	// --- TryParseAppIds ---

	[Fact]
	public void TryParseAppIds_WithCsvString_ParsesAndTrims()
	{
		bool ok = GetGameInfoBatchAction.TryParseAppIds("730, 570,400", out var appIds, out string? error);

		Assert.True(ok);
		Assert.Null(error);
		Assert.Equal(new List<uint> { 730, 570, 400 }, appIds);
	}

	[Fact]
	public void TryParseAppIds_WithJsonArrayOfNumbersAndStrings_ParsesBoth()
	{
		// The WS/SQLite round-trip turns payload arrays into JsonElements whose
		// entries may be numbers or strings depending on how the caller sent them.
		JsonElement raw = JsonSerializer.SerializeToElement(new object[] { 730, "570", 400 });

		bool ok = GetGameInfoBatchAction.TryParseAppIds(raw, out var appIds, out string? error);

		Assert.True(ok);
		Assert.Null(error);
		Assert.Equal(new List<uint> { 730, 570, 400 }, appIds);
	}

	[Theory]
	[InlineData("730,abc")]
	[InlineData("730,0")]
	[InlineData("730,,400")]
	[InlineData("")]
	public void TryParseAppIds_WithBadEntries_ReportsEntryError(string csv)
	{
		bool ok = GetGameInfoBatchAction.TryParseAppIds(csv, out _, out string? error);

		Assert.False(ok);
		Assert.NotNull(error);
	}

	[Fact]
	public void TryParseAppIds_Missing_ReturnsRequiredError()
	{
		bool ok = GetGameInfoBatchAction.TryParseAppIds(null, out _, out string? error);

		Assert.False(ok);
		Assert.Equal("app_ids is required", error);
	}

	[Fact]
	public void TryParseAppIds_ExceedingBatchLimit_Fails()
	{
		string csv = string.Join(",", Enumerable.Range(1, GetGameInfoBatchAction.MaxAppsPerBatch + 1));

		bool ok = GetGameInfoBatchAction.TryParseAppIds(csv, out _, out string? error);

		Assert.False(ok);
		Assert.Contains("batch limit", error);
	}

	[Fact]
	public void TryParseAppIds_WithDotNetList_ParsesMixedNumericTypes()
	{
		// In-memory dispatch payloads may carry a plain list with mixed numeric
		// element types before any JSON round-trip normalizes them.
		var raw = new List<object?> { 730, 570L, (short)400, (ushort)240 };

		bool ok = GetGameInfoBatchAction.TryParseAppIds(raw, out var appIds, out string? error);

		Assert.True(ok);
		Assert.Null(error);
		Assert.Equal(new List<uint> { 730, 570, 400, 240 }, appIds);
	}

	[Fact]
	public void TryParseAppIds_WithEmptyList_FailsRequired()
	{
		bool ok = GetGameInfoBatchAction.TryParseAppIds(new List<object?>(), out _, out string? error);

		Assert.False(ok);
		Assert.Equal("app_ids is required", error);
	}

	[Fact]
	public void TryParseAppIds_WithUnsupportedElementKind_NamesTheEntry()
	{
		// Elements that are neither text nor numeric (a boxed bool here, likewise a
		// JsonElement of kind True/Object/Null) must be rejected naming the entry.
		var raw = new List<object?> { 730, true };

		bool ok = GetGameInfoBatchAction.TryParseAppIds(raw, out _, out string? error);

		Assert.False(ok);
		Assert.Contains("invalid app_ids entry 'True'", error);
	}

	[Fact]
	public async Task ExecuteAsync_WhenClientFactoryThrows_SurfacesError()
	{
		var action = new GetGameInfoBatchAction(
			NullLogger<GetGameInfoBatchAction>.Instance,
			_ => throw new ArgumentException("factory broke"));

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "730" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("factory broke", result.Error);
	}

	// --- ExecuteAsync ---

	[Fact]
	public async Task ExecuteAsync_WithMultipleApps_ReturnsAllGames()
	{
		var client = ClientReturning((730, "CS2"), (570, "Dota 2"), (400, "Portal"));
		var action = CreateAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "730,570,400" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Equal(3, result.Output!["total_count"]);
		Assert.Equal(3, result.Output["fetched"]);
		Assert.Equal(0, result.Output["failed"]);
		var games = Assert.IsType<List<GameInfo>>(result.Output["games"]);
		Assert.Equal(["CS2", "Dota 2", "Portal"], games.Select(g => g.Name).ToArray());
		Assert.Empty(Assert.IsType<List<Dictionary<string, object?>>>(result.Output["errors"]));
	}

	[Fact]
	public async Task ExecuteAsync_WithUnknownApp_ReportsErrorButKeepsGoing()
	{
		var client = ClientReturning((730, "CS2"), (99999, null), (400, "Portal"));
		var action = CreateAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "730,99999,400" },
			CancellationToken.None);

		Assert.True(result.Success); // fetched > 0 → success despite one failure
		Assert.Equal(2, result.Output!["fetched"]);
		Assert.Equal(1, result.Output["failed"]);
		var errors = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["errors"]);
		var error = Assert.Single(errors);
		Assert.Equal(99999U, error["app_id"]);
		Assert.Contains("not found", (string)error["error"]!);
	}

	[Fact]
	public async Task ExecuteAsync_WhenTransportThrowsForOneApp_IsolatesTheFailure()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetGameInfoAsync(730U, It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(SampleGame(730, "CS2"));
		clientMock
			.Setup(c => c.GetGameInfoAsync(570U, It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("connection reset"));
		var action = CreateAction(clientMock);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "730,570" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["fetched"]);
		var errors = Assert.IsType<List<Dictionary<string, object?>>>(result.Output["errors"]);
		Assert.Equal("connection reset", (string)Assert.Single(errors)["error"]!);
	}

	[Fact]
	public async Task ExecuteAsync_WhenEveryAppFails_ReportsOverallFailureWithDetails()
	{
		var client = ClientReturning((99998, null), (99999, null));
		var action = CreateAction(client);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "99998,99999" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("all 2 apps failed", result.Error);
		Assert.Equal(0, result.Output!["fetched"]);
		Assert.Equal(2, result.Output["failed"]);
		Assert.NotEmpty(Assert.IsType<List<Dictionary<string, object?>>>(result.Output["errors"]));
	}

	[Fact]
	public async Task ExecuteAsync_WithoutAppIds_FailsBeforeTouchingTheClient()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetGameInfoAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.Throws(new InvalidOperationException("should not be called"));
		var action = CreateAction(clientMock);

		var missing = await action.ExecuteAsync(CreateSession(), new Dictionary<string, object?>(), CancellationToken.None);
		var empty = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "" },
			CancellationToken.None);

		Assert.All(new[] { missing, empty }, r =>
		{
			Assert.False(r.Success);
			Assert.Equal("app_ids is required", r.Error);
		});
	}

	[Fact]
	public async Task ExecuteAsync_WithJsonArrayPayload_WorksAfterRoundTripShape()
	{
		var client = ClientReturning((730, "CS2"), (570, "Dota 2"));
		var action = CreateAction(client);
		JsonElement raw = JsonSerializer.SerializeToElement(new[] { 730, 570 });

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = raw },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["fetched"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithCustomCountry_UsesItForFetchAndOutput()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.Setup(c => c.GetGameInfoAsync(570U, "DE", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SampleGame(570, "Dota 2"));
		var action = CreateAction(clientMock);

		var result = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "570", ["cc"] = "DE" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("DE", result.Output!["cc"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithCache_SecondRunHitsCacheForEveryApp()
	{
		var client = ClientReturning((730, "CS2"), (570, "Dota 2"));
		using var cache = new MemoryVaporCache();
		var action = CreateAction(client, cache);
		var payload = new Dictionary<string, object?> { ["app_ids"] = "730,570" };

		var first = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);
		var second = await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		Assert.True(first.Success);
		Assert.True(second.Success);
		client.Verify(c => c.GetGameInfoAsync(It.IsAny<uint>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
		Assert.Equal(2, cache.Count);
	}

	[Fact]
	public async Task ExecuteAsync_WithCacheDisabledByPayload_BypassesCache()
	{
		var client = ClientReturning((730, "CS2"));
		using var cache = new MemoryVaporCache();
		var action = CreateAction(client, cache);
		var payload = new Dictionary<string, object?> { ["app_ids"] = "730", ["cache_ttl_seconds"] = 0 };

		await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);
		await action.ExecuteAsync(CreateSession(), payload, CancellationToken.None);

		client.Verify(c => c.GetGameInfoAsync(730U, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
		Assert.Equal(0, cache.Count);
	}

	[Fact]
	public async Task ExecuteAsync_ForceRefresh_BypassesCacheAndRepopulates()
	{
		var clientMock = new Mock<ISteamStoreApiClient>(MockBehavior.Strict);
		clientMock
			.SetupSequence(c => c.GetGameInfoAsync(730U, It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(SampleGame(730, "Old Name"))
			.ReturnsAsync(SampleGame(730, "New Name"));
		using var cache = new MemoryVaporCache();
		var action = CreateAction(clientMock, cache);
		var session = CreateSession();

		var cached = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_ids"] = "730" }, CancellationToken.None);
		var forced = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_ids"] = "730", ["force_refresh"] = true }, CancellationToken.None);
		var again = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["app_ids"] = "730" }, CancellationToken.None);

		Assert.True(cached.Success);
		Assert.True(forced.Success);
		Assert.True(again.Success);
		Assert.Equal("New Name", ((List<GameInfo>)again.Output!["games"]!).Single().Name);
		clientMock.Verify(c => c.GetGameInfoAsync(730U, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
	}

	[Fact]
	public async Task ExecuteAsync_PacesOnlyRealStoreRequests()
	{
		var client = ClientReturning((730, "CS2"), (570, "Dota 2"));
		var delays = new List<TimeSpan>();
		using var cache = new MemoryVaporCache();
		var action = CreateAction(client, cache, delays);
		var session = CreateSession();
		var cold = new Dictionary<string, object?> { ["app_ids"] = "730,570", ["interval_ms"] = 250 };

		// Cold run: two real fetches → one inter-app gap (none after the last).
		Assert.True((await action.ExecuteAsync(session, cold, CancellationToken.None)).Success);
		Assert.Equal([TimeSpan.FromMilliseconds(250)], delays);

		// Warm run: everything served from cache → no pacing at all.
		delays.Clear();
		Assert.True((await action.ExecuteAsync(session, cold, CancellationToken.None)).Success);
		Assert.Empty(delays);
	}

	[Fact]
	public async Task ExecuteAsync_IntervalMsIsClampedToAllowedRange()
	{
		var client = ClientReturning((730, "CS2"), (570, "Dota 2"));
		var delays = new List<TimeSpan>();
		var action = CreateAction(client, delays: delays);

		var over = await action.ExecuteAsync(
			CreateSession(),
			new Dictionary<string, object?> { ["app_ids"] = "730,570", ["interval_ms"] = 99999 },
			CancellationToken.None);

		Assert.True(over.Success);
		Assert.Equal([TimeSpan.FromMilliseconds(5000)], delays); // clamped to 5000
	}

	[Fact]
	public async Task ExecuteAsync_WithoutWebHandler_ReturnsError()
	{
		var action = new GetGameInfoBatchAction(
			NullLogger<GetGameInfoBatchAction>.Instance,
			_ => throw new InvalidOperationException("unreachable factory"));

		var result = await action.ExecuteAsync(
			CreateSession(withWebHandler: false),
			new Dictionary<string, object?> { ["app_ids"] = "730" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Steam web handler not available", result.Error);
	}

	[Fact]
	public void Metadata_DescribesBatchFetchWithoutLogin()
	{
		var action = new GetGameInfoBatchAction(NullLogger<GetGameInfoBatchAction>.Instance);

		Assert.Equal("get_game_info_batch", action.Name);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(240, action.Metadata.TimeoutSeconds);
	}
}
