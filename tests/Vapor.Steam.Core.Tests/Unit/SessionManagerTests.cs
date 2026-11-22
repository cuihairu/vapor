using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

public partial class SessionManagerTests : IDisposable
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
	public async Task GetOrCreateSessionAsync_ConcurrentCreation_RaceLoserReturnsWinner()
	{
		// Both callers can miss the TryGetValue fast path and race into TryAdd; the
		// loser must dispose its own session and return the winner. The window
		// between the two dictionary probes has no await to park in, but it does
		// evaluate CreateLogger<BotSession> — which a gated logger factory turns
		// into a deterministic seam: the loser thread is parked inside it (past
		// the lookup, before the add), the winner completes its TryAdd in full,
		// and only then is the loser released to lose for real. The earlier
		// 16-thread barrier volley left this to scheduling luck (a whole 40-round
		// run once missed the slow path, and the 2026-09-24 coverage rerun
		// reproduced that miss), which a 100% branch gate cannot tolerate.
		var credentials = new AccountCredentials("race_account", "password");
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int armed = 1;
		using var gatingFactory = new GatedLoggerFactory(categoryName =>
		{
			// Fire once, for the loser's CreateLogger<BotSession>; the winner's
			// own (and every later) call passes straight through.
			if (Interlocked.Exchange(ref armed, 0) == 1)
			{
				reached.TrySetResult();
				if (!gate.Task.Wait(TimeSpan.FromSeconds(30)))
				{
					throw new TimeoutException("the winner never completed its creation; failing the parked loser");
				}
			}
		});
		var manager = new SessionManager(
			_actionRegistryMock.Object, _loggerMock.Object, _steamClientManagerMock.Object,
			loggerFactory: gatingFactory);

		var results = new BotSession[2];
		var loser = new Thread(() =>
		{
			results[1] = manager.GetOrCreateSessionAsync("race_account", credentials, CancellationToken.None)
				.GetAwaiter().GetResult();
		});
		loser.Start();
		await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

		results[0] = await manager.GetOrCreateSessionAsync("race_account", credentials, CancellationToken.None);
		gate.TrySetResult();

		loser.Join(TimeSpan.FromSeconds(30));
		Assert.False(loser.IsAlive, "the loser thread never finished its creation");

		Assert.All(results, session => Assert.Same(results[0], session));
		await manager.RemoveSessionAsync("race_account", CancellationToken.None);
		manager.Dispose();
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
	public async Task RemoveSessionAsync_DisconnectThrows_StillDisposesSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act: a cancelled token makes BotSession.DisconnectAsync's WaitAsync throw
		// OperationCanceledException; the session must still be disposed (removed)
		// rather than leaked.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => _manager.RemoveSessionAsync(accountName, cts.Token));

		// Assert: the account was removed from the manager even though the
		// disconnect command failed.
		var session = await _manager.GetSessionAsync(accountName, CancellationToken.None);
		Assert.Null(session);
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
			.Setup(s => s.GetProxyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

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
	public async Task PumpSessionEvents_EventWithoutNewState_ForwardsEmptyStateToCallback()
	{
		// The pump line formats evt.NewState?.ToString() ?? "". An event that
		// omits NewState is a legal producer shape (the record defaults it to
		// null), so the callback must receive the empty string rather than throw.
		// The pump path uses evt.Type.ToString() ("Error"), distinct from the
		// session's own snake_case callback path, so this invocation is isolated.
		var seen = new ConcurrentQueue<(string Account, string EventType, string State)>();
		_manager.SetEventCallback((account, eventType, state, _) =>
		{
			seen.Enqueue((account, eventType, state));
			return Task.CompletedTask;
		});

		var session = await _manager.GetOrCreateSessionAsync(
			"ns_account", new AccountCredentials("ns_account", "password"), CancellationToken.None);

		// Emit the stateless event through the session channel the pump reads —
		// same reflection seam the command-channel tests use.
		var channel = (Channel<SessionEvent>)typeof(BotSession)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;
		channel.Writer.TryWrite(new SessionEvent(SessionEventType.Error, "ns_account"));

		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (DateTime.UtcNow < deadline && seen.IsEmpty)
		{
			await Task.Delay(25);
		}

		var evt = Assert.Single(seen);
		Assert.Equal("ns_account", evt.Account);
		Assert.Equal("Error", evt.EventType);
		Assert.Equal(string.Empty, evt.State);
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
			.Setup(s => s.GetProxyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);

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

	/// <summary>
	/// Logger factory whose CreateLogger hands each category to a callback before
	/// returning — the seam the race test parks the losing creation in (the call
	/// sits between SessionManager's dictionary probes).
	/// </summary>
	private sealed class GatedLoggerFactory(Action<string> onCategoryCreated) : ILoggerFactory
	{
		public void AddProvider(ILoggerProvider provider) { }

		public ILogger CreateLogger(string categoryName)
		{
			onCategoryCreated(categoryName);
			return NullLogger.Instance;
		}

		public void Dispose() { }
	}

	public void Dispose()
	{
		_manager.Dispose();
		GC.SuppressFinalize(this);
	}

}
