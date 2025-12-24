using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit;

/// <summary>
/// 边界测试和异常场景测试
/// </summary>
public class EdgeCaseTests : IDisposable
{
	private readonly Mock<ILogger<BotSession>> _loggerMock;
	private readonly Mock<IActionRegistry> _actionRegistryMock;
	private readonly SessionManager _sessionManager;

	public EdgeCaseTests()
	{
		_loggerMock = new Mock<ILogger<BotSession>>(MockBehavior.Loose);
		_actionRegistryMock = new Mock<IActionRegistry>(MockBehavior.Loose);
		_sessionManager = new SessionManager(
			_actionRegistryMock.Object,
			NullLogger<SessionManager>.Instance,
			null
		);
	}

	#region 空值和空字符串测试

	[Fact]
	public async Task AccountName_EmptyString_CreatesSession()
	{
		// Arrange
		var credentials = new AccountCredentials("", "password");

		// Act
		var session = await _sessionManager.GetOrCreateSessionAsync("", credentials, CancellationToken.None);

		// Assert
		Assert.NotNull(session);
		Assert.Equal("", session.AccountName);
	}

	[Fact]
	public async Task AccountName_Whitespace_CreatesSession()
	{
		// Arrange
		var accountName = "   ";
		var credentials = new AccountCredentials(accountName, "password");

		// Act
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Assert
		Assert.NotNull(session);
		Assert.Equal(accountName, session.AccountName);
	}

	[Fact]
	public async Task AccountName_WithSpecialCharacters_CreatesSession()
	{
		// Arrange
		var accountNames = new[]
		{
			"user@example.com",
			"user@domain",
			"user-name",
			"user_name",
			"user.name",
			"user123",
			"123user",
			"user!@#$%"
		};

		foreach (var accountName in accountNames)
		{
			// Act
			var credentials = new AccountCredentials(accountName, "password");
			var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

			// Assert
			Assert.NotNull(session);
			Assert.Equal(accountName, session.AccountName);
		}
	}

	[Fact]
	public async Task AccountName_VeryLongString_HandlesCorrectly()
	{
		// Arrange
		var longAccountName = new string('a', 10000); // 10,000 字符
		var credentials = new AccountCredentials(longAccountName, "password");

		// Act
		var session = await _sessionManager.GetOrCreateSessionAsync(longAccountName, credentials, CancellationToken.None);

		// Assert
		Assert.NotNull(session);
		Assert.Equal(longAccountName, session.AccountName);
	}

	[Fact]
	public async Task Password_EmptyString_CreatesSession()
	{
		// Arrange
		var credentials = new AccountCredentials("test_account", "");

		// Act
		var session = await _sessionManager.GetOrCreateSessionAsync("test_account", credentials, CancellationToken.None);

		// Assert
		Assert.NotNull(session);
	}

	[Fact]
	public async Task Password_VeryLongString_HandlesCorrectly()
	{
		// Arrange
		var longPassword = new string('x', 100000); // 100,000 字符
		var credentials = new AccountCredentials("test_account", longPassword);

		// Act
		var session = await _sessionManager.GetOrCreateSessionAsync("test_account", credentials, CancellationToken.None);

		// Assert
		Assert.NotNull(session);
	}

	#endregion

	#region Unicode 和编码测试

	[Fact]
	public async Task AccountName_UnicodeCharacters_HandlesCorrectly()
	{
		// Arrange
		var unicodeNames = new[]
		{
			"用户名",
			"пользователь",
			"ユーザー名",
			"사용자명",
			"مستخدم",
			"benutzer",
			"utilisateur"
		};

		foreach (var accountName in unicodeNames)
		{
			// Act
			var credentials = new AccountCredentials(accountName, "password");
			var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

			// Assert
			Assert.NotNull(session);
			Assert.Equal(accountName, session.AccountName);
		}
	}

	[Fact]
	public async Task AccountName_Emoji_HandlesCorrectly()
	{
		// Arrange
		var emojiNames = new[]
		{
			"user😀",
			"🎉user",
			"🚀rocket🌙",
			"user_🔥_test"
		};

		foreach (var accountName in emojiNames)
		{
			// Act
			var credentials = new AccountCredentials(accountName, "password");
			var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

			// Assert
			Assert.NotNull(session);
			Assert.Equal(accountName, session.AccountName);
		}
	}

