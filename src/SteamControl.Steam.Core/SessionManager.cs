using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Threading.Channels;
using SteamControl.Steam.Core.Steam;

namespace SteamControl.Steam.Core;

public interface ISessionManager
{
	Task<BotSession> GetOrCreateSessionAsync(
		string accountName,
		AccountCredentials credentials,
		CancellationToken cancellationToken = default
	);

	Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default);

	Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default);

	IReadOnlyList<BotSession> ListSessions();

	IAsyncEnumerable<SessionEvent> SubscribeAllEvents(CancellationToken cancellationToken = default);

	void SetEventCallback(SessionEventDelegate? callback);
}

public sealed class SessionManager : ISessionManager, IDisposable
{
	private readonly ConcurrentDictionary<string, BotSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
	private readonly IActionRegistry _actionRegistry;
	private readonly ILogger<SessionManager> _logger;
	private readonly ILoggerFactory _loggerFactory;
	private readonly Channel<SessionEvent> _eventChannel;
	private readonly CancellationTokenSource _cts;
	private readonly ISteamClientManager? _steamClientManager;
	private SessionEventDelegate? _eventCallback;

	public SessionManager(
		IActionRegistry actionRegistry,
		ILogger<SessionManager> logger,
		ISteamClientManager? steamClientManager = null,
		ILoggerFactory? loggerFactory = null)
	{
		_actionRegistry = actionRegistry;
		_logger = logger;
		_steamClientManager = steamClientManager;
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_eventChannel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = false });
		_cts = new CancellationTokenSource();
	}

	public void SetEventCallback(SessionEventDelegate? callback)
	{
		_eventCallback = callback;
	}

	public async Task<BotSession> GetOrCreateSessionAsync(
		string accountName,
		AccountCredentials credentials,
		CancellationToken cancellationToken = default)
	{
		if (_sessions.TryGetValue(accountName, out var existing))
		{
			return existing;
		}

		var session = new BotSession(
			accountName,
			credentials,
			_actionRegistry,
			_loggerFactory.CreateLogger<BotSession>(),
			_steamClientManager,
			_eventCallback
		);

		if (_sessions.TryAdd(accountName, session))
		{
			session.Start();

			_ = Task.Run(async () =>
			{
				try
				{
					await foreach (var evt in session.SubscribeEvents(_cts.Token))
					{
						_eventChannel.Writer.TryWrite(evt);
						// Also forward to event callback if set
						if (_eventCallback != null)
						{
							await _eventCallback.Invoke(accountName, evt.Type.ToString(), evt.NewState?.ToString() ?? "", evt.Message);
						}
					}
				}
				catch (OperationCanceledException)
				{
				}
			}, _cts.Token);

			_logger.LogInformation("Session created for {AccountName}", accountName);
		}
		else
		{
			session.Dispose();
			return _sessions[accountName];
		}

		return session;
	}

	public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(_sessions.TryGetValue(accountName, out var session) ? session : null);
	}

	public async Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default)
	{
		if (_sessions.TryRemove(accountName, out var session))
		{
			await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
			session.Dispose();
			_logger.LogInformation("Session removed for {AccountName}", accountName);
		}
	}

	public IReadOnlyList<BotSession> ListSessions()
	{
		return _sessions.Values.ToList();
	}

	public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents(CancellationToken cancellationToken = default)
	{
		await foreach (var evt in _eventChannel.Reader.ReadAllAsync(cancellationToken))
		{
			yield return evt;
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		_cts.Dispose();
		
		foreach (var session in _sessions.Values)
		{
			session.Dispose();
		}
		_sessions.Clear();
	}
}
