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
	private readonly TimeSpan _tokenRefreshCheckInterval;
	private readonly TimeSpan _tokenRefreshLeadTime;
	private readonly ConcurrentDictionary<string, byte> _tokenRefreshInFlight = new(StringComparer.OrdinalIgnoreCase);
	private SessionEventDelegate? _eventCallback;
	private readonly Task? _tokenRefreshTask;

	public SessionManager(
		IActionRegistry actionRegistry,
		ILogger<SessionManager> logger,
		ISteamClientManager? steamClientManager = null,
		ICredentialStore? credentialStore = null,
		ILoggerFactory? loggerFactory = null,
		TimeSpan? tokenRefreshCheckInterval = null,
		TimeSpan? tokenRefreshLeadTime = null)
	{
		_actionRegistry = actionRegistry;
		_logger = logger;
		_steamClientManager = steamClientManager;
		_credentialStore = credentialStore;
		_loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
		_tokenRefreshCheckInterval = tokenRefreshCheckInterval ?? TimeSpan.FromMinutes(1);
		_tokenRefreshLeadTime = tokenRefreshLeadTime ?? TimeSpan.FromMinutes(15);
		_eventChannel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = false });
		_cts = new CancellationTokenSource();

		if (_steamClientManager != null && _credentialStore != null)
		{
			_tokenRefreshTask = Task.Run(() => RunTokenRefreshLoopAsync(_cts.Token), _cts.Token);
		}
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

			var restoreResult = await session.LoginAsync(cancellationToken).ConfigureAwait(false);
			if (!restoreResult.Success)
			{
				_sessions.TryRemove(accountName, out _);
				session.Dispose();
				_logger.LogWarning("Session restore failed for {AccountName}: {Error}", accountName, restoreResult.Error);
				return null;
			}

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

	private async Task RunTokenRefreshLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			using var timer = new PeriodicTimer(_tokenRefreshCheckInterval);
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
			{
				await RefreshExpiringSessionsAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Background token refresh loop failed");
		}
	}

	private async Task RefreshExpiringSessionsAsync(CancellationToken cancellationToken)
	{
		if (_credentialStore == null || _steamClientManager == null)
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		foreach (var session in _sessions.Values)
		{
			if (session.State != SessionState.Connected)
			{
				continue;
			}

			var accountName = session.AccountName;
			var accessToken = await _credentialStore.GetAccessTokenAsync(accountName, cancellationToken).ConfigureAwait(false);

			var shouldRefresh = accessToken == null || accessToken.ExpiresAt <= now.Add(_tokenRefreshLeadTime);
			if (!shouldRefresh)
			{
				continue;
			}

			if (!await _credentialStore.HasCredentialsAsync(accountName, cancellationToken).ConfigureAwait(false))
			{
				continue;
			}

			if (!_tokenRefreshInFlight.TryAdd(accountName, 0))
			{
				continue;
			}

			try
			{
				_logger.LogInformation("Refreshing access token for {AccountName}", accountName);
				var refreshed = await _steamClientManager.RefreshAccessTokenAsync(accountName, cancellationToken).ConfigureAwait(false);
				if (!refreshed)
				{
					_logger.LogWarning("Access token refresh failed for {AccountName}", accountName);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Access token refresh threw for {AccountName}", accountName);
			}
			finally
			{
				_tokenRefreshInFlight.TryRemove(accountName, out _);
			}
		}
	}
}
