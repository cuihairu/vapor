using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// QR sign-in flow on the session: challenge URL surfacing, refresh-token hand-off
/// to the token log-on path, and the failure mappings.
/// </summary>
public sealed class BotSessionQrLoginTests : IDisposable
{
	private const string Account = "qr_account";
	private const string ChallengeUrl = "https://s.team/q/1/ABCDEF";
	private const string RefreshToken = "refresh.jwt";

	private readonly Mock<ILogger<BotSession>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<IActionRegistry> _actionRegistryMock = new(MockBehavior.Loose);
	private readonly Mock<ISteamClientManager> _transportMock = new(MockBehavior.Loose);
	private readonly List<(string EventType, string State, string? Message)> _events = [];
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public async Task QrLogin_Approved_SignsInWithRefreshToken()
	{
		SetupQrResult(onUrl: _ => { });
		var session = CreateSession();
		session.Start();

		var result = await session.LoginAsync();

		Assert.True(result.Success);
		_transportMock.Verify(t => t.ConnectAsync(It.IsAny<CancellationToken>()), Times.Once);
		// Only the refresh token is staged: LogOnDetails.AccessToken must carry the refresh JWT.
		_transportMock.Verify(t => t.UpdateLogOnDetailsAsync(Account, null, RefreshToken), Times.Once);
		_transportMock.Verify(t => t.LoginAsync(Account, string.Empty, It.IsAny<CancellationToken>()), Times.Once);
		_transportMock.Verify(t => t.LoginAsync(Account, It.Is<string>(p => !string.IsNullOrEmpty(p)), It.IsAny<CancellationToken>()), Times.Never);
		await WaitForEventsAsync(e => e.EventType == "state_changed" && e.State == "Connected");
	}

	[Fact]
	public async Task QrLogin_ChallengeUrl_IsSurfacedAsQrRequiredEvent()
	{
		SetupQrResult(onUrl: url => { });
		var session = CreateSession();
		session.Start();

		await session.LoginAsync();

		await WaitForEventsAsync(e => e.EventType == "qr_required" && e.State == "ConnectingWaitQr" && e.Message == ChallengeUrl);
	}

	[Fact]
	public async Task QrLogin_ChallengeUrlRotation_RepublishesEvent()
	{
		var rotatedUrl = "https://s.team/q/1/ROTATED";
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.Returns((string _, Action<string> onUrl, CancellationToken _) =>
			{
				onUrl(ChallengeUrl);
				onUrl(rotatedUrl);
				return Task.FromResult(new QrLoginResult(true, null, RefreshToken));
			});
		var session = CreateSession();
		session.Start();

		await session.LoginAsync();

		await WaitForEventsAsync(events => events.Count(e => e.EventType == "qr_required") >= 2
			&& events.Any(e => e.Message == rotatedUrl));
	}

	[Fact]
	public async Task QrLogin_NotApproved_FailsWithFatalError()
	{
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new QrLoginResult(false, "QR sign-in was not approved in time (challenge expired or timed out)"));
		var session = CreateSession();
		session.Start();

		var result = await session.LoginAsync();

		Assert.False(result.Success);
		Assert.Contains("not approved", result.Error, StringComparison.Ordinal);
		_transportMock.Verify(t => t.LoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		await WaitForEventsAsync(e => e.EventType == "state_changed" && e.State == "FatalError");
	}

	[Fact]
	public async Task QrLogin_ConnectFailure_FailsBeforeChallenge()
	{
		_transportMock
			.Setup(t => t.ConnectAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("Steam client failed to connect"));
		var session = CreateSession();
		session.Start();

		var result = await session.LoginAsync();

		Assert.False(result.Success);
		_transportMock.Verify(t => t.BeginQrLoginAsync(It.IsAny<string>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()), Times.Never);
		await WaitForEventsAsync(e => e.EventType == "state_changed" && e.State == "FatalError");
	}

	[Fact]
	public async Task QrLogin_TransportThrows_MapsToFailure()
	{
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new OperationCanceledException());
		var session = CreateSession();
		session.Start();

		var result = await session.LoginAsync();

		Assert.False(result.Success);
		await WaitForEventsAsync(events => events.Any(e => e.EventType == "state_changed" && e.State is "FatalError" or "Disconnected"));
	}

	[Fact]
	public async Task QrLogin_StubMode_ConnectsDirectly()
	{
		var session = new BotSession(
			Account,
			new AccountCredentials(Account, string.Empty, QrLogin: true),
			_actionRegistryMock.Object,
			_loggerMock.Object,
			steamClientManager: null);
		_sessions.Add(session);
		session.Start();

		var result = await session.LoginAsync();

		Assert.True(result.Success);
		Assert.Equal(SessionState.Connected, session.State);
	}

	private void SetupQrResult(Action<string> onUrl)
	{
		_transportMock
			.Setup(t => t.BeginQrLoginAsync(Account, It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
			.Returns((string _, Action<string> callback, CancellationToken _) =>
			{
				callback(ChallengeUrl);
				onUrl(ChallengeUrl);
				return Task.FromResult(new QrLoginResult(true, null, RefreshToken));
			});
	}

	private BotSession CreateSession()
	{
		var session = new BotSession(
			Account,
			new AccountCredentials(Account, string.Empty, QrLogin: true),
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_transportMock.Object,
			null,
			(_, eventType, state, message) =>
			{
				lock (_events)
				{
					_events.Add((eventType, state, message));
				}

				return Task.CompletedTask;
			});
		_sessions.Add(session);
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

	private async Task WaitForEventsAsync(Func<IReadOnlyList<(string EventType, string State, string? Message)>, bool> predicate, int timeoutMs = 3000)
	{
		var start = Environment.TickCount64;
		while (Environment.TickCount64 - start < timeoutMs)
		{
			lock (_events)
			{
				if (predicate(_events))
				{
					return;
				}
			}

			await Task.Delay(25);
		}

		lock (_events)
		{
			Assert.True(predicate(_events), $"expected event combination not observed; seen: [{string.Join(", ", _events.Select(e => $"{e.EventType}/{e.State}"))}]");
		}
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
