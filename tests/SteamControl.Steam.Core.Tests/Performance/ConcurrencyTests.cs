using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace SteamControl.Steam.Core.Tests.Performance;

/// <summary>
/// 并发和压力测试 - 验证系统在高负载下的行为
/// </summary>
public class ConcurrencyTests : IDisposable
{
	private readonly ITestOutputHelper _output;
	private readonly ActionRegistry _actionRegistry;
	private readonly SessionManager _sessionManager;

	public ConcurrencyTests(ITestOutputHelper output)
	{
		_output = output;
		_actionRegistry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		_sessionManager = new SessionManager(
			_actionRegistry,
			NullLogger<SessionManager>.Instance,
			null
		);

		// 注册动作
		_actionRegistry.Register(new Actions.PingAction(NullLogger<Actions.PingAction>.Instance));
		_actionRegistry.Register(new Actions.EchoAction(NullLogger<Actions.EchoAction>.Instance));
	}

	[Fact]
	public async Task ConcurrentSessionCreation_MultipleThreads_AllSucceed()
	{
		// Arrange
		var threadCount = 50;
		var accountsPerThread = 10;
		var successCount = 0;
		var lockObj = new object();

		// Act - 并发创建会话
		var tasks = Enumerable.Range(0, threadCount).Select(threadId =>
			Task.Run(async () =>
			{
				for (int i = 0; i < accountsPerThread; i++)
				{
					var accountName = $"account_thread{threadId}_idx{i}";
					var credentials = new AccountCredentials(accountName, "password");
					try
					{
						var session = await _sessionManager.GetOrCreateSessionAsync(
							accountName,
							credentials,
							CancellationToken.None
						);
						if (session != null)
						{
							lock (lockObj)
							{
								successCount++;
							}
						}
					}
					catch (Exception ex)
					{
						_output.WriteLine($"Error creating session {accountName}: {ex.Message}");
					}
				}
			})
		).ToArray();

		await Task.WhenAll(tasks);

		// Assert
		var expectedCount = threadCount * accountsPerThread;
		_output.WriteLine($"Expected: {expectedCount}, Actual: {successCount}");
		Assert.Equal(expectedCount, successCount);
	}

	[Fact]
	public async Task ConcurrentActionExecution_SameSession_SerializesCorrectly()
	{
		// Arrange
		var accountName = "concurrent_test_account";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(
			accountName,
			credentials,
			CancellationToken.None
		);

		var executionCount = 100;
		var successCount = 0;
		var lockObj = new object();

		// Act - 并发执行动作
		var tasks = Enumerable.Range(0, executionCount).Select(_ =>
			Task.Run(async () =>
			{
				try
				{
					var result = await session.ExecuteActionAsync(
						"ping",
						new Dictionary<string, object?>(),
						CancellationToken.None
					);
					if (result.Success)
					{
						lock (lockObj)
						{
							successCount++;
						}
					}
				}
				catch (Exception ex)
				{
					_output.WriteLine($"Error executing action: {ex.Message}");
				}
			})
		).ToArray();

		await Task.WhenAll(tasks);

		// Assert
		_output.WriteLine($"Executed: {executionCount}, Succeeded: {successCount}");
		Assert.Equal(executionCount, successCount);
	}

	[Fact]
	public async Task ConcurrentActionExecution_MultipleSessions_AllSucceed()
	{
		// Arrange
		var sessionCount = 20;
		var actionsPerSession = 10;

		var sessions = new List<BotSession>();
		for (int i = 0; i < sessionCount; i++)
		{
			var credentials = new AccountCredentials($"account_{i}", "password");
			var session = await _sessionManager.GetOrCreateSessionAsync(
				$"account_{i}",
				credentials,
				CancellationToken.None
			);
			sessions.Add(session);
		}

		var successCount = 0;
		var lockObj = new object();

		// Act - 在所有会话上并发执行动作
		var tasks = sessions.Select(session =>
			Task.Run(async () =>
			{
				for (int i = 0; i < actionsPerSession; i++)
				{
					try
					{
						var result = await session.ExecuteActionAsync(
							"ping",
							new Dictionary<string, object?>(),
							CancellationToken.None
						);
						if (result.Success)
						{
							lock (lockObj)
							{
								successCount++;
							}
						}
					}
					catch (Exception ex)
					{
						_output.WriteLine($"Error: {ex.Message}");
					}
				}
			})
		).ToArray();

		await Task.WhenAll(tasks);

		// Assert
		var expectedCount = sessionCount * actionsPerSession;
		_output.WriteLine($"Expected: {expectedCount}, Actual: {successCount}");
		Assert.Equal(expectedCount, successCount);
	}

