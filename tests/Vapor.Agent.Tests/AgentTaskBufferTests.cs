using System.Threading.Channels;
using Vapor.Agent;
using Vapor.Protocol;
using Xunit;

namespace Vapor.Agent.Tests;

public class AgentTaskBufferTests
{
	[Fact]
	public void ResolveCapacity_UsesDefaultWhenUnset()
	{
		Assert.Equal(AgentTaskBuffer.DefaultCapacity, AgentTaskBuffer.ResolveCapacity(null));
	}

	[Fact]
	public void ResolveCapacity_UsesParsedPositiveValue()
	{
		Assert.Equal(64, AgentTaskBuffer.ResolveCapacity("64"));
	}

	[Fact]
	public void ResolveCapacity_FallsBackForInvalidOrNonPositive()
	{
		Assert.Equal(AgentTaskBuffer.DefaultCapacity, AgentTaskBuffer.ResolveCapacity(""));
		Assert.Equal(AgentTaskBuffer.DefaultCapacity, AgentTaskBuffer.ResolveCapacity("abc"));
		Assert.Equal(AgentTaskBuffer.DefaultCapacity, AgentTaskBuffer.ResolveCapacity("0"));
		Assert.Equal(AgentTaskBuffer.DefaultCapacity, AgentTaskBuffer.ResolveCapacity("-3"));
	}

	[Fact]
	public void CreateChannel_RejectsNonPositiveCapacity()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => AgentTaskBuffer.CreateChannel(0));
	}

	[Fact]
	public async Task CreateChannel_IsBounded_WaitsWhenFullInsteadOfDropping()
	{
		// Capacity 2, no reader: writes 1 and 2 succeed; the 3rd must block (Wait)
		// until a slot frees, never silently dropping the oldest task.
		var channel = AgentTaskBuffer.CreateChannel(2);
		var msg = new WSMessage("task", null, new JobTask("t", "j", "a", "ping", "local", null, JobTaskStatus.Running, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), null);

		await channel.Writer.WriteAsync(msg);
		await channel.Writer.WriteAsync(msg);

		var third = channel.Writer.WriteAsync(msg);
		Assert.False(third.IsCompleted);

		Assert.True(channel.Reader.TryRead(out var first));
		Assert.Equal("t", first!.Task!.Id);
		Assert.True(channel.Reader.TryRead(out var second));
		Assert.Equal("t", second!.Task!.Id);

		// Now a slot is free, the pending write completes.
		await third;
	}
}
