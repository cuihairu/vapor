using Microsoft.Extensions.Logging;
using Vapor.Steam.Core;

namespace Vapor.Plugins.Core;

/// <summary>
/// Fans host session events out to all registered <see cref="IEventPlugin"/>s.
/// The host registers subscribers as plugins load and removes them as they unload;
/// handler failures are isolated per plugin (logged, never propagated to the event
/// source or other subscribers). Start once per host; disposing stops the pump.
/// </summary>
public sealed class PluginEventDispatcher : IAsyncDisposable
{
	private readonly object _gate = new();
	private readonly List<IEventPlugin> _subscribers = [];
	private readonly ILogger<PluginEventDispatcher> _logger;
	private CancellationTokenSource? _pumpCts;
	private Task? _pump;
	private bool _disposed;

	public PluginEventDispatcher(ILoggerFactory loggerFactory)
	{
		_logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<PluginEventDispatcher>();
	}

	/// <summary>Number of plugins currently registered for events.</summary>
	public int SubscriberCount
	{
		get
		{
			lock (_gate)
			{
				return _subscribers.Count;
			}
		}
	}

	/// <summary>Registers a subscriber (no-op when already registered).</summary>
	public void Add(IEventPlugin plugin)
	{
		ArgumentNullException.ThrowIfNull(plugin);

		lock (_gate)
		{
			if (_subscribers.Any(existing => ReferenceEquals(existing, plugin)))
			{
				return;
			}

			_subscribers.Add(plugin);
		}
	}

	/// <summary>Removes a subscriber; returns false when it was not registered.</summary>
	public bool Remove(IEventPlugin plugin)
	{
		ArgumentNullException.ThrowIfNull(plugin);

		lock (_gate)
		{
			for (int i = 0; i < _subscribers.Count; i++)
			{
				if (ReferenceEquals(_subscribers[i], plugin))
				{
					_subscribers.RemoveAt(i);
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>Starts pumping session events from the manager to all subscribers. Idempotent.</summary>
	public void Start(ISessionManager sessionManager)
	{
		ArgumentNullException.ThrowIfNull(sessionManager);

		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_pump is not null)
			{
				return;
			}

			_pumpCts = new CancellationTokenSource();
			_pump = PumpAsync(sessionManager, _pumpCts.Token);
		}
	}

	private async Task PumpAsync(ISessionManager sessionManager, CancellationToken cancellationToken)
	{
		try
		{
			await foreach (SessionEvent evt in sessionManager.SubscribeAllEvents(cancellationToken).ConfigureAwait(false))
			{
				await DispatchAsync(evt, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Session event pump stopped unexpectedly");
		}
	}

	private async Task DispatchAsync(SessionEvent evt, CancellationToken cancellationToken)
	{
		IEventPlugin[] subscribers;
		lock (_gate)
		{
			subscribers = [.. _subscribers];
		}

		foreach (IEventPlugin plugin in subscribers)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				return;
			}

			try
			{
				await plugin.OnSessionEventAsync(evt, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Plugin {PluginId} failed handling session event {EventType}", plugin.Info.Id, evt.Type);
			}
		}
	}

	public async ValueTask DisposeAsync()
	{
		Task? pump;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_pumpCts?.Cancel();
			pump = _pump;
			_subscribers.Clear();
		}

		if (pump is not null)
		{
			// The pump swallows its own errors, so this await never throws.
			await pump.ConfigureAwait(false);
		}

		_pumpCts?.Dispose();
		_pumpCts = null;
		_pump = null;
	}
}
