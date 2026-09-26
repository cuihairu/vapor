using FsCheck.Xunit;
using Xunit;
using Vapor.Agent;

namespace Vapor.Agent.Tests;

/// <summary>
/// Property-based tests over <see cref="TaskTimeoutPolicy"/>. The example tests
/// pin concrete environment parses; the properties pin the invariants that must
/// hold for every legal configuration at once: the environment accessor
/// round-trips any non-positive integer as a disabled watchdog and any positive
/// integer as an enabled one with the exact timeout, and the classification
/// predicate is sound (a reported timeout always has the execution token fired
/// with neither shutdown nor server cancel in flight) and complete for enabled
/// policies (that exact combination is always reported).
/// </summary>
public sealed class TaskTimeoutPolicyPropertyTests
{
	[Property]
	public void FromEnvironment_AnyInteger_RoundTripsSeconds(int seconds)
	{
		var policy = TaskTimeoutPolicy.FromEnvironment(_ => seconds.ToString());

		Assert.Equal(TimeSpan.FromSeconds(seconds), policy.Timeout);
		Assert.Equal(seconds > 0, policy.IsEnabled);
	}

	[Property]
	public void FromEnvironment_AnyInteger_TimeoutErrorCarriesSeconds(int seconds)
	{
		var policy = TaskTimeoutPolicy.FromEnvironment(_ => seconds.ToString());

		Assert.Equal($"task timeout after {seconds}s", policy.TimeoutError);
	}

	[Property]
	public void IsTaskTimeout_SoundForEveryConfiguration(int seconds, bool executionCancelled, bool globalCancelled, bool cancelledByServer)
	{
		var policy = new TaskTimeoutPolicy(TimeSpan.FromSeconds(seconds));

		if (policy.IsTaskTimeout(executionCancelled, globalCancelled, cancelledByServer))
		{
			Assert.True(policy.IsEnabled);
			Assert.True(executionCancelled);
			Assert.False(globalCancelled);
			Assert.False(cancelledByServer);
		}
	}

	[Property]
	public void IsTaskTimeout_CompleteForEnabledWatchdogs(int seconds, bool globalCancelled, bool cancelledByServer)
	{
		int positive = (int)((uint)seconds % 3_600 + 1);
		var policy = new TaskTimeoutPolicy(TimeSpan.FromSeconds(positive));

		if (!globalCancelled && !cancelledByServer)
		{
			Assert.True(policy.IsTaskTimeout(executionCancelled: true, globalCancelled, cancelledByServer));
		}
	}
}
