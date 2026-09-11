namespace Vapor.Steam.Core;

/// <summary>
/// Receives action execution outcomes for observability. Implementations must be
/// exception-safe and fast: they run inline on the session's action execution path.
/// </summary>
public interface IActionExecutionObserver
{
	/// <summary>
	/// Called after an action execution completes.
	/// </summary>
	/// <param name="actionName">The executed action's name.</param>
	/// <param name="success">Whether the action reported success.</param>
	/// <param name="durationMs">Wall-clock duration of the execution in milliseconds.</param>
	void OnActionExecuted(string actionName, bool success, double durationMs);
}
