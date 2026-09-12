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
