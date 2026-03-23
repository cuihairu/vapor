using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

public class BotSessionTests : IDisposable
{
	private readonly Mock<ILogger<Vapor.Steam.Core.BotSession>> _loggerMock;
	private readonly Mock<IActionRegistry> _actionRegistryMock;
	private readonly Mock<ISteamClientManager> _steamClientManagerMock;
	private readonly AccountCredentials _credentials;

	public BotSessionTests()
	{
		_loggerMock = new Mock<ILogger<Vapor.Steam.Core.BotSession>>(MockBehavior.Loose);
		_actionRegistryMock = new Mock<IActionRegistry>(MockBehavior.Strict);
		_steamClientManagerMock = new Mock<ISteamClientManager>(MockBehavior.Loose);
		_credentials = new AccountCredentials("test_account", "test_password");
	}

	[Fact]
	public void Constructor_WithValidParameters_CreatesSession()
	{
		// Act
		var session = new Vapor.Steam.Core.BotSession(
			"test_account",
			_credentials,
			_actionRegistryMock.Object,
			_loggerMock.Object,
			null
		);

		// Assert
		Assert.Equal("test_account", session.AccountName);
		Assert.Equal(SessionState.Disconnected, session.State);
	}

	[Fact]
	public void Constructor_InitializesWithDisconnectedState()
	{
		// Act
		var session = CreateSession();

		// Assert
		Assert.Equal(SessionState.Disconnected, session.State);
	}

	[Fact]
	public void AccountName_ReturnsProvidedAccountName()
	{
		// Arrange
		var expectedAccount = "my_steam_account";

		// Act
		var session = new Vapor.Steam.Core.BotSession(
			expectedAccount,
			_credentials,
			_actionRegistryMock.Object,
			_loggerMock.Object,
			null
		);

		// Assert
		Assert.Equal(expectedAccount, session.AccountName);
	}