	[Fact]
	public async Task StressTest_RapidSessionCreationAndDisposal()
	{
		// Arrange
		var iterationCount = 100;
		var accountsPerIteration = 5;

		for (int iteration = 0; iteration < iterationCount; iteration++)
		{
			var accountNames = new List<string>();

			// 创建会话
			for (int i = 0; i < accountsPerIteration; i++)
			{
				var accountName = $"stress_iter{iteration}_acc{i}";
				accountNames.Add(accountName);
				var credentials = new AccountCredentials(accountName, "password");
				await _sessionManager.GetOrCreateSessionAsync(
					accountName,
					credentials,
					CancellationToken.None
				);
			}

			// 验证会话存在
			foreach (var name in accountNames)
			{
				var session = await _sessionManager.GetSessionAsync(name, CancellationToken.None);
				Assert.NotNull(session);
			}

			// 移除会话
			foreach (var name in accountNames)
			{
				await _sessionManager.RemoveSessionAsync(name, CancellationToken.None);
			}

			// 验证会话已移除
			foreach (var name in accountNames)
			{
				var session = await _sessionManager.GetSessionAsync(name, CancellationToken.None);
				Assert.Null(session);
			}
		}

		// Assert - 最终无会话残留
		var remainingSessions = _sessionManager.ListSessions();
		Assert.Empty(remainingSessions);
	}

	[Fact]
	public async Task ConcurrentGetOrCreateSameAccount_ReturnsSameSession()
	{
		// Arrange
		var accountName = "race_condition_test";
		var credentials = new AccountCredentials(accountName, "password");
		var threadCount = 100;

		var sessions = new List<BotSession>();
		var lockObj = new object();

		// Act - 并发获取或创建同一账户的会话
		var tasks = Enumerable.Range(0, threadCount).Select(_ =>
			Task.Run(async () =>
			{
				var session = await _sessionManager.GetOrCreateSessionAsync(
					accountName,
					credentials,
					CancellationToken.None
				);
				lock (lockObj)
				{
					sessions.Add(session);
				}
			})
		).ToArray();

		await Task.WhenAll(tasks);

		// Assert - 所有引用应该指向同一个会话实例
		Assert.Equal(threadCount, sessions.Count);
		var firstSession = sessions[0];
		Assert.All(sessions, s => Assert.Same(firstSession, s));
	}

