using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Protocol;

namespace Vapor.ControlPlane;

/// <summary>
/// Bridges the three EventBroker event streams (job events, session events, auth
/// challenges) to the registered notification sinks. Each sink is filtered by its own
/// rule and isolated: a sink that throws (or exhausts its delivery retries) never
/// blocks the event loop or the other sinks.
/// </summary>
public sealed class NotificationService : BackgroundService
{
	private readonly IEventBroker _broker;
	private readonly INotificationSink[] _sinks;
	private readonly ILogger<NotificationService> _logger;

	public NotificationService(IEventBroker broker, IEnumerable<INotificationSink> sinks, ILogger<NotificationService> logger)
	{
		_broker = broker;
		_sinks = sinks.ToArray();
		_logger = logger;
	}

	public int SinkCount => _sinks.Length;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (_sinks.Length == 0)
		{
			return;
		}

		_logger.LogInformation("Notification service started with {SinkCount} sink(s): {Sinks}",
			_sinks.Length, string.Join(", ", _sinks.Select(s => s.Name)));

		await Task.WhenAll(
			PumpJobs(stoppingToken),
			PumpSessions(stoppingToken),
			PumpAuthChallenges(stoppingToken)).ConfigureAwait(false);
	}

	private async Task PumpJobs(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (Event e in _broker.Subscribe(cancellationToken, "*").ConfigureAwait(false))
			{
				await DispatchAsync(new NotificationEvent(
					e.Id, "job", e.Type, e.JobId,
					AccountNameFromPayload(e.Payload),
					StateFromPayload(e.Payload),
					MessageFromPayload(e.Payload),
					e.Ts, e.Payload), cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private async Task PumpSessions(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (SessionEvent e in _broker.SubscribeSessions(cancellationToken).ConfigureAwait(false))
			{
				await DispatchAsync(new NotificationEvent(
					e.Id, "session", e.EventType, null,
					e.AccountName, e.State, e.Message,
					e.Timestamp, null), cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private async Task PumpAuthChallenges(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (AuthChallengeEvent e in _broker.SubscribeAuthChallenges(cancellationToken).ConfigureAwait(false))
			{
				// Deliberately excludes the challenge code itself: it must not leak to
				// webhook consumers. "codeSupplied" signals a code was already provided.
				var payload = new Dictionary<string, object?> { ["codeSupplied"] = e.Code is not null };
				await DispatchAsync(new NotificationEvent(
					e.Id, "auth_challenge", e.ChallengeType, e.JobId,
					e.AccountName, null, e.Message,
					e.Timestamp, payload), cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	private async Task DispatchAsync(NotificationEvent notification, CancellationToken cancellationToken)
	{
		foreach (INotificationSink sink in _sinks)
		{
			if (!sink.Rule.Matches(notification))
			{
				continue;
			}

			try
			{
				await sink.HandleAsync(notification, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				// Failure isolation: one broken sink must not block the event loop or the others.
				_logger.LogError(ex, "Notification sink {Sink} failed to handle {Category}/{Type} (account={Account}, job={Job})",
					sink.Name, notification.Category, notification.Type, notification.AccountName ?? "<none>", notification.JobId ?? "<none>");
			}
		}
	}

	private static string? AccountNameFromPayload(IReadOnlyDictionary<string, object?>? payload) =>
		StringFromPayload(payload, "accountName") ?? StringFromPayload(payload, "account") ?? StringFromPayload(payload, "target");

	private static string? StateFromPayload(IReadOnlyDictionary<string, object?>? payload) =>
		StringFromPayload(payload, "state");

	private static string? MessageFromPayload(IReadOnlyDictionary<string, object?>? payload) =>
		StringFromPayload(payload, "message") ?? StringFromPayload(payload, "error");

	private static string? StringFromPayload(IReadOnlyDictionary<string, object?>? payload, string key)
	{
		if (payload is null || !payload.TryGetValue(key, out object? value))
		{
			return null;
		}

		return value switch
		{
			string s => s,
			JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
			_ => null,
		};
	}
}
