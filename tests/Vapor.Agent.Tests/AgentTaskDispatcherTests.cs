using Vapor.Agent;
using Vapor.Protocol;
using Xunit;

namespace Vapor.Agent.Tests;

public sealed class AgentTaskDispatcherTests
{
	private static WSMessage TaskMessage(string id) =>
		new("task", null, new JobTask(id, "job-1", "target-1", "ping", "local", null, JobTaskStatus.Running, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), null);

	[Fact]
	public void Enqueue_WhenBufferFull_ParksOverflowWithoutBlocking()
	{
		var dispatcher = new AgentTaskDispatcher(2);

		dispatcher.Enqueue(TaskMessage("t1"));
		dispatcher.Enqueue(TaskMessage("t2"));

		// Buffer is full (capacity 2). The third frame must not block; it is
		// parked in the overflow queue so the receive loop keeps reading.
		dispatcher.Enqueue(TaskMessage("t3"));

		Assert.Equal(1, dispatcher.OverflowCount);

		// Draining a slot lets the serial loop flush the parked frame back in.
		Assert.True(dispatcher.Reader.TryRead(out _));
		dispatcher.FlushOverflow();

		Assert.Equal(0, dispatcher.OverflowCount);
		Assert.True(dispatcher.Reader.TryRead(out _));
		Assert.True(dispatcher.Reader.TryRead(out var third));
		Assert.Equal("t3", third!.Task!.Id);
	}

	[Fact]
	public void Enqueue_PreservesOrderAcrossOverflow()
	{
		var dispatcher = new AgentTaskDispatcher(2);

		dispatcher.Enqueue(TaskMessage("a"));
		dispatcher.Enqueue(TaskMessage("b"));
		dispatcher.Enqueue(TaskMessage("c")); // overflow
		dispatcher.Enqueue(TaskMessage("d")); // overflow, after c

		Assert.True(dispatcher.Reader.TryRead(out _)); // a
		dispatcher.FlushOverflow();
		Assert.True(dispatcher.Reader.TryRead(out _)); // b
		dispatcher.FlushOverflow();

		Assert.True(dispatcher.Reader.TryRead(out var c));
		Assert.True(dispatcher.Reader.TryRead(out var d));
		Assert.Equal("c", c!.Task!.Id);
		Assert.Equal("d", d!.Task!.Id);
	}

	[Fact]
	public void Enqueue_DoesNotParkWhenBufferHasSpace()
	{
		var dispatcher = new AgentTaskDispatcher(2);

		dispatcher.Enqueue(TaskMessage("t1"));

		Assert.Equal(0, dispatcher.OverflowCount);
		Assert.True(dispatcher.Reader.TryRead(out var first));
		Assert.Equal("t1", first!.Task!.Id);
	}

	[Fact]
	public void Complete_EndsReader()
	{
		var dispatcher = new AgentTaskDispatcher(2);
		dispatcher.Complete();
		Assert.True(dispatcher.Reader.Completion.IsCompleted);
	}

	[Fact]
	public void Constructor_RejectsNonPositiveCapacity()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskDispatcher(0));
	}
}
