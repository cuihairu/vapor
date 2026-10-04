using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.Steam.Core;

public interface IAction
{
	string Name { get; }

	ActionMetadata Metadata { get; }

	Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken
	);
}

public sealed record ActionMetadata(
	string Name,
	string Description,
	bool RequiresLogin = false,
	int? TimeoutSeconds = null
)
{
	/// <summary>
	/// Execution-safety classification (see <see cref="ActionSafety"/>). Defaults to
	/// <see cref="ActionSafety.Unknown"/>, which the scheduler treats conservatively
	/// (bounded redelivery). Added as an init-only property rather than a positional
	/// parameter so plugin assemblies built against older metadata keep loading.
	/// </summary>
	public ActionSafety Safety { get; init; } = ActionSafety.Unknown;
}

public sealed record ActionResult(
	bool Success,
	string? Error = null,
	IReadOnlyDictionary<string, object?>? Output = null
);

public interface IActionRegistry
{
	void Register(IAction action);
	bool Unregister(string name);
	IAction? Get(string? name);
	IReadOnlyList<string> ListNames();
}

public sealed class ActionRegistry : IActionRegistry
{
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAction> _actions = new(StringComparer.OrdinalIgnoreCase);
	private readonly System.Collections.Concurrent.ConcurrentDictionary<IActionExecutionObserver, byte> _executionObservers = new();
	private readonly ILogger<ActionRegistry> _logger;

	public ActionRegistry(ILogger<ActionRegistry> logger)
	{
		_logger = logger;
	}

	public void Register(IAction action)
	{
		_actions[action.Name] = action;
		_logger.LogInformation("Registered action: {ActionName}", action.Name);
		if (action.Metadata.Safety == ActionSafety.Unknown)
		{
			_logger.LogWarning(
				"Registered action {ActionName} without an execution-safety classification; the scheduler treats it conservatively (bounded redispatch)",
				action.Name);
		}
	}

	public bool Unregister(string name)
	{
		var removed = _actions.TryRemove(name, out _);
		if (removed)
		{
			_logger.LogInformation("Unregistered action: {ActionName}", name);
		}

		return removed;
	}

	/// <summary>
	/// Subscribes an observer to action execution outcomes. Observers are notified via
	/// <see cref="RaiseActionExecuted"/> and must be removed before disposal
	/// (e.g. when a plugin unloads).
	/// </summary>
	public void AddExecutionObserver(IActionExecutionObserver observer)
	{
		_executionObservers[observer] = 1;
	}

	/// <summary>Removes a previously subscribed execution observer.</summary>
	public bool RemoveExecutionObserver(IActionExecutionObserver observer)
	{
		return _executionObservers.TryRemove(observer, out _);
	}

	/// <summary>
	/// Notifies all subscribed observers of an execution outcome. Observer exceptions are
	/// swallowed so monitoring can never break action execution.
	/// </summary>
	public void RaiseActionExecuted(string actionName, bool success, double durationMs)
	{
		foreach (var observer in _executionObservers.Keys)
		{
			try
			{
				observer.OnActionExecuted(actionName, success, durationMs);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Action execution observer threw for action {ActionName}", actionName);
			}
		}
	}

	public IAction? Get(string? name)
	{
		if (name is null)
		{
			return null;
		}

		return _actions.TryGetValue(name, out var action) ? action : null;
	}

	public IReadOnlyList<string> ListNames()
	{
		return _actions.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
	}
}

