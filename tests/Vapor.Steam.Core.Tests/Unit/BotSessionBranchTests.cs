using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Branch coverage for the session command loop: caller cancellation mid-action,
/// auth-code / 2FA provisioning while the log-on waits for them, and the post-QR
/// log-on failure mappings.
/// </summary>
public sealed class BotSessionBranchTests : IDisposable
{
	private const string Account = "branch_account";

	private readonly Mock<ILogger<BotSession>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<IActionRegistry> _actionRegistryMock = new(MockBehavior.Loose);
	private readonly Mock<ISteamClientManager> _transportMock = new(MockBehavior.Loose);
	private readonly List<(string EventType, string State, string? Message)> _events = [];
	private readonly List<BotSession> _sessions = [];

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}

	private void SetupConnect()
	{
		_transportMock
			.Setup(t => t.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
	}

	private BotSession CreateSession(bool withEventCallback = true, bool qrLogin = false)
	{
		var session = new BotSession(
			Account,
			new AccountCredentials(Account, qrLogin ? string.Empty : "password", QrLogin: qrLogin),
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_transportMock.Object,
			null,
			withEventCallback
				? (_, eventType, state, message) =>
				{
					lock (_events)
					{
						_events.Add((eventType, state, message));
					}

					return Task.CompletedTask;
				}
		: null);
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

	[Fact]
	public async Task ExecuteActionAsync_WhenCallerCancelsMidAction_ReportsCanceled()
	{
		var session = CreateSession();
		var parkedInAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("hang", "Hangs until canceled", RequiresLogin: false, TimeoutSeconds: 60));
		mockAction
			.Setup(a => a.ExecuteAsync(It.IsAny<BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Returns(async (BotSession _, IReadOnlyDictionary<string, object?> _, CancellationToken ct) =>
			{
				parkedInAction.TrySetResult();
				await releaseAction.Task.WaitAsync(ct);
				return new ActionResult(true, null, null);
			});
		_actionRegistryMock.Setup(r => r.Get("hang")).Returns(mockAction.Object);

		using var cts = new CancellationTokenSource();
		var pending = session.ExecuteActionAsync("hang", new Dictionary<string, object?>(), cts.Token);
		await parkedInAction.Task.WaitAsync(TimeSpan.FromSeconds(30)); // deterministically parked

		cts.Cancel();
		releaseAction.TrySetResult();

		// Two unwindings race legitimately: the caller's WaitAsync can observe the
		// cancellation directly (OCE), or the loop can finish unwinding the parked
		// action first and hand back its "canceled" result. Accept both outcomes.
		try
		{
			SessionCommandResult result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
			Assert.False(result.Success);
			Assert.Equal("canceled", result.Error);
		}
		catch (OperationCanceledException)
		{
			// The caller's own wait won the race — equally valid.
		}
	}

	[Fact]
	public async Task LoginAsync_AuthCodeRequiredThenProvided_StagesCodeAndRetriesLogon()
	{
		SetupConnect();
		_transportMock
			.SetupSequence(t => t.LoginAsync(Account, "password", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new SteamAuthCodeRequiredException("email Steam Guard code required"))
			.Returns(Task.CompletedTask);
		var session = CreateSession();

		var first = await session.LoginAsync();

		Assert.False(first.Success);
		Assert.Equal(SessionState.ConnectingWaitAuthCode, session.State);
		await WaitForEventsAsync(e => e.EventType == "auth_code_required" && e.State == "ConnectingWaitAuthCode");

		session.ProvideAuthCode("12345");

		await WaitForEventsAsync(e => e.EventType == "state_changed" && e.State == "Connected");
		Assert.Equal(SessionState.Connected, session.State);
		_transportMock.Verify(t => t.SetAuthCode(Account, "12345"), Times.Once);
		_transportMock.Verify(t => t.LoginAsync(Account, "password", It.IsAny<CancellationToken>()), Times.Exactly(2));
	}

	[Fact]
	public async Task LoginAsync_TwoFactorRequiredThenProvided_StagesCodeAndRetriesLogon()
	{
		SetupConnect();
		_transportMock
			.SetupSequence(t => t.LoginAsync(Account, "password", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new SteamTwoFactorCodeRequiredException("Steam Guard 2FA code required"))
			.Returns(Task.CompletedTask);
		var session = CreateSession();

		var first = await session.LoginAsync();

		Assert.False(first.Success);
		Assert.Equal(SessionState.ConnectingWait2FA, session.State);
		await WaitForEventsAsync(e => e.EventType == "2fa_required" && e.State == "ConnectingWait2FA");

		session.Provide2FACode("ABC123");

		await WaitForEventsAsync(e => e.EventType == "state_changed" && e.State == "Connected");
		Assert.Equal(SessionState.Connected, session.State);
		_transportMock.Verify(t => t.SetTwoFactorCode(Account, "ABC123"), Times.Once);
	}

	[Fact]
	public async Task QrLogin_PostQrLogonNeedsAuthCode_SurfacesAuthCodeNeededEvent()
	{
		SetupQrApproved();
		SetupConnect();
		_transportMock
			.Setup(t => t.LoginAsync(Account, string.Empty, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new SteamAuthCodeRequiredException("post-QR Steam Guard"));
		_transportMock
			.Setup(t => t.UpdateLogOnDetailsAsync(Account, null, QrRefreshToken))
			.Returns(Task.CompletedTask);
		var session = CreateSession(qrLogin: true);

		var result = await session.LoginAsync();

		Assert.False(result.Success);
		Assert.Equal("post-QR Steam Guard", result.Error);
		Assert.Equal(SessionState.ConnectingWaitAuthCode, session.State);
		await WaitForEventsAsync(e => e.EventType == "auth_code_required" && e.Message == "post-QR Steam Guard");
	}

	[Fact]
	public async Task QrLogin_PostQrLogonNeedsTwoFactor_SurfacesTwoFactorEvent()
	{
		SetupQrApproved();
		SetupConnect();
		_transportMock
			.Setup(t => t.LoginAsync(Account, string.Empty, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new SteamTwoFactorCodeRequiredException("post-QR 2FA"));
		_transportMock
			.Setup(t => t.UpdateLogOnDetailsAsync(Account, null, QrRefreshToken))
			.Returns(Task.CompletedTask);
		var session = CreateSession(qrLogin: true);

		var result = await session.LoginAsync();

		Assert.False(result.Success);
		Assert.Equal(SessionState.ConnectingWait2FA, session.State);
		await WaitForEventsAsync(e => e.EventType == "2fa_required" && e.Message == "post-QR 2FA");
	}

	[Fact]
	public async Task QrLogin_PostQrLogonCanceledBySessionShutdown_ReportsCanceled()
	{
		SetupQrApproved();
		// The session-level token (linked into the transport call) is what the OCE
		// filters check: bridge it so disposing the session cancels the parked call.
		var parkedInLogon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_transportMock
			.Setup(t => t.LoginAsync(Account, string.Empty, It.IsAny<CancellationToken>()))
			.Returns((string _, string _, CancellationToken ct) =>
			{
				var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				ct.Register(() => parked.TrySetCanceled(ct));
				parkedInLogon.TrySetResult(); // the loop is now parked inside the post-QR logon
				return parked.Task;
			});
		_transportMock
			.Setup(t => t.UpdateLogOnDetailsAsync(Account, null, QrRefreshToken))
			.Returns(Task.CompletedTask);
		var session = CreateSession(qrLogin: true);

		var pending = session.LoginAsync();
		// Wait for the transport call itself rather than the qr_required event:
		// RaiseEventCallback hands delivery to Task.Run, which a starved CI thread
		// pool can delay for seconds — the parked call is the deterministic proof.
		// The budget only covers pool scheduling of the login chain, so be generous.
		await parkedInLogon.Task.WaitAsync(TimeSpan.FromSeconds(30));
		session.Dispose();
		_sessions.Remove(session);

		var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task QrLogin_PostQrLogonThrows_FailsWithFatalError()
	{
		SetupQrApproved();
		_transportMock
			.Setup(t => t.LoginAsync(Account, string.Empty, It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("logon pipeline exploded"));
		_transportMock
			.Setup(t => t.UpdateLogOnDetailsAsync(Account, null, QrRefreshToken))
			.Returns(Task.CompletedTask);
		var session = CreateSession(qrLogin: true);

		var result = await session.LoginAsync();

		Assert.False(result.Success);
		Assert.Equal("logon pipeline exploded", result.Error);
		Assert.Equal(SessionState.FatalError, session.State);
	}

	[Fact]
	public async Task QrLogin_BeginChallengeCanceledBySessionShutdown_ReportsCanceled()
	{
		// The session-level token (linked into the transport call) is what the OCE
		// filters check: bridge it so disposing the session cancels the parked call.
		var parkedInChallenge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.Returns((string _, Action<string> _, CancellationToken ct) =>
			{
				var parked = new TaskCompletionSource<QrLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
				ct.Register(() => parked.TrySetCanceled(ct));
				parkedInChallenge.TrySetResult(); // the loop is now parked inside the challenge
				return parked.Task;
			});
		var session = CreateSession(qrLogin: true);

		var pending = session.LoginAsync();
		await parkedInChallenge.Task.WaitAsync(TimeSpan.FromSeconds(30)); // deterministically parked
		session.Dispose();
		_sessions.Remove(session);

		var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task QrChallenge_WithoutEventCallback_StillPublishesToChannel()
	{
		// The callback-less session must skip the Task.Run fan-out without crashing;
		// the challenge still reaches the event channel for direct subscribers.
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.Returns((string _, Action<string> onUrl, CancellationToken _) =>
			{
				onUrl("https://s.team/q/1/NOCHAT");
				return Task.FromResult(new QrLoginResult(true, null, QrRefreshToken));
			});
		SetupConnect();
		_transportMock
			.Setup(t => t.LoginAsync(Account, string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_transportMock
			.Setup(t => t.UpdateLogOnDetailsAsync(Account, null, QrRefreshToken))
			.Returns(Task.CompletedTask);
		var session = CreateSession(withEventCallback: false, qrLogin: true);

		var received = new List<SessionEvent>();
		using var cts = new CancellationTokenSource();
		var pump = Task.Run(async () =>
		{
			await foreach (var evt in session.SubscribeEvents(cts.Token))
			{
				lock (received)
				{
					received.Add(evt);
				}

				if (evt.Type == SessionEventType.QrCodeNeeded)
				{
					return;
				}
			}
		});

		var result = await session.LoginAsync();

		Assert.True(result.Success);
		var deadline = DateTime.UtcNow.AddSeconds(3);
		while (!received.Any(e => e.Type == SessionEventType.QrCodeNeeded) && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25);
		}

		Assert.Contains(received, e => e.Type == SessionEventType.QrCodeNeeded && e.Message == "https://s.team/q/1/NOCHAT");
		cts.Cancel();
		await pump.WaitAsync(TimeSpan.FromSeconds(3));
	}

	[Fact]
	public async Task QrChallengeRotation_WithoutEventCallback_RepublishesWithoutFanOut()
	{
		// A rotated challenge URL arrives while already in ConnectingWaitQr: the
		// republish path calls RaiseEventCallback unconditionally, and the
		// callback-less session must return from it instead of fanning out.
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.Returns((string _, Action<string> onUrl, CancellationToken _) =>
			{
				onUrl("https://s.team/q/1/NOCHAT");
				onUrl("https://s.team/q/1/ROTATED");
				return Task.FromResult(new QrLoginResult(true, null, QrRefreshToken));
			});
		SetupConnect();
		_transportMock
			.Setup(t => t.LoginAsync(Account, string.Empty, It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_transportMock
			.Setup(t => t.UpdateLogOnDetailsAsync(Account, null, QrRefreshToken))
			.Returns(Task.CompletedTask);
		var session = CreateSession(withEventCallback: false, qrLogin: true);

		var received = new List<SessionEvent>();
		using var cts = new CancellationTokenSource();
		var pump = Task.Run(async () =>
		{
			await foreach (var evt in session.SubscribeEvents(cts.Token))
			{
				lock (received)
				{
					received.Add(evt);
				}

				// The first URL also emits a StateChanged alongside the challenge;
				// count challenges, not raw events.
				if (received.Count(e => e.Type == SessionEventType.QrCodeNeeded) == 2)
				{
					return;
				}
			}
		});

		var result = await session.LoginAsync();

		Assert.True(result.Success);
		await pump.WaitAsync(TimeSpan.FromSeconds(30)); // both challenges delivered
		var urls = received.Where(e => e.Type == SessionEventType.QrCodeNeeded).Select(e => e.Message).ToList();
		Assert.Equal(new[] { "https://s.team/q/1/NOCHAT", "https://s.team/q/1/ROTATED" }, urls);
		cts.Cancel();
	}

	private const string QrRefreshToken = "refresh.jwt";

	private void SetupQrApproved()
	{
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.Returns((string _, Action<string> onUrl, CancellationToken _) =>
			{
				onUrl("https://s.team/q/1/BRANCH");
				return Task.FromResult(new QrLoginResult(true, null, QrRefreshToken));
			});
	}

	[Fact]
	public async Task LoginAsync_WithoutExplicitStart_LazilyStartsCommandLoop()
	{
		SetupConnect();
		_transportMock
			.Setup(t => t.LoginAsync(Account, "password", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		// Construct without Start(): LoginAsync must lazily start the command loop.
		var session = new BotSession(
			Account,
			new AccountCredentials(Account, "password"),
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_transportMock.Object);
		_sessions.Add(session);

		var result = await session.LoginAsync();

		Assert.True(result.Success);
		Assert.Equal(SessionState.Connected, session.State);
		// An explicit Start() after the lazy start is a programming error.
		Assert.Throws<InvalidOperationException>(() => session.Start());
	}

	[Fact]
	public async Task DisconnectAsync_AfterLogin_RunsDisconnectCaseToEndOfLoop()
	{
		SetupConnect();
		_transportMock
			.Setup(t => t.LoginAsync(Account, "password", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_transportMock
			.Setup(t => t.DisconnectAsync())
			.Returns(Task.CompletedTask);
		var session = CreateSession(withEventCallback: false);
		await session.LoginAsync();

		await session.DisconnectAsync();

		Assert.Equal(SessionState.Disconnected, session.State);
	}

	[Fact]
	public async Task CommandLoop_UnexpectedCrash_LogsCriticalAndEntersFatalError()
	{
		var session = CreateSession();
		// A null command makes the loop's switch dereference cmd.Type; the inner
		// handler catch re-throws on the same null, and the crash escapes to the
		// loop's outer catch which flips the session into FatalError.
		var channel = (Channel<SessionCommand>)typeof(BotSession)
			.GetField("_commandChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;
		channel.Writer.TryWrite(null!);

		await WaitForEventsAsync(e => e.EventType == "state_changed" && e.State == "FatalError");
		Assert.Equal(SessionState.FatalError, session.State);
	}

	[Fact]
	public async Task CommandLoop_ExitsCleanly_WhenTheCommandChannelCompletes()
	{
		// Dispose cancels the CTS, so the loop normally ends through the
		// OperationCanceledException arm; completing the channel instead must
		// let the await-foreach drain and finish just as cleanly.
		BotSession session = CreateSession();
		var channel = (Channel<SessionCommand>)typeof(BotSession)
			.GetField("_commandChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;
		channel.Writer.Complete();
		var background = (Task)typeof(BotSession)
			.GetField("_backgroundTask", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;

		await background;

		Assert.Equal(TaskStatus.RanToCompletion, background.Status);
	}

	[Fact]
	public async Task SteamCallbackLoop_TicksThenUnwindsOnCancellation()
	{
		// Driven directly (not via Task.Run) so the loop is guaranteed to enter:
		// one tick of the 100ms poll cadence must land before the cancellation.
		BotSession session = CreateSession();
		var method = typeof(BotSession).GetMethod(
			"RunSteamCallbacksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
		Assert.NotNull(method);
		using var cts = new CancellationTokenSource();
		var loop = (Task)method.Invoke(session, [cts.Token])!;

		await Task.Delay(300, CancellationToken.None); // ≥3 windows of the 100ms cadence
		cts.Cancel();

		await loop.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(TaskStatus.RanToCompletion, loop.Status);
	}

	[Fact]
	public void Dispose_AfterCtsAlreadyDisposed_DoesNotThrow()
	{
		var session = CreateSession(withEventCallback: false);
		var cts = (CancellationTokenSource)typeof(BotSession)
			.GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;
		cts.Dispose();

		// The Dispose guard must swallow the ObjectDisposedException from Cancel().
		session.Dispose();
	}

	// --- raw-command plumbing -----------------------------------------------

	private static Channel<SessionCommand> GetCommandChannel(BotSession session) =>
		(Channel<SessionCommand>)typeof(BotSession)
			.GetField("_commandChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;

	/// <summary>
	/// Writes a hand-built command straight into the channel with no Completion:
	/// the SingleReader unbounded FIFO plus a sentinel command awaited through
	/// the public API act as a barrier — the sentinel can only complete once
	/// every command written before it has been fully processed.
	/// </summary>
	private void WriteRawCommand(BotSession session, SessionCommand command)
	{
		GetCommandChannel(session).Writer.TryWrite(command);
	}

	private Mock<IAction> RegisterAction(string name, bool requiresLogin = false, int? timeoutSeconds = null)
	{
		var action = new Mock<IAction>();
		action.Setup(a => a.Name).Returns(name);
		action.Setup(a => a.Metadata).Returns(new ActionMetadata(name, name + " description", RequiresLogin: requiresLogin, TimeoutSeconds: timeoutSeconds));
		_actionRegistryMock.Setup(r => r.Get(name)).Returns(action.Object);
		return action;
	}

	private void RegisterSentinelAction()
	{
		var sentinel = RegisterAction("sentinel_ok");
		sentinel
			.Setup(a => a.ExecuteAsync(It.IsAny<BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ActionResult(true));
	}

	private async Task RunSentinelAsync(BotSession session)
	{
		var result = await session.ExecuteActionAsync("sentinel_ok", new Dictionary<string, object?>())
			.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.True(result.Success);
	}

	private BotSession CreateStubSession()
	{
		// No transport: _steamClientManager stays null (stub login mode).
		var session = new BotSession(
			Account,
			new AccountCredentials(Account, "password"),
			_actionRegistryMock.Object,
			_loggerMock.Object);
		_sessions.Add(session);
		session.Start();
		return session;
	}

	private static async Task WaitForStateAsync(BotSession session, SessionState expected, int timeoutMs = 30000)
	{
		var start = Environment.TickCount64;
		while (Environment.TickCount64 - start < timeoutMs && session.State != expected)
		{
			await Task.Delay(25);
		}

		Assert.Equal(expected, session.State);
	}

	// --- command-loop arms with Completion-less raw commands ------------------

	[Fact]
	public async Task ExecuteAction_ActionThrowsWithoutCompletion_LoopSurvivesAndRunsSentinel()
	{
		var session = CreateSession();
		var throwing = RegisterAction("boom");
		throwing
			.Setup(a => a.ExecuteAsync(It.IsAny<BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("action exploded"));
		RegisterSentinelAction();

		// Completion=null: the loop's catch must take the ?. no-op arm and keep reading.
		WriteRawCommand(session, new SessionCommand(
			Guid.NewGuid().ToString(), SessionCommandType.ExecuteAction, "boom", null, null, CancellationToken.None));
		await RunSentinelAsync(session);
	}

	[Fact]
	public async Task ExecuteAction_UnknownActionWithoutCompletion_LoopSurvivesAndRunsSentinel()
	{
		var session = CreateSession();
		RegisterSentinelAction();

		WriteRawCommand(session, new SessionCommand(
			Guid.NewGuid().ToString(), SessionCommandType.ExecuteAction, "not_registered", null, null, CancellationToken.None));
		await RunSentinelAsync(session);
	}

	[Fact]
	public async Task ExecuteAction_RequiresLoginWhileDisconnectedWithoutCompletion_LoopSurvivesAndRunsSentinel()
	{
		var session = CreateSession(); // transport wired, never logged in → Disconnected
		RegisterAction("gated", requiresLogin: true);
		RegisterSentinelAction();

		WriteRawCommand(session, new SessionCommand(
			Guid.NewGuid().ToString(), SessionCommandType.ExecuteAction, "gated", null, null, CancellationToken.None));
		await RunSentinelAsync(session);

		// The gated action was refused, not executed; the session never left Disconnected.
		Assert.Equal(SessionState.Disconnected, session.State);
	}

	[Fact]
	public async Task ExecuteAction_SuccessWithoutCompletion_LoopSurvivesAndSentinelSucceeds()
	{
		var session = CreateSession();
		var succeeding = RegisterAction("succeeds");
		succeeding
			.Setup(a => a.ExecuteAsync(It.IsAny<BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ActionResult(true));
		RegisterSentinelAction();

		WriteRawCommand(session, new SessionCommand(
			Guid.NewGuid().ToString(), SessionCommandType.ExecuteAction, "succeeds", null, null, CancellationToken.None));

		// The success path's Completion?. no-op must not disturb the result relay:
		// the sentinel still completes with Success.
		await RunSentinelAsync(session);
	}

	[Fact]
	public async Task ExecuteAction_TimeoutExpiresWithoutCompletion_ReportsAndUnwinds()
	{
		var session = CreateSession();
		var slow = RegisterAction("slow", timeoutSeconds: 1);
		slow
			.Setup(a => a.ExecuteAsync(It.IsAny<BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Returns(async (BotSession _, IReadOnlyDictionary<string, object?> _, CancellationToken ct) =>
			{
				await Task.Delay(TimeSpan.FromSeconds(30), ct);
				return new ActionResult(true);
			});
		RegisterSentinelAction();

		WriteRawCommand(session, new SessionCommand(
			Guid.NewGuid().ToString(), SessionCommandType.ExecuteAction, "slow", null, null, CancellationToken.None));

		// The 1s action budget fires while the action parks on the 30s delay; the
		// timeout arm's Completion?. no-op must still unwind the action so the
		// sentinel (queued behind it) can complete.
		await RunSentinelAsync(session);
	}

	[Fact]
	public async Task ExecuteAction_CommandTokenCanceledInsideActionWithoutCompletion_ReportsCanceled()
	{
		var session = CreateSession();
		using var commandCts = new CancellationTokenSource();
		var selfCanceling = RegisterAction("self_canceling");
		selfCanceling
			.Setup(a => a.ExecuteAsync(It.IsAny<BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Returns((BotSession _, IReadOnlyDictionary<string, object?> _, CancellationToken ct) =>
			{
				// Cancel the command token from inside the action body: the timeout
				// filter stays false (no timeout armed) and the effective-token
				// filter catches. Pre-canceling instead would trip the action-lock
				// wait before the action ever runs.
				commandCts.Cancel();
				ct.ThrowIfCancellationRequested();
				return Task.FromResult(new ActionResult(true));
			});
		RegisterSentinelAction();

		WriteRawCommand(session, new SessionCommand(
			Guid.NewGuid().ToString(), SessionCommandType.ExecuteAction, "self_canceling", null, null, commandCts.Token));
		await RunSentinelAsync(session);
	}

	[Fact]
	public async Task ProvideAuthCode_WithoutTransport_NullShortCircuitRetriesLoginToConnected()
	{
		// Stub-mode session: SetAuthCode's ?. no-ops on the null transport, yet
		// the code still releases the wait and the auto-queued Login reaches
		// Connected through the stub log-on path.
		var session = CreateStubSession();
		typeof(BotSession)
			.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(session, SessionState.ConnectingWaitAuthCode);

		session.ProvideAuthCode("12345");

		await WaitForStateAsync(session, SessionState.Connected);
	}

	[Fact]
	public async Task Provide2FACode_WithoutTransport_NullShortCircuitRetriesLoginToConnected()
	{
		var session = CreateStubSession();
		typeof(BotSession)
			.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(session, SessionState.ConnectingWait2FA);

		session.Provide2FACode("ABC123");

		await WaitForStateAsync(session, SessionState.Connected);
	}

	[Fact]
	public async Task DisconnectCommandWithoutCompletion_LoopExitsRanToCompletion()
	{
		var session = CreateStubSession();
		WriteRawCommand(session, new SessionCommand(
			Guid.NewGuid().ToString(), SessionCommandType.Disconnect, null, null, null, CancellationToken.None));
		var background = (Task)typeof(BotSession)
			.GetField("_backgroundTask", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;

		// The Disconnect case is the loop's only healthy exit: the task completing
		// (rather than only cancelling) proves the Completion?. no-op arm ran.
		await background.WaitAsync(TimeSpan.FromSeconds(30));

		Assert.Equal(SessionState.Disconnected, session.State);
		Assert.Equal(TaskStatus.RanToCompletion, background.Status);
	}
}
