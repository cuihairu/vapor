using System.Diagnostics;

namespace Vapor.ControlPlane;

/// <summary>
/// ActivitySource and W3C trace-context helpers for control-plane dispatch spans.
/// OTLP export is only wired up when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set;
/// without a listener the source is inert (<see cref="ActivitySource.StartActivity"/>
/// returns null) and costs essentially nothing.
/// </summary>
public static class VaporTracing {
	public const string SourceName = "Vapor.ControlPlane";

	public static readonly ActivitySource Source = new(SourceName, "1.0.0");

	/// <summary>Wraps the activity's W3C id as a traceparent header for tunnel propagation.</summary>
	public static IReadOnlyDictionary<string, string>? InjectTraceparent(Activity? activity) {
		if (activity is null || activity.Id is null) {
			return null;
		}

		return new Dictionary<string, string> { ["traceparent"] = activity.Id };
	}

	/// <summary>Parses a traceparent header set produced by <see cref="InjectTraceparent"/>.</summary>
	public static bool TryExtractContext(IReadOnlyDictionary<string, string>? traceHeaders, out ActivityContext context) {
		context = default;
		return traceHeaders is not null
			&& traceHeaders.TryGetValue("traceparent", out string? traceparent)
			&& ActivityContext.TryParse(traceparent, null, out context);
	}
}
