using Microsoft.Extensions.Logging;

namespace SteamControl.Steam.Core;

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
	bool RequiresLogin = true,
	int? TimeoutSeconds = null
);

public sealed record ActionResult(
	bool Success,
	string? Error = null,
	IReadOnlyDictionary<string, object?>? Output = null
);

public interface IActionRegistry
{
	void Register(IAction action);
	IAction? Get(string name);
	IReadOnlyList<string> ListNames();
}

public sealed class ActionRegistry : IActionRegistry
{
	private readonly Dictionary<string, IAction> _actions = new(StringComparer.OrdinalIgnoreCase);
	private readonly ILogger<ActionRegistry> _logger;

	public ActionRegistry(ILogger<ActionRegistry> logger)
	{
		_logger = logger;
	}

	public void Register(IAction action)
	{
		_actions[action.Name] = action;
		_logger.LogInformation("Registered action: {ActionName}", action.Name);
	}

	public IAction? Get(string name)
	{
		return _actions.TryGetValue(name, out var action) ? action : null;
	}

	public IReadOnlyList<string> ListNames()
	{
		return _actions.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
	}
}
