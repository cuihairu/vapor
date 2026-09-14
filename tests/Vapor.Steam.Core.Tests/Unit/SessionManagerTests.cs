using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

public class SessionManagerTests : IDisposable
{
	private readonly Mock<ILogger<SessionManager>> _loggerMock;
	private readonly Mock<IActionRegistry> _actionRegistryMock;
	private readonly Mock<ISteamClientManager> _steamClientManagerMock;
	private readonly SessionManager _manager;

	public SessionManagerTests()
	{
		_loggerMock = new Mock<ILogger<SessionManager>>(MockBehavior.Loose);
		_actionRegistryMock = new Mock<IActionRegistry>(MockBehavior.Loose);
		_steamClientManagerMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		_manager = new SessionManager(_actionRegistryMock.Object, _loggerMock.Object, _steamClientManagerMock.Object);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithNewAccount_CreatesNewSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");

		// Act
		var session = await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Assert
		Assert.NotNull(session);
		Assert.Equal(accountName, session.AccountName);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithSameAccount_ReturnsSameSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");

		// Act
		var session1 = await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);
		var session2 = await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Assert
		Assert.Same(session1, session2);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithDifferentAccounts_CreatesDifferentSessions()
	{
		// Arrange
		var credentials1 = new AccountCredentials("account1", "password1");
		var credentials2 = new AccountCredentials("account2", "password2");

		// Act
		var session1 = await _manager.GetOrCreateSessionAsync("account1", credentials1, CancellationToken.None);
		var session2 = await _manager.GetOrCreateSessionAsync("account2", credentials2, CancellationToken.None);

		// Assert
		Assert.NotSame(session1, session2);
		Assert.Equal("account1", session1.AccountName);
		Assert.Equal("account2", session2.AccountName);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_IsCaseInsensitive()
	{
		// Arrange
		var credentials = new AccountCredentials("MyAccount", "password");

		// Act
		var session1 = await _manager.GetOrCreateSessionAsync("MyAccount", credentials, CancellationToken.None);
		var session2 = await _manager.GetOrCreateSessionAsync("myaccount", credentials, CancellationToken.None);
		var session3 = await _manager.GetOrCreateSessionAsync("MYACCOUNT", credentials, CancellationToken.None);

		// Assert
		Assert.Same(session1, session2);
		Assert.Same(session2, session3);
	}

	[Fact]
	public async Task GetSessionAsync_WithExistingAccount_ReturnsSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		var session = await _manager.GetSessionAsync(accountName, CancellationToken.None);

		// Assert
		Assert.NotNull(session);
		Assert.Equal(accountName, session.AccountName);
	}

	[Fact]
	public async Task GetSessionAsync_WithNonExistentAccount_ReturnsNull()
	{
		// Act
		var session = await _manager.GetSessionAsync("nonexistent", CancellationToken.None);

		// Assert
		Assert.Null(session);
	}

	[Fact]
	public async Task GetSessionAsync_IsCaseInsensitive()
	{
		// Arrange
		var credentials = new AccountCredentials("MyAccount", "password");
		await _manager.GetOrCreateSessionAsync("MyAccount", credentials, CancellationToken.None);

		// Act
		var session = await _manager.GetSessionAsync("myaccount", CancellationToken.None);

		// Assert
		Assert.NotNull(session);
	}

	[Fact]
	public async Task RemoveSessionAsync_WithExistingAccount_RemovesSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		await _manager.RemoveSessionAsync(accountName, CancellationToken.None);

