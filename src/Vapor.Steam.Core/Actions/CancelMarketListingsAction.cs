using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Action to cancel the account's own market listings in bulk, selected by
/// filter (app, market hash name, buyer-price range, listing age). Mirrors the
/// confirm_all_confirmations semantics: every matched listing is attempted
/// individually so one failure does not abort the batch, and the per-listing
/// results are reported back. <c>dry_run</c> defaults to true and then only
/// reports what *would* be canceled; a real run with no filter at all is
/// refused so a fat-fingered call can't wipe every listing. Listings are
/// fetched page by page (up to a defensive cap) and cancellations are spaced
/// by <c>delay_ms</c> (default 1s, the market page's own pacing) as the
/// market-specific conservative rate control — no auto-repricing, no crawling.
/// </summary>
public sealed class CancelMarketListingsAction : IAction
{
	// Defensive cap: 20 pages × 500 per page is far beyond any sane inventory,
	// and bounds the loop if Steam's total_count lies.
	private const int MaxPages = 20;
	private const int PageSize = 500;

	private readonly ILogger<CancelMarketListingsAction> _logger;
	private readonly Func<SteamWebHandler, SteamMarketClient> _marketClientFactory;

	public CancelMarketListingsAction(ILogger<CancelMarketListingsAction> logger)
	{
		_logger = logger;
		_marketClientFactory = static webHandler => new SteamMarketClient(webHandler, NullLogger<SteamMarketClient>.Instance);
	}

	// Constructor for testing with a custom factory
	internal CancelMarketListingsAction(
		ILogger<CancelMarketListingsAction> logger,
		Func<SteamWebHandler, SteamMarketClient> marketClientFactory)
	{
		_logger = logger;
		_marketClientFactory = marketClientFactory;
	}

	public string Name => "cancel_market_listings";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Cancel own market listings matching a filter (dry_run by default; per-listing pacing; one failure does not abort the batch)",
		RequiresLogin: true,
		TimeoutSeconds: 600
	);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var webHandler = session.SteamWebHandler;
		if (webHandler == null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		int? appIdRaw = PayloadReader.GetInt32(payload, "app_id");
		uint? appId = appIdRaw is > 0 ? (uint)appIdRaw.Value : null;
		string? hashName = PayloadReader.GetString(payload, "market_hash_name")?.Trim();
		if (string.IsNullOrEmpty(hashName))
		{
			hashName = null;
		}

		int? minPrice = PayloadReader.GetInt32(payload, "min_price_cents");
		int? maxPrice = PayloadReader.GetInt32(payload, "max_price_cents");
		int? olderThanRaw = PayloadReader.GetInt32(payload, "older_than_seconds");
		int? olderThanSeconds = olderThanRaw is > 0 ? olderThanRaw.Value : null;
		bool dryRun = PayloadReader.GetBool(payload, "dry_run") ?? true;
		int delayMs = Math.Max(PayloadReader.GetInt32(payload, "delay_ms") ?? 1000, 0);

		if (minPrice is < 0 || maxPrice is < 0)
		{
			return new ActionResult(false, "min_price_cents and max_price_cents must not be negative", null);
		}

		if (minPrice is not null && maxPrice is not null && minPrice > maxPrice)
		{
			return new ActionResult(false, "min_price_cents must not exceed max_price_cents", null);
		}

		bool hasFilter = appId is not null || hashName is not null || minPrice is not null || maxPrice is not null || olderThanSeconds is not null;
		if (!hasFilter && !dryRun)
		{
			return new ActionResult(false, "refusing to cancel with no filter: pass at least one filter, or keep dry_run to preview", null);
		}

		try
		{
			var marketClient = _marketClientFactory(webHandler);
			DateTimeOffset? cutoff = olderThanSeconds is null ? null : DateTimeOffset.UtcNow - TimeSpan.FromSeconds(olderThanSeconds.Value);

			var allListings = new List<MyMarketListing>();
			for (int page = 0; page < MaxPages; page++)
			{
				var listingPage = await marketClient.GetMyListingsAsync(page * PageSize, PageSize, cancellationToken).ConfigureAwait(false);
				if (listingPage is null)
				{
					return new ActionResult(false, "Failed to fetch own market listings (is the session logged on?)", null);
				}

				allListings.AddRange(listingPage.Listings);
				int fetched = (page + 1) * PageSize;
				if (listingPage.Listings.Count < PageSize || listingPage.TotalCount <= fetched)
				{
					break;
				}
			}

			IEnumerable<MyMarketListing> matched = allListings.Where(l => MatchesFilter(l, appId, hashName, minPrice, maxPrice, cutoff));

			var results = new List<Dictionary<string, object?>>();
			int succeeded = 0;
			foreach (MyMarketListing listing in matched.ToList())
			{
				cancellationToken.ThrowIfCancellationRequested();

				var entry = new Dictionary<string, object?>
				{
					["listing_id"] = listing.ListingId,
					["market_hash_name"] = listing.MarketHashName,
					["price_cents"] = listing.PriceCents
				};

				if (dryRun)
				{
					entry["would_cancel"] = true;
				}
				else
				{
					bool canceled = await marketClient.CancelListingAsync(listing.ListingId, cancellationToken).ConfigureAwait(false);
					entry["succeeded"] = canceled;
					if (canceled)
					{
						succeeded++;
					}
				}

				results.Add(entry);

				// Market-page pacing between cancellations (not after the last one,
				// and not in dry runs, which only ever issue listing reads).
				if (!dryRun && delayMs > 0)
				{
					await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
				}
			}

			int total = results.Count;
			_logger.LogInformation(
				"Batch market cancel for {AccountName}: {Matched} matched, {Succeeded} canceled (dry_run={DryRun})",
				session.AccountName, total, dryRun ? 0 : succeeded, dryRun);

			var output = new Dictionary<string, object?>
			{
				["dry_run"] = dryRun,
				["matched"] = total,
				["scanned"] = allListings.Count,
				["succeeded"] = dryRun ? null : succeeded,
				["failed"] = dryRun ? null : total - succeeded,
				["listings"] = results
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ActionResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to cancel market listings");
			return new ActionResult(false, ex.Message, null);
		}
	}

	private static bool MatchesFilter(
		MyMarketListing listing,
		uint? appId,
		string? hashName,
		int? minPrice,
		int? maxPrice,
		DateTimeOffset? createdBefore)
	{
		if (appId is not null && listing.AppId != appId.Value)
		{
			return false;
		}

		if (hashName is not null && !string.Equals(listing.MarketHashName, hashName, StringComparison.Ordinal))
		{
			return false;
		}

		if (minPrice is not null && listing.PriceCents < minPrice.Value)
		{
			return false;
		}

		if (maxPrice is not null && listing.PriceCents > maxPrice.Value)
		{
			return false;
		}

		if (createdBefore is not null && (listing.TimeCreated is null || listing.TimeCreated.Value >= createdBefore.Value))
		{
			return false;
		}

		return true;
	}
}
