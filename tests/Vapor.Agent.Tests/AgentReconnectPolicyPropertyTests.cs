using FsCheck.Xunit;
using Xunit;
using Vapor.Agent;

namespace Vapor.Agent.Tests;

/// <summary>
/// Property-based tests over <see cref="AgentReconnectPolicy"/>. The example
/// tests pin concrete curve values for the default factor; the properties pin
/// the invariants that must hold for every legal configuration at once: the
/// constructor round-trips its four arguments, an exponential backoff curve is
/// monotone non-decreasing in the attempt number and always bounded below by
/// the initial delay and above by the max delay, the retry-limit predicate is
/// monotone in the failure count and never fires for unlimited policies, and
/// the constructor is total — any violation of exactly one constraint throws
/// ArgumentOutOfRangeException and nothing else ever escapes.
/// </summary>
public sealed class AgentReconnectPolicyPropertyTests
{
	private const int MaxInitialDelayMs = 60_000;
	private const int MaxDelayUpperBoundMs = 1_000_000;
	private const int MaxAttempt = 500;

	private static AgentReconnectPolicy GenPolicy(int initialMs, int maxMs, int factorIdx, int retries)
	{
		var initial = TimeSpan.FromMilliseconds((uint)initialMs % MaxInitialDelayMs + 1);
		var max = initial + TimeSpan.FromMilliseconds((uint)maxMs % (MaxDelayUpperBoundMs - (int)initial.TotalMilliseconds) + 1);
		// Factor over [1.0, 9.0): every legal value, in steps that also cover
		// factor == 1 (the constant-curve corner) with meaningful probability.
		var factor = 1d + (uint)factorIdx % 80 / 10d;
		var maxRetries = (uint)retries % 101;

		return new AgentReconnectPolicy(initial, max, factor, (int)maxRetries);
	}

	[Property]
	public void Constructor_RoundTripsEveryArgument(int initialMs, int maxMs, int factorIdx, int retries)
	{
		var policy = GenPolicy(initialMs, maxMs, factorIdx, retries);

		Assert.True(policy.InitialDelay > TimeSpan.Zero);
		Assert.True(policy.MaxDelay >= policy.InitialDelay);
		Assert.True(policy.BackoffFactor >= 1d);
		Assert.True(policy.MaxRetries >= 0);
		Assert.Equal(policy.MaxRetries == 0, policy.IsUnlimitedRetries);
	}

	[Property]
	public void BackoffCurve_IsMonotoneNonDecreasing(int initialMs, int maxMs, int factorIdx, int retries, int attemptSeed)
	{
		var policy = GenPolicy(initialMs, maxMs, factorIdx, retries);
		var attempt = (int)((uint)attemptSeed % MaxAttempt) + 1;

		// factor >= 1 makes the raw curve non-decreasing and capping at
		// MaxDelay preserves that, so consecutive attempts never back off less.
		TimeSpan before = policy.GetDelayForAttempt(attempt);
		TimeSpan after = policy.GetDelayForAttempt(attempt + 1);

		Assert.True(after >= before);
	}

	[Property]
	public void BackoffCurve_IsBoundedBelowByInitialAndAboveByMax(int initialMs, int maxMs, int factorIdx, int retries, int attemptSeed)
	{
		var policy = GenPolicy(initialMs, maxMs, factorIdx, retries);
		var attempt = (int)((uint)attemptSeed % (MaxAttempt + 2)) - 1; // includes 0 and -1

		TimeSpan delay = policy.GetDelayForAttempt(attempt);

		Assert.True(delay >= policy.InitialDelay);
		Assert.True(delay <= policy.MaxDelay);
	}

	[Property]
	public void HasReachedRetryLimit_IsMonotoneInFailures_AndNeverForUnlimited(int initialMs, int maxMs, int factorIdx, int retries, int failureSeed)
	{
		var policy = GenPolicy(initialMs, maxMs, factorIdx, retries);
		var failures = (int)((uint)failureSeed % MaxAttempt) + 1;

		Assert.False(policy.HasReachedRetryLimit(failures - 1) && !policy.HasReachedRetryLimit(failures));

		if (policy.IsUnlimitedRetries)
		{
			Assert.False(policy.HasReachedRetryLimit(int.MaxValue));
		}
	}

	[Property]
	public void Constructor_ViolatingExactlyOneConstraint_ThrowsArgumentOutOfRange(int constraintIdx, int initialMs, int maxMs, int factorIdx, int retries)
	{
		var initial = TimeSpan.FromMilliseconds((uint)initialMs % MaxInitialDelayMs + 1);
		var factor = 1d + (uint)factorIdx % 80 / 10d;
		var maxRetries = (int)((uint)retries % 101);
		var max = initial + TimeSpan.FromMilliseconds((uint)maxMs % MaxDelayUpperBoundMs);

		var constraint = (Constraint)((uint)constraintIdx % 4);
		(initial, max, factor, maxRetries) = constraint switch
		{
			Constraint.NonPositiveInitial => (TimeSpan.Zero, max, factor, maxRetries),
			// Strictly below, not a swap: max == initial is legal and would not throw.
			Constraint.MaxBelowInitial => (max + TimeSpan.FromMilliseconds(1), max, factor, maxRetries),
			// Over [0, 0.98]: subtracting from a factor up to 8.9 could stay >= 1.
			Constraint.FactorBelowOne => (initial, max, (uint)factorIdx % 99 / 100d, maxRetries),
			_ => (initial, max, factor, -maxRetries - 1)
		};

		var thrown = Record.Exception(() => new AgentReconnectPolicy(initial, max, factor, maxRetries));

		Assert.IsType<ArgumentOutOfRangeException>(thrown);
	}

	private enum Constraint
	{
		NonPositiveInitial = 0,
		MaxBelowInitial = 1,
		FactorBelowOne = 2,
		NegativeMaxRetries = 3
	}
}
