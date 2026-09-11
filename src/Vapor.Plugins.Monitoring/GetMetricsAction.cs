using Vapor.Steam.Core;

namespace Vapor.Plugins.Monitoring;

/// <summary>
/// Returns the current metrics snapshot (Prometheus exposition plus a JSON summary) so
/// the Control Plane can pull monitoring data through normal task execution.
/// </summary>
public sealed class GetMetricsAction : IAction
{
	private readonly Func<string> _renderPrometheus;
	private readonly Func<string> _renderJson;

	public GetMetricsAction(Func<string> renderPrometheus, Func<string> renderJson)
	{
		_renderPrometheus = renderPrometheus;
		_renderJson = renderJson;
	}

	public string Name => "get_metrics";

	public ActionMetadata Metadata { get; } = new(
		Name: "get_metrics",
		Description: "Returns the current metrics snapshot (Prometheus text format and JSON summary).",
		RequiresLogin: false,
		TimeoutSeconds: 10);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var output = new Dictionary<string, object?>
		{
			["format"] = "prometheus",
			["metrics"] = _renderPrometheus(),
			["summary"] = _renderJson()
		};

		return Task.FromResult(new ActionResult(true, Output: output));
	}
}
