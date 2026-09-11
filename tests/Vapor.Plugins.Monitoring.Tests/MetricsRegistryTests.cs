using Xunit;
using Vapor.Plugins.Monitoring;

namespace Vapor.Plugins.Monitoring.Tests;

public class MetricsRegistryTests
{
	[Fact]
	public void CounterInc_AccumulatesValues()
	{
		var registry = new MetricsRegistry();

		registry.CounterInc("test_total", "Test counter.");
		registry.CounterInc("test_total", "Test counter.");
		registry.CounterInc("test_total", "Test counter.", 2.5);

		Assert.Equal(4.5, registry.GetValue("test_total"));
	}

	[Fact]
	public void CounterInc_WithLabels_KeepsSeparateSeries()
	{
		var registry = new MetricsRegistry();

		registry.CounterInc("hits_total", "Hits.", 1, ("action", "echo"), ("status", "success"));
		registry.CounterInc("hits_total", "Hits.", 1, ("action", "echo"), ("status", "failure"));

		Assert.Equal(1, registry.GetValue("hits_total", ("action", "echo"), ("status", "success")));
		Assert.Equal(1, registry.GetValue("hits_total", ("action", "echo"), ("status", "failure")));
		Assert.Equal(2, registry.SumSeries("hits_total"));
	}

	[Fact]
	public void CounterSet_OverridesValue()
	{
		var registry = new MetricsRegistry();

		registry.CounterInc("abs_total", "Absolute counter.", 10);
		registry.CounterSet("abs_total", "Absolute counter.", 42);

		Assert.Equal(42, registry.GetValue("abs_total"));
	}

	[Fact]
	public void GaugeSet_OverridesValue()
	{
		var registry = new MetricsRegistry();

		registry.GaugeSet("depth", "Queue depth.", 5);
		registry.GaugeSet("depth", "Queue depth.", 2);

		Assert.Equal(2, registry.GetValue("depth"));
	}

	[Fact]
	public void GaugeAdd_MovesValueUpAndDown()
	{
		var registry = new MetricsRegistry();

		registry.GaugeAdd("inflight", "In flight.", 3);
		registry.GaugeAdd("inflight", "In flight.", -1);

		Assert.Equal(2, registry.GetValue("inflight"));
	}

	[Fact]
	public void GetValue_UnknownSeries_ReturnsZero()
	{
		var registry = new MetricsRegistry();

		Assert.Equal(0, registry.GetValue("never_written"));
	}

	[Fact]
	public void RenderPrometheus_EmitsHelpTypeAndSortedSeries()
	{
		var registry = new MetricsRegistry();
		registry.CounterInc("zz_total", "Zeta counter.", 1, ("b", "2"));
		registry.CounterInc("zz_total", "Zeta counter.", 1, ("a", "1"));
		registry.GaugeSet("aa_gauge", "Alpha gauge.", 7);

		var text = registry.RenderPrometheus();
		var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

		Assert.Contains("# HELP aa_gauge Alpha gauge.", lines);
		Assert.Contains("# TYPE aa_gauge gauge", lines);
		Assert.Contains("# HELP zz_total Zeta counter.", lines);
		Assert.Contains("# TYPE zz_total counter", lines);
		Assert.Contains("aa_gauge 7", lines);
		Assert.Contains("zz_total{a=\"1\"} 1", lines);
		Assert.Contains("zz_total{b=\"2\"} 1", lines);

		// HELP/TYPE block first, series sorted afterwards.
		var firstSeries = lines.FindIndex(l => !l.StartsWith('#'));
		Assert.Equal("aa_gauge 7", lines[firstSeries]);
	}

	[Fact]
	public void RenderPrometheus_EscapesLabelValues()
	{
		var registry = new MetricsRegistry();
		registry.CounterInc("esc_total", "Escaping.", 1, ("path", "a\"b\\c\nd"));

		var text = registry.RenderPrometheus();

		Assert.Contains("esc_total{path=\"a\\\"b\\\\c\\nd\"} 1", text, StringComparison.Ordinal);
	}

	[Fact]
	public void RenderPrometheus_UsesInvariantNumberFormatting()
	{
		var registry = new MetricsRegistry();
		registry.GaugeSet("fraction", "Fraction.", 1.5);

		var text = registry.RenderPrometheus();

		Assert.Contains("fraction 1.5", text, StringComparison.Ordinal);
	}
}
