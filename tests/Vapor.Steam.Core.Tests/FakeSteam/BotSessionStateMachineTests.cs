using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.FakeSteam;

/// <summary>
/// 40.7-1 FakeSteam 确定性后端上的会话状态机六场景(登录/断线重连/挑战/超时/错误/受限):
/// 全部传输行为在 arrange 段脚本排定,不依赖时序、不依赖真实 Steam 网络;断言走会话
/// 状态、事件回调与 fake 调用日志三面证据。
///
/// 接口 audit 结论(计划书 P1-D):ISteamClientManager/ISteamTransport seam 足以表达
/// 六类场景(connect 失败/挂起、typed 挑战异常、staged code 重试、QR 挑战回调、token
/// 重登),无需"模拟器扩展"缩小范围;计划书点名的 SteamCallbackSimulator 是零引用的
/// NotImplemented 存根,已删除并由本 fake 取代。两条随场景入册的契约:①受限
/// (EResult.RateLimitExceeded 84)经 seam 无类型化异常,与其它登录失败同归 FatalError
/// (RateLimit 场景钉住);②会话内重连不存在——Disconnect 命令终结命令循环,产品语义
/// 的重连是"会话重建"(RemoveSession + TryRestore 的 token 重登)(Disconnect/Reconnect
/// 两场景钉住)。
/// </summary>
public sealed class BotSessionStateMachineTests : IDisposable
{
	private const string Account = "state_machine_account";

