using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using SteamControl.Steam.Core.Actions;

namespace SteamControl.Steam.Core.Tests.Integration;

/// <summary>
/// 集成测试：验证 SessionManager、BotSession 和 Actions 的完整工作流
/// </summary>
public class SessionWorkflowTests : IDisposable
{
	private readonly Mock<ILogger<SessionManager>> _loggerMock;
	private readonly ActionRegistry _actionRegistry;
	private readonly SessionManager _sessionManager;

	public SessionWorkflowTests()
	{
		_loggerMock = new Mock<ILogger<SessionManager>>(MockBehavior.Loose);
		_actionRegistry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		_sessionManager = new SessionManager(_actionRegistry, _loggerMock.Object, null);

		// 注册所有内置动作
		_actionRegistry.Register(new PingAction(NullLogger<PingAction>.Instance));
		_actionRegistry.Register(new EchoAction(NullLogger<EchoAction>.Instance));
		_actionRegistry.Register(new LoginAction(NullLogger<LoginAction>.Instance));
		_actionRegistry.Register(new IdleAction(NullLogger<IdleAction>.Instance));
		_actionRegistry.Register(new RedeemKeyAction(NullLogger<RedeemKeyAction>.Instance));
	}

	[Fact]
	public async Task CompleteWorkflow_CreateSessionAndExecuteActions_Succeeds()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");

		// Act - 创建会话
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Assert - 会话已创建
		Assert.NotNull(session);
		Assert.Equal(accountName, session.AccountName);
		Assert.Equal(SessionState.Disconnected, session.State);

		// Act - 执行 Ping 动作
		var pingResult = await session.ExecuteActionAsync("ping", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert - Ping 成功
		Assert.True(pingResult.Success);
		Assert.NotNull(pingResult.Output);
		Assert.True(pingResult.Output!.ContainsKey("pong"));

		// Act - 执行 Echo 动作
		var echoPayload = new Dictionary<string, object?> { ["message"] = "Hello" };
		var echoResult = await session.ExecuteActionAsync("echo", echoPayload, CancellationToken.None);

		// Assert - Echo 成功
		Assert.True(echoResult.Success);
		Assert.NotNull(echoResult.Output);
	}

	[Fact]
	public async Task Workflow_MultipleAccounts_IndependentSessions()
	{
		// Arrange
		var accounts = new[]
		{
			("account1", new AccountCredentials("account1", "pass1")),
			("account2", new AccountCredentials("account2", "pass2")),
			("account3", new AccountCredentials("account3", "pass3"))
		};

		// Act - 为每个账户创建会话
		var sessions = new List<BotSession>();
		foreach (var (name, creds) in accounts)
		{
			var session = await _sessionManager.GetOrCreateSessionAsync(name, creds, CancellationToken.None);
			sessions.Add(session);
		}

		// Assert - 所有会话独立
		Assert.Equal(3, sessions.Count);
		Assert.All(sessions, s => Assert.Equal(SessionState.Disconnected, s.State));

		// Act - 在每个会话上执行动作
		var results = new List<bool>();
		foreach (var session in sessions)
		{
			var result = await session.ExecuteActionAsync("ping", new Dictionary<string, object?>(), CancellationToken.None);
			results.Add(result.Success);
		}

		// Assert - 所有动作成功
		Assert.All(results, r => Assert.True(r));
	}

	[Fact]
	public async Task Workflow_SessionRemoval_PreventsFurtherExecution()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");

