using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Trading;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Trading;

/// <summary>
/// Property-based tests for the pure trade URL parser and state-machine
/// predicates. Complements the example-based TradeUrlParams edge tests with
/// input-domain invariants: malformed input never throws, both 32/64-bit
/// partner forms normalize above the SteamID64 base, URL-encoded tokens
/// round-trip, and the accept/decline predicates stay mutually consistent.
/// </summary>
public sealed class TradeParsingPropertyTests
{
	private const ulong Base = TradeOfferStateMachine.SteamId64Base;

	// Same bounded-offset helper as the protocol round-trip properties: any
	// long collapses into the representable DateTimeOffset range (epoch-based),
	// keeping the default FsCheck seed deterministic.
	private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
	private const long MaxMs = 253402300799999;

	private static DateTimeOffset FromOffset(long offsetMs) =>
		DateTimeOffset.FromUnixTimeMilliseconds(((offsetMs % MaxMs) + MaxMs) % MaxMs);

	private static TradeOffer Offer(bool isOurOffer, TradeOfferState state, DateTimeOffset? expires) =>
		new()
		{
			TradeOfferId = 1,
			AccountIdOther = 4242,
			IsOurOffer = isOurOffer,
			State = state,
			TimeExpires = expires
		};

	// --- TradeUrlParams.TryParse ---

	[Property]
	public void TryParse_AnyInput_NeverThrows_AndSuccessfulIdsStayAboveBase(string? input)
	{
		TradeUrlParams? parsed = TradeUrlParams.TryParse(input!);

		if (parsed is null)
		{
			return;
		}

		// Both partner forms normalize: 32-bit values get the base added,
		// 64-bit values pass through, so a successful parse is always ≥ base.
		Assert.True(parsed.PartnerSteamId >= Base, $"parsed {parsed.PartnerSteamId} below SteamID64 base");
	}

	[Property]
	public void TryParse_32BitPartnerForm_AddsBase(uint partner)
	{
		TradeUrlParams? parsed = TradeUrlParams.TryParse($"https://steamcommunity.com/tradeoffer/new/?partner={partner}");

		Assert.NotNull(parsed);
		Assert.Equal(Base + partner, parsed!.PartnerSteamId);
	}

	[Property]
	public void TryParse_64BitPartnerForm_PassesThrough(ulong raw)
	{
		// Build the 64-bit domain as base + raw (wrapping is fine — the guard
		// skips the rare sub-base values the wrap can produce).
		ulong partner = Base + raw;
		if (partner < Base)
		{
			return;
		}

		TradeUrlParams? parsed = TradeUrlParams.TryParse($"https://steamcommunity.com/tradeoffer/new/?partner={partner}");

		Assert.NotNull(parsed);
		Assert.Equal(partner, parsed!.PartnerSteamId);
	}

	[Property]
	public void TryParse_Token_UrlEncodingRoundTrips(string token)
	{
		// Uri.EscapeDataString is lossy for lone surrogates (same class of
		// values the crypto round-trip properties filter out).
		if (token is null || token.Any(char.IsSurrogate))
		{
			return;
		}

		string url = $"https://steamcommunity.com/tradeoffer/new/?partner=12345678&token={Uri.EscapeDataString(token)}";

		TradeUrlParams? parsed = TradeUrlParams.TryParse(url);

		Assert.NotNull(parsed);
		Assert.Equal(token, parsed!.Token);
	}

	// --- TradeOfferStateMachine predicates ---

	[Property]
	public void IsExpired_NeverForOfferWithoutExpiry(long nowOffsetMs)
	{
		TradeOffer offer = Offer(isOurOffer: false, TradeOfferState.Active, expires: null);

		Assert.False(TradeOfferStateMachine.IsExpired(offer, FromOffset(nowOffsetMs)));
	}

	[Property]
	public void CanAccept_Implies_CanDecline_AndReceivedActiveState(bool isOurOffer, TradeOfferState state, long expiresOffsetMs)
	{
		TradeOffer offer = Offer(isOurOffer, state, FromOffset(expiresOffsetMs));

		if (!TradeOfferStateMachine.CanAccept(offer, FixedNow))
		{
			return;
		}

		// Accept's precondition set is strictly narrower than decline's:
		// accepting implies the offer is a received, Active, unexpired one.
		Assert.True(TradeOfferStateMachine.CanDecline(offer));
		Assert.False(offer.IsOurOffer);
		Assert.Equal(TradeOfferState.Active, offer.State);
	}

	[Property]
	public void ValidateForAccept_WithSelfPartner_AcceptsWhateverCanAccept(
		ulong accountIdOther, bool isOurOffer, TradeOfferState state, long expiresOffsetMs)
	{
		// AccountIdOther is the sender's canonical 64-bit SteamID; sub-base
		// values are folded to 0 by ToAccountId by design (defensive), so the
		// identity only holds on the canonical domain.
		if (accountIdOther < Base)
		{
			return;
		}

		TradeOffer offer = Offer(isOurOffer, state, FromOffset(expiresOffsetMs)) with { AccountIdOther = accountIdOther };

		if (!TradeOfferStateMachine.CanAccept(offer, FixedNow))
		{
			return;
		}

		// When the expected partner equals the actual sender, the partner
		// check must never reject on its own.
		string? error = TradeOfferStateMachine.ValidateForAccept(offer, offer.AccountIdOther, FixedNow);

		Assert.Null(error);
	}
}