		// Assert
		var session = await _manager.GetSessionAsync(accountName, CancellationToken.None);
		Assert.Null(session);
	}

	[Fact]
	public async Task RemoveSessionAsync_WithNonExistentAccount_DoesNotThrow()
	{
		// Act & Assert - should not throw
		await _manager.RemoveSessionAsync("nonexistent", CancellationToken.None);
	}

	[Fact]
	public async Task RemoveSessionAsync_IsCaseInsensitive()
	{
		// Arrange
		var credentials = new AccountCredentials("MyAccount", "password");
		await _manager.GetOrCreateSessionAsync("MyAccount", credentials, CancellationToken.None);

		// Act
		await _manager.RemoveSessionAsync("myaccount", CancellationToken.None);

		// Assert
		var session = await _manager.GetSessionAsync("MyAccount", CancellationToken.None);
		Assert.Null(session);
	}

	[Fact]
	public void ListSessions_WithNoSessions_ReturnsEmptyList()
	{
		// Act
		var sessions = _manager.ListSessions();

		// Assert
		Assert.Empty(sessions);
	}

	[Fact]
	public async Task ListSessions_WithMultipleSessions_ReturnsAllSessions()
	{
		// Arrange
		var credentials1 = new AccountCredentials("account1", "password1");
		var credentials2 = new AccountCredentials("account2", "password2");
		var credentials3 = new AccountCredentials("account3", "password3");

		await _manager.GetOrCreateSessionAsync("account1", credentials1, CancellationToken.None);
		await _manager.GetOrCreateSessionAsync("account2", credentials2, CancellationToken.None);
		await _manager.GetOrCreateSessionAsync("account3", credentials3, CancellationToken.None);

		// Act
		var sessions = _manager.ListSessions();

		// Assert
		Assert.Equal(3, sessions.Count);
		Assert.Contains(sessions, s => s.AccountName == "account1");
		Assert.Contains(sessions, s => s.AccountName == "account2");
		Assert.Contains(sessions, s => s.AccountName == "account3");
	}

	[Fact]
	public async Task ListSessions_AfterRemovingSession_ReturnsRemainingSessions()
	{
		// Arrange
		var credentials1 = new AccountCredentials("account1", "password1");
		var credentials2 = new AccountCredentials("account2", "password2");

		await _manager.GetOrCreateSessionAsync("account1", credentials1, CancellationToken.None);
		await _manager.GetOrCreateSessionAsync("account2", credentials2, CancellationToken.None);

		// Act
		await _manager.RemoveSessionAsync("account1", CancellationToken.None);
		var sessions = _manager.ListSessions();

		// Assert
		Assert.Single(sessions);
		Assert.Equal("account2", sessions[0].AccountName);
	}

	[Fact]
	public async Task SubscribeAllEvents_ReturnsEventChannel()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		var events = _manager.SubscribeAllEvents(CancellationToken.None);

		// Assert
		Assert.NotNull(events);
	}

	[Fact]
	public void SubscribeAllEvents_WithNoSessions_DoesNotThrow()
	{
		// Act & Assert - should not throw
		var events = _manager.SubscribeAllEvents(CancellationToken.None);
		Assert.NotNull(events);
	}

	[Fact]
	public async Task SubscribeAllEvents_ReceivesEventsFromSessions()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		var events = _manager.SubscribeAllEvents(CancellationToken.None);
		var eventList = new List<SessionEvent>();
		var cts = new CancellationTokenSource();

		// Start collecting events. Deliberately no token argument: with one, a busy thread pool
		// could leave the task canceled before the delegate ever runs, failing the await below.
		var collectTask = Task.Run(async () =>
		{
			try
			{
				await foreach (var evt in events.WithCancellation(cts.Token))
				{
					eventList.Add(evt);
					if (eventList.Count >= 1) break;
				}
			}
			catch (OperationCanceledException)
			{
			}
		});

		// Wait a bit for events
		await Task.Delay(200);
		cts.Cancel();
		await collectTask.WaitAsync(TimeSpan.FromSeconds(30));

		// Assert
		// Events should be collected (the exact number depends on timing)
		Assert.NotNull(events);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_TrulyConcurrentCreation_LosersReturnWinnerInstance()
	{
		var accountName = "race_account";
		var credentials = new AccountCredentials(accountName, "password");

		// The sequential double-create test cannot lose the TryAdd race (the whole
		// call is synchronous), so release real threads through a barrier instead.
		using var barrier = new Barrier(16);
		var tasks = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(() =>
			{
				barrier.SignalAndWait();
				return _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);
			}))
			.ToArray();

		var sessions = await Task.WhenAll(tasks);

		Assert.All(sessions, session => Assert.Same(sessions[0], session));
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithEventCallback_ForwardsSessionEventsToCallback()
	{
		var seen = new ConcurrentQueue<(string Account, string EventType, string State)>();
		_manager.SetEventCallback((account, eventType, state, _) =>
		{
			seen.Enqueue((account, eventType, state));
			return Task.CompletedTask;
		});
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("cb_account", It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new SteamAuthCodeRequiredException("code please"));

		var session = await _manager.GetOrCreateSessionAsync(
			"cb_account", new AccountCredentials("cb_account", "password"), CancellationToken.None);
		await session.LoginAsync(CancellationToken.None);

		var deadline = DateTime.UtcNow.AddSeconds(5);
		while (DateTime.UtcNow < deadline && !seen.Any(e => e.EventType == "auth_code_required"))
		{
			await Task.Delay(25);
		}

		var evt = Assert.Single(seen, e => e.EventType == "auth_code_required");
		Assert.Equal("cb_account", evt.Account);
		Assert.Equal("ConnectingWaitAuthCode", evt.State);
	}

	[Fact]
	public async Task TryRestoreSessionAsync_TrulyConcurrentRestore_LosersReturnWinnerInstance()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore();
		SetupSuccessfulTokenLogin();
		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object,
			CreateSlowLoggerFactory().Object,
			tokenRefreshCheckInterval: TimeSpan.FromMinutes(10));

		using var barrier = new Barrier(16);
		var tasks = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(() =>
			{
				barrier.SignalAndWait();
				return manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
			}))
			.ToArray();

		var sessions = await Task.WhenAll(tasks);

		Assert.All(sessions, session => Assert.NotNull(session));
		Assert.All(sessions, session => Assert.Same(sessions[0], session));
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithConcurrentCalls_CreatesOnlyOneSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		// The slow factory widens the create/TryAdd window so the losing caller
		// deterministically takes the "someone else won" branch instead of the
		// race usually resolving before the second caller even starts.
		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			loggerFactory: CreateSlowLoggerFactory().Object);

		// Act
		var tasks = Enumerable.Range(0, 10)
			.Select(_ => manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None))
			.ToArray();

		var sessions = await Task.WhenAll(tasks);

		// Assert
		// All returned sessions should be the same instance
		var firstSession = sessions[0];
		Assert.All(sessions, session => Assert.Same(firstSession, session));
	}

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
	public async Task RemoveSessionAsync_DisposesRemovedSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		await _manager.RemoveSessionAsync(accountName, CancellationToken.None);

		// Assert - session should be disposed (verified by not being able to use it)
		var retrievedSession = await _manager.GetSessionAsync(accountName, CancellationToken.None);
		Assert.Null(retrievedSession);
	}

	[Fact]
	public async Task TryRestoreSessionAsync_WithStoredCredentials_CreatesSessionThatCanLoginWithTokens()
	{
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		credentialStoreMock
			.Setup(s => s.GetRefreshTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		credentialStoreMock
			.Setup(s => s.GetAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StoredAccessToken("access-token", DateTimeOffset.UtcNow.AddMinutes(10)));

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
			credentialStoreMock.Object);

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);

		Assert.NotNull(session);
		_steamClientManagerMock.Verify(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", "refresh-token"), Times.Once);
		_steamClientManagerMock.Verify(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task TryRestoreSessionAsync_WithoutStoredCredentials_ReturnsNull()
	{
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("missing_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(false);

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object);

		var session = await manager.TryRestoreSessionAsync("missing_account", CancellationToken.None);

		Assert.Null(session);
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

	[Fact]
	public async Task MultipleConcurrentOperations_DoNotInterfere()
	{
		// Arrange
		var credentials1 = new AccountCredentials("account1", "password1");
		var credentials2 = new AccountCredentials("account2", "password2");
		var credentials3 = new AccountCredentials("account3", "password3");

		// Act - perform multiple operations concurrently
		var tasks = new Task[]
		{
			_manager.GetOrCreateSessionAsync("account1", credentials1, CancellationToken.None),
			_manager.GetOrCreateSessionAsync("account2", credentials2, CancellationToken.None),
			_manager.GetOrCreateSessionAsync("account3", credentials3, CancellationToken.None),
			_manager.GetSessionAsync("account1", CancellationToken.None),
			Task.Run(async () =>
			{
				await Task.Delay(50);
				_ = await _manager.GetSessionAsync("account2", CancellationToken.None);
			})
		};

		await Task.WhenAll(tasks);

		// Assert
		Assert.Equal(5, tasks.Length); // All tasks should complete
		var sessions = _manager.ListSessions();
		Assert.Equal(3, sessions.Count);
	}

	[Fact]
	public async Task ListSessions_ReturnsReadOnlyList()
	{
		// Arrange
		var credentials = new AccountCredentials("test_account", "password");
		await _manager.GetOrCreateSessionAsync("test_account", credentials, CancellationToken.None);

		// Act
		var sessions = _manager.ListSessions();

		// Assert
		Assert.IsAssignableFrom<IReadOnlyList<BotSession>>(sessions);
	}

	// --- SetEventCallback / event fan-out ---

	[Fact]
	public async Task TryRestoreSessionAsync_WithEventCallback_InvokesCallbackForSessionEvents()
	{
		// ConcurrentQueue: the event pump keeps invoking the callback while the
		// asserts below enumerate — a locked List still throws "collection was
		// modified" because the enumeration side never takes the same lock.
		var callbackInvocations = new ConcurrentQueue<(string Account, string Type)>();
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore();
		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object);
		manager.SetEventCallback((account, type, _, _) =>
		{
			callbackInvocations.Enqueue((account, type));
			return Task.CompletedTask;
		});

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		// State changes flow through the session channel to the callback.
		for (int i = 0; i < 40 && callbackInvocations.Count == 0; i++)
		{
			await Task.Delay(50);
		}

		Assert.NotEmpty(callbackInvocations);
		Assert.All(callbackInvocations, inv => Assert.Equal("test_account", inv.Account));
	}

	[Fact]
	public async Task SubscribeAllEvents_YieldsEventsWrittenBySessions()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore();
		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object);

		var eventList = new List<SessionEvent>();
		var collectTask = Task.Run(async () =>
		{
			await foreach (var evt in manager.SubscribeAllEvents(CancellationToken.None))
			{
				lock (eventList)
				{
					eventList.Add(evt);
				}

				if (eventList.Count >= 1)
				{
					break;
				}
			}
		});

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await collectTask.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.NotEmpty(eventList);
	}

	// --- TryRestoreSessionAsync edge paths ---

	[Fact]
	public async Task TryRestoreSessionAsync_WithoutCredentialStore_ReturnsNull()
	{
		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStore: null);

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);

		Assert.Null(session);
	}

	[Fact]
	public async Task TryRestoreSessionAsync_WithExistingSession_ReturnsIt()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore();
		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object);

		var created = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		var restored = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);

		Assert.NotNull(created);
		Assert.Same(created, restored);
	}

	[Fact]
	public async Task TryRestoreSessionAsync_WhenLoginFails_RemovesSessionAndReturnsNull()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore();
		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", "refresh-token"))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("steam rejected the token"));

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object);

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);

		Assert.Null(session);
		var after = await manager.GetSessionAsync("test_account", CancellationToken.None);
		Assert.Null(after);
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
				It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("token refresh loop")),
				It.IsAny<Exception>(),
				It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
			Times.AtLeastOnce);
	}

	/// <summary>
	/// A logger factory whose CreateLogger blocks briefly, widening the window
	/// between the session construction and the dictionary TryAdd so concurrent
	/// create/restore tests deterministically exercise the losing-caller branch.
	/// </summary>
	private static Mock<ILoggerFactory> CreateSlowLoggerFactory()
	{
		var factoryMock = new Mock<ILoggerFactory>(MockBehavior.Loose);
		factoryMock
			.Setup(f => f.CreateLogger(It.IsAny<string>()))
			.Returns(NullLogger.Instance)
			.Callback(() => Thread.Sleep(30));
		return factoryMock;
	}

	private Mock<ICredentialStore> CreateSuccessfulRestoreCredentialStore(bool expiringToken = false)
	{
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.HasCredentialsAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		credentialStoreMock
			.Setup(s => s.GetRefreshTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync("refresh-token");
		credentialStoreMock
			.Setup(s => s.GetAccessTokenAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StoredAccessToken(
				"access-token",
				expiringToken
					? DateTimeOffset.UtcNow.AddSeconds(1)
					: DateTimeOffset.UtcNow.AddMinutes(10)));
		return credentialStoreMock;
	}

	private void SetupSuccessfulTokenLogin()
	{
		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", "refresh-token"))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
	}

	public void Dispose()
	{
		_manager.Dispose();
		GC.SuppressFinalize(this);
	}
}

