using Microsoft.Extensions.Logging;
using Vapor.Protocol;
using Vapor.Steam.Core;

namespace Vapor.Agent;

/// <summary>
/// Host action "set_proxy": assigns (or clears) an account's egress proxy. The
/// agent validates and persists the endpoint in its credential store, then
/// rebuilds a live session through the new exit; the output reports whether the
/// restart happened immediately or is deferred to the next login. Output only
/// ever carries the masked endpoint form — credentials never leave the agent.
/// </summary>
public sealed class SetProxyAction : IHostAction
{
	private readonly ISessionManager _sessionManager;
	private readonly ILogger _logger;

	public SetProxyAction(ISessionManager sessionManager, ILogger logger)
	{
		_sessionManager = sessionManager;
		_logger = logger;
	}

	public string Name => "set_proxy";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Assigns (or clears) the egress proxy for an account and rebuilds its live session through the new exit",
		RequiresLogin: false,
		TimeoutSeconds: 120
	)
	{ Safety = ActionSafety.GuardedWrite };

	public async Task<ActionResult> ExecuteAsync(
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		string? account = PayloadReader.GetString(payload, "account");
		string? proxy = PayloadReader.GetString(payload, "proxy");

		if (string.IsNullOrWhiteSpace(account))
		{
			return new ActionResult(false, "payload field 'account' (target account name) is required", null);
		}

		// A missing proxy field means clear; an invalid endpoint surfaces as a
		// failed result carrying the static (credential-free) validation text.
		ProxyAssignmentResult assignment;
		try
		{
			assignment = await _sessionManager
				.SetProxyAsync(account, proxy, cancellationToken).ConfigureAwait(false);
		}
		catch (ArgumentException ex)
		{
			return new ActionResult(false, ex.Message, null);
		}

		_logger.LogInformation(
			"set_proxy for {AccountName} completed: {State}, session restart {Outcome}",
			account, assignment.Cleared ? "cleared" : "assigned",
			assignment.SessionRestarted ? "immediate" : "deferred");

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["account"] = account,
			["proxy"] = assignment.Proxy,
			["cleared"] = assignment.Cleared,
			["sessionRestarted"] = assignment.SessionRestarted
		});
	}
}
