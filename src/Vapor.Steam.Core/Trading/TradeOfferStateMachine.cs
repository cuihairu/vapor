using Vapor.Steam.Core.Models;

namespace Vapor.Steam.Core.Trading;

/// <summary>
/// State machine describing which operations are legal for a <see cref="TradeOffer"/>
/// in its current state, mirroring Steam's trade offer lifecycle:
///
/// Active/CreatedNeedsConfirmation → (accept) → Accepted/InEscrow
/// Active (received) → (decline) → Declined
/// Active/CreatedNeedsConfirmation (sent) → (cancel) → Canceled
/// any non-terminal state → (expire/invalid) → Expired/InvalidItems (server side)
/// </summary>
public static class TradeOfferStateMachine
{
	/// <summary>Base offset between a 64-bit SteamID and its 32-bit account ID.</summary>
	public const ulong SteamId64Base = 76561197960265728;

	/// <summary>
	/// Converts a 64-bit SteamID to its 32-bit account ID.
	/// </summary>
	public static uint ToAccountId(ulong steamId64)
	{
		return steamId64 >= SteamId64Base ? (uint)(steamId64 - SteamId64Base) : 0;
	}

	/// <summary>
	/// Converts a 32-bit account ID to a 64-bit SteamID.
	/// </summary>
	public static ulong ToSteamId64(uint partnerId)
	{
		return SteamId64Base + partnerId;
	}

	/// <summary>
	/// Whether the given state is terminal (no further operations are possible).
	/// </summary>
	public static bool IsTerminal(TradeOfferState state)
	{
		return state is TradeOfferState.Accepted
			or TradeOfferState.Countered
			or TradeOfferState.Expired
			or TradeOfferState.Declined
			or TradeOfferState.InvalidItems
			or TradeOfferState.Canceled
			or TradeOfferState.CanceledBySecondFactor;
	}

	/// <summary>
	/// Whether the offer's expiration time has passed.
	/// </summary>
	public static bool IsExpired(TradeOffer offer, DateTimeOffset? now = null)
	{
		ArgumentNullException.ThrowIfNull(offer);

		return offer.TimeExpires.HasValue && offer.TimeExpires.Value <= (now ?? DateTimeOffset.UtcNow);
	}

	/// <summary>
	/// Whether the offer can be accepted: it must be a received offer in Active state, not expired.
	/// </summary>
	public static bool CanAccept(TradeOffer offer, DateTimeOffset? now = null)
	{
		ArgumentNullException.ThrowIfNull(offer);

		return !offer.IsOurOffer
			&& offer.State == TradeOfferState.Active
			&& !IsExpired(offer, now);
	}

	/// <summary>
	/// Whether the offer can be declined: it must be a received offer in Active state.
	/// </summary>
	public static bool CanDecline(TradeOffer offer)
	{
		ArgumentNullException.ThrowIfNull(offer);

		return !offer.IsOurOffer && offer.State == TradeOfferState.Active;
	}

	/// <summary>
	/// Whether the offer can be canceled: it must be a sent offer still pending
	/// (Active, or awaiting mobile confirmation).
	/// </summary>
	public static bool CanCancel(TradeOffer offer)
	{
		ArgumentNullException.ThrowIfNull(offer);

		return offer.IsOurOffer
			&& offer.State is TradeOfferState.Active or TradeOfferState.CreatedNeedsConfirmation;
	}

	/// <summary>
	/// Validates that the offer may be accepted. Returns an error message, or null when valid.
	/// When <paramref name="expectedPartnerSteamId"/> is provided, the offer sender must match it.
	/// </summary>
	public static string? ValidateForAccept(TradeOffer offer, ulong? expectedPartnerSteamId = null, DateTimeOffset? now = null)
	{
		ArgumentNullException.ThrowIfNull(offer);

		if (offer.IsOurOffer)
		{
			return $"Trade offer {offer.TradeOfferId} was sent by us and cannot be accepted";
		}

		if (IsTerminal(offer.State))
		{
			return $"Trade offer {offer.TradeOfferId} is in terminal state {offer.State} and cannot be accepted";
		}

		if (offer.State != TradeOfferState.Active)
		{
			return $"Trade offer {offer.TradeOfferId} is in state {offer.State}, only Active offers can be accepted";
		}

		if (IsExpired(offer, now))
		{
			return $"Trade offer {offer.TradeOfferId} expired at {offer.TimeExpires:O}";
		}

		if (expectedPartnerSteamId.HasValue)
		{
			uint expectedAccountId = ToAccountId(expectedPartnerSteamId.Value);
			if (offer.AccountIdOther != expectedAccountId)
			{
				return $"Trade offer {offer.TradeOfferId} is from account {offer.AccountIdOther}, expected {expectedAccountId}";
			}
		}

		return null;
	}

	/// <summary>
	/// Validates that the offer may be declined. Returns an error message, or null when valid.
	/// </summary>
	public static string? ValidateForDecline(TradeOffer offer)
	{
		ArgumentNullException.ThrowIfNull(offer);

		if (offer.IsOurOffer)
		{
			return $"Trade offer {offer.TradeOfferId} was sent by us and cannot be declined (use cancel instead)";
		}

		if (offer.State != TradeOfferState.Active)
		{
			return $"Trade offer {offer.TradeOfferId} is in state {offer.State}, only Active offers can be declined";
		}

		return null;
	}

	/// <summary>
	/// Validates that the offer may be canceled. Returns an error message, or null when valid.
	/// </summary>
	public static string? ValidateForCancel(TradeOffer offer)
	{
		ArgumentNullException.ThrowIfNull(offer);

		if (!offer.IsOurOffer)
		{
			return $"Trade offer {offer.TradeOfferId} was received and cannot be canceled (use decline instead)";
		}

		if (!CanCancel(offer))
		{
			return $"Trade offer {offer.TradeOfferId} is in state {offer.State} and cannot be canceled";
		}

		return null;
	}
}
