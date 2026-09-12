using Vapor.Protocol;

namespace Vapor.ControlPlane;

public interface IJobStore {
	Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken);
	Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken);
	Task<IReadOnlyList<Job>> ListJobs(int limit, CancellationToken cancellationToken);
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