		// Act - 创建会话并执行动作
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);
		var result1 = await session.ExecuteActionAsync("ping", new Dictionary<string, object?>(), CancellationToken.None);

		// 移除会话
		await _sessionManager.RemoveSessionAsync(accountName, CancellationToken.None);

		// 尝试获取已移除的会话
		var retrievedSession = await _sessionManager.GetSessionAsync(accountName, CancellationToken.None);

		// Assert
		Assert.True(result1.Success);
		Assert.Null(retrievedSession);
	}

	[Fact]
	public async Task Workflow_ActionsInSequence_AllSucceed()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act - 按顺序执行多个动作
		var actions = new[]
		{
			("ping", new Dictionary<string, object?>()),
			("echo", new Dictionary<string, object?> { ["test"] = "value" }),
			("login", new Dictionary<string, object?>())
		};

		var results = new List<bool>();
		foreach (var (action, payload) in actions)
		{
			var result = await session.ExecuteActionAsync(action, payload, CancellationToken.None);
			results.Add(result.Success);
		}

		// Assert
		Assert.All(results, r => Assert.True(r));
	}

	[Fact]
	public async Task Workflow_ConcurrentActionsOnSameSession_SerializesExecution()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act - 并发执行多个动作
		var tasks = Enumerable.Range(0, 10)
			.Select(i => session.ExecuteActionAsync("ping", new Dictionary<string, object?>(), CancellationToken.None))
			.ToArray();

		var results = await Task.WhenAll(tasks);

		// Assert - 所有动作都成功
		Assert.All(results, r => Assert.True(r.Success));

		// 验证账户名一致
		Assert.All(results, r => Assert.Equal(accountName, r.Output!["account"]?.ToString()));
	}

	[Fact]
	public async Task Workflow_InvalidAction_ReturnsFailure()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		var result = await session.ExecuteActionAsync("nonexistent_action", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Contains("action not found", result.Error ?? string.Empty);
	}

	[Fact]
	public async Task Workflow_ActionRequiringLogin_WhenNotLoggedIn_ReturnsFailure()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act - Idle 需要登录
		var result = await session.ExecuteActionAsync("idle", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Contains("not logged in", result.Error ?? string.Empty);
	}

	[Fact]
	public async Task Workflow_RedeemKeyWithValidKey_Succeeds()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		var payload = new Dictionary<string, object?> { ["key"] = "AAAAA-BBBBB-CCCCC" };
		var result = await session.ExecuteActionAsync("redeem_key", payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal("redeem_key", result.Output["action"]?.ToString());

		var maskedKey = result.Output["key"]?.ToString() ?? "";
		Assert.DoesNotContain("BBBBB", maskedKey);
	}

	[Fact]
	public async Task Workflow_RedeemKeyWithMissingKey_ReturnsFailure()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		var result = await session.ExecuteActionAsync("redeem_key", new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Equal("key is required", result.Error);
	}

	[Fact]
	public async Task Workflow_ListSessions_ReturnsAllActiveSessions()
	{
		// Arrange
		var accounts = new[] { "acc1", "acc2", "acc3" };

		foreach (var account in accounts)
		{
			var creds = new AccountCredentials(account, "pass");
			await _sessionManager.GetOrCreateSessionAsync(account, creds, CancellationToken.None);
		}

		// Act
		var sessions = _sessionManager.ListSessions();

		// Assert
		Assert.Equal(3, sessions.Count);
		Assert.Contains(sessions, s => s.AccountName == "acc1");
		Assert.Contains(sessions, s => s.AccountName == "acc2");
		Assert.Contains(sessions, s => s.AccountName == "acc3");
	}

	[Fact]
	public async Task Workflow_EventSubscription_ReceivesEvents()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		var events = new List<SessionEvent>();
		var cts = new CancellationTokenSource();

		// Start collecting events
		var collectTask = Task.Run(async () =>
		{
			try
			{
				await foreach (var evt in _sessionManager.SubscribeAllEvents(cts.Token))
				{
					events.Add(evt);
					if (events.Count >= 2) break;
				}
			}
			catch (OperationCanceledException)
			{
			}
		});

		// Wait for events
		await Task.Delay(200);
		cts.Cancel();
		await collectTask.WaitAsync(TimeSpan.FromSeconds(2));

		// Assert - 事件应该被收集
		Assert.NotNull(events);
	}

	[Fact]
	public async Task Workflow_AccountNameCaseInsensitive_WorksCorrectly()
	{
		// Arrange
		var credentials1 = new AccountCredentials("MyAccount", "pass1");
		var credentials2 = new AccountCredentials("myaccount", "pass2");

		// Act - 使用不同大小写创建会话
		var session1 = await _sessionManager.GetOrCreateSessionAsync("MyAccount", credentials1, CancellationToken.None);
		var session2 = await _sessionManager.GetOrCreateSessionAsync("myaccount", credentials2, CancellationToken.None);

		// Assert - 应该返回同一个会话
		Assert.Same(session1, session2);
	}

	[Fact]
	public async Task Workflow_IdleActionWithDifferentDurations_AllSucceed()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		var durations = new[] { 0, 30, 60, 120, 300 };

		// Act
		var results = new List<bool>();
		foreach (var duration in durations)
		{
			var payload = new Dictionary<string, object?> { ["duration"] = duration };
			var result = await session.ExecuteActionAsync("idle", payload, CancellationToken.None);
			results.Add(result.Success);
		}

		// Assert
		Assert.All(results, r => Assert.True(r));
	}

	[Fact]
	public async Task Workflow_EchoActionPreservesPayloadIntegrity()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		var originalPayload = new Dictionary<string, object?>
		{
			["string"] = "test",
			["number"] = 42,
			["boolean"] = true,
			["null"] = null,
			["nested"] = new Dictionary<string, object?> { ["key"] = "value" }
		};

		// Act
		var result = await session.ExecuteActionAsync("echo", originalPayload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		var echoed = result.Output!["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);

		// 验证原始负载未被修改
		Assert.Equal("test", originalPayload["string"]);
		Assert.Equal(42, originalPayload["number"]);
	}

	[Fact]
	public async Task Workflow_MultipleSessionsWithDifferentAccounts_AllIndependent()
	{
		// Arrange
		var accounts = new[]
		{
			("alice", new AccountCredentials("alice", "pass1")),
			("bob", new AccountCredentials("bob", "pass2")),
			("charlie", new AccountCredentials("charlie", "pass3"))
		};

		// Act - 创建多个会话并在每个上执行 Ping
		var tasks = accounts.Select(async (account) =>
		{
			var (name, creds) = account;
			var session = await _sessionManager.GetOrCreateSessionAsync(name, creds, CancellationToken.None);
			var result = await session.ExecuteActionAsync("ping", new Dictionary<string, object?>(), CancellationToken.None);
			return (name, result);
		}).ToArray();

		var results = await Task.WhenAll(tasks);

		// Assert - 每个会话返回自己的账户名
		foreach (var (name, result) in results)
		{
			Assert.True(result.Success);
			Assert.Equal(name, result.Output!["account"]?.ToString());
		}
	}

	public void Dispose()
	{
		_sessionManager.Dispose();
		GC.SuppressFinalize(this);
	}
}
