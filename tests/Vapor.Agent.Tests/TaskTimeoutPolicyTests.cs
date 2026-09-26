using Xunit;

namespace Vapor.Agent.Tests;

public class TaskTimeoutPolicyTests
{
	[Fact]
	public void DefaultTimeout_IsFifteenMinutes()
	{
		Assert.Equal(TimeSpan.FromSeconds(900), TaskTimeoutPolicy.DefaultTimeout);
	}

	[Fact]
	public void Constructor_PositiveTimeout_IsEnabled()
	{
		var policy = new TaskTimeoutPolicy(TimeSpan.FromSeconds(30));

		Assert.True(policy.IsEnabled);
		Assert.Equal(TimeSpan.FromSeconds(30), policy.Timeout);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(-900)]
	public void Constructor_NonPositiveTimeout_DisablesWatchdog(int seconds)
	{
		var policy = new TaskTimeoutPolicy(TimeSpan.FromSeconds(seconds));

		Assert.False(policy.IsEnabled);
	}

	[Fact]
	public void TimeoutError_IncludesTheConfiguredSeconds()
	{
		var policy = new TaskTimeoutPolicy(TimeSpan.FromSeconds(120));

		Assert.Equal("task timeout after 120s", policy.TimeoutError);
	}

	[Fact]
	public void FromEnvironment_NoVariable_UsesDefault()
	{
		var policy = TaskTimeoutPolicy.FromEnvironment(_ => null);

		Assert.Equal(TaskTimeoutPolicy.DefaultTimeout, policy.Timeout);
		Assert.True(policy.IsEnabled);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("  ")]
	public void FromEnvironment_BlankValue_FallsBackToDefault(string? value)
	{
		var policy = TaskTimeoutPolicy.FromEnvironment(_ => value);

		Assert.Equal(TaskTimeoutPolicy.DefaultTimeout, policy.Timeout);
	}

	[Theory]
	[InlineData("1", 1)]
	[InlineData("60", 60)]
	[InlineData("3600", 3600)]
	public void FromEnvironment_ValidValue_OverridesTimeout(string value, int expectedSeconds)
	{
		var policy = TaskTimeoutPolicy.FromEnvironment(name => name == TaskTimeoutPolicy.EnvironmentVariable ? value : null);

		Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.Timeout);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("-5")]
	public void FromEnvironment_NonPositiveValue_DisablesWatchdog(string value)
	{
		var policy = TaskTimeoutPolicy.FromEnvironment(name => name == TaskTimeoutPolicy.EnvironmentVariable ? value : null);

		Assert.False(policy.IsEnabled);
	}

	[Theory]
	[InlineData("abc")]
	[InlineData("900s")]
	[InlineData("1.5")]
	public void FromEnvironment_InvalidValue_Throws(string invalidValue)
	{
		var ex = Assert.Throws<InvalidOperationException>(() =>
			TaskTimeoutPolicy.FromEnvironment(name => name == TaskTimeoutPolicy.EnvironmentVariable ? invalidValue : null));

		Assert.Contains(TaskTimeoutPolicy.EnvironmentVariable, ex.Message);
	}

	[Fact]
	public void FromEnvironment_NullAccessor_Throws()
	{
		Assert.Throws<ArgumentNullException>(() => TaskTimeoutPolicy.FromEnvironment(null!));
	}

	[Theory]
	[InlineData(true, false, false, true)]
	[InlineData(false, false, false, false)]
	[InlineData(true, true, false, false)]
	[InlineData(true, false, true, false)]
	[InlineData(false, true, true, false)]
	public void IsTaskTimeout_ClassifiesTheCancellationSource(
		bool executionCancelled, bool globalCancelled, bool cancelledByServer, bool expected)
	{
		var policy = new TaskTimeoutPolicy(TimeSpan.FromSeconds(10));

		Assert.Equal(expected, policy.IsTaskTimeout(executionCancelled, globalCancelled, cancelledByServer));
	}

	[Fact]
	public void IsTaskTimeout_DisabledWatchdog_NeverReportsTimeout()
	{
		var policy = new TaskTimeoutPolicy(TimeSpan.Zero);

		Assert.False(policy.IsTaskTimeout(executionCancelled: true, globalCancelled: false, cancelledByServer: false));
	}
}
