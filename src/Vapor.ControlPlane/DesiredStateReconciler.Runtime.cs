namespace Vapor.ControlPlane;

/// <summary>Per-account runtime record and trade-offer runtime records.</summary>
public sealed partial class DesiredStateReconciler
{
	private sealed class AccountRuntime
	{
		public string? AssignedAgent;
		public string? ActiveJobId;
		public string? ActiveJobAction;
		public DateTimeOffset ActiveJobDispatchedAt;
		public int LoginAttempts;
		public DateTimeOffset NextAttemptAt = DateTimeOffset.MinValue;
		public bool Idling;
		public List<uint>? FarmQueue;
		public uint? FarmingAppId;
		public DateTimeOffset FarmQueueCheckedAt = DateTimeOffset.MinValue;
		public List<uint>? FarmCompletedApps;
		public List<uint>? FarmSkippedApps;
		public DateTimeOffset? FarmAppStartedAt;
		public DateTimeOffset? FarmStatsStartedAt;
		public int? FarmCardsRemaining;
		public int FarmCardsCollected;
		public bool FarmCompletedNotified;
		public List<uint>? BoostUnmetApps;
		public List<uint>? BoostPlayingApps;
		public DateTimeOffset BoostCheckedAt = DateTimeOffset.MinValue;
		public List<PendingGiftOffer>? TradeOffersToAccept;
		public DateTimeOffset TradeOfferCheckedAt = DateTimeOffset.MinValue;
		public PendingGiftOffer? AcceptingOffer;
		public string? StandingSummary;
		public DateTimeOffset StandingCheckedAt = DateTimeOffset.MinValue;
		public bool StandingQuarantined;
		public string? LastCountedFailureKey;
		public int? SpecVersion;
		public DateTimeOffset LastActionAt = DateTimeOffset.MinValue;
		public string? LastAction;
		public string? LastDeviation;
	}

	/// <summary>An incoming gifts-only offer from a whitelisted partner, awaiting its accept dispatch.</summary>
	internal sealed record PendingGiftOffer(ulong OfferId, ulong PartnerSteamId64);

	/// <summary>
	/// One evaluated received offer from a policy scan: "accept" (queued for the
	/// auto-accept dispatch) or "skip" with the red line that disqualified it
	/// (partner_not_whitelisted / has_give_items / our_offer / give_count_unreadable).
	/// </summary>
	internal sealed record TradeOfferDecision(ulong OfferId, ulong PartnerSteamId64, string Decision, string? Reason);
}
