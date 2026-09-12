using Vapor.Steam.Core.Models;

namespace Vapor.Plugins.MarketWatch;

/// <summary>A registered price watch with its alerting state.</summary>
public sealed class WatchEntry
{
	public WatchEntry(uint appId, decimal thresholdPercent, string country, string currency)
	{
		AppId = appId;
		ThresholdPercent = thresholdPercent;
		Country = country;
		Currency = currency;
	}

	/// <summary>The watched Steam AppID.</summary>
	public uint AppId { get; }

	/// <summary>Alert when the price moves at least this many percent from the baseline.</summary>
	public decimal ThresholdPercent { get; set; }

	/// <summary>Steam store country code used for price lookups (e.g. "us").</summary>
	public string Country { get; set; }

	/// <summary>Currency captured with the baseline price (informational; Steam may return a different code).</summary>
	public string Currency { get; set; }

	/// <summary>Price the current threshold is measured against; null until the first successful poll.</summary>
	public decimal? BaselinePrice { get; set; }

	/// <summary>Last successfully observed price; null until the first successful poll.</summary>
	public decimal? LastPrice { get; set; }

	/// <summary>Last time a poll fetched a price for this entry (UTC).</summary>
	public DateTime? LastCheckedAt { get; set; }

	/// <summary>Times an alert fired for this entry since registration.</summary>
	public int AlertCount { get; set; }
}

/// <summary>A fired price alert.</summary>
public sealed record PriceAlert(
	uint AppId,
	string Country,
	decimal BaselinePrice,
	decimal NewPrice,
	decimal ChangePercent,
	decimal ThresholdPercent,
	string Currency,
	DateTime CheckedAt
);

/// <summary>Result of recording an observed price for a watch entry.</summary>
public enum PriceRecordOutcome
{
	/// <summary>No watch registered for the app id.</summary>
	UnknownWatch,

	/// <summary>The fetch failed (null price); entry state unchanged except the miss counter.</summary>
	FetchFailed,

	/// <summary>First observation for this entry: recorded as the alerting baseline.</summary>
	BaselineRecorded,

	/// <summary>Observed within the threshold; last-price state updated.</summary>
	Recorded,

	/// <summary>Observed beyond the threshold: an alert fired and the baseline was reset.</summary>
	AlertFired
}

/// <summary>
/// Thread-safe store of watch entries plus the threshold evaluation. The evaluator uses a
/// moving baseline: an alert fires when the observed price differs from the baseline by at
/// least the entry's threshold percent; the baseline then resets to the observed price so
/// steady prices do not re-alert on every poll.
/// </summary>
public sealed class MarketWatchStore
{
	private readonly object _gate = new();
	private readonly Dictionary<uint, WatchEntry> _watches = new();

	/// <summary>Registers a watch; returns false when one already exists for the app id.</summary>
	public bool Add(uint appId, decimal thresholdPercent, string country, string currency)
	{
		lock (_gate)
		{
			if (_watches.ContainsKey(appId))
			{
				return false;
			}

			_watches[appId] = new WatchEntry(appId, thresholdPercent, country, currency);
			return true;
		}
	}

	/// <summary>Removes a watch; returns false when none was registered.</summary>
	public bool Remove(uint appId)
	{
		lock (_gate)
		{
			return _watches.Remove(appId);
		}
	}

	/// <summary>Current watch list (entry copies are live objects; callers should treat them as read-only).</summary>
	public IReadOnlyList<WatchEntry> Snapshot()
	{
		lock (_gate)
		{
			return _watches.Values.OrderBy(static w => w.AppId).ToArray();
		}
	}

	/// <summary>Total registered watches.</summary>
	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _watches.Count;
			}
		}
	}

	/// <summary>
	/// Records an observed price and evaluates the threshold. Returns the fired alert
	/// (when <see cref="PriceRecordOutcome.AlertFired"/>) plus the outcome.
	/// </summary>
	public (PriceRecordOutcome Outcome, PriceAlert? Alert) RecordPrice(
		uint appId, PriceOverview? price, DateTime checkedAt)
	{
		lock (_gate)
		{
			if (!_watches.TryGetValue(appId, out var entry))
			{
				return (PriceRecordOutcome.UnknownWatch, null);
			}

			if (price?.Final is not decimal observed)
			{
				return (PriceRecordOutcome.FetchFailed, null);
			}

			entry.LastCheckedAt = checkedAt;

			if (entry.BaselinePrice is not decimal baseline)
			{
				entry.BaselinePrice = observed;
				entry.LastPrice = observed;
				entry.Currency = price.Currency;
				return (PriceRecordOutcome.BaselineRecorded, null);
			}

			entry.LastPrice = observed;

			var changePercent = CalculateChangePercent(baseline, observed);
			if (Math.Abs(changePercent) < entry.ThresholdPercent)
			{
				return (PriceRecordOutcome.Recorded, null);
			}

			entry.BaselinePrice = observed;
			entry.AlertCount++;
			return (PriceRecordOutcome.AlertFired, new PriceAlert(
				AppId: appId,
				Country: entry.Country,
				BaselinePrice: baseline,
				NewPrice: observed,
				ChangePercent: changePercent,
				ThresholdPercent: entry.ThresholdPercent,
				Currency: price.Currency,
				CheckedAt: checkedAt));
		}
	}

	/// <summary>Percentage change from baseline to observed (negative = price drop).</summary>
	internal static decimal CalculateChangePercent(decimal baseline, decimal observed)
	{
		if (baseline == 0)
		{
			return 0;
		}

		return decimal.Round((observed - baseline) / baseline * 100m, 2);
	}
}
