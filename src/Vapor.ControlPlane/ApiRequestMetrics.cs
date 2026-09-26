using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Vapor.ControlPlane;

/// <summary>
/// RED metrics (request rate, error rate, duration) for the REST surface,
/// exposed on <c>/metrics</c> independently of the optional OpenTelemetry
/// pipeline so the scrape endpoint is a complete story on its own. Only
/// endpoint-resolved requests are recorded, with the route pattern (e.g.
/// <c>/v1/jobs/{jobId}</c>) as the label — cardinality is bounded by the route
/// table, not by traffic. The label alphabet (HTTP method tokens and route
/// pattern characters) cannot produce quotes or backslashes, so no label
/// escaping is needed.
/// </summary>
public sealed class ApiRequestMetrics
{
	private readonly ConcurrentDictionary<(string Method, string Route, int Status), long> _requests = new();
	private readonly ConcurrentDictionary<(string Method, string Route), (long Count, double Sum)> _durations = new();
	private long _rateLimited;

	public long RateLimited => Interlocked.Read(ref _rateLimited);

	public void RecordRateLimited()
	{
		Interlocked.Increment(ref _rateLimited);
	}

	public void RecordRequest(string method, string route, int statusCode, double durationSeconds)
	{
		_requests.AddOrUpdate((method, route, statusCode), 1, static (_, count) => count + 1);
		_durations.AddOrUpdate((method, route), (1, durationSeconds), (_, pair) => (pair.Count + 1, pair.Sum + durationSeconds));
	}

	/// <summary>Appends the API metric families to a Prometheus exposition, sorted for stable output.</summary>
	public void Render(StringBuilder sb)
	{
		sb.Append("# HELP vapor_controlplane_http_requests_total HTTP requests handled by the REST surface, by method, route pattern and status.\n");
		sb.Append("# TYPE vapor_controlplane_http_requests_total counter\n");
		foreach ((string Method, string Route, int Status) key in _requests.Keys.OrderBy(k => k.Method, StringComparer.Ordinal).ThenBy(k => k.Route, StringComparer.Ordinal).ThenBy(k => k.Status))
		{
			sb.Append("vapor_controlplane_http_requests_total{method=\"").Append(key.Method)
				.Append("\",route=\"").Append(key.Route)
				.Append("\",status=\"").Append(key.Status.ToString(CultureInfo.InvariantCulture))
				.Append("\"} ").Append(_requests[key].ToString(CultureInfo.InvariantCulture)).Append('\n');
		}

		sb.Append("# HELP vapor_controlplane_http_request_duration_seconds HTTP request durations by method and route pattern.\n");
		sb.Append("# TYPE vapor_controlplane_http_request_duration_seconds summary\n");
		foreach ((string Method, string Route) key in _durations.Keys.OrderBy(k => k.Method, StringComparer.Ordinal).ThenBy(k => k.Route, StringComparer.Ordinal))
		{
			(long count, double sum) = _durations[key];
			sb.Append("vapor_controlplane_http_request_duration_seconds_sum{method=\"").Append(key.Method)
				.Append("\",route=\"").Append(key.Route)
				.Append("\"} ").Append(sum.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
			sb.Append("vapor_controlplane_http_request_duration_seconds_count{method=\"").Append(key.Method)
				.Append("\",route=\"").Append(key.Route)
				.Append("\"} ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
		}

		sb.Append("# HELP vapor_controlplane_rate_limited_total Requests rejected by the opt-in per-key rate limiter.\n");
		sb.Append("# TYPE vapor_controlplane_rate_limited_total counter\n");
		sb.Append("vapor_controlplane_rate_limited_total ").Append(RateLimited.ToString(CultureInfo.InvariantCulture)).Append('\n');
	}
}