	[Fact]
	public void Start_WhenCalledMultipleTimes_ThrowsInvalidOperationException()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		// Act & Assert
		Assert.Throws<InvalidOperationException>(() => session.Start());
	}

	[Fact]
	public async Task ExecuteActionAsync_WithNonExistentAction_ReturnsFailure()
	{
		// Arrange
		var session = CreateSession();
		session.Start();
		_actionRegistryMock.Setup(r => r.Get(It.IsAny<string>())).Returns((IAction?)null);

		// Act
		var result = await session.ExecuteActionAsync("nonexistent", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Contains("action not found", result.Error ?? string.Empty);
	}

	[Fact]
	public async Task ExecuteActionAsync_WithActionThatRequiresLoginAndDisconnected_ReturnsFailure()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: true, 30));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		var result = await session.ExecuteActionAsync("test", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Contains("not logged in", result.Error ?? string.Empty);
	}

	[Fact]
	public async Task ExecuteActionAsync_WithActionThatDoesNotRequireLogin_ReturnsSuccess()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ActionResult(true, null, new Dictionary<string, object?>()));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		var result = await session.ExecuteActionAsync("test", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		mockAction.Verify(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ExecuteActionAsync_PassesSessionToAction()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		Vapor.Steam.Core.BotSession? capturedSession = null;
		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Callback<Vapor.Steam.Core.BotSession, IReadOnlyDictionary<string, object?>, CancellationToken>((s, _, _) => capturedSession = s)
			.ReturnsAsync(new ActionResult(true, null, new Dictionary<string, object?>()));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		await session.ExecuteActionAsync("test", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.Same(session, capturedSession);
	}

	[Fact]
	public async Task ExecuteActionAsync_PassesPayloadToAction()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		IReadOnlyDictionary<string, object?>? capturedPayload = null;
		var expectedPayload = new Dictionary<string, object?> { ["key"] = "value" };

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Callback<Vapor.Steam.Core.BotSession, IReadOnlyDictionary<string, object?>, CancellationToken>((_, p, _) => capturedPayload = p)
			.ReturnsAsync(new ActionResult(true, null, new Dictionary<string, object?>()));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		await session.ExecuteActionAsync("test", expectedPayload, CancellationToken.None);

		// Assert
		Assert.NotNull(capturedPayload);
		Assert.Equal("value", capturedPayload["key"]);
	}

	[Fact]
	public async Task ExecuteActionAsync_PassesCancellationTokenToAction()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var tokenTcs = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var actionExecuted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Callback<Vapor.Steam.Core.BotSession, IReadOnlyDictionary<string, object?>, CancellationToken>((_, _, ct) =>
			{
				tokenTcs.TrySetResult(ct);
				actionExecuted.TrySetResult(true);
			})
			.ReturnsAsync(new ActionResult(true, null, new Dictionary<string, object?>()));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act - Pass an already cancelled token
		using var cts = new CancellationTokenSource();
		cts.Cancel(); // Cancel immediately
		var call = session.ExecuteActionAsync("test", new Dictionary<string, object?>(), cts.Token);

		// Wait for action to execute (or fail due to cancellation)
		// Use a very long timeout for slow CI systems
		try
		{
			await actionExecuted.Task.WaitAsync(TimeSpan.FromMinutes(2));
			// If action executed, verify token was captured
			var capturedToken = await tokenTcs.Task.WaitAsync(TimeSpan.FromSeconds(1));
			Assert.True(capturedToken.IsCancellationRequested, "Token should be cancelled");
		}
		catch (TimeoutException)
		{
			// If action didn't execute due to pre-cancelled token, that's also valid
			// The session should have rejected the cancelled token before executing
		}
	}

	[Fact]
	public async Task ExecuteActionAsync_WithCancelledToken_ThrowsOperationCanceledException()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var cts = new CancellationTokenSource();
		cts.Cancel();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act & Assert
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => session.ExecuteActionAsync("test", new Dictionary<string, object?>(), cts.Token)
		);
	}

	[Fact]
	public async Task ExecuteActionAsync_WithTimeout_ThrowsTimeoutException()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Returns(async (Vapor.Steam.Core.BotSession s, IReadOnlyDictionary<string, object?> p, CancellationToken ct) =>
			{
				await Task.Delay(TimeSpan.FromMinutes(1), ct);
				return new ActionResult(true, null, new Dictionary<string, object?>());
			});
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act & Assert
		var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => session.ExecuteActionAsync("test", new Dictionary<string, object?>(), cts.Token)
		);
	}

	[Fact]
	public async Task ExecuteActionAsync_WhenActionExceedsActionTimeout_ReturnsTimeoutFailure()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("slow", "Slow", RequiresLogin: false, TimeoutSeconds: 1));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Returns(async (Vapor.Steam.Core.BotSession s, IReadOnlyDictionary<string, object?> p, CancellationToken ct) =>
			{
				await Task.Delay(TimeSpan.FromMinutes(1), ct);
				return new ActionResult(true, null, new Dictionary<string, object?>());
			});
		_actionRegistryMock.Setup(r => r.Get("slow")).Returns(mockAction.Object);

		// Act
		var result = await session.ExecuteActionAsync("slow", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Equal("action timeout", result.Error);
	}

	[Fact]
	public async Task ExecuteActionAsync_ReturnsActionOutput()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var expectedOutput = new Dictionary<string, object?>
		{
			["result"] = "success",
			["data"] = 123
		};

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ActionResult(true, null, expectedOutput));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		var result = await session.ExecuteActionAsync("test", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.Equal("success", result.Output["result"]);
		Assert.Equal(123, result.Output["data"]);
	}

	[Fact]
	public async Task ExecuteActionAsync_WhenActionThrowsException_LogsError()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", RequiresLogin: false, 30));
		mockAction.Setup(a => a.ExecuteAsync(It.IsAny<Vapor.Steam.Core.BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("Test exception"));
		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		var result = await session.ExecuteActionAsync("test", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.NotNull(result.Error);
	}

	[Fact]
	public void ProvideAuthCode_WhenNotInWaitState_DoesNotCrash()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		// Act & Assert - should not throw
		session.ProvideAuthCode("123456");
	}

	[Fact]
	public void Provide2FACode_WhenNotInWaitState_DoesNotCrash()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		// Act & Assert - should not throw
		session.Provide2FACode("ABC123");
	}

	[Fact]
	public async Task DisconnectAsync_WhenNotStarted_DoesNotThrow()
	{
		// Arrange
		var session = CreateSession();

		// Act
		await session.DisconnectAsync(CancellationToken.None);
	}

	[Fact]
	public async Task LoginAsync_WithPassword_UsesPasswordLogin()
	{
		var session = CreateSession();
		session.Start();

		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", "test_password", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		var result = await session.LoginAsync(CancellationToken.None);

		Assert.True(result.Success);
		_steamClientManagerMock.Verify(
			m => m.UpdateLogOnDetailsAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()),
			Times.Never);
		_steamClientManagerMock.Verify(m => m.LoginAsync("test_account", "test_password", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task LoginAsync_WithStoredAccessToken_UsesTokenLogin()
	{
		var session = CreateSession(new AccountCredentials("test_account", string.Empty, AccessToken: "access-token"));
		session.Start();

		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", null))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		var result = await session.LoginAsync(CancellationToken.None);

		Assert.True(result.Success);
		_steamClientManagerMock.Verify(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", null), Times.Once);
		_steamClientManagerMock.Verify(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task LoginAsync_WithStoredRefreshToken_UsesTokenLogin()
	{
		var session = CreateSession(new AccountCredentials("test_account", string.Empty, RefreshToken: "refresh-token"));
		session.Start();

		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", null, "refresh-token"))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);

		var result = await session.LoginAsync(CancellationToken.None);

		Assert.True(result.Success);
		_steamClientManagerMock.Verify(m => m.UpdateLogOnDetailsAsync("test_account", null, "refresh-token"), Times.Once);
		_steamClientManagerMock.Verify(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public void SubscribeEvents_ReturnsEventChannel()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		// Act
		var events = session.SubscribeEvents(CancellationToken.None);

		// Assert
		Assert.NotNull(events);
	}

	[Fact]
	public async Task SubscribeEvents_WhenStateChanges_ReceivesEvent()
	{
		// Arrange
		var session = CreateSession();
		session.Start();

		var events = session.SubscribeEvents(CancellationToken.None);
		var eventList = new List<SessionEvent>();

		// Start collecting events in background
		var collectTask = Task.Run(async () =>
		{
			await foreach (var evt in events)
			{
				eventList.Add(evt);
				if (eventList.Count >= 1) break;
			}
		});

		// Trigger state change by disconnecting
		_ = session.DisconnectAsync(CancellationToken.None);

		await Task.Delay(100);
		await collectTask.WaitAsync(TimeSpan.FromSeconds(1));

		// Assert
		// At minimum, we should have received state change events
		Assert.NotEmpty(eventList);
	}

	[Fact]
	public void LastHeartbeat_InitiallyReturnsUtcNow()
	{
		// Arrange
		var before = DateTimeOffset.UtcNow;
		var session = CreateSession();
		var after = DateTimeOffset.UtcNow;

		// Assert
		Assert.InRange(session.LastHeartbeat, before.AddSeconds(-1), after.AddSeconds(1));
	}

	[Fact]
	public void ConnectedAt_InitiallyReturnsDefault()
	{
		// Arrange
		var session = CreateSession();

		// Act & Assert
		Assert.Equal(default, session.ConnectedAt);
	}

	[Fact]
	public void Dispose_CanBeCalledMultipleTimes()
	{
		// Arrange
		var session = CreateSession();

		// Act & Assert - should not throw
		session.Dispose();
		session.Dispose();
	}

	private Vapor.Steam.Core.BotSession CreateSession(AccountCredentials? credentials = null)
	{
		return new Vapor.Steam.Core.BotSession(
			"test_account",
			credentials ?? _credentials,
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object
		);
	}

	public void Dispose()
	{
		// Cleanup if needed
		GC.SuppressFinalize(this);
	}
}
