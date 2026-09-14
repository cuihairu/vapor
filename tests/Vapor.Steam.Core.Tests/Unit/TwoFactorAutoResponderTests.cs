using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

public sealed class TwoFactorAutoResponderTests
{
	// base64("12345678901234567890") — the shared RFC 4226 test secret.
	private const string SharedSecret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";

	private readonly Mock<ISessionManager> _sessionManager = new(MockBehavior.Loose);
	private readonly Mock<ICredentialStore> _credentials = new();

	private TwoFactorAutoResponder CreateResponder(TimeSpan? cooldown = null) => new(
		_sessionManager.Object,
		_credentials.Object,
		new SteamTimeSynchronizer(_ => Task.FromResult(1_700_000_000L)),
		NullLogger<TwoFactorAutoResponder>.Instance,
		cooldown);

	private static BotSession CreateSession(string account) => new(
		account,
		new AccountCredentials(account, "pw"),
		new Mock<IActionRegistry>(MockBehavior.Loose).Object,
		NullLogger<BotSession>.Instance,
		null);

	[Fact]
	public async Task WithoutSharedSecret_SkipsAndLeavesChallengeToManualChannel()
	{
		_credentials
			.Setup(c => c.GetSharedSecretAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		var responder = CreateResponder();

		bool answered = await responder.TryAnswerAsync("alice");

		Assert.False(answered);
		Assert.Equal(0, responder.AnsweredCount);
		Assert.Equal(1, responder.SkippedNoSecretCount);
		// No session lookup even happened: the manual channel is the sole handler.
		_sessionManager.Verify(m => m.GetSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task WithSharedSecret_AnswersChallengeLocally()
	{
		BotSession session = CreateSession("alice");
		_credentials
			.Setup(c => c.GetSharedSecretAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SharedSecret);
		_sessionManager
			.Setup(m => m.GetSessionAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(session);
		var responder = CreateResponder();

		bool answered = await responder.TryAnswerAsync("alice");

		Assert.True(answered);
		Assert.Equal(1, responder.AnsweredCount);
	}

	[Fact]
	public async Task WithSharedSecret_ButNoActiveSession_LeavesChallengeUnanswered()
	{
		_credentials
			.Setup(c => c.GetSharedSecretAsync("ghost", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SharedSecret);
		_sessionManager
			.Setup(m => m.GetSessionAsync("ghost", It.IsAny<CancellationToken>()))
			.ReturnsAsync((BotSession?)null);
		var responder = CreateResponder();

		bool answered = await responder.TryAnswerAsync("ghost");

		Assert.False(answered);
		Assert.Equal(0, responder.AnsweredCount);
		// The secret existed, so the no-secret skip counter must stay untouched.
		Assert.Equal(0, responder.SkippedNoSecretCount);
	}

	[Fact]
	public async Task CooldownWindow_BlocksImmediateSecondAnswer()
	{
		BotSession session = CreateSession("alice");
		_credentials
			.Setup(c => c.GetSharedSecretAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SharedSecret);
		_sessionManager
			.Setup(m => m.GetSessionAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(session);
		var responder = CreateResponder(cooldown: TimeSpan.FromSeconds(60));

		Assert.True(await responder.TryAnswerAsync("alice"));
		Assert.False(await responder.TryAnswerAsync("alice"));
		Assert.Equal(1, responder.AnsweredCount);
	}

	[Fact]
	public async Task Cooldown_IsTrackedPerAccount()
	{
		BotSession alice = CreateSession("alice");
		BotSession bob = CreateSession("bob");
		_credentials
			.Setup(c => c.GetSharedSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(SharedSecret);
		_sessionManager
			.Setup(m => m.GetSessionAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(alice);
		_sessionManager
			.Setup(m => m.GetSessionAsync("bob", It.IsAny<CancellationToken>()))
			.ReturnsAsync(bob);
		var responder = CreateResponder(cooldown: TimeSpan.FromSeconds(60));

		Assert.True(await responder.TryAnswerAsync("alice"));
		Assert.True(await responder.TryAnswerAsync("bob"));
		Assert.Equal(2, responder.AnsweredCount);
	}

	[Fact]
	public async Task RunAsync_AnswersOnlyTwoFactorChallenges()
	{
		BotSession session = CreateSession("alice");
		_credentials
			.Setup(c => c.GetSharedSecretAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(SharedSecret);
		_sessionManager
			.Setup(m => m.GetSessionAsync("alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(session);
		_sessionManager
			.Setup(m => m.SubscribeAllEvents(It.IsAny<CancellationToken>()))
			.Returns(EnumerateEventsAsync(
				new SessionEvent(SessionEventType.StateChanged, "alice", SessionState.Connecting, null),
				new SessionEvent(SessionEventType.AuthCodeNeeded, "alice", SessionState.ConnectingWaitAuthCode, "email code"),
				new SessionEvent(SessionEventType.TwoFactorCodeNeeded, "alice", SessionState.ConnectingWait2FA, "2fa")));

		var responder = CreateResponder();
		await responder.RunAsync(CancellationToken.None);

		Assert.Equal(1, responder.AnsweredCount);
	}

	[Fact]
	public async Task RunAsync_HandlesMultipleAccountsWithoutSecrets()
	{
		_credentials
			.Setup(c => c.GetSharedSecretAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((string?)null);
		_sessionManager
			.Setup(m => m.SubscribeAllEvents(It.IsAny<CancellationToken>()))
			.Returns(EnumerateEventsAsync(
				new SessionEvent(SessionEventType.TwoFactorCodeNeeded, "alice", SessionState.ConnectingWait2FA, null),
				new SessionEvent(SessionEventType.TwoFactorCodeNeeded, "bob", SessionState.ConnectingWait2FA, null)));

		var responder = CreateResponder();
		await responder.RunAsync(CancellationToken.None);

		Assert.Equal(0, responder.AnsweredCount);
		Assert.Equal(2, responder.SkippedNoSecretCount);
	}

	private static async IAsyncEnumerable<SessionEvent> EnumerateEventsAsync(params SessionEvent[] events)
	{
		foreach (SessionEvent e in events)
		{
			await Task.Yield();
			yield return e;
		}
	}
}
