using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Dispatches <c>get_trade_offers</c> to an account and waits (bounded) for the
/// agent's result, powering the synchronous trade-offers REST endpoint. Wait and
/// poll intervals are internal-static so tests can shrink them.
/// </summary>
internal static class TradeOffersReader
{
	internal static TimeSpan WaitWindow = TimeSpan.FromSeconds(30);
	internal static TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

	internal const string Action = "get_trade_offers";

	/// <summary>Creates a one-target job for the account and polls it to a terminal state.</summary>
	public static async Task<TradeOffersReadResult> ReadAsync(
		IJobStore store,
		string accountName,
		bool activeOnly,
		CancellationToken cancellationToken)
	{
		var created = await store.CreateJob(new CreateJobRequest(
			Action,
			Region: null,
			Targets: [accountName],
			Payload: new Dictionary<string, object?> { ["active_only"] = activeOnly },
			Meta: new Dictionary<string, string> { ["origin"] = "trade-offers-api" }
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
				return new TradeOffersReadResult(jobId, task.Status, task.Output, task.Error);
			}
		}

		// The agent has not reported within the window — hand the job id back so
		// the caller can keep polling GET /v1/jobs/{id} instead of blocking.
		return new TradeOffersReadResult(jobId, JobTaskStatus.Queued, null, null);
	}
}

/// <summary>Outcome of one bounded trade-offers read. Status Queued means "still pending".</summary>
internal sealed record TradeOffersReadResult(
	string JobId,
	JobTaskStatus Status,
	IReadOnlyDictionary<string, object?>? Output,
	string? Error);
