using System.Text.Json;
using Vapor.ControlPlane;
using Vapor.Protocol;

namespace Vapor.ControlPlane.Tests;

internal sealed class FakeCrawlJobStore : IJobStore
{
	private readonly Dictionary<string, JobWithTasks> _jobs = new();

	public List<CreateJobRequest> Created { get; } = [];
	public List<JobWithTasks> CreatedJobs { get; } = [];
	public Dictionary<string, (JobTaskStatus Status, string? Error)> Outcomes { get; } = new();
	public Dictionary<string, JsonElement> Outputs { get; } = new();
	/// <summary>Task output handed through without a JSON round-trip (in-memory object shapes).</summary>
	public Dictionary<string, IReadOnlyDictionary<string, object?>> RawOutputs { get; } = new();
	public List<string> Cancelled { get; } = [];
	public bool ThrowOnCreate { get; set; }
	public bool ThrowCanceledOnCreate { get; set; }
	public bool CancelThrows { get; set; }
	/// <summary>CancelJob throws cancellation (the stopping-token rethrow path).</summary>
	public bool CancelCanceledThrows { get; set; }
	/// <summary>Exceptions thrown from GetJob once each (OCE → rethrow path, IOException → swallow path).</summary>
	public Queue<Exception> GetJobExceptions { get; } = new();
	/// <summary>Job snapshots returned from GetJob before falling through to the dictionary
	/// (deterministic poll sequences, e.g. a pass where the task row is not yet visible).</summary>
	public Queue<JobWithTasks> GetJobReplies { get; } = new();
	public Func<CreateJobRequest, bool>? ThrowOnCreateWhen { get; set; }
	/// <summary>Awaited inside CreateJob before the throw decision (parks a dispatch mid-call).</summary>
	public Func<CreateJobRequest, Task>? CreateGate { get; set; }

	public void Drop(string jobId) => _jobs.Remove(jobId);

	public async Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken)
	{
		if (CreateGate is { } gate)
		{
			await gate(request);
		}

		if (ThrowCanceledOnCreate)
		{
			throw new OperationCanceledException(cancellationToken);
		}

		if (ThrowOnCreate || ThrowOnCreateWhen?.Invoke(request) == true)
		{
			throw new IOException("store unavailable");
		}

		string jobId = $"job-{Created.Count + 1}";
		DateTimeOffset now = DateTimeOffset.UtcNow;
		var job = new Job(jobId, request.Action, request.Region, request.Targets, request.Meta, JobStatus.Queued, now, now);
		var task = new JobTask(jobId + "-t1", jobId, request.Targets.FirstOrDefault() ?? "", request.Action, request.Region, request.Payload, JobTaskStatus.Queued, 1, now, now);
		var jobWithTasks = new JobWithTasks(job, [task]);
		_jobs[jobId] = jobWithTasks;
		Created.Add(request);
		CreatedJobs.Add(jobWithTasks);
		return jobWithTasks;
	}

	public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken)
	{
		if (GetJobExceptions.Count > 0)
		{
			throw GetJobExceptions.Dequeue();
		}

		if (GetJobReplies.Count > 0)
		{
			return Task.FromResult(GetJobReplies.Dequeue());
		}

		if (!_jobs.TryGetValue(jobId, out JobWithTasks? jobWithTasks))
		{
			throw new NotFoundException("job not found");
		}

		if (Outcomes.TryGetValue(jobId, out (JobTaskStatus Status, string? Error) outcome))
		{
			JobTask task = jobWithTasks.Tasks[0] with
			{
				Status = outcome.Status,
				Error = outcome.Error,
				Output = RawOutputs.TryGetValue(jobId, out IReadOnlyDictionary<string, object?>? raw)
					? raw
					: Outputs.TryGetValue(jobId, out JsonElement output)
						? JsonSerializer.Deserialize<IReadOnlyDictionary<string, object?>>(output.GetRawText(), JsonDefaults.Options)
						: null
			};
			Job finishedJob = jobWithTasks.Job with
			{
				Status = outcome.Status switch
				{
					JobTaskStatus.Finished => JobStatus.Finished,
					JobTaskStatus.Failed => JobStatus.Failed,
					_ => JobStatus.Canceled
				}
			};
			return Task.FromResult(new JobWithTasks(finishedJob, [task]));
		}

		return Task.FromResult(jobWithTasks);
	}

	public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken)
	{
		if (CancelCanceledThrows)
		{
			throw new OperationCanceledException(cancellationToken);
		}

		if (CancelThrows)
		{
			throw new IOException("cancel unavailable");
		}

		Cancelled.Add(jobId);
		return Task.FromResult<IReadOnlyList<TaskCancel>>([]);
	}

	public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<Job>>([]);

	public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<Job>>([]);

	public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);

	public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken)
		=> Task.FromResult<Job?>(null);

	public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);

	public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<JobTask>>([]);

	public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => Task.FromResult<JobTask?>(null);

	public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;

	public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);
	public Task MarkTaskClaimedBy(string taskId, int attempt, string agentId, CancellationToken cancellationToken) => Task.CompletedTask;
	public Task<Dictionary<string, int>> CountRunningTasksByAgent(CancellationToken cancellationToken) => Task.FromResult(new Dictionary<string, int>());

	public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => Task.FromResult(false);

	public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken)
		=> throw new NotSupportedException();

	public Task<(int Jobs, int Tasks)> PurgeSecretPayloadsAsync(bool dryRun, string? backupPath, CancellationToken cancellationToken)
		=> Task.FromResult((0, 0));

	public Task<IReadOnlyDictionary<JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyDictionary<JobTaskStatus, int>>(new Dictionary<JobTaskStatus, int>());
}
