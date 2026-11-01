using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class AuthChallengeOutboxTests
{
	[Fact]
	public void Stage_ThenTryGet_ReturnsStampedEvent()
	{
		var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
		var outbox = new AuthChallengeOutbox(time);
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		AuthChallengeEvent? evt = outbox.TryGet("alice");
		Assert.NotNull(evt);
		Assert.Equal("code_provided_2fa", evt!.ChallengeType);
		Assert.Equal("123456", evt.Code);
		Assert.Equal(time.GetUtcNow(), evt.Timestamp);
		Assert.Equal(1, outbox.Count);
	}

	[Fact]
	public void TryGet_BlankAccount_ReturnsFalse()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		Assert.Null(outbox.TryGet(""));
		Assert.Null(outbox.TryGet("   "));
		Assert.Null(outbox.TryGet("missing"));
	}

	[Fact]
	public void TryGet_Expired_RemovesEntryAndReturnsFalse()
	{
		var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
		var outbox = new AuthChallengeOutbox(time);
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		time.Advance(TimeSpan.FromSeconds(120));
		Assert.Null(outbox.TryGet("alice"));
		Assert.Equal(0, outbox.Count);
	}

	[Fact]
	public void Expire_RemovesOnlyExpiredEntries()
	{
		var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
		var outbox = new AuthChallengeOutbox(time);
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "111111"), TimeSpan.FromSeconds(60));
		outbox.Stage(NewEvent("bob", "code_provided_email", "222222"), TimeSpan.FromSeconds(180));

		time.Advance(TimeSpan.FromSeconds(120));
		Assert.Equal(1, outbox.Expire());
		Assert.Null(outbox.TryGet("alice"));
		Assert.NotNull(outbox.TryGet("bob"));
	}

	[Fact]
	public void Acknowledge_RemovesMatchingStagedDelivery()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		outbox.Acknowledge("alice", "code_provided_2fa", "123456");

		Assert.Equal(0, outbox.Count);
		Assert.Null(outbox.TryGet("alice"));
	}

	[Fact]
	public void Acknowledge_Mismatch_KeepsStagedDelivery()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		outbox.Acknowledge("alice", "code_provided_2fa", "999999");
		Assert.Equal(1, outbox.Count);
		outbox.Acknowledge("alice", "code_provided_email", "123456");
		Assert.Equal(1, outbox.Count);
	}

	[Fact]
	public void Stage_ReplacesPreviousDeliveryForSameAccount()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "111111"), TimeSpan.FromSeconds(120));
		outbox.Stage(NewEvent("alice", "code_provided_totp", "222222"), TimeSpan.FromSeconds(120));

		AuthChallengeEvent? evt = outbox.TryGet("alice");
		Assert.NotNull(evt);
		Assert.Equal("code_provided_totp", evt!.ChallengeType);
		Assert.Equal("222222", evt.Code);
	}

	[Fact]
	public void Stage_ZeroTtl_IsImmediatelyExpired()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));

		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.Zero);

		Assert.Equal(1, outbox.Count);
		Assert.Equal(1, outbox.Expire());
		Assert.Equal(0, outbox.Count);
	}

	[Fact]
	public void Stage_OverflowTtl_RemainsAvailable()
	{
		var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
		var outbox = new AuthChallengeOutbox(time);

		// now + ttl exceeds DateTimeOffset.MaxValue; the outbox must clamp rather
		// than throw, keeping the delivery available for the whole TTL window.
		TimeSpan overflowTtl = TimeSpan.FromDays(365 * 100);
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), overflowTtl);

		AuthChallengeEvent? evt = outbox.TryGet("alice");
		Assert.NotNull(evt);
		Assert.Equal(time.GetUtcNow(), evt!.Timestamp);
	}

	[Fact]
	public void TryGet_TrimsAccountName()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		Assert.NotNull(outbox.TryGet("  alice  "));
	}

	[Fact]
	public void Stage_TrimsAccountName()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("  alice  ", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		// TryGet/Acknowledge always trim their lookup key; a staged event whose
		// account name has surrounding whitespace must land on the same trimmed key.
		AuthChallengeEvent? evt = outbox.TryGet("alice");
		Assert.NotNull(evt);
		Assert.Equal("code_provided_2fa", evt!.ChallengeType);
		Assert.Equal("123456", evt.Code);
		Assert.Equal(1, outbox.Count);
	}

	[Fact]
	public void Acknowledge_BlankAccountName_DoesNothing()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.FromSeconds(120));

		outbox.Acknowledge("", "code_provided_2fa", "123456");

		Assert.Equal(1, outbox.Count);
	}

	[Fact]
	public async Task Cleaner_RemovesExpiredDeliveries()
	{
		var outbox = new AuthChallengeOutbox(new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
		outbox.Stage(NewEvent("alice", "code_provided_2fa", "123456"), TimeSpan.Zero);
		using var cleaner = new AuthChallengeOutboxCleaner(outbox, TimeSpan.FromMilliseconds(5));

		await cleaner.StartAsync(CancellationToken.None);
		var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
		while (outbox.Count != 0)
		{
			Assert.True(DateTimeOffset.UtcNow < deadline, "cleaner never removed the expired delivery");
			await Task.Delay(10);
		}

		await cleaner.StopAsync(CancellationToken.None);
		Assert.Equal(0, outbox.Count);
	}

	private static AuthChallengeEvent NewEvent(string accountName, string challengeType, string code) =>
		new(
			Id: Guid.NewGuid().ToString("N"),
			AccountName: accountName,
			ChallengeType: challengeType,
			Message: null,
			Code: code,
			Timestamp: DateTimeOffset.UtcNow,
			JobId: null);

	private sealed class FixedTimeProvider : TimeProvider
	{
		private DateTimeOffset _now;

		public FixedTimeProvider(DateTimeOffset now) => _now = now;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan delta) => _now = _now + delta;
	}
}
