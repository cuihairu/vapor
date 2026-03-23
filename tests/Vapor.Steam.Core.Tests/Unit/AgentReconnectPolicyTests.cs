using Vapor.Agent;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

public sealed class AgentReconnectPolicyTests
{
	[Fact]
	public void DefaultPolicy_HasExpectedValues()
	{
		var policy = AgentReconnectPolicy.Default;

		Assert.Equal(TimeSpan.FromMilliseconds(500), policy.InitialDelay);
		Assert.Equal(TimeSpan.FromSeconds(10), policy.MaxDelay);
		Assert.Equal(2d, policy.BackoffFactor);
		Assert.True(policy.IsUnlimitedRetries);
	}

	[Fact]
	public void GetDelayForAttempt_UsesExponentialBackoffAndCapsAtMax()
	{
		var policy = new AgentReconnectPolicy(
			initialDelay: TimeSpan.FromMilliseconds(500),
			maxDelay: TimeSpan.FromMilliseconds(2_000),
			backoffFactor: 2d,
			maxRetries: 0);

		Assert.Equal(TimeSpan.FromMilliseconds(500), policy.GetDelayForAttempt(1));
		Assert.Equal(TimeSpan.FromMilliseconds(1_000), policy.GetDelayForAttempt(2));
		Assert.Equal(TimeSpan.FromMilliseconds(2_000), policy.GetDelayForAttempt(3));
		Assert.Equal(TimeSpan.FromMilliseconds(2_000), policy.GetDelayForAttempt(5));
	}

	[Fact]
	public void HasReachedRetryLimit_RespectsConfiguredMaximum()
	{
		var policy = new AgentReconnectPolicy(
			initialDelay: TimeSpan.FromMilliseconds(500),
			maxDelay: TimeSpan.FromSeconds(10),
			backoffFactor: 2d,
			maxRetries: 3);

		Assert.False(policy.HasReachedRetryLimit(1));
		Assert.False(policy.HasReachedRetryLimit(2));
		Assert.True(policy.HasReachedRetryLimit(3));
	}

	[Fact]
	public void FromEnvironment_ReadsOverrides()
	{
		var values = new Dictionary<string, string?>
		{
			["AGENT_RECONNECT_INITIAL_DELAY_MS"] = "1000",
			["AGENT_RECONNECT_MAX_DELAY_MS"] = "8000",
			["AGENT_RECONNECT_BACKOFF_FACTOR"] = "1.5",
			["AGENT_RECONNECT_MAX_RETRIES"] = "4"
		};

		var policy = AgentReconnectPolicy.FromEnvironment(key => values.TryGetValue(key, out var value) ? value : null);

		Assert.Equal(TimeSpan.FromMilliseconds(1000), policy.InitialDelay);
		Assert.Equal(TimeSpan.FromMilliseconds(8000), policy.MaxDelay);
		Assert.Equal(1.5d, policy.BackoffFactor);
		Assert.Equal(4, policy.MaxRetries);
	}
}
