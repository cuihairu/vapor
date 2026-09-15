using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Dispatches a single-target job for one account and waits (bounded) for the
/// agent's result, powering the synchronous per-account REST endpoints (trade
/// offers listing, offer accept/decline). Wait and poll intervals are
/// internal-static so tests can shrink them.
/// </summary>
internal static class AccountTaskRunner
{
	internal static TimeSpan WaitWindow = TimeSpan.FromSeconds(30);
	internal static TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

	internal const string TradeOffersAction = "get_trade_offers";
	internal const string AcceptTradeOfferAction = "accept_trade_offer";
	internal const string DeclineTradeOfferAction = "decline_trade_offer";
	internal const string ConfirmTradeOfferAction = "confirm_trade_offer";
	internal const string ConfirmAllConfirmationsAction = "confirm_all_confirmations";
	internal const string LootInventoryAction = "loot_inventory";
	internal const string AddLicenseAction = "add_license";
	internal const string GetInventoryAction = "get_inventory";
	internal const string FindDuplicatesAction = "find_duplicates";
	internal const string SwapDuplicatesAction = "swap_duplicates";
	internal const string GetMyMarketListingsAction = "get_my_market_listings";
	internal const string CancelMarketListingsAction = "cancel_market_listings";
	internal const string CreateMarketListingAction = "create_market_listing";
	internal const string GetPointsShopSummaryAction = "get_points_shop_summary";
	internal const string ClaimPointsShopItemsAction = "claim_points_shop_items";

	/// <summary>Lists an account's own market listings via the get_my_market_listings action.</summary>
	public static Task<TaskRunResult> ReadMarketListingsAsync(
		IJobStore store,
		string accountName,
		int start,
		int count,
		CancellationToken cancellationToken)
	{
		return DispatchAsync(
			store,
			GetMyMarketListingsAction,
			accountName,
			new Dictionary<string, object?> { ["start"] = start, ["count"] = count },
			cancellationToken);
	}

	/// <summary>Cancels an account's own market listings matching a filter via the cancel_market_listings action.</summary>
	public static Task<TaskRunResult> CancelMarketListingsAsync(
		IJobStore store,
		string accountName,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		return DispatchAsync(
			store,
			CancelMarketListingsAction,
			accountName,
			payload,
			cancellationToken);
	}

	/// <summary>Creates one market listing via the create_market_listing action.</summary>
	public static Task<TaskRunResult> CreateMarketListingAsync(
		IJobStore store,
		string accountName,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		return DispatchAsync(
			store,
			CreateMarketListingAction,
			accountName,
			payload,
			cancellationToken);
	}

	/// <summary>Reads an account's points shop balance (and definitions) via the get_points_shop_summary action.</summary>
	public static Task<TaskRunResult> ReadPointsShopSummaryAsync(
		IJobStore store,
		string accountName,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		return DispatchAsync(
			store,
			GetPointsShopSummaryAction,
			accountName,
			payload,
			cancellationToken);
	}

	/// <summary>Redeems points shop reward definitions via the claim_points_shop_items action.</summary>
	public static Task<TaskRunResult> ClaimPointsShopItemsAsync(
		IJobStore store,
		string accountName,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		return DispatchAsync(
			store,
			ClaimPointsShopItemsAction,
			accountName,
			payload,
			cancellationToken);
	}

	/// <summary>Lists an account's trade offers via the get_trade_offers action.</summary>
	public static Task<TaskRunResult> ReadTradeOffersAsync(
		IJobStore store,
		string accountName,
		bool activeOnly,
		CancellationToken cancellationToken)
	{
		return DispatchAsync(
			store,
			TradeOffersAction,
			accountName,
			new Dictionary<string, object?> { ["active_only"] = activeOnly },
			cancellationToken);
	}

	/// <summary>
	/// Creates a one-target job for the account and polls it to a terminal state.
	/// Status <see cref="JobTaskStatus.Queued"/> on the result means "still pending
	/// when the window closed".
	/// </summary>
	public static async Task<TaskRunResult> DispatchAsync(
		IJobStore store,
		string action,
		string accountName,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var created = await store.CreateJob(new CreateJobRequest(
			action,
			Region: null,
			Targets: [accountName],
			Payload: payload,
			Meta: new Dictionary<string, string> { ["origin"] = "accounts-api" }
		), cancellationToken).ConfigureAwait(false);

		string jobId = created.Job.Id;
		string taskId = created.Tasks.Single(t => t.Target == accountName).Id;

		DateTimeOffset deadline = DateTimeOffset.UtcNow + WaitWindow;
		while (DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
			JobWithTasks job = await store.GetJob(jobId, cancellationToken).ConfigureAwait(false);
			JobTask? task = job.Tasks.FirstOrDefault(t => t.Id == taskId);
			if (task is null)
			{
				continue;
			}

			if (task.Status is JobTaskStatus.Finished or JobTaskStatus.Failed or JobTaskStatus.Canceled)
			{
				return new TaskRunResult(jobId, action, task.Status, task.Output, task.Error);
			}
		}

		// The agent has not reported within the window — hand the job id back so
		// the caller can keep polling GET /v1/jobs/{id} instead of blocking.
		return new TaskRunResult(jobId, action, JobTaskStatus.Queued, null, null);
	}
}

/// <summary>Outcome of one bounded single-account dispatch. Status Queued means "still pending".</summary>
internal sealed record TaskRunResult(
	string JobId,
	string Action,
	JobTaskStatus Status,
	IReadOnlyDictionary<string, object?>? Output,
	string? Error);
