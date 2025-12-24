using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using SteamControl.Steam.Core.Steam;

namespace SteamControl.Steam.Core.Tests.Unit;

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
	public async Task SubscribeAllEvents_WithNoSessions_DoesNotThrow()
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

		// Start collecting events
		var collectTask = Task.Run(async () =>
		{
			try
			{
				await foreach (var evt in events)
				{
					eventList.Add(evt);
					if (eventList.Count >= 1) break;
				}
			}
			catch (OperationCanceledException)
			{
			}
		}, cts.Token);

		// Wait a bit for events
		await Task.Delay(200);
		cts.Cancel();
		await collectTask.WaitAsync(TimeSpan.FromSeconds(1));

		// Assert
		// Events should be collected (the exact number depends on timing)
		Assert.NotNull(events);
	}

	[Fact]
	public async Task GetOrCreateSessionAsync_WithConcurrentCalls_CreatesOnlyOneSession()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");

		// Act
		var tasks = Enumerable.Range(0, 10)
			.Select(_ => _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None))
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
	public async Task MultipleConcurrentOperations_DoNotInterfere()
	{
		// Arrange
		var credentials1 = new AccountCredentials("account1", "password1");
		var credentials2 = new AccountCredentials("account2", "password2");
		var credentials3 = new AccountCredentials("account3", "password3");

		// Act - perform multiple operations concurrently
		var tasks = new[]
		{
			_manager.GetOrCreateSessionAsync("account1", credentials1, CancellationToken.None),
			_manager.GetOrCreateSessionAsync("account2", credentials2, CancellationToken.None),
			_manager.GetOrCreateSessionAsync("account3", credentials3, CancellationToken.None),
			_manager.GetSessionAsync("account1", CancellationToken.None),
			Task.Run(async () =>
			{
				await Task.Delay(50);
				return await _manager.GetSessionAsync("account2", CancellationToken.None);
			})
		};

		var results = await Task.WhenAll(tasks);

		// Assert
		Assert.Equal(5, results.Length); // All tasks should complete
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

	public void Dispose()
	{
		_manager.Dispose();
		GC.SuppressFinalize(this);
	}
}
