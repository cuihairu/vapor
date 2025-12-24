using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamKit2;

namespace SteamControl.Steam.Core.Steam;

public interface ISteamClientManager
{
	Task<SteamUser.LogOnDetails?> GetLogOnDetailsAsync(string accountName);
	Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken);
	SteamClient GetClient();
	Task ConnectAsync(CancellationToken cancellationToken = default);
	Task DisconnectAsync();
	Task<bool> IsConnectedAsync();
	Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default);
	void SetAuthCode(string accountName, string code);
	void SetTwoFactorCode(string accountName, string code);
	void RunCallbacks();
}

public sealed class SteamAuthCodeRequiredException : Exception
{
	public SteamAuthCodeRequiredException(string message) : base(message) { }
}

public sealed class SteamTwoFactorCodeRequiredException : Exception
{
	public SteamTwoFactorCodeRequiredException(string message) : base(message) { }
}

public sealed class SteamClientManager : ISteamClientManager, IDisposable
{
	private sealed record LoginState(string AccountName, string Password)
	{
		public string? AccessToken { get; init; }
		public string? RefreshToken { get; init; }
		public string? AuthCode { get; init; }
		public string? TwoFactorCode { get; init; }
		public TaskCompletionSource<SteamUser.LoggedOnCallback>? LoginTcs { get; init; }
	}

	private readonly SteamClient _steamClient;
	private readonly CallbackManager _callbackManager;
	private readonly ILogger<SteamClientManager> _logger;
	private readonly ConcurrentDictionary<string, LoginState> _loginStates = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _connectLock = new();
	private TaskCompletionSource<bool> _connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private string? _activeLoginAccountName;
	private bool _disposed;

	public SteamClientManager(ILogger<SteamClientManager> logger)
	{
		_logger = logger;
		_steamClient = new SteamClient();
		_callbackManager = new CallbackManager(_steamClient);

		SubscribeCallbacks();
	}

	public SteamClient GetClient() => _steamClient;

	public Task<SteamUser.LogOnDetails?> GetLogOnDetailsAsync(string accountName)
	{
		if (!_loginStates.TryGetValue(accountName, out var state))
		{
			return Task.FromResult<SteamUser.LogOnDetails?>(null);
		}

		return Task.FromResult<SteamUser.LogOnDetails?>(new SteamUser.LogOnDetails
		{
			Username = state.AccountName,
			Password = state.Password,
			AuthCode = state.AuthCode,
			TwoFactorCode = state.TwoFactorCode,
			AccessToken = state.AccessToken,
			ShouldRememberPassword = !string.IsNullOrWhiteSpace(state.AccessToken)
		});
	}

	public Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { AccessToken = accessToken, RefreshToken = refreshToken },
			(_, existing) => existing with { AccessToken = accessToken, RefreshToken = refreshToken }
		);

		return Task.CompletedTask;
	}

	public Task<bool> IsConnectedAsync()
	{
		return Task.FromResult(_steamClient.IsConnected);
	}

	public async Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ThrowIfDisposed();

		if (_steamClient.IsConnected)
		{
			return;
		}

		TaskCompletionSource<bool> tcs;
		lock (_connectLock)
		{
			if (_steamClient.IsConnected)
			{
				return;
			}

			_connectedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			tcs = _connectedTcs;
			_steamClient.Connect();
		}

		bool connected = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		if (!connected)
		{
			throw new InvalidOperationException("Steam client failed to connect");
		}
	}

	public async Task DisconnectAsync()
	{
		if (_disposed)
		{
			return;
		}

		try
		{
			_steamClient.Disconnect();
		}
		catch
		{
		}

		await Task.Delay(100).ConfigureAwait(false);
	}

	public async Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		var tcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
		_activeLoginAccountName = accountName;

		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, password) { LoginTcs = tcs },
			(_, existing) => existing with { Password = password, LoginTcs = tcs }
		);

		if (!_steamClient.IsConnected)
		{
			throw new InvalidOperationException("Steam client is not connected");
		}

		var steamUser = _steamClient.GetHandler<SteamUser>() ?? throw new InvalidOperationException("SteamUser handler not available");
		steamUser.LogOn(BuildLogOnDetails(accountName, password));

		var callback = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

		if (callback.Result == EResult.OK)
		{
			return;
		}

		if (callback.Result == EResult.AccountLogonDenied)
		{
			throw new SteamAuthCodeRequiredException("Steam auth code required (email Steam Guard)");
		}

		if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
		{
			throw new SteamTwoFactorCodeRequiredException("Steam 2FA code required (authenticator)");
		}

		throw new InvalidOperationException($"Steam login failed: {callback.Result}");
	}

	public void SetAuthCode(string accountName, string code)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { AuthCode = code },
			(_, existing) => existing with { AuthCode = code }
		);
	}

	public void SetTwoFactorCode(string accountName, string code)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { TwoFactorCode = code },
			(_, existing) => existing with { TwoFactorCode = code }
		);
	}

	public void RunCallbacks()
	{
		if (_disposed)
		{
			return;
		}

		_callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
	}

	private SteamUser.LogOnDetails BuildLogOnDetails(string accountName, string password)
	{
		_loginStates.TryGetValue(accountName, out var state);

		return new SteamUser.LogOnDetails
		{
			Username = accountName,
			Password = password,
			AuthCode = state?.AuthCode,
			TwoFactorCode = state?.TwoFactorCode,
			AccessToken = state?.AccessToken,
			ShouldRememberPassword = !string.IsNullOrWhiteSpace(state?.AccessToken)
		};
	}

	private void SubscribeCallbacks()
	{
		_callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
		_callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
		_callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
		_callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
	}

	private void OnConnected(SteamClient.ConnectedCallback callback)
	{
		_logger.LogInformation("Steam client connected: {Result}", callback.Result);
		_connectedTcs.TrySetResult(callback.Result == EResult.OK);
	}

	private void OnDisconnected(SteamClient.DisconnectedCallback callback)
	{
		_logger.LogInformation("Steam client disconnected: {UserInitiated}", callback.UserInitiated);
		_connectedTcs.TrySetResult(false);
	}

	private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
	{
		var accountName = _activeLoginAccountName;
		if (string.IsNullOrWhiteSpace(accountName))
		{
			return;
		}

		if (_loginStates.TryGetValue(accountName, out var state))
		{
			state.LoginTcs?.TrySetResult(callback);
		}
	}

	private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
	{
		_logger.LogInformation("Logged off: {Result}", callback.Result);
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			_steamClient.Disconnect();
		}
		catch
		{
		}

		_steamClient.Dispose();
		_loginStates.Clear();
	}
}

