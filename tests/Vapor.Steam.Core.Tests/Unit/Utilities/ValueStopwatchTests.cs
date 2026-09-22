using Vapor.Steam.Core.Utilities;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Utilities;

public sealed class ValueStopwatchTests
{
	[Fact]
	public void Elapsed_OnDefaultInstance_IsZero()
	{
		// default(ValueStopwatch) never started: IsActive is false and Elapsed
		// must take the TimeSpan.Zero arm instead of diffing from timestamp 0.
		ValueStopwatch stopwatch = default;

		Assert.False(stopwatch.IsActive);
		Assert.Equal(TimeSpan.Zero, stopwatch.Elapsed);
		Assert.Equal(0, stopwatch.ElapsedMilliseconds);
	}

	[Fact]
	public void StartNew_MeasuresNonNegativeElapsedTime()
	{
		ValueStopwatch stopwatch = ValueStopwatch.StartNew();

		Assert.True(stopwatch.IsActive);
		Assert.True(stopwatch.Elapsed >= TimeSpan.Zero);
		Assert.True(stopwatch.ElapsedMilliseconds >= 0);
	}
}
