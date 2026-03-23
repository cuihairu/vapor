namespace Vapor.Agent;

public sealed class AgentReconnectPolicy
{
	public static readonly AgentReconnectPolicy Default = new(
		initialDelay: TimeSpan.FromMilliseconds(500),
		maxDelay: TimeSpan.FromSeconds(10),
		backoffFactor: 2d,
		maxRetries: 0);

	public AgentReconnectPolicy(
		TimeSpan initialDelay,
		TimeSpan maxDelay,
		double backoffFactor,
		int maxRetries)
	{
		if (initialDelay <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be positive.");
		}

		if (maxDelay < initialDelay)
		{
			throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be greater than or equal to initial delay.");
		}

		if (backoffFactor < 1d)
		{
			throw new ArgumentOutOfRangeException(nameof(backoffFactor), "Backoff factor must be at least 1.");
		}

		if (maxRetries < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries cannot be negative.");
		}

		InitialDelay = initialDelay;
		MaxDelay = maxDelay;
		BackoffFactor = backoffFactor;
		MaxRetries = maxRetries;
	}

	public TimeSpan InitialDelay { get; }
	public TimeSpan MaxDelay { get; }
	public double BackoffFactor { get; }
	public int MaxRetries { get; }

	public bool IsUnlimitedRetries => MaxRetries == 0;

	public TimeSpan GetDelayForAttempt(int consecutiveFailures)
	{
		if (consecutiveFailures <= 0)
		{
			return InitialDelay;
		}

		var rawDelay = InitialDelay.TotalMilliseconds * Math.Pow(BackoffFactor, consecutiveFailures - 1);
		var boundedDelay = Math.Min(rawDelay, MaxDelay.TotalMilliseconds);
		return TimeSpan.FromMilliseconds(boundedDelay);
	}

	public bool HasReachedRetryLimit(int consecutiveFailures)
	{
		return !IsUnlimitedRetries && consecutiveFailures >= MaxRetries;
	}

	public static AgentReconnectPolicy FromEnvironment(Func<string, string?> getEnvironmentVariable)
	{
		ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

		var initialDelayMs = ParseInt(getEnvironmentVariable("AGENT_RECONNECT_INITIAL_DELAY_MS"), 500, "AGENT_RECONNECT_INITIAL_DELAY_MS");
		var maxDelayMs = ParseInt(getEnvironmentVariable("AGENT_RECONNECT_MAX_DELAY_MS"), 10_000, "AGENT_RECONNECT_MAX_DELAY_MS");
		var maxRetries = ParseInt(getEnvironmentVariable("AGENT_RECONNECT_MAX_RETRIES"), 0, "AGENT_RECONNECT_MAX_RETRIES");
		var backoffFactor = ParseDouble(getEnvironmentVariable("AGENT_RECONNECT_BACKOFF_FACTOR"), 2d, "AGENT_RECONNECT_BACKOFF_FACTOR");

		return new AgentReconnectPolicy(
			TimeSpan.FromMilliseconds(initialDelayMs),
			TimeSpan.FromMilliseconds(maxDelayMs),
			backoffFactor,
			maxRetries);
	}

	private static int ParseInt(string? value, int fallback, string variableName)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		if (!int.TryParse(value, out var parsed))
		{
			throw new InvalidOperationException($"{variableName} must be a valid integer.");
		}

		return parsed;
	}

	private static double ParseDouble(string? value, double fallback, string variableName)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		if (!double.TryParse(value, out var parsed))
		{
			throw new InvalidOperationException($"{variableName} must be a valid number.");
		}

		return parsed;
	}
}
