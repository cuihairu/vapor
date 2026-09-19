using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Trading;

public sealed class TradeOfferStateMachineTests
{
	private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData(12345678UL)]
	[InlineData(0UL)]
	[InlineData(uint.MaxValue)]
	public void SteamIdConversion_RoundTrips(ulong rawId64)
	{
		ulong steamId64 = TradeOfferStateMachine.ToSteamId64((uint)rawId64);

		Assert.Equal(TradeOfferStateMachine.SteamId64Base + rawId64, steamId64);
		Assert.Equal((uint)rawId64, TradeOfferStateMachine.ToAccountId(steamId64));
	}

	[Fact]
	public void ToAccountId_WithSubBaseValue_ReturnsZero()
	{
		Assert.Equal(0U, TradeOfferStateMachine.ToAccountId(12345UL));
	}

	[Theory]
	[InlineData(TradeOfferState.Accepted)]
	[InlineData(TradeOfferState.Countered)]
	[InlineData(TradeOfferState.Expired)]
	[InlineData(TradeOfferState.Declined)]
	[InlineData(TradeOfferState.InvalidItems)]
	[InlineData(TradeOfferState.Canceled)]
	[InlineData(TradeOfferState.CanceledBySecondFactor)]
	public void IsTerminal_ForFinishedStates_ReturnsTrue(TradeOfferState state)
	{
		Assert.True(TradeOfferStateMachine.IsTerminal(state));
	}

	[Theory]
	[InlineData(TradeOfferState.Active)]
	[InlineData(TradeOfferState.CreatedNeedsConfirmation)]
	[InlineData(TradeOfferState.InEscrow)]
	public void IsTerminal_ForPendingStates_ReturnsFalse(TradeOfferState state)
	{
		Assert.False(TradeOfferStateMachine.IsTerminal(state));
	}

	private static TradeOffer ReceivedActiveOffer(
		TradeOfferState state = TradeOfferState.Active,
		DateTimeOffset? expires = null,
		uint accountIdOther = 4242) =>
		new()
		{
			TradeOfferId = 100,
			AccountIdOther = accountIdOther,
			IsOurOffer = false,
			State = state,
			TimeCreated = Now.AddHours(-1),
			TimeExpires = expires
		};

	[Fact]
	public void CanAccept_ForActiveReceivedOffer_ReturnsTrue()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddHours(1));

		Assert.True(TradeOfferStateMachine.CanAccept(offer, Now));
	}

	[Fact]
	public void CanAccept_ForSentOffer_ReturnsFalse()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddHours(1)) with { IsOurOffer = true };

		Assert.False(TradeOfferStateMachine.CanAccept(offer, Now));
	}

	[Fact]
	public void CanAccept_ForExpiredOffer_ReturnsFalse()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddMinutes(-1));

		Assert.True(TradeOfferStateMachine.IsExpired(offer, Now));
		Assert.False(TradeOfferStateMachine.CanAccept(offer, Now));
	}

	[Fact]
	public void CanAccept_ForNonActiveState_ReturnsFalse()
	{
		var offer = ReceivedActiveOffer(state: TradeOfferState.Accepted);

		Assert.False(TradeOfferStateMachine.CanAccept(offer, Now));
	}

	[Fact]
	public void ValidateForAccept_WithMatchingPartner_ReturnsNull()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddHours(1), accountIdOther: 4242);
		ulong partnerSteamId = TradeOfferStateMachine.ToSteamId64(4242);

		Assert.Null(TradeOfferStateMachine.ValidateForAccept(offer, partnerSteamId, Now));
	}

	[Fact]
	public void ValidateForAccept_WithMismatchedPartner_ReturnsError()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddHours(1), accountIdOther: 4242);
		ulong partnerSteamId = TradeOfferStateMachine.ToSteamId64(9999);

		string? error = TradeOfferStateMachine.ValidateForAccept(offer, partnerSteamId, Now);

		Assert.NotNull(error);
		Assert.Contains("expected", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ValidateForAccept_WithoutPartnerCheck_SkipsComparison()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddHours(1), accountIdOther: 4242);

		Assert.Null(TradeOfferStateMachine.ValidateForAccept(offer, expectedPartnerSteamId: null, now: Now));
	}

	[Fact]
	public void ValidateForAccept_ForOurOwnOffer_ExplainsAcceptIsImpossible()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddHours(1)) with { IsOurOffer = true };

		string? error = TradeOfferStateMachine.ValidateForAccept(offer, null, Now);

		Assert.NotNull(error);
		Assert.Contains("sent by us", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CanDecline_ForActiveReceivedOffer_ReturnsTrue()
	{
		Assert.True(TradeOfferStateMachine.CanDecline(ReceivedActiveOffer()));
	}

	[Fact]
	public void CanDecline_ForSentOffer_ReturnsFalse()
	{
		var offer = ReceivedActiveOffer() with { IsOurOffer = true };

		Assert.False(TradeOfferStateMachine.CanDecline(offer));
	}

	[Fact]
	public void CanDecline_ForNonActiveState_ReturnsFalse()
	{
		var offer = ReceivedActiveOffer(state: TradeOfferState.Declined);

		Assert.False(TradeOfferStateMachine.CanDecline(offer));
	}

	[Fact]
	public void CanDecline_ForNullOffer_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => TradeOfferStateMachine.CanDecline(null!));
	}

	[Fact]
	public void ValidateForAccept_ForPendingConfirmationState_ExplainsOnlyActiveAccepted()
	{
		var offer = ReceivedActiveOffer(state: TradeOfferState.CreatedNeedsConfirmation, expires: Now.AddHours(1));

		string? error = TradeOfferStateMachine.ValidateForAccept(offer, null, Now);

		Assert.NotNull(error);
		Assert.Contains("only Active offers", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ValidateForAccept_ForExpiredOffer_ReportsExpiry()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddMinutes(-1));

		string? error = TradeOfferStateMachine.ValidateForAccept(offer, null, Now);

		Assert.NotNull(error);
		Assert.Contains("expired", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ValidateForDecline_ForReceivedActiveOffer_ReturnsNull()
	{
		Assert.Null(TradeOfferStateMachine.ValidateForDecline(ReceivedActiveOffer()));
	}

	[Fact]
	public void ValidateForDecline_ForOurOwnOffer_SuggestsCancel()
	{
		var offer = ReceivedActiveOffer() with { IsOurOffer = true };

		string? error = TradeOfferStateMachine.ValidateForDecline(offer);

		Assert.NotNull(error);
		Assert.Contains("cancel", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ValidateForDecline_ForTerminalState_ReturnsError()
	{
		var offer = ReceivedActiveOffer(state: TradeOfferState.Declined);

		Assert.NotNull(TradeOfferStateMachine.ValidateForDecline(offer));
	}

	[Fact]
	public void ValidateForCancel_ForSentActiveOffer_ReturnsNull()
	{
		var offer = ReceivedActiveOffer(expires: Now.AddHours(1)) with { IsOurOffer = true };

		Assert.Null(TradeOfferStateMachine.ValidateForCancel(offer));
	}

	[Fact]
	public void ValidateForCancel_ForSentOfferNeedingConfirmation_ReturnsNull()
	{
		var offer = ReceivedActiveOffer(state: TradeOfferState.CreatedNeedsConfirmation) with { IsOurOffer = true };

		Assert.True(TradeOfferStateMachine.CanCancel(offer));
		Assert.Null(TradeOfferStateMachine.ValidateForCancel(offer));
	}

	[Fact]
	public void ValidateForCancel_ForReceivedOffer_SuggestsDecline()
	{
		string? error = TradeOfferStateMachine.ValidateForCancel(ReceivedActiveOffer());

		Assert.NotNull(error);
		Assert.Contains("decline", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ValidateForCancel_ForAcceptedOffer_ReturnsError()
	{
		var offer = ReceivedActiveOffer(state: TradeOfferState.Accepted) with { IsOurOffer = true };

		Assert.NotNull(TradeOfferStateMachine.ValidateForCancel(offer));
	}
}
