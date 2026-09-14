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

	private async Task WaitForEventsAsync(Func<(string EventType, string State, string? Message), bool> predicate, int timeoutMs = 3000)
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
		var mockAction = new Mock<IAction>();
		mockAction.Setup(a => a.Metadata).Returns(new ActionMetadata("hang", "Hangs until canceled", RequiresLogin: false, TimeoutSeconds: 60));
		mockAction
			.Setup(a => a.ExecuteAsync(It.IsAny<BotSession>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
			.Returns(async (BotSession _, IReadOnlyDictionary<string, object?> _, CancellationToken ct) =>
			{
				await Task.Delay(TimeSpan.FromMinutes(1), ct);
				return new ActionResult(true, null, null);
			});
		_actionRegistryMock.Setup(r => r.Get("hang")).Returns(mockAction.Object);

		var cts = new CancellationTokenSource();
		var pending = session.ExecuteActionAsync("hang", new Dictionary<string, object?>(), cts.Token);
		await Task.Delay(150); // let the action park inside its delay
		cts.Cancel();

		// The same token cancels the caller's wait immediately; the loop's own
		// "canceled" result (and its observer notification) happens in the background.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
		await Task.Delay(200); // let the command loop unwind the parked action
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
		await parkedInLogon.Task.WaitAsync(TimeSpan.FromSeconds(10));
		session.Dispose();
		_sessions.Remove(session);

		var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
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
		await parkedInChallenge.Task.WaitAsync(TimeSpan.FromSeconds(10)); // deterministically parked
		session.Dispose();
		_sessions.Remove(session);

		var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
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
}
