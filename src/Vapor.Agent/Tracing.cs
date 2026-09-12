using System.Diagnostics;
using Vapor.Protocol;

namespace Vapor.Agent;

/// <summary>
/// ActivitySource and W3C trace-context helpers for agent task-execution spans.
/// OTLP export is only wired up when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set;
/// without a listener the source is inert and costs essentially nothing.
/// </summary>
public static class VaporAgentTracing
{
	public const string SourceName = "Vapor.Agent";

	public static readonly ActivitySource Source = new(SourceName, "1.0.0");

	/// <summary>
	/// Starts a task.execute span parented to the dispatch span that sent the task
	/// (via the traceparent header on the tunnel message), or to the ambient context
	/// when no usable header is present.
	/// </summary>
	public static Activity? StartExecuteSpan(JobTask task, IReadOnlyDictionary<string, string>? traceHeaders)
	{
		ActivityContext parent = default;
		bool hasParent = traceHeaders is not null
			&& traceHeaders.TryGetValue("traceparent", out string? traceparent)
			&& ActivityContext.TryParse(traceparent, null, out parent);

		Activity? activity = Source.StartActivity("task.execute", ActivityKind.Consumer, parent);
		if (activity is null)
		{
			return null;
		}

		activity.SetTag("vapor.task_id", task.Id)
			.SetTag("vapor.job_id", task.JobId)
			.SetTag("vapor.action", task.Action)
			.SetTag("vapor.target", task.Target)
			.SetTag("vapor.attempt", task.Attempt);

		return activity;
	}
}
