using Vapor.Protocol;

namespace Vapor.ControlPlane;

public interface IJobStore
{
	Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken);
	Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken);
	Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken);
	/// <summary>Scheduled job templates whose next trigger point is at or before <paramref name="now"/>.</summary>
	Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken);
	/// <summary>Whether a previous run of the scheduled template is still queued or running (overlap control).</summary>
	Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken);
	/// <summary>
	/// Atomically creates one run of a scheduled template (child job + tasks, <c>parent_job_id</c> set)
	/// and advances the template's next trigger point. Returns null when the template is gone or no
	/// longer scheduled (canceled concurrently).
	/// </summary>
	Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken);
	/// <summary>Advances a scheduled template's next trigger point without creating a run (missed triggers are dropped).</summary>
	Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken);
	/// <summary>Most recent tasks targeting one account, newest first (for the account aggregate view).</summary>
	Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken);
	Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken);
	/// <summary>Task counts grouped by status across all jobs (for metrics/monitoring).</summary>
	Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken);

	Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken);
	/// <summary>Returns a claimed (running) task to the queue; the task becomes claimable again after <paramref name="retryDelay"/>.</summary>
	Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken);
	Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken);
	Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken);
	Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken);
	/// <summary>Marks a claimed (running) task as failed with a terminal error, e.g. after exhausting dispatch retries.</summary>
	Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken);
}

