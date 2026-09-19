using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Proxy wiring through SessionManager: a session created with a configured
/// proxy exposes it via ConfiguredProxy and persists it to the credential
/// store; a malformed proxy fails session creation with the parse error before
/// any session exists; a restored session carries the stored proxy.
/// </summary>
public sealed class SessionManagerProxyTests : IDisposable
{
	private readonly List<BotSession> _sessions = [];
	private readonly Mock<ICredentialStore> _storeMock = new(MockBehavior.Loose);

	[Fact]
	public async Task GetOrCreateSessionAsync_WithConfiguredProxy_PersistsAndExposesIt()
	{
		using var manager = CreateManager();
		const string proxy = "socks5://gw.example.com:1080";

		var session = await manager.GetOrCreateSessionAsync(
			"alice", new AccountCredentials("alice", "password", Proxy: proxy));

		Assert.Equal(proxy, session.ConfiguredProxy);
		Assert.NotNull(session.SteamWebHandler);
		_storeMock.Verify(
			s => s.SaveProxyAsync("alice", proxy, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithoutProxy_DoesNotTouchStore()
	{
		using var manager = CreateManager();

		var session = await manager.GetOrCreateSessionAsync(
			"alice", new AccountCredentials("alice", "password"));

		Assert.Null(session.ConfiguredProxy);
		_storeMock.Verify(
			s => s.SaveProxyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WhenProxyPersistenceFails_LoginStillSucceeds()
	{
		_storeMock
			.Setup(s => s.SaveProxyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new IOException("disk full"));
		using var manager = CreateManager();

		var session = await manager.GetOrCreateSessionAsync(
			"alice", new AccountCredentials("alice", "password", Proxy: "socks5://gw.example.com:1080"));

		// Persistence is best-effort: the warning is logged but the session lives.
		Assert.NotNull(session);
		Assert.Equal("socks5://gw.example.com:1080", session.ConfiguredProxy);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithMalformedProxy_ThrowsBeforeSessionIsCreated()
	{
		using var manager = CreateManager();

		await Assert.ThrowsAsync<ArgumentException>(() =>
			manager.GetOrCreateSessionAsync("alice", new AccountCredentials("alice", "password", Proxy: "garbage")));

		Assert.Null(await manager.GetSessionAsync("alice"));
	}

	[Fact]
	public async Task TryRestoreSessionAsync_WithStoredProxy_RestoresItOntoTheSession()
	{
		_storeMock
			.Setup(s => s.HasCredentialsAsync("bob", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		_storeMock
			.Setup(s => s.GetRefreshTokenAsync("bob", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		_storeMock
			.Setup(s => s.GetProxyAsync("bob", It.IsAny<CancellationToken>()))
			.ReturnsAsync("http://egress.example.net:3128");

		using var manager = CreateManager();

		var session = await manager.TryRestoreSessionAsync("bob");

		Assert.NotNull(session);
		Assert.Equal("http://egress.example.net:3128", session.ConfiguredProxy);
	}

	private SessionManager CreateManager() =>
		new(
			new Mock<IActionRegistry>(MockBehavior.Loose).Object,
			NullLogger<SessionManager>.Instance,
			steamClientManager: null, // stub mode: restored logins succeed without a transport
			credentialStore: _storeMock.Object,
			loggerFactory: NullLoggerFactory.Instance);

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
