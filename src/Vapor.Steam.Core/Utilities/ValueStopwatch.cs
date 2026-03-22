namespace Vapor.Steam.Core.Utilities;

/// <summary>
/// A value type for measuring elapsed time with minimal overhead.
/// Unlike Stopwatch, this is a struct and doesn't allocate.
/// </summary>
public readonly struct ValueStopwatch
{
	private readonly long _startTimestamp;

	private ValueStopwatch(long startTimestamp)
	{
		_startTimestamp = startTimestamp;
	}

	/// <summary>
	/// Gets a value indicating whether the stopwatch is running.
	/// </summary>
	public bool IsActive => _startTimestamp != 0;

	/// <summary>
	/// Creates a new ValueStopwatch and starts it.
	/// </summary>
	public static ValueStopwatch StartNew() => new(GetTimestamp());

	/// <summary>
	/// Gets the elapsed time since the stopwatch was started.
	/// </summary>
	public TimeSpan Elapsed => IsActive
		? TimeSpan.FromTicks(GetElapsedTicks(_startTimestamp))
		: TimeSpan.Zero;

	/// <summary>
	/// Gets the elapsed time in milliseconds.
	/// </summary>
	public long ElapsedMilliseconds => Elapsed.Ticks / TimeSpan.TicksPerMillisecond;

	private static long GetTimestamp()
	{
		// Use DateTime.UtcNow.Ticks as a simple timestamp
		// For higher precision, consider using Environment.TickCount64 or Stopwatch.GetTimestamp
		return DateTime.UtcNow.Ticks;
	}

	private static long GetElapsedTicks(long startTimestamp)
	{
		return GetTimestamp() - startTimestamp;
	}
}