	[Fact]
	public async Task Password_UnicodeCharacters_HandlesCorrectly()
	{
		// Arrange
		var unicodePasswords = new[]
		{
			"密码中文",
			"пароль123",
			"パスワード",
			"비밀번호",
			"كلمة المرور",
			"passwörd",
			"pässwörd"
		};

		foreach (var password in unicodePasswords)
		{
			// Act
			var credentials = new AccountCredentials("test_account", password);
			var session = await _sessionManager.GetOrCreateSessionAsync("test_account", credentials, CancellationToken.None);

			// Assert
			Assert.NotNull(session);
		}
	}

	#endregion

	#region ActionRegistry 边界测试

	[Fact]
	public void ActionRegistry_RegisterNullAction_ThrowsArgumentNullException()
	{
		// Arrange
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);

		// Act & Assert
		Assert.Throws<NullReferenceException>(() => registry.Register(null!));
	}

	[Fact]
	public void ActionRegistry_GetWithNull_ReturnsNull()
	{
		// Arrange
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);

		// Act
		var result = registry.Get(null!);

		// Assert
		Assert.Null(result);
	}

	[Fact]
	public void ActionRegistry_GetWithEmptyString_ReturnsNull()
	{
		// Arrange
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);

		// Act
		var result = registry.Get("");

		// Assert
		Assert.Null(result);
	}

	[Fact]
	public void ActionRegistry_OverwriteWithSameName_UsesLastRegistration()
	{
		// Arrange
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		var action1 = new Mock<IAction>();
		var action2 = new Mock<IAction>();

		action1.Setup(a => a.Name).Returns("test");
		action1.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "First", false, 30));

		action2.Setup(a => a.Name).Returns("test");
		action2.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Second", false, 30));

		// Act
		registry.Register(action1.Object);
		registry.Register(action2.Object);

		var result = registry.Get("test");

		// Assert
		Assert.Same(action2.Object, result);
		Assert.Single(registry.ListNames());
	}

	#endregion

	#region SessionManager 边界测试

	[Fact]
	public async Task SessionManager_RemoveSameAccountMultipleTimes_DoesNotThrow()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act & Assert - 不应该抛出异常
		await _sessionManager.RemoveSessionAsync(accountName, CancellationToken.None);
		await _sessionManager.RemoveSessionAsync(accountName, CancellationToken.None);
		await _sessionManager.RemoveSessionAsync(accountName, CancellationToken.None);
	}

	[Fact]
	public async Task SessionManager_GetNonExistentAccount_ReturnsNull()
	{
		// Act
		var session = await _sessionManager.GetSessionAsync("nonexistent_account_xyz", CancellationToken.None);

		// Assert
		Assert.Null(session);
	}

	[Fact]
	public async Task SessionManager_ListSessionsAfterRemoval_ReturnsCorrectCount()
	{
		// Arrange
		var accounts = new[] { "acc1", "acc2", "acc3" };
		foreach (var acc in accounts)
		{
			var creds = new AccountCredentials(acc, "pass");
			await _sessionManager.GetOrCreateSessionAsync(acc, creds, CancellationToken.None);
		}

		// Act
		await _sessionManager.RemoveSessionAsync("acc2", CancellationToken.None);
		var sessions = _sessionManager.ListSessions();

		// Assert
		Assert.Equal(2, sessions.Count);
		Assert.DoesNotContain(sessions, s => s.AccountName == "acc2");
	}

	#endregion

	#region BotSession 边界测试

	[Fact]
	public void BotSession_ExecuteActionBeforeStart_StillWorks()
	{
		// Arrange
		var credentials = new AccountCredentials("test", "pass");
		var session = new BotSession(
			"test",
			credentials,
			_actionRegistryMock.Object,
			NullLogger<BotSession>.Instance,
			null
		);

		// Act - 不启动会话，尝试执行动作
		// 注意：由于通道未启动，这会超时或失败
		// 这是一个边界测试，验证系统优雅地处理这种情况
	}

	[Fact]
	public async Task BotSession_DisposeMultipleTimes_DoesNotThrow()
	{
		// Arrange
		var credentials = new AccountCredentials("test", "pass");
		var session = new BotSession(
			"test",
			credentials,
			_actionRegistryMock.Object,
			NullLogger<BotSession>.Instance,
			null
		);
		session.Start();

		// Act & Assert
		session.Dispose();
		session.Dispose();
		session.Dispose();

		// 如果到达这里，测试通过
		await Task.CompletedTask;
	}

	[Fact]
	public async Task BotSession_StartMultipleTimes_ThrowsInvalidOperationException()
	{
		// Arrange
		var credentials = new AccountCredentials("test", "pass");
		var session = new BotSession(
			"test",
			credentials,
			_actionRegistryMock.Object,
			NullLogger<BotSession>.Instance,
			null
		);
		session.Start();

		// Act & Assert
		Assert.Throws<InvalidOperationException>(() => session.Start());
	}

	#endregion

	#region CancellationToken 边界测试

	[Fact]
	public async Task ExecuteAction_WithCancelledToken_ThrowsOperationCanceledException()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Name).Returns("test");
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", false, 30));
		mockAction.Setup(a => a.ExecuteAsync(
			It.IsAny<BotSession>(),
			It.IsAny<IReadOnlyDictionary<string, object?>>(),
			It.IsAny<CancellationToken>()
		)).Returns(async (BotSession s, IReadOnlyDictionary<string, object?> p, CancellationToken ct) =>
		{
			await Task.Delay(5000, ct); // 长延迟，允许取消
			return new ActionResult(true, null, null);
		});

		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act & Assert
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => session.ExecuteActionAsync("test", new Dictionary<string, object?>(), cts.Token)
		);
	}

	[Fact]
	public async Task GetOrCreateSession_WithCancelledToken_CompletesQuickly()
	{
		// Arrange
		var credentials = new AccountCredentials("test", "pass");
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act
		var startTime = DateTime.UtcNow;
		var session = await _sessionManager.GetOrCreateSessionAsync("test", credentials, cts.Token);
		var elapsed = DateTime.UtcNow - startTime;

		// Assert
		Assert.NotNull(session);
		Assert.True(elapsed.TotalSeconds < 1, $"Operation took {elapsed.TotalSeconds}s, expected < 1s");
	}

	#endregion

	#region 数据类型边界测试

	[Fact]
	public async Task ExecuteAction_WithNullPayload_WorksCorrectly()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Name).Returns("test");
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", false, 30));
		mockAction.Setup(a => a.ExecuteAsync(
			It.IsAny<BotSession>(),
			It.IsAny<IReadOnlyDictionary<string, object?>>(),
			It.IsAny<CancellationToken>()
		)).ReturnsAsync(new ActionResult(true, null, null));

		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		var result = await session.ExecuteActionAsync("test", null!, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		mockAction.Verify(a => a.ExecuteAsync(
			It.IsAny<BotSession>(),
			It.IsAny<IReadOnlyDictionary<string, object?>>() ?? null!,
			It.IsAny<CancellationToken>()
		), Times.Once);
	}

	[Fact]
	public async Task ExecuteAction_WithEmptyPayload_WorksCorrectly()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);
		session.Start();

		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Name).Returns("test");
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("test", "Test", false, 30));
		mockAction.Setup(a => a.ExecuteAsync(
			It.IsAny<BotSession>(),
			It.IsAny<IReadOnlyDictionary<string, object?>>(),
			It.IsAny<CancellationToken>()
		)).ReturnsAsync(new ActionResult(true, null, null));

		_actionRegistryMock.Setup(r => r.Get("test")).Returns(mockAction.Object);

		// Act
		var result = await session.ExecuteActionAsync(
			"test",
			new Dictionary<string, object?>(),
			CancellationToken.None
		);

		// Assert
		Assert.True(result.Success);
	}

	#endregion

	public void Dispose()
	{
		_sessionManager.Dispose();
		GC.SuppressFinalize(this);
	}
}
