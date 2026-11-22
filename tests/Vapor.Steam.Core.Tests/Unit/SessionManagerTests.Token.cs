using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Vapor.Steam.Core.Security;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>Background token refresh tests: near-expiry, missing/fresh token, failure handling and credential-store faults.</summary>
public partial class SessionManagerTests
{
	[Fact]
	public async Task TokenRefreshLoop_WhenRefreshThrows_LogsWarningAndKeepsLoopAlive()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore(expiringToken: true);
		SetupSuccessfulTokenLogin();
		_steamClientManagerMock
			.Setup(m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.Returns(Task.FromException<bool>(new InvalidOperationException("refresh exploded")));
		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50));

		var session = await manager.GetOrCreateSessionAsync(
			"test_account", new AccountCredentials("test_account", string.Empty), CancellationToken.None);
		await session.LoginAsync(CancellationToken.None);

		// The loop swallows the refresh failure and keeps ticking.
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				_steamClientManagerMock.Verify(
					m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()),
					Times.AtLeastOnce);
				break;
			}
			catch (MockException)
			{
				await Task.Delay(25);
			}
		}

		_steamClientManagerMock.Verify(
			m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()),
			Times.AtLeastOnce);
		Assert.Equal(SessionState.Connected, session.State);
	}

	[Fact]
	public async Task BackgroundTokenRefresh_WhenTokenNearExpiry_RefreshesConnectedSession()
	{
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		credentialStoreMock
			.Setup(s => s.GetRefreshTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		credentialStoreMock
			.Setup(s => s.GetProxyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		credentialStoreMock
			.Setup(s => s.GetAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StoredAccessToken("access-token", DateTimeOffset.UtcNow.AddSeconds(1)));

		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", "refresh-token"))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50),
			tokenRefreshLeadTime: TimeSpan.FromMinutes(5));

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await Task.Delay(250);

		_steamClientManagerMock.Verify(
			m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()),
			Times.AtLeastOnce);
	}

	[Fact]
	public async Task BackgroundTokenRefresh_WhenAccessTokenMissing_RefreshesConnectedSession()
	{
		// A connected session whose store holds no access token at all: the null
		// short-circuits the shouldRefresh check straight into a refresh attempt.
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		credentialStoreMock
			.Setup(s => s.GetRefreshTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		credentialStoreMock
			.Setup(s => s.GetProxyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		credentialStoreMock
			.Setup(s => s.GetAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync((StoredAccessToken?)null);

		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", "refresh-token"))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50),
			tokenRefreshLeadTime: TimeSpan.FromMinutes(5));

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await Task.Delay(250);

		_steamClientManagerMock.Verify(
			m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()),
			Times.AtLeastOnce);
	}

	[Fact]
	public async Task BackgroundTokenRefresh_WhenTokenIsFresh_DoesNotRefresh()
	{
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		credentialStoreMock
			.Setup(s => s.GetRefreshTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		credentialStoreMock
			.Setup(s => s.GetProxyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		credentialStoreMock
			.Setup(s => s.GetAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StoredAccessToken("access-token", DateTimeOffset.UtcNow.AddHours(2)));

		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", "refresh-token"))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50),
			tokenRefreshLeadTime: TimeSpan.FromMinutes(5));

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await Task.Delay(250);

		_steamClientManagerMock.Verify(
			m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()),
			Times.Never);
	}

	// --- background token refresh edge paths ---
	[Fact]
	public async Task BackgroundTokenRefresh_SkipsDisconnectedSessionsAndMissingCredentials()
	{
		var hasCredentials = new Queue<bool>(new[] { true, false }); // restore, then first refresh tick
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => hasCredentials.Count > 0 && hasCredentials.Dequeue());
		credentialStoreMock
			.Setup(s => s.GetRefreshTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		credentialStoreMock
			.Setup(s => s.GetProxyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		credentialStoreMock
			.Setup(s => s.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StoredAccessToken("access-token", DateTimeOffset.UtcNow.AddSeconds(-10))); // expired

		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50),
			tokenRefreshLeadTime: TimeSpan.FromMinutes(5));

		// A session that never logs in stays Disconnected and must be skipped.
		var idleCredentials = new AccountCredentials("idle_account", "password");
		await manager.GetOrCreateSessionAsync("idle_account", idleCredentials, CancellationToken.None);

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await Task.Delay(250);

		_steamClientManagerMock.Verify(
			m => m.RefreshAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	[Fact]
	public async Task BackgroundTokenRefresh_WhenRefreshReportsFailure_DoesNotThrow()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore(expiringToken: true);
		SetupSuccessfulTokenLogin();
		_steamClientManagerMock
			.Setup(m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50),
			tokenRefreshLeadTime: TimeSpan.FromMinutes(5));

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await Task.Delay(250);

		_steamClientManagerMock.Verify(
			m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()),
			Times.AtLeastOnce);
	}

	[Fact]
	public async Task BackgroundTokenRefresh_WhenRefreshThrows_SwallowsAndKeepsGoing()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore(expiringToken: true);
		SetupSuccessfulTokenLogin();
		_steamClientManagerMock
			.Setup(m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("CM unreachable"));

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50),
			tokenRefreshLeadTime: TimeSpan.FromMinutes(5));

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await Task.Delay(250);

		_steamClientManagerMock.Verify(
			m => m.RefreshAccessTokenAsync("test_account", It.IsAny<CancellationToken>()),
			Times.AtLeastOnce);
	}

	[Fact]
	public async Task BackgroundTokenRefresh_WhenCredentialStoreThrows_LoopExitsGracefully()
	{
		// The first access-token read (the restore itself) succeeds; every later
		// read — from the refresh loop — throws, which must kill the loop with a
		// logged error instead of crashing the process.
		var accessTokenReads = 0;
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		credentialStoreMock
			.Setup(s => s.GetRefreshTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		credentialStoreMock
			.Setup(s => s.GetProxyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

		credentialStoreMock
			.Setup(s => s.GetAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.Returns(() =>
			{
				if (Interlocked.Increment(ref accessTokenReads) == 1)
				{
					return Task.FromResult<StoredAccessToken?>(new StoredAccessToken(
						"access-token",
						DateTimeOffset.UtcNow.AddSeconds(-10)));
				}

				throw new InvalidOperationException("store corrupted");
			});

		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			tokenRefreshCheckInterval: TimeSpan.FromMilliseconds(50));

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await Task.Delay(250);

		// The refresh loop logged the failure and exited gracefully (disposed via using).
		_loggerMock.Verify(
			l => l.Log(
				LogLevel.Error,
				It.IsAny<EventId>(),
				It.Is<It.IsAnyType>((v, t) => v!.ToString()!.Contains("token refresh loop")),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce);
	}

}
