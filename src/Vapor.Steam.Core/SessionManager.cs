using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core;

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

	/// <summary>
	/// Attempts to restore a session for the given account using stored credentials.
	/// </summary>
	Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default);
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
	private readonly ICredentialStore? _credentialStore;
	private SessionEventDelegate? _eventCallback;

	public SessionManager(
		IActionRegistry actionRegistry,
		ILogger<SessionManager> logger,
		ISteamClientManager? steamClientManager = null,
		ICredentialStore? credentialStore = null,
		ILoggerFactory? loggerFactory = null)
	{
		_actionRegistry = actionRegistry;
		_logger = logger;
		_steamClientManager = steamClientManager;
		_credentialStore = credentialStore;
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_eventChannel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = false });
		_cts = new CancellationTokenSource();
	}

	public void SetEventCallback(SessionEventDelegate? callback)
	{
		_eventCallback = callback;
	}

	public Task<BotSession> GetOrCreateSessionAsync(
		string accountName,
		AccountCredentials credentials,
		CancellationToken cancellationToken = default)
	{
		if (_sessions.TryGetValue(accountName, out var existing))
		{
			return Task.FromResult(existing);
		}

		var session = new BotSession(
			accountName,
			credentials,
			_actionRegistry,
			_loggerFactory.CreateLogger<BotSession>(),
			_steamClientManager,
			steamWebHandler: new SteamWebHandler(
				new SteamWebHandlerConfig(),
				_loggerFactory.CreateLogger<SteamWebHandler>()
			),
			eventCallback: _eventCallback
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
			return Task.FromResult(_sessions[accountName]);
		}

		return Task.FromResult(session);
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

	public async Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		if (_credentialStore == null)
		{
			_logger.LogDebug("Cannot restore session: no credential store configured");
			return null;
		}

		// Check if session already exists
		if (_sessions.TryGetValue(accountName, out var existing))
		{
			return existing;
		}

		// Check if credentials exist in store
		var hasCredentials = await _credentialStore.HasCredentialsAsync(accountName, cancellationToken).ConfigureAwait(false);
		if (!hasCredentials)
		{
			_logger.LogDebug("No stored credentials found for {AccountName}", accountName);
			return null;
		}

		// Create credentials from stored tokens
		var credentials = new AccountCredentials(
			AccountName: accountName,
			Password: string.Empty, // No password needed for token-based login
			RefreshToken: await _credentialStore.GetRefreshTokenAsync(accountName, cancellationToken).ConfigureAwait(false),
			AccessToken: (await _credentialStore.GetAccessTokenAsync(accountName, cancellationToken).ConfigureAwait(false))?.Token
		);

		// Create a new session with restored credentials
		var session = new BotSession(
			accountName,
			credentials,
			_actionRegistry,
			_loggerFactory.CreateLogger<BotSession>(),
			_steamClientManager,
			steamWebHandler: new SteamWebHandler(
				new SteamWebHandlerConfig(),
				_loggerFactory.CreateLogger<SteamWebHandler>()
			),
			eventCallback: _eventCallback
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

			_logger.LogInformation("Session restored for {AccountName} from stored credentials", accountName);
		}
		else
		{
			session.Dispose();
			return _sessions.TryGetValue(accountName, out var existingSession) ? existingSession : null;
		}

		return session;
	}

	public IReadOnlyList<BotSession> ListSessions()
	{
		return _sessions.Values.ToList();
	}

	public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
