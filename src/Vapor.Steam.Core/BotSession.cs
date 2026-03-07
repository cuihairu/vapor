using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core;

public sealed record AccountCredentials(
	string AccountName,
	string Password,
	string? AuthCode = null,
	string? TwoFactorCode = null,
	string? RefreshToken = null,
	string? AccessToken = null
);

public delegate Task SessionEventDelegate(string accountName, string eventType, string state, string? message);

public sealed class BotSession : IDisposable
{
	private readonly ILogger<BotSession> _logger;
	private readonly string _accountName;
	private readonly AccountCredentials _credentials;
	private readonly IActionRegistry _actionRegistry;
	private readonly Channel<SessionCommand> _commandChannel;
	private readonly Channel<SessionEvent> _eventChannel;
	private readonly CancellationTokenSource _cts;
	private readonly SemaphoreSlim _actionLock;
	private readonly ISteamClientManager? _steamClientManager;
	private readonly SessionEventDelegate? _eventCallback;
	private readonly SteamWebHandler? _steamWebHandler;
	private readonly object _startLock = new();
	private int _disposed;
	
	private SessionState _state = SessionState.Disconnected;
	private DateTimeOffset _lastHeartbeat = DateTimeOffset.UtcNow;
	private Task? _backgroundTask;
	private Task? _steamCallbackTask;

	public string AccountName => _accountName;
	public SessionState State => _state;
	public DateTimeOffset ConnectedAt { get; private set; }
	public DateTimeOffset LastHeartbeat => _lastHeartbeat;
	public ISteamClientManager? SteamClientManager => _steamClientManager;
	public SteamWebHandler? SteamWebHandler => _steamWebHandler;

