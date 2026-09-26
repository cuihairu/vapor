using System.Globalization;
using System.Text;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// ApiRequestMetrics — the RED families rendered on /metrics independently of
// the optional OpenTelemetry pipeline: count/sum aggregation per (method,
// route pattern, status), a deterministically sorted exposition, invariant
// culture floats, and the rate-limited counter.
public sealed class ApiRequestMetricsTests
{
	private static string Render(ApiRequestMetrics metrics)
	{
		StringBuilder sb = new();
		metrics.Render(sb);
		return sb.ToString();
	}

	[Fact]
	public void Render_EmptyMetrics_ShowFamiliesWithZeroCounters()
	{
		string exposition = Render(new ApiRequestMetrics());

		Assert.Contains("# HELP vapor_controlplane_http_requests_total ", exposition);
		Assert.Contains("# TYPE vapor_controlplane_http_requests_total counter", exposition);
		Assert.Contains("# TYPE vapor_controlplane_http_request_duration_seconds summary", exposition);
		Assert.Contains("# TYPE vapor_controlplane_rate_limited_total counter", exposition);
		Assert.Contains("vapor_controlplane_rate_limited_total 0", exposition);
		// No traffic yet → family headers exist, sample lines do not.
		Assert.DoesNotContain("vapor_controlplane_http_requests_total{", exposition);
		Assert.DoesNotContain("vapor_controlplane_http_request_duration_seconds_sum{", exposition);
	}

	[Fact]
	public void RecordRequest_AggregatesCountsAndSumsPerRoute()
	{
		ApiRequestMetrics metrics = new();
		metrics.RecordRequest("GET", "/v1/a", 200, 0.5);
		metrics.RecordRequest("GET", "/v1/a", 200, 0.25);
		metrics.RecordRequest("GET", "/v1/a", 500, 1.0);
		metrics.RecordRequest("POST", "/v1/b", 202, 2.0);

		string exposition = Render(metrics);
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/v1/a\",status=\"200\"} 2", exposition);
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/v1/a\",status=\"500\"} 1", exposition);
		Assert.Contains("vapor_controlplane_http_requests_total{method=\"POST\",route=\"/v1/b\",status=\"202\"} 1", exposition);
		Assert.Contains("vapor_controlplane_http_request_duration_seconds_sum{method=\"GET\",route=\"/v1/a\"} 1.75", exposition);
		Assert.Contains("vapor_controlplane_http_request_duration_seconds_count{method=\"GET\",route=\"/v1/a\"} 3", exposition);
		Assert.Contains("vapor_controlplane_http_request_duration_seconds_count{method=\"POST\",route=\"/v1/b\"} 1", exposition);
		Assert.Contains("vapor_controlplane_rate_limited_total 0", exposition);
	}

	[Fact]
	public void Render_SortsByMethodRouteAndStatus()
	{
		ApiRequestMetrics metrics = new();
		metrics.RecordRequest("POST", "/v1/z", 200, 0.1);
		metrics.RecordRequest("GET", "/v1/z", 500, 0.1);
		metrics.RecordRequest("GET", "/v1/z", 200, 0.1);
		metrics.RecordRequest("GET", "/v1/a", 200, 0.1);

		string exposition = Render(metrics);
		int getA = exposition.IndexOf("route=\"/v1/a\"", StringComparison.Ordinal);
		int getZ200 = exposition.IndexOf("vapor_controlplane_http_requests_total{method=\"GET\",route=\"/v1/z\",status=\"200\"}", StringComparison.Ordinal);
		int getZ500 = exposition.IndexOf("route=\"/v1/z\",status=\"500\"", StringComparison.Ordinal);
		int postZ = exposition.IndexOf("method=\"POST\"", StringComparison.Ordinal);
		Assert.True(getA < getZ200 && getZ200 < getZ500 && getZ500 < postZ, exposition);
	}

	[Fact]
	public void Render_UsesInvariantCultureForDurations()
	{
		CultureInfo original = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator
		try
		{
			ApiRequestMetrics metrics = new();
			metrics.RecordRequest("GET", "/v1/c", 200, 1.5);

			string exposition = Render(metrics);
			Assert.Contains("vapor_controlplane_http_request_duration_seconds_sum{method=\"GET\",route=\"/v1/c\"} 1.5", exposition);
			Assert.DoesNotContain("1,5", exposition);
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	[Fact]
	public void RecordRateLimited_CountsRejections()
	{
		ApiRequestMetrics metrics = new();
		metrics.RecordRateLimited();
		metrics.RecordRateLimited();
		metrics.RecordRateLimited();

		Assert.Equal(3, metrics.RateLimited);
		Assert.Contains("vapor_controlplane_rate_limited_total 3", Render(metrics));
	}
}
