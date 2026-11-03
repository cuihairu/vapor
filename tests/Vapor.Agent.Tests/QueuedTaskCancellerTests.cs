using Vapor.Agent;
using Vapor.Protocol;
using Xunit;

namespace Vapor.Agent.Tests;

public class QueuedTaskCancellerTests
{
	private static JobTask MakeTask(string id = "task-1", int attempt = 1) =>
		new(id, "job-1", "target-1", "ping", "local", null, JobTaskStatus.Running, attempt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

	private static TaskCancel MakeCancel(string id, int attempt) =>
		new(id, attempt, DateTimeOffset.UtcNow, "job canceled");

	[Fact]
	public void TryRemove_MatchesOnlySameTaskAndAttempt()
	{
		var canceller = new QueuedTaskCanceller();
		canceller.Add(MakeCancel("task-1", 2));

		Assert.False(canceller.TryRemove(MakeTask("task-1", 1), out _));
		Assert.False(canceller.TryRemove(MakeTask("task-2", 2), out _));
		Assert.True(canceller.TryRemove(MakeTask("task-1", 2), out var cancel));
		Assert.Equal("task-1", cancel.TaskId);
		Assert.Equal(2, cancel.Attempt);
		Assert.Equal(0, canceller.Count);
	}

	[Fact]
	public void TryRemove_StaleAttemptDoesNotSuppressNewAttempt()
	{
		var canceller = new QueuedTaskCanceller();
		canceller.Add(MakeCancel("task-1", 1)); // old attempt cancel

		// The new attempt is not suppressed by the stale cancel.
		Assert.False(canceller.TryRemove(MakeTask("task-1", 2), out _));
		Assert.True(canceller.TryRemove(MakeTask("task-1", 1), out _));
	}

	[Fact]
	public void Add_IsIdempotentPerFence()
	{
		var canceller = new QueuedTaskCanceller();
		canceller.Add(MakeCancel("task-1", 1));
		canceller.Add(MakeCancel("task-1", 1));
		Assert.Equal(1, canceller.Count);
	}

	[Fact]
	public void Clear_RemovesAllPendingCancels()
	{
		var canceller = new QueuedTaskCanceller();
		canceller.Add(MakeCancel("task-1", 1));
		canceller.Add(MakeCancel("task-2", 2));
		Assert.Equal(2, canceller.Count);

		canceller.Clear();

		Assert.Equal(0, canceller.Count);
	}
}
