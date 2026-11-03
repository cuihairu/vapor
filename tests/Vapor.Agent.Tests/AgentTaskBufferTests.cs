using Vapor.Agent;
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

}