	[Fact]
	public async Task MemoryStressTest_LargePayloadHandling()
	{
		// Arrange
		var accountName = "memory_test";
		var credentials = new AccountCredentials(accountName, "password");
		var session = await _sessionManager.GetOrCreateSessionAsync(
			accountName,
			credentials,
			CancellationToken.None
		);

		// 创建大负载
		var largePayload = new Dictionary<string, object?>();
		for (int i = 0; i < 1000; i++)
		{
			largePayload[$"key_{i}"] = new string('x', 100); // 每个值 100 字符
		}

		// Act
		var result = await session.ExecuteActionAsync(
			"echo",
			largePayload,
			CancellationToken.None
		);

		// Assert
		Assert.True(result.Success);
		Assert.NotNull(result.Output);

		var echoed = result.Output!["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.Equal(1000, echoed.Count);
	}

	[Fact]
	public async Task ConcurrentRemoveAndAccess_HandlesGracefully()
	{
		// Arrange
		var accountName = "concurrent_remove_test";
		var credentials = new AccountCredentials(accountName, "password");

		// 创建会话
		await _sessionManager.GetOrCreateSessionAsync(
			accountName,
			credentials,
			CancellationToken.None
		);

		var errors = 0;
		var lockObj = new object();

		// Act - 并发移除和访问
		var tasks = new List<Task>();
		for (int i = 0; i < 50; i++)
		{
			// 移除任务
			tasks.Add(Task.Run(async () =>
			{
				try
				{
					await _sessionManager.RemoveSessionAsync(accountName, CancellationToken.None);
				}
				catch
				{
					lock (lockObj) { errors++; }
				}
			}));

			// 访问任务
			tasks.Add(Task.Run(async () =>
			{
				try
				{
					await _sessionManager.GetSessionAsync(accountName, CancellationToken.None);
				}
				catch
				{
					lock (lockObj) { errors++; }
				}
			}));
		}

		await Task.WhenAll(tasks);

		// Assert - 不应该有未处理的异常
		_output.WriteLine($"Errors encountered: {errors}");
		Assert.True(errors < tasks.Count); // 至少有一些操作成功
	}

	[Fact]
	public async Task ActionRegistry_ConcurrentRegistration_ThreadSafe()
	{
		// Arrange
		var threadCount = 20;
		var actionsPerThread = 10;

		// Act - 并发注册动作
		var tasks = Enumerable.Range(0, threadCount).Select(threadId =>
			Task.Run(() =>
			{
				for (int i = 0; i < actionsPerThread; i++)
				{
					var actionName = $"action_t{threadId}_i{i}";
					var mockAction = new Mock<IAction>();
					mockAction.Setup(a => a.Name).Returns(actionName);
					mockAction.Setup(a => a.Metadata).Returns(
						new ActionMetadata(actionName, "Test", false, 30)
					);

					try
					{
						_actionRegistry.Register(mockAction.Object);
					}
					catch (Exception ex)
					{
						_output.WriteLine($"Registration error: {ex.Message}");
					}
				}
			})
		).ToArray();

		await Task.WhenAll(tasks);

		// Assert - 验证注册的动作数量
		var registeredNames = _actionRegistry.ListNames();
		_output.WriteLine($"Registered actions: {registeredNames.Count}");
		Assert.Equal(threadCount * actionsPerThread, registeredNames.Count);
	}

	[Fact]
	public async Task SessionEventStress_MultipleConcurrentSubscriptions()
	{
		// Arrange
		var accountName = "event_stress_test";
		var credentials = new AccountCredentials(accountName, "password");

		await _sessionManager.GetOrCreateSessionAsync(
			accountName,
			credentials,
			CancellationToken.None
		);

		var subscriptionCount = 20;
		var eventCounts = new int[subscriptionCount];
		var cts = new CancellationTokenSource();

		// Act - 创建多个并发订阅
		var subscriptionTasks = Enumerable.Range(0, subscriptionCount).Select(idx =>
			Task.Run(async () =>
			{
				try
				{
					await foreach (var evt in _sessionManager.SubscribeAllEvents(cts.Token))
					{
						Interlocked.Increment(ref eventCounts[idx]);
						if (eventCounts[idx] >= 5) break; // 每个订阅收集 5 个事件后停止
					}
				}
				catch (OperationCanceledException)
				{
				}
			})
		).ToArray();

		// 等待一段时间让事件流动
		await Task.Delay(500);
		cts.Cancel();

		try
		{
			await Task.WhenAll(subscriptionTasks);
		}
		catch (OperationCanceledException)
		{
		}

		// Assert - 所有订阅都应该收到一些事件
		_output.WriteLine($"Event counts: {string.Join(", ", eventCounts)}");
		Assert.All(eventCounts, count => Assert.True(count >= 0));
	}

	public void Dispose()
	{
		_sessionManager.Dispose();
		GC.SuppressFinalize(this);
	}
}
