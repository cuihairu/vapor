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
	string? AccessToken = null,
	bool QrLogin = false
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

		// The command channel is unbounded, so TryWrite always succeeds.
		_commandChannel.Writer.TryWrite(cmd);

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

	private async Task RunSteamCallbacksAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				_steamClientManager?.RunCallbacks();
				// 异步等待把线程在轮询空档归还线程池；同步 Sleep 会让每个活会话
				// 永久占住一个池线程，是 CI 线程池饥饿的已知根源。
				await Task.Delay(100, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
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

		// CA2000 suppressed: timeoutCts is disposed in this method's finally block.
#pragma warning disable CA2000
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
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				var result = await action.ExecuteAsync(this, cmd.Payload ?? new Dictionary<string, object?>(), effectiveToken).ConfigureAwait(false);
				cmd.Completion?.TrySetResult(new SessionCommandResult(result.Success, result.Error, result.Output));
				NotifyActionExecuted(action.Name, result.Success, stopwatch.Elapsed.TotalMilliseconds);
			}
			catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !linkedCts.IsCancellationRequested)
			{
				// timeoutCts is linked to the caller token, so a caller cancel cancels it
				// too; only report a timeout when the caller itself is still running.
				cmd.Completion?.TrySetResult(new SessionCommandResult(false, "action timeout", null));
				NotifyActionExecuted(action.Name, false, stopwatch.Elapsed.TotalMilliseconds);
			}
			catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
			{
				cmd.Completion?.TrySetResult(new SessionCommandResult(false, "canceled", null));
				NotifyActionExecuted(action.Name, false, stopwatch.Elapsed.TotalMilliseconds);
			}
		}
		finally
		{
			timeoutCts?.Dispose();
			_actionLock.Release();
#pragma warning restore CA2000
		}
	}

	private void NotifyActionExecuted(string actionName, bool success, double durationMs)
	{
		if (_actionRegistry is ActionRegistry registry)
		{
			registry.RaiseActionExecuted(actionName, success, durationMs);
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

		if (_credentials.QrLogin)
		{
			return await LoginViaQrAsync(cancellationToken).ConfigureAwait(false);
		}

		SetState(SessionState.Connecting, "connecting to Steam");

		try
		{
			if (!string.IsNullOrWhiteSpace(_credentials.AccessToken) || !string.IsNullOrWhiteSpace(_credentials.RefreshToken))
			{
				await _steamClientManager.UpdateLogOnDetailsAsync(
					_accountName,
					_credentials.AccessToken,
					_credentials.RefreshToken
				).ConfigureAwait(false);
			}

			await _steamClientManager.ConnectAsync(cancellationToken).ConfigureAwait(false);

			if (!string.IsNullOrWhiteSpace(_credentials.AccessToken) || !string.IsNullOrWhiteSpace(_credentials.RefreshToken))
			{
				await _steamClientManager.LoginAsync(_accountName, string.Empty, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await _steamClientManager.LoginAsync(_accountName, _credentials.Password, cancellationToken).ConfigureAwait(false);
			}

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

	// Steam rotates the QR challenge URL periodically; the sign-in window bounds how
	// long the session will wait for the phone-side approval before giving up.
	private static readonly TimeSpan QrLoginTimeout = TimeSpan.FromMinutes(3);

	/// <summary>
	/// QR sign-in: connect, surface the challenge URL (republished whenever Steam
	/// rotates it), wait for the phone-side approval, then hand the minted refresh
	/// token to the standard token log-on path. The request key never leaves the
	/// transport; only the challenge URL — which is useless without an approved
	/// phone session — is surfaced upstream.
	/// </summary>
	private async Task<SessionCommandResult> LoginViaQrAsync(CancellationToken cancellationToken)
	{
		SetState(SessionState.Connecting, "connecting to Steam for QR sign-in");

		try
		{
			await _steamClientManager!.ConnectAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Connect failed before QR sign-in for {AccountName}", _accountName);
			SetState(SessionState.FatalError, ex.Message);
			return new SessionCommandResult(false, ex.Message, null);
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(QrLoginTimeout);

		QrLoginResult result;
		try
		{
			var challenge = new SessionEvent(SessionEventType.QrCodeNeeded, _accountName, SessionState.ConnectingWaitQr, null);
			result = await _steamClientManager.BeginQrLoginAsync(
				_accountName,
				url => PublishQrChallenge(url, challenge),
				timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			SetState(SessionState.Disconnected, "QR sign-in canceled");
			return new SessionCommandResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "QR sign-in failed for {AccountName}", _accountName);
			SetState(SessionState.FatalError, ex.Message);
			return new SessionCommandResult(false, ex.Message, null);
		}

		if (!result.Success || string.IsNullOrWhiteSpace(result.RefreshToken))
		{
			var error = result.Error ?? "QR sign-in was not approved";
			_logger.LogWarning("QR sign-in failed for {AccountName}: {Error}", _accountName, error);
			SetState(SessionState.FatalError, error);
			return new SessionCommandResult(false, error, null);
		}

		SetState(SessionState.Connecting, "QR sign-in approved; logging on with the new refresh token");

		try
		{
			// Only the refresh token is staged: LogOnDetails.AccessToken must carry the
			// long-lived refresh JWT, and the logon callback persists it via the store.
			await _steamClientManager.UpdateLogOnDetailsAsync(_accountName, null, result.RefreshToken).ConfigureAwait(false);
			await _steamClientManager.LoginAsync(_accountName, string.Empty, cancellationToken).ConfigureAwait(false);

			SetState(SessionState.Connected, "connected to Steam via QR sign-in");
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
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			SetState(SessionState.Disconnected, "QR sign-in canceled");
			return new SessionCommandResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Post-QR logon failed for {AccountName}", _accountName);
			SetState(SessionState.FatalError, ex.Message);
			return new SessionCommandResult(false, ex.Message, null);
		}
	}

	private void PublishQrChallenge(string url, SessionEvent firstChallenge)
	{
		if (_state != SessionState.ConnectingWaitQr)
		{
			_eventChannel.Writer.TryWrite(firstChallenge with { Message = url });
			SetState(SessionState.ConnectingWaitQr, url);
			return;
		}

		// Same state: still republish so dashboards refresh the QR challenge in place.
		_eventChannel.Writer.TryWrite(new SessionEvent(SessionEventType.QrCodeNeeded, _accountName, SessionState.ConnectingWaitQr, url));
		RaiseEventCallback("qr_required", SessionState.ConnectingWaitQr, url);
	}

	private void RaiseEventCallback(string eventType, SessionState state, string? message)
	{
		if (_eventCallback == null)
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			if (_eventCallback != null)
			{
				await _eventCallback.Invoke(_accountName, eventType, state.ToString(), message);
			}
		});
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

		// Notify the agent host for every state change; auth-challenge states keep their
		// dedicated event types so the control plane raises challenges, all other states
		// report as state_changed so the control plane tracker mirrors the session.
		if (_eventCallback != null)
		{
			string eventType = newState switch
			{
				SessionState.ConnectingWaitAuthCode => "auth_code_required",
				SessionState.ConnectingWait2FA => "2fa_required",
				SessionState.ConnectingWaitQr => "qr_required",
				_ => "state_changed"
			};
			RaiseEventCallback(eventType, newState, message);
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