	public BotSession(
		string accountName,
		AccountCredentials credentials,
		IActionRegistry actionRegistry,
		ILogger<BotSession> logger,
		ISteamClientManager? steamClientManager = null,
		SteamWebHandler? steamWebHandler = null,
		SessionEventDelegate? eventCallback = null)
	{
		_accountName = accountName;
		_credentials = credentials;
		_actionRegistry = actionRegistry;
		_logger = logger;
		_steamClientManager = steamClientManager;
		_steamWebHandler = steamWebHandler;
		_eventCallback = eventCallback;
		_commandChannel = Channel.CreateUnbounded<SessionCommand>(new UnboundedChannelOptions { SingleReader = true });
		_eventChannel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = false });
		_cts = new CancellationTokenSource();
		_actionLock = new SemaphoreSlim(1, 1);
	}

	public void Start()
	{
		lock (_startLock)
		{
			if (_backgroundTask != null)
			{
				throw new InvalidOperationException("Session already started");
			}

			StartCore();
		}
	}

	public async Task<SessionCommandResult> ExecuteActionAsync(
		string actionName,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken = default)
	{
		var tcs = new TaskCompletionSource<SessionCommandResult>();
		
		var cmd = new SessionCommand(
			Guid.NewGuid().ToString(),
			SessionCommandType.ExecuteAction,
			actionName,
			payload,
			tcs,
			cancellationToken
		);

		if (!_commandChannel.Writer.TryWrite(cmd))
		{
			return new SessionCommandResult(false, "command queue full", null);
		}

		return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public void ProvideAuthCode(string code)
	{
		var cmd = new SessionCommand(
			Guid.NewGuid().ToString(),
			SessionCommandType.ProvideAuthCode,
			code,
			null,
			null,
			CancellationToken.None
		);
		_commandChannel.Writer.TryWrite(cmd);
	}

	public void Provide2FACode(string code)
	{
		var cmd = new SessionCommand(
			Guid.NewGuid().ToString(),
			SessionCommandType.Provide2FACode,
			code,
			null,
			null,
			CancellationToken.None
		);
		_commandChannel.Writer.TryWrite(cmd);
	}

	public async Task DisconnectAsync(CancellationToken cancellationToken = default)
	{
		if (_backgroundTask == null)
		{
			return;
		}

		var tcs = new TaskCompletionSource<SessionCommandResult>();
		var cmd = new SessionCommand(
			Guid.NewGuid().ToString(),
			SessionCommandType.Disconnect,
			null,
			null,
			tcs,
			cancellationToken
		);

		_commandChannel.Writer.TryWrite(cmd);
		await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<SessionCommandResult> LoginAsync(CancellationToken cancellationToken = default)
	{
		EnsureStarted();
		return await ConnectAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task<SessionCommandResult> LoginDirectAsync(CancellationToken cancellationToken = default)
	{
		return LoginCoreAsync(cancellationToken);
	}

	public IAsyncEnumerable<SessionEvent> SubscribeEvents(CancellationToken cancellationToken = default)
	{
		return _eventChannel.Reader.ReadAllAsync(cancellationToken);
	}

	private void EnsureStarted()
	{
		lock (_startLock)
		{
			if (_backgroundTask != null)
			{
				return;
			}

			StartCore();
		}
	}

	private void StartCore()
	{
		_backgroundTask = RunAsync(_cts.Token);

		if (_steamClientManager != null)
		{
			_steamCallbackTask = Task.Run(() => RunSteamCallbacksAsync(_cts.Token), _cts.Token);
		}
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var cmd in _commandChannel.Reader.ReadAllAsync(cancellationToken))
			{
				try
				{
					switch (cmd.Type)
					{
						case SessionCommandType.ExecuteAction:
							await HandleExecuteAction(cmd, cancellationToken).ConfigureAwait(false);
							break;
						case SessionCommandType.ProvideAuthCode:
							HandleAuthCode(cmd);
							break;
						case SessionCommandType.Provide2FACode:
							Handle2FACode(cmd);
							break;
						case SessionCommandType.Disconnect:
							await HandleDisconnect(cmd).ConfigureAwait(false);
							return;
						case SessionCommandType.Login:
							await HandleLogin(cmd, cancellationToken).ConfigureAwait(false);
							break;
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error handling command {CommandType}", cmd.Type);
					cmd.Completion?.TrySetResult(new SessionCommandResult(false, ex.Message, null));
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			_logger.LogCritical(ex, "Session {AccountName} crashed", _accountName);
			SetState(SessionState.FatalError, ex.Message);
		}
	}

	private void RunSteamCallbacksAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				_steamClientManager?.RunCallbacks();
				Thread.Sleep(100);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Steam callback loop error");
		}
	}

	private async Task HandleLogin(SessionCommand cmd, CancellationToken cancellationToken)
	{
		var result = await LoginCoreAsync(cancellationToken).ConfigureAwait(false);
		cmd.Completion?.TrySetResult(result);
	}

	private async Task HandleExecuteAction(SessionCommand cmd, CancellationToken cancellationToken)
	{
		var action = _actionRegistry.Get(cmd.ActionName);
		if (action == null)
		{
			cmd.Completion?.TrySetResult(new SessionCommandResult(false, $"action not found: {cmd.ActionName}", null));
			return;
		}

		// Only enforce login when a real steam client is wired in; in stub mode, actions can run while disconnected.
		if (action.Metadata.RequiresLogin && _steamClientManager != null && _state != SessionState.Connected)
		{
			cmd.Completion?.TrySetResult(new SessionCommandResult(false, $"not logged in. Current state: {_state}", null));
			return;
		}

		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cmd.CancellationToken);
		CancellationToken effectiveToken = linkedCts.Token;

		CancellationTokenSource? timeoutCts = null;
		if (action.Metadata.TimeoutSeconds is > 0)
		{
			timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(effectiveToken);
			timeoutCts.CancelAfter(TimeSpan.FromSeconds(action.Metadata.TimeoutSeconds.Value));
			effectiveToken = timeoutCts.Token;
		}

		await _actionLock.WaitAsync(effectiveToken).ConfigureAwait(false);
		try
		{
			try
			{
				var result = await action.ExecuteAsync(this, cmd.Payload ?? new Dictionary<string, object?>(), effectiveToken).ConfigureAwait(false);
				cmd.Completion?.TrySetResult(new SessionCommandResult(result.Success, result.Error, result.Output));
			}
			catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
			{
				cmd.Completion?.TrySetResult(new SessionCommandResult(false, "action timeout", null));
			}
			catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
			{
				cmd.Completion?.TrySetResult(new SessionCommandResult(false, "canceled", null));
			}
		}
		finally
		{
			timeoutCts?.Dispose();
			_actionLock.Release();
		}
	}

	private void HandleAuthCode(SessionCommand cmd)
	{
		if (_state == SessionState.ConnectingWaitAuthCode)
		{
			_logger.LogInformation("Auth code provided for {AccountName}", _accountName);
			_steamClientManager?.SetAuthCode(_accountName, cmd.ActionName!);
			SetState(SessionState.Connecting, "auth code provided; retrying login");
				_commandChannel.Writer.TryWrite(new SessionCommand(
					Guid.NewGuid().ToString(),
					SessionCommandType.Login,
					null,
					null,
					null,
					CancellationToken.None
				));
			}
		}

	private void Handle2FACode(SessionCommand cmd)
	{
		if (_state == SessionState.ConnectingWait2FA)
		{
			_logger.LogInformation("2FA code provided for {AccountName}", _accountName);
			_steamClientManager?.SetTwoFactorCode(_accountName, cmd.ActionName!);
			SetState(SessionState.Connecting, "2FA code provided; retrying login");
				_commandChannel.Writer.TryWrite(new SessionCommand(
					Guid.NewGuid().ToString(),
					SessionCommandType.Login,
					null,
					null,
					null,
					CancellationToken.None
				));
			}
		}

	private async Task HandleDisconnect(SessionCommand cmd)
	{
		await DisconnectInternalAsync(CancellationToken.None).ConfigureAwait(false);
		cmd.Completion?.TrySetResult(new SessionCommandResult(true, null, null));
	}

	private async Task<SessionCommandResult> ConnectAsync(CancellationToken cancellationToken)
	{
		var tcs = new TaskCompletionSource<SessionCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cmd = new SessionCommand(
			Guid.NewGuid().ToString(),
			SessionCommandType.Login,
			null,
			null,
			tcs,
			cancellationToken
		);

		_commandChannel.Writer.TryWrite(cmd);
		return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task<SessionCommandResult> LoginCoreAsync(CancellationToken cancellationToken)
	{
		if (_steamClientManager == null)
		{
			SetState(SessionState.Connected, "logged in (stub mode)");
			return new SessionCommandResult(true, null, null);
		}

		SetState(SessionState.Connecting, "connecting to Steam");

		try
		{
			await _steamClientManager.ConnectAsync(cancellationToken).ConfigureAwait(false);
			await _steamClientManager.LoginAsync(_accountName, _credentials.Password, cancellationToken).ConfigureAwait(false);
			SetState(SessionState.Connected, "connected to Steam");
			ConnectedAt = DateTimeOffset.UtcNow;
			return new SessionCommandResult(true, null, null);
		}
		catch (SteamAuthCodeRequiredException ex)
		{
			SetState(SessionState.ConnectingWaitAuthCode, ex.Message);
			_eventChannel.Writer.TryWrite(new SessionEvent(SessionEventType.AuthCodeNeeded, _accountName, SessionState.ConnectingWaitAuthCode, ex.Message));
			return new SessionCommandResult(false, ex.Message, null);
		}
		catch (SteamTwoFactorCodeRequiredException ex)
		{
			SetState(SessionState.ConnectingWait2FA, ex.Message);
			_eventChannel.Writer.TryWrite(new SessionEvent(SessionEventType.TwoFactorCodeNeeded, _accountName, SessionState.ConnectingWait2FA, ex.Message));
			return new SessionCommandResult(false, ex.Message, null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Login failed for {AccountName}", _accountName);
			SetState(SessionState.FatalError, ex.Message);
			return new SessionCommandResult(false, ex.Message, null);
		}
	}

	private async Task DisconnectInternalAsync(CancellationToken cancellationToken)
	{
		SetState(SessionState.Disconnecting, null);
		
		if (_steamClientManager != null)
		{
			await _steamClientManager.DisconnectAsync().ConfigureAwait(false);
		}
		
		await Task.Delay(100, cancellationToken).ConfigureAwait(false);
		SetState(SessionState.Disconnected, "disconnected");
	}

	private void SetState(SessionState newState, string? message)
	{
		if (_state == newState) return;

		var oldState = _state;
		_state = newState;
		_lastHeartbeat = DateTimeOffset.UtcNow;

		_logger.LogInformation("Session {AccountName}: {OldState} -> {NewState} ({Message})",
			_accountName, oldState, newState, message ?? string.Empty);

		var evt = new SessionEvent(SessionEventType.StateChanged, _accountName, newState, message);
		_eventChannel.Writer.TryWrite(evt);

		// Notify callback for auth challenges
		if (newState == SessionState.ConnectingWaitAuthCode || newState == SessionState.ConnectingWait2FA)
		{
			_ = Task.Run(async () =>
			{
				if (_eventCallback != null)
				{
					await _eventCallback.Invoke(_accountName, newState == SessionState.ConnectingWaitAuthCode ? "auth_code_required" : "2fa_required", newState.ToString(), message);
				}
			});
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		try
		{
			_cts.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}

		_cts.Dispose();
		_actionLock.Dispose();
	}
}

public enum SessionCommandType
{
	Login,
	ExecuteAction,
	ProvideAuthCode,
	Provide2FACode,
	Disconnect
}

public sealed record SessionCommand(
	string Id,
	SessionCommandType Type,
	string? ActionName,
	IReadOnlyDictionary<string, object?>? Payload,
	TaskCompletionSource<SessionCommandResult>? Completion,
	CancellationToken CancellationToken
);

public sealed record SessionCommandResult(
	bool Success,
	string? Error,
	IReadOnlyDictionary<string, object?>? Output
);
