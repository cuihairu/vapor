using Vapor.Steam.Core.Models;
using Xunit;

namespace Vapor.Plugins.MarketWatch.Tests;

/// <summary>
/// Tests for the watch store and threshold evaluation: baseline recording, moving-baseline
/// alerting and change-percent calculation.
/// </summary>
public sealed class MarketWatchStoreTests
{
	private static readonly DateTime Now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

	private static PriceOverview Price(decimal final, string currency = "USD") => new()
	{
		Currency = currency,
		Final = final,
		Initial = final,
		DiscountPercent = 0
	};

	[Fact]
	public void Add_ThenDuplicate_ReturnsFalse()
	{
		var store = new MarketWatchStore();

		Assert.True(store.Add(570, 10m, "us", ""));
		Assert.False(store.Add(570, 20m, "de", ""));
		Assert.Equal(1, store.Count);
	}

	[Fact]
	public void Remove_UnknownOrKnown()
	{
		var store = new MarketWatchStore();
		store.Add(570, 10m, "us", "");

		Assert.False(store.Remove(440));
		Assert.True(store.Remove(570));
		Assert.Equal(0, store.Count);
	}

	[Fact]
	public void Snapshot_SortedByAppId()
	{
		var store = new MarketWatchStore();
		store.Add(730, 5m, "us", "");
		store.Add(440, 5m, "us", "");

		Assert.Equal([440u, 730u], store.Snapshot().Select(static w => w.AppId));
	}

	[Fact]
	public void RecordPrice_FirstObservation_IsBaselineWithoutAlert()
	{
		var store = new MarketWatchStore();
		store.Add(570, 10m, "us", "");

		var (outcome, alert) = store.RecordPrice(570, Price(9.99m), Now);

		Assert.Equal(PriceRecordOutcome.BaselineRecorded, outcome);
		Assert.Null(alert);
		var entry = Assert.Single(store.Snapshot());
		Assert.Equal(9.99m, entry.BaselinePrice);
		Assert.Equal("USD", entry.Currency);
	}

	[Fact]
	public void RecordPrice_UnknownWatch_ReturnsUnknownWatch()
	{
		var store = new MarketWatchStore();

		var (outcome, alert) = store.RecordPrice(570, Price(9.99m), Now);

		Assert.Equal(PriceRecordOutcome.UnknownWatch, outcome);
		Assert.Null(alert);
	}

	[Fact]
	public void RecordPrice_FailedFetch_KeepsBaseline()
	{
		var store = new MarketWatchStore();
		store.Add(570, 10m, "us", "");
		store.RecordPrice(570, Price(9.99m), Now);

		var (outcome, _) = store.RecordPrice(570, null, Now);

		Assert.Equal(PriceRecordOutcome.FetchFailed, outcome);
		Assert.Equal(9.99m, Assert.Single(store.Snapshot()).BaselinePrice);
	}

	[Fact]
	public void RecordPrice_WithinThreshold_RecordsWithoutAlert()
	{
		var store = new MarketWatchStore();
		store.Add(570, 10m, "us", "");
		store.RecordPrice(570, Price(100m), Now);

		var (outcome, alert) = store.RecordPrice(570, Price(105m), Now);

		Assert.Equal(PriceRecordOutcome.Recorded, outcome);
		Assert.Null(alert);
		Assert.Equal(100m, Assert.Single(store.Snapshot()).BaselinePrice);
	}

	[Theory]
	[InlineData(100, 85, -15)]
	[InlineData(100, 120, 20)]
	public void RecordPrice_BeyondThreshold_FiresAlertAndResetsBaseline(decimal baseline, decimal observed, decimal expectedChange)
	{
		var store = new MarketWatchStore();
		store.Add(570, 10m, "us", "");
		store.RecordPrice(570, Price(baseline), Now);

		var (outcome, alert) = store.RecordPrice(570, Price(observed), Now);

		Assert.Equal(PriceRecordOutcome.AlertFired, outcome);
		Assert.NotNull(alert);
		Assert.Equal(baseline, alert!.BaselinePrice);
		Assert.Equal(observed, alert.NewPrice);
		Assert.Equal(expectedChange, alert.ChangePercent);
		Assert.Equal(10m, alert.ThresholdPercent);

		var entry = Assert.Single(store.Snapshot());
		Assert.Equal(observed, entry.BaselinePrice);
		Assert.Equal(1, entry.AlertCount);
	}

	[Fact]
	public void RecordPrice_AfterAlert_SteadyPriceDoesNotReAlert()
	{
		var store = new MarketWatchStore();
		store.Add(570, 10m, "us", "");
		store.RecordPrice(570, Price(100m), Now);
		store.RecordPrice(570, Price(50m), Now);

		var (outcome, alert) = store.RecordPrice(570, Price(52m), Now);

		Assert.Equal(PriceRecordOutcome.Recorded, outcome);
		Assert.Null(alert);
	}

	[Theory]
	[InlineData(100, 100, 0)]
	[InlineData(100, 90, -10)]
	[InlineData(0, 5, 0)]
	[InlineData(200, 300, 50)]
	public void CalculateChangePercent_MatchesExpectation(decimal baseline, decimal observed, decimal expected) =>
		Assert.Equal(expected, MarketWatchStore.CalculateChangePercent(baseline, observed));
}
