using System.Collections.Concurrent;
using System.Text;

namespace Vapor.Plugins.Monitoring;

/// <summary>
/// A time series value identified by a metric name plus sorted label set.
/// </summary>
internal readonly record struct SeriesKey(string Name, string LabelSet) : IComparable<SeriesKey>
{
	public int CompareTo(SeriesKey other)
	{
		var byName = string.CompareOrdinal(Name, other.Name);
		return byName != 0 ? byName : string.CompareOrdinal(LabelSet, other.LabelSet);
	}
}

/// <summary>
/// Minimal thread-safe metrics registry with Prometheus text exposition (format 0.0.4).
/// Counters are monotonic; gauges can go up and down. All values are held as doubles so
/// rendering needs no per-series locking beyond Interlocked operations.
/// </summary>
public sealed class MetricsRegistry
{
	private readonly ConcurrentDictionary<SeriesKey, double> _values = new();
	private readonly ConcurrentDictionary<string, MetricHelp> _helps = new(StringComparer.Ordinal);

	private sealed record MetricHelp(string Help, MetricType Type);

	private enum MetricType
	{
		Counter,
		Gauge
	}

	/// <summary>
	/// Increments a counter series. <paramref name="help"/> is only needed on first use.
	/// </summary>
	public void CounterInc(string name, string help, double value = 1, params (string Name, string Value)[] labels)
	{
		_helps.TryAdd(name, new MetricHelp(help, MetricType.Counter));
		AddToSeries(name, value, labels);
	}

	/// <summary>
	/// Sets a counter series to an absolute value (e.g. a host counter already cumulative
	/// since process start, like GC collection counts).
	/// </summary>
	public void CounterSet(string name, string help, double value, params (string Name, string Value)[] labels)
	{
		_helps.TryAdd(name, new MetricHelp(help, MetricType.Counter));
		_values[BuildKey(name, labels)] = value;
	}

	/// <summary>Adds a delta to a gauge series (use negative values to decrement).</summary>
	public void GaugeAdd(string name, string help, double value, params (string Name, string Value)[] labels)
	{
		_helps.TryAdd(name, new MetricHelp(help, MetricType.Gauge));
		AddToSeries(name, value, labels);
	}

	/// <summary>Sets a gauge series to an absolute value.</summary>
	public void GaugeSet(string name, string help, double value, params (string Name, string Value)[] labels)
	{
		_helps.TryAdd(name, new MetricHelp(help, MetricType.Gauge));
		_values[BuildKey(name, labels)] = value;
	}

	/// <summary>Reads the current value of a series (0 when never written).</summary>
	public double GetValue(string name, params (string Name, string Value)[] labels)
	{
		return _values.TryGetValue(BuildKey(name, labels), out var value) ? value : 0;
	}

	/// <summary>Sums every label set of a metric (0 when the metric has no series).</summary>
	public double SumSeries(string name)
	{
		var prefix = new SeriesKey(name, string.Empty);
		double sum = 0;
		foreach (var (key, value) in _values)
		{
			if (string.Equals(key.Name, prefix.Name, StringComparison.Ordinal))
			{
				sum += value;
			}
		}

		return sum;
	}

	/// <summary>Renders all series in Prometheus text exposition format (sorted by name).</summary>
	public string RenderPrometheus()
	{
		var sb = new StringBuilder();

		foreach (var (name, help) in _helps.OrderBy(static h => h.Key, StringComparer.Ordinal))
		{
			sb.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(help.Help)).Append('\n');
			sb.Append("# TYPE ").Append(name).Append(' ').Append(help.Type switch
			{
				MetricType.Counter => "counter",
				_ => "gauge"
			}).Append('\n');
		}

		foreach (var (key, value) in _values.OrderBy(static kv => kv.Key))
		{
			sb.Append(key.Name).Append(key.LabelSet).Append(' ')
				.Append(FormatValue(value)).Append('\n');
		}

		return sb.ToString();
	}

	private void AddToSeries(string name, double value, (string Name, string Value)[] labels)
	{
		_values.AddOrUpdate(BuildKey(name, labels), value, (_, current) => current + value);
	}

	private static SeriesKey BuildKey(string name, (string Name, string Value)[] labels)
	{
		if (labels.Length == 0)
		{
			return new SeriesKey(name, string.Empty);
		}

		var ordered = labels.OrderBy(static l => l.Name, StringComparer.Ordinal);
		var sb = new StringBuilder();
		sb.Append('{');
		foreach (var (labelName, labelValue) in ordered)
		{
			sb.Append(labelName).Append("=\"").Append(EscapeLabelValue(labelValue)).Append("\",");
		}

		sb[sb.Length - 1] = '}';
		return new SeriesKey(name, sb.ToString());
	}

	private static string EscapeLabelValue(string value) =>
		value.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal)
			.Replace("\n", "\\n", StringComparison.Ordinal);

	private static string EscapeHelp(string help) =>
		help.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

	private static string FormatValue(double value) =>
		double.IsFinite(value)
			? value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
			: value is double.PositiveInfinity ? "+Inf" : value is double.NegativeInfinity ? "-Inf" : "NaN";
}