	private readonly FakeSteamClientManager _fake = new();
	private readonly ActionRegistry _registry = new(NullLogger<ActionRegistry>.Instance);
	private readonly List<(string EventType, string State, string? Message)> _events = [];
	private readonly List<BotSession> _sessions = [];

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}

	private BotSession CreateSession(AccountCredentials? credentials = null)
	{
		var session = new BotSession(
			Account,
			credentials ?? new AccountCredentials(Account, "password"),
			_registry,
			NullLogger<BotSession>.Instance,
			_fake,
			eventCallback: (_, eventType, state, message) =>
			{
				lock (_events)
				{
					_events.Add((eventType, state, message));
				}

				return Task.CompletedTask;
			});
		_sessions.Add(session);
		session.Start();
		return session;
	}

	// 30s budget: healthy paths deliver in milliseconds; the budget only covers
	// thread-pool scheduling delays (the coverage CI job's coverlet instrumentation
	// can park event fan-out for seconds — same family 88371dc raised to 30s).
	private async Task WaitForEventsAsync(Func<(string EventType, string State, string? Message), bool> predicate, int timeoutMs = 30000)
	{
		var start = Environment.TickCount64;
		while (Environment.TickCount64 - start < timeoutMs)
		{
			lock (_events)
			{
				if (_events.Any(predicate))
				{
					return;
				}
			}

			await Task.Delay(25);
		}

		lock (_events)
		{
			Assert.True(_events.Any(predicate), $"expected event not observed; seen: [{string.Join(", ", _events.Select(e => $"{e.EventType}/{e.State}"))}]");
		}
	}

	private async Task WaitForAsync(Func<bool> condition, string because, int timeoutMs = 30000)
	{
		var start = Environment.TickCount64;
		while (Environment.TickCount64 - start < timeoutMs)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(25);
		}

		Assert.True(condition(), because);
	}

	// --- 登录 --------------------------------------------------------------------

	[Fact]
	public async Task Login_PasswordLogOn_ConnectsThroughTheFake()
	{
		var session = CreateSession();

		var result = await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(result.Success);
		Assert.Equal(SessionState.Connected, session.State);
		Assert.Equal(1, _fake.ConnectCount);
		var attempt = Assert.Single(_fake.LoginAttempts);
		Assert.Equal(Account, attempt.AccountName);
		Assert.Equal("password", attempt.Password);
		Assert.Null(attempt.AuthCode);
		Assert.Null(attempt.TwoFactorCode);
		Assert.False(attempt.TokenLogOn);
	}

	[Fact]
	public async Task Login_QrChallengeApproved_MintsAStagedTokenLogOn()
	{
		_fake.QueueQrSignIn(["https://fake.steam/challenge"], new QrLoginResult(true, null, "rt-qr"));
		var session = CreateSession(new AccountCredentials(Account, string.Empty, QrLogin: true));

		var result = await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(result.Success);
		Assert.Equal(SessionState.Connected, session.State);
		await WaitForEventsAsync(e => e.EventType == "qr_required" && e.Message == "https://fake.steam/challenge");
		// The minted refresh token crossed the seam via UpdateLogOnDetailsAsync
		// before the token log-on; the journal is the deterministic proof.
		var stage = Assert.Single(_fake.TokenStages);
		Assert.Equal((Account, null, "rt-qr"), (stage.AccountName, stage.AccessToken, stage.RefreshToken));
		var attempt = Assert.Single(_fake.LoginAttempts);
		Assert.True(attempt.TokenLogOn);
		Assert.Equal(string.Empty, attempt.Password);
	}

	// --- 断线重连 ------------------------------------------------------------------

	[Fact]
	public async Task Disconnect_TerminatesTheCommandLoop_FollowUpLoginSurfacesCancellation()
	{
		var session = CreateSession();
		await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(SessionState.Disconnected, session.State);
		Assert.Equal(1, _fake.DisconnectCount);

		// By design a Disconnect command ends the command loop, so an in-session
		// re-login is never processed; with a pre-canceled caller token the
		// unprocessed command surfaces as cancellation deterministically.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => session.LoginAsync(new CancellationToken(canceled: true)));
	}

	[Fact]
	public async Task Reconnect_RebuiltSession_LogsOnWithTheStagedToken()
	{
		// The product's reconnect is a session rebuild (RemoveSession +
		// TryRestoreSessionAsync): a fresh BotSession with token-only credentials.
		var rebuilt = CreateSession(new AccountCredentials(Account, Password: string.Empty, RefreshToken: "rt-reconnect"));

		var result = await rebuilt.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.True(result.Success);
		Assert.Equal(SessionState.Connected, rebuilt.State);
		var stage = Assert.Single(_fake.TokenStages);
		Assert.Equal((Account, null, "rt-reconnect"), (stage.AccountName, stage.AccessToken, stage.RefreshToken));
		var attempt = Assert.Single(_fake.LoginAttempts);
		Assert.True(attempt.TokenLogOn);
		Assert.Equal(string.Empty, attempt.Password);
	}

	// --- 挑战 --------------------------------------------------------------------

	[Fact]
	public async Task Challenge_AuthCodeRequired_ProvidedCodeRetriesAndConnects()
	{
		_fake.QueueLoginOutcomes(FakeLoginOutcome.AuthCodeRequired, FakeLoginOutcome.Success);
		var session = CreateSession();

		var first = await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.False(first.Success);
		Assert.Equal(FakeSteamClientManager.AuthCodeRequiredMessage, first.Error);
		Assert.Equal(SessionState.ConnectingWaitAuthCode, session.State);
		await WaitForEventsAsync(e => e.EventType == "auth_code_required");

		session.ProvideAuthCode("12345");

		// ProvideAuthCode stages the code and auto-issues the retry login command;
		// the fake's journal is the deterministic proof the retry carried it.
		await WaitForAsync(
			() => session.State == SessionState.Connected && _fake.LoginAttempts.Count >= 2,
			"auth code retry never reached Connected");
		Assert.Equal(SessionState.Connected, session.State);
		Assert.Equal("12345", _fake.LoginAttempts[1].AuthCode);
	}

	[Fact]
	public async Task Challenge_TwoFactorRequired_ProvidedCodeRetriesAndConnects()
	{
		_fake.QueueLoginOutcomes(FakeLoginOutcome.TwoFactorRequired, FakeLoginOutcome.Success);
		var session = CreateSession();

		var first = await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.False(first.Success);
		Assert.Equal(FakeSteamClientManager.TwoFactorRequiredMessage, first.Error);
		Assert.Equal(SessionState.ConnectingWait2FA, session.State);
		await WaitForEventsAsync(e => e.EventType == "2fa_required");

		session.Provide2FACode("654321");

		await WaitForAsync(
			() => session.State == SessionState.Connected && _fake.LoginAttempts.Count >= 2,
			"2FA retry never reached Connected");
		Assert.Equal(SessionState.Connected, session.State);
		Assert.Equal("654321", _fake.LoginAttempts[1].TwoFactorCode);
	}

	// --- 超时 --------------------------------------------------------------------

	[Fact]
	public async Task Timeout_CallerCancelsAHungConnect_CancellationSurfacesAndShutdownSettlesFatalError()
	{
		_fake.ParkNextConnect();
		var session = CreateSession();
		using var cts = new CancellationTokenSource();

		var pending = session.LoginAsync(cts.Token);
		await _fake.WhenConnectStarted.WaitAsync(TimeSpan.FromSeconds(30)); // parked deterministically

		cts.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

		// The caller's wait is gone but the loop is still parked inside the fake's
		// connect; the parked call is bound to the session token, so shutdown is
		// what settles it — into FatalError via the generic login failure mapping.
		session.Dispose();
		await WaitForAsync(() => session.State == SessionState.FatalError, "parked connect never settled after shutdown");
	}

	[Fact]
	public async Task Timeout_ActionExceedsItsBudget_ReportsActionTimeout()
	{
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_registry.Register(new ScriptedAction(
			"hang",
			new ActionMetadata("hang", "hangs past its own timeout", TimeoutSeconds: 1),
			async ct =>
			{
				started.TrySetResult();
				await release.Task.WaitAsync(ct);
				return new ActionResult(true, null, null);
			}));
		var session = CreateSession();

		var result = await session.ExecuteActionAsync("hang", new Dictionary<string, object?>())
			.WaitAsync(TimeSpan.FromSeconds(30));

		release.TrySetResult(); // unwind the abandoned action body
		await started.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the body did run
		Assert.False(result.Success);
		Assert.Equal("action timeout", result.Error);
	}

	// --- 错误 --------------------------------------------------------------------

	[Fact]
	public async Task Error_LoginRejected_MapsToFatalErrorWithTheTransportMessage()
	{
		_fake.QueueLoginOutcomes(FakeLoginOutcome.Failure);
		_fake.QueueFailure("Steam login failed: InvalidPassword");
		var session = CreateSession();

		var result = await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.False(result.Success);
		Assert.Equal("Steam login failed: InvalidPassword", result.Error);
		Assert.Equal(SessionState.FatalError, session.State);
		await WaitForEventsAsync(e => e.EventType == "state_changed" && e.State == nameof(SessionState.FatalError));
	}

	[Fact]
	public async Task Error_ConnectRefused_MapsToFatalError()
	{
		_fake.SetConnectFailure(new InvalidOperationException("Steam client failed to connect"));
		var session = CreateSession();

		var result = await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.False(result.Success);
		Assert.Equal("Steam client failed to connect", result.Error);
		Assert.Equal(SessionState.FatalError, session.State);
		Assert.Equal(1, _fake.ConnectCount);
	}

	// --- 受限 --------------------------------------------------------------------

	[Fact]
	public async Task RateLimit_LoginDenied_LandsInFatalErrorCarryingTheRateLimitSignal()
	{
		// Audit contract pinned here: EResult.RateLimitExceeded (84) has no typed
		// exception through the transport seam — the real transport throws the same
		// InvalidOperationException shape as any other non-OK result, so the session
		// layer cannot distinguish 受限 from other errors (both settle FatalError).
		_fake.QueueLoginOutcomes(FakeLoginOutcome.Failure);
		_fake.QueueFailure("Steam login failed: RateLimitExceeded");
		var session = CreateSession();

		var result = await session.LoginAsync().WaitAsync(TimeSpan.FromSeconds(30));

		Assert.False(result.Success);
		Assert.Contains("RateLimitExceeded", result.Error);
		Assert.Equal(SessionState.FatalError, session.State);
	}

	/// <summary>Hand-rolled action so the timeout scenario runs without a mocking library.</summary>
	private sealed class ScriptedAction : IAction
	{
		private readonly Func<CancellationToken, Task<ActionResult>> _body;

		public ScriptedAction(string name, ActionMetadata metadata, Func<CancellationToken, Task<ActionResult>> body)
		{
			Name = name;
			Metadata = metadata;
			_body = body;
		}

		public string Name { get; }

		public ActionMetadata Metadata { get; }

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken) => _body(cancellationToken);
	}
}
