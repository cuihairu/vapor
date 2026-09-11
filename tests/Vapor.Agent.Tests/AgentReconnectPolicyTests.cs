using Xunit;

namespace Vapor.Agent.Tests;

public class AgentReconnectPolicyTests
{
	[Fact]
	public void Default_HasExpectedValues()
	{
		var policy = AgentReconnectPolicy.Default;

		Assert.Equal(TimeSpan.FromMilliseconds(500), policy.InitialDelay);
		Assert.Equal(TimeSpan.FromSeconds(10), policy.MaxDelay);
		Assert.Equal(2d, policy.BackoffFactor);
		Assert.Equal(0, policy.MaxRetries);
		Assert.True(policy.IsUnlimitedRetries);
	}

	[Fact]
	public void Constructor_ValidatesArguments()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new AgentReconnectPolicy(
			TimeSpan.Zero, TimeSpan.FromSeconds(10), 2d, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AgentReconnectPolicy(
			TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(500), 2d, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AgentReconnectPolicy(
			TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10), 0.5d, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => new AgentReconnectPolicy(
			TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10), 2d, -1));
	}

	[Fact]
	public void Constructor_AcceptsMaxDelayEqualToInitialDelay()
	{
		var policy = new AgentReconnectPolicy(
			TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1d, 3);

		Assert.Equal(1d, policy.BackoffFactor);
		Assert.False(policy.IsUnlimitedRetries);
	}

	[Theory]
	[InlineData(1, 500)]
	[InlineData(2, 1000)]
	[InlineData(3, 2000)]
	[InlineData(4, 4000)]
	[InlineData(5, 8000)]
	public void GetDelayForAttempt_DoublesPerAttempt(int attempt, double expectedMs)
	{
		var delay = AgentReconnectPolicy.Default.GetDelayForAttempt(attempt);

		Assert.Equal(expectedMs, delay.TotalMilliseconds);
	}

	[Fact]
	public void GetDelayForAttempt_CapsAtMaxDelay()
	{
		var delay = AgentReconnectPolicy.Default.GetDelayForAttempt(10);

		Assert.Equal(TimeSpan.FromSeconds(10), delay);
	}

	[Fact]
	public void GetDelayForAttempt_NonPositiveFailures_ReturnsInitialDelay()
	{
		var policy = AgentReconnectPolicy.Default;

		Assert.Equal(policy.InitialDelay, policy.GetDelayForAttempt(0));
		Assert.Equal(policy.InitialDelay, policy.GetDelayForAttempt(-3));
	}

	[Fact]
	public void GetDelayForAttempt_FactorOne_IsConstant()
	{
		var policy = new AgentReconnectPolicy(
			TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5), 1d, 0);

		Assert.Equal(300, policy.GetDelayForAttempt(1).TotalMilliseconds);
		Assert.Equal(300, policy.GetDelayForAttempt(7).TotalMilliseconds);
	}

	[Fact]
	public void HasReachedRetryLimit_ZeroMaxRetries_NeverReached()
	{
		var policy = AgentReconnectPolicy.Default;

		Assert.False(policy.HasReachedRetryLimit(1));
		Assert.False(policy.HasReachedRetryLimit(1_000_000));
	}

	[Theory]
	[InlineData(1, false)]
	[InlineData(2, false)]
	[InlineData(3, true)]
	[InlineData(10, true)]
	public void HasReachedRetryLimit_WithMaxRetries(int consecutiveFailures, bool expected)
	{
		var policy = new AgentReconnectPolicy(
			TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(10), 2d, maxRetries: 3);

		Assert.Equal(expected, policy.HasReachedRetryLimit(consecutiveFailures));
	}

	[Fact]
	public void FromEnvironment_NoVariables_UsesDefaults()
	{
		var policy = AgentReconnectPolicy.FromEnvironment(_ => null);

		Assert.Equal(AgentReconnectPolicy.Default.InitialDelay, policy.InitialDelay);
		Assert.Equal(AgentReconnectPolicy.Default.MaxDelay, policy.MaxDelay);
		Assert.Equal(AgentReconnectPolicy.Default.BackoffFactor, policy.BackoffFactor);
		Assert.Equal(AgentReconnectPolicy.Default.MaxRetries, policy.MaxRetries);
	}

	[Fact]
	public void FromEnvironment_AllVariablesSet_UsesOverrides()
	{
		var policy = AgentReconnectPolicy.FromEnvironment(name => name switch
		{
			"AGENT_RECONNECT_INITIAL_DELAY_MS" => "250",
			"AGENT_RECONNECT_MAX_DELAY_MS" => "5000",
			"AGENT_RECONNECT_BACKOFF_FACTOR" => "1.5",
			"AGENT_RECONNECT_MAX_RETRIES" => "8",
			_ => null
		});

		Assert.Equal(250, policy.InitialDelay.TotalMilliseconds);
		Assert.Equal(5000, policy.MaxDelay.TotalMilliseconds);
		Assert.Equal(1.5, policy.BackoffFactor);
		Assert.Equal(8, policy.MaxRetries);
		Assert.False(policy.IsUnlimitedRetries);
	}

	[Fact]
	public void FromEnvironment_PartialOverride_KeepsDefaultsForMissing()
	{
		var policy = AgentReconnectPolicy.FromEnvironment(name => name == "AGENT_RECONNECT_MAX_RETRIES" ? "5" : null);

		Assert.Equal(5, policy.MaxRetries);
		Assert.Equal(AgentReconnectPolicy.Default.InitialDelay, policy.InitialDelay);
		Assert.Equal(AgentReconnectPolicy.Default.MaxDelay, policy.MaxDelay);
	}

	[Fact]
	public void FromEnvironment_BlankValue_FallsBackToDefault()
	{
		var policy = AgentReconnectPolicy.FromEnvironment(name => name == "AGENT_RECONNECT_INITIAL_DELAY_MS" ? "  " : null);

		Assert.Equal(AgentReconnectPolicy.Default.InitialDelay, policy.InitialDelay);
	}

	[Theory]
	[InlineData("AGENT_RECONNECT_INITIAL_DELAY_MS", "abc")]
	[InlineData("AGENT_RECONNECT_MAX_DELAY_MS", "10s")]
	[InlineData("AGENT_RECONNECT_BACKOFF_FACTOR", "fast")]
	[InlineData("AGENT_RECONNECT_MAX_RETRIES", "many")]
	public void FromEnvironment_InvalidValue_Throws(string variableName, string invalidValue)
	{
		var ex = Assert.Throws<InvalidOperationException>(() =>
			AgentReconnectPolicy.FromEnvironment(name => name == variableName ? invalidValue : null));

		Assert.Contains(variableName, ex.Message);
	}

	[Fact]
	public void FromEnvironment_NullAccessor_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => AgentReconnectPolicy.FromEnvironment(null!));
	}
}
