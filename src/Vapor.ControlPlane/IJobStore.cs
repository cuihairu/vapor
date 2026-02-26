using Vapor.Protocol;

namespace Vapor.ControlPlane;

public interface IJobStore {
	Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken);
	Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken);
	Task<IReadOnlyList<Job>> ListJobs(int limit, CancellationToken cancellationToken);
	Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken);

	Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken);
	Task RequeueTask(string taskId, CancellationToken cancellationToken);
	Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken);
	Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken);
	Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken);
}

