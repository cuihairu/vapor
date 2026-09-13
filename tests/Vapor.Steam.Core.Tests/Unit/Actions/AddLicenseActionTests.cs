using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class AddLicenseActionTests : IDisposable
{
	private readonly Mock<ISteamStoreApiClient> _storeClientMock = new(MockBehavior.Loose);
	private readonly AddLicenseAction _action;

	public AddLicenseActionTests()
	{
		_action = new AddLicenseAction(
			NullLogger<AddLicenseAction>.Instance,
			_ => _storeClientMock.Object);
	}

	public void Dispose()
	{
		_storeClientMock.VerifyAll();
	}

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		Assert.Equal("add_license", _action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		Assert.Equal("add_license", _action.Metadata.Name);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(120, _action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_WithNoIds_ReturnsFailure()
	{
		BotSession session = CreateSession();

		ActionResult result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("either app_ids or sub_ids is required", result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_AppIds_GrantedViaClientProtocol()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(
				It.Is<IReadOnlyCollection<uint>>(ids => ids.Count == 1 && ids.Contains(12345u)),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new FreeLicenseResult(SteamResult.OK, [12345u], [555u]));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new List<uint> { 12345 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.NotNull(result.Output);
		Assert.Equal("OK", result.Output!["apps_result"]);
		Assert.Equal(new List<uint> { 12345u }, result.Output["granted_app_ids"]);
		Assert.Equal(new List<uint> { 555u }, result.Output["granted_package_ids"]);
	}

	[Fact]
	public async Task ExecuteAsync_AppIds_ClientResultNotOk_Fails()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new FreeLicenseResult(SteamResult.Fail, [], []));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new List<uint> { 12345 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Fail", result.Output!["apps_result"]);
	}

	[Fact]
	public async Task ExecuteAsync_AppIds_NoResponseFromSteam_Fails()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((FreeLicenseResult?)null);

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new List<uint> { 12345 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("no response from Steam", result.Output!["apps_result"]);
	}

	[Fact]
	public async Task ExecuteAsync_AppIds_WithoutClientManager_Fails()
	{
		BotSession session = CreateSession(clientManager: null);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new List<uint> { 12345 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam client not available", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_SubIds_PurchaseOk()
	{
		_storeClientMock
			.Setup(m => m.AddFreeLicenseAsync(88888u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StorePurchaseResult(true, StorePurchaseResult.Ok));

		BotSession session = CreateSession(webHandler: CreateWebHandler());

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["sub_ids"] = new List<uint> { 88888 } },
			CancellationToken.None);

		Assert.True(result.Success);
		var purchases = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["purchases"]);
		Dictionary<string, object?> entry = Assert.Single(purchases);
		Assert.Equal(88888u, entry["id"]);
		Assert.Equal(true, entry["success"]);
		Assert.Equal(1, entry["detail"]);
	}

	[Fact]
	public async Task ExecuteAsync_SubIds_AlreadyPurchased_TreatedAsSuccess()
	{
		_storeClientMock
			.Setup(m => m.AddFreeLicenseAsync(88888u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StorePurchaseResult(true, StorePurchaseResult.AlreadyPurchased));

		BotSession session = CreateSession(webHandler: CreateWebHandler());

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["sub_ids"] = new List<uint> { 88888 } },
			CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_SubIds_PurchaseFails()
	{
		// detail 9 (an unspecified store refusal) is neither ok nor already-purchased.
		_storeClientMock
			.Setup(m => m.AddFreeLicenseAsync(88888u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StorePurchaseResult(false, 9));

		BotSession session = CreateSession(webHandler: CreateWebHandler());

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["sub_ids"] = new List<uint> { 88888 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("one or more free-license requests failed", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_SubIds_TransportFailure_ReportsNullDetail()
	{
		_storeClientMock
			.Setup(m => m.AddFreeLicenseAsync(88888u, It.IsAny<CancellationToken>()))
			.ReturnsAsync((StorePurchaseResult?)null);

		BotSession session = CreateSession(webHandler: CreateWebHandler());

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["sub_ids"] = new List<uint> { 88888 } },
			CancellationToken.None);

		Assert.False(result.Success);
		var purchases = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["purchases"]);
		Dictionary<string, object?> entry = Assert.Single(purchases);
		Assert.Equal(false, entry["success"]);
		Assert.Null(entry["detail"]);
	}

	[Fact]
	public async Task ExecuteAsync_SubIds_WithoutWebHandler_Fails()
	{
		BotSession session = CreateSession(webHandler: null);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["sub_ids"] = new List<uint> { 88888 } },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam web handler not available", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExecuteAsync_MixedPaths_BothSucceed()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new FreeLicenseResult(SteamResult.OK, [12345u], []));
		_storeClientMock
			.Setup(m => m.AddFreeLicenseAsync(88888u, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StorePurchaseResult(true, StorePurchaseResult.Ok));

		BotSession session = CreateSession(clientMock.Object, CreateWebHandler());

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new List<uint> { 12345 }, ["sub_ids"] = new List<uint> { 88888 } },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("OK", result.Output!["apps_result"]);
		Assert.NotNull(result.Output["purchases"]);
	}

	[Fact]
	public async Task ExecuteAsync_DuplicateIds_AreDeduplicated()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		List<uint>? requestedIds = null;
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.Callback<IReadOnlyCollection<uint>, CancellationToken>((ids, _) => requestedIds = ids.ToList())
			.ReturnsAsync(new FreeLicenseResult(SteamResult.OK, [], []));

		BotSession session = CreateSession(clientMock.Object);

		await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = new List<uint> { 12345, 12345, 999 } },
			CancellationToken.None);

		Assert.NotNull(requestedIds);
		Assert.Equal(new List<uint> { 12345u, 999u }, requestedIds);
	}

	[Fact]
	public async Task ExecuteAsync_PayloadAfterJsonRoundTrip_IsUnderstood()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(
				It.Is<IReadOnlyCollection<uint>>(ids => ids.SequenceEqual(new List<uint> { 12345u })),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(new FreeLicenseResult(SteamResult.OK, [12345u], []));

		BotSession session = CreateSession(clientMock.Object);
		Dictionary<string, object?> payload = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"app_ids\":[12345]}")!;

		ActionResult result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public void Constructor_Default_UsesRealStoreClient()
	{
		// Public constructor wires the real store client factory without touching Steam.
		var action = new AddLicenseAction(NullLogger<AddLicenseAction>.Instance);
		Assert.Equal("add_license", action.Name);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsAsSingleUint_IsAccepted()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		List<uint>? requestedIds = null;
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.Callback<IReadOnlyCollection<uint>, CancellationToken>((ids, _) => requestedIds = ids.ToList())
			.ReturnsAsync(new FreeLicenseResult(SteamResult.OK, [], []));

		BotSession session = CreateSession(clientMock.Object);

		ActionResult result = await _action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["app_ids"] = 12345u },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(new List<uint> { 12345u }, requestedIds);
	}

	[Fact]
	public async Task ExecuteAsync_AppIdsMixedValueShapes_ParseKnownAndSkipRest()
	{
		var clientMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		List<uint>? requestedIds = null;
		clientMock
			.Setup(m => m.RequestFreeLicenseAsync(It.IsAny<IReadOnlyCollection<uint>>(), It.IsAny<CancellationToken>()))
			.Callback<IReadOnlyCollection<uint>, CancellationToken>((ids, _) => requestedIds = ids.ToList())
			.ReturnsAsync(new FreeLicenseResult(SteamResult.OK, [], []));

		BotSession session = CreateSession(clientMock.Object);

		// Every supported value shape (uint, int, whole double, long, JsonElement
		// number, JsonElement numeric string, plain string) plus junk that must be
		// skipped (bool, zero, null).
		var payload = new Dictionary<string, object?>
		{
			["app_ids"] = new List<object?>
			{
				12345u,
				12346,
				12347.0,
				12348L,
				JsonSerializer.Deserialize<JsonElement>("12349"),
				JsonSerializer.Deserialize<JsonElement>("\"12350\""),
				"12351",
				true,
				0,
				null
			}
		};

		ActionResult result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(
			new List<uint> { 12345u, 12346u, 12347u, 12348u, 12349u, 12350u, 12351u },
			requestedIds);
	}

	private static BotSession CreateSession(ISteamClientManager? clientManager = null, SteamWebHandler? webHandler = null)
	{
		var registryMock = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(
			"test_account",
			new AccountCredentials("test_account", "test_password"),
			registryMock.Object,
			NullLogger<BotSession>.Instance,
			clientManager,
			webHandler);
	}

	private static SteamWebHandler CreateWebHandler()
	{
		var fake = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
		return new SteamWebHandler(
			new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
			NullLogger<SteamWebHandler>.Instance,
			fake);
	}

	private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return Task.FromResult(responder(request));
		}
	}
}
