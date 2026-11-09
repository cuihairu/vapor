using SteamKit2;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Steam;

/// <summary>Connection lifecycle: CM socket creation, proxy wiring, connect/disconnect and settle.</summary>
public sealed partial class SteamClientManager
{
	public Task<TransportLogOnDetails?> GetLogOnDetailsAsync(string accountName)
	{
		if (!_loginStates.TryGetValue(accountName, out var state))
		{
			return Task.FromResult<TransportLogOnDetails?>(null);
		}

		var token = state.AccessToken ?? state.RefreshToken;

		return Task.FromResult<TransportLogOnDetails?>(new TransportLogOnDetails(
			state.AccountName,
			state.Password,
			state.AuthCode,
			state.TwoFactorCode,
			token,
			!string.IsNullOrWhiteSpace(token)));
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

	internal Task UpdateSteamIdAsync(string accountName, SteamID steamId)
	{
		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { SteamId = steamId },
			(_, existing) => existing with { SteamId = steamId });

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

	public async Task SetAccountProxyAsync(string accountName, string? proxy, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);
		if (proxy != null)
		{
			ProxyOptions.Parse(proxy, nameof(proxy)); // fail fast on malformed endpoints
		}

		_loginStates.AddOrUpdate(
			accountName,
			_ => new LoginState(accountName, string.Empty) { Proxy = proxy },
			(_, existing) => existing with { Proxy = proxy });

		// The CM socket is process-wide: when the requested exit differs from the
		// staged one, tear the live connection down and wait for the disconnect
		// callback so the caller's connect re-establishes it through the new proxy.
		if (string.Equals(proxy, _pendingProxy, StringComparison.Ordinal))
		{
			return;
		}

		Task settle;
		TaskCompletionSource<bool>? disconnectTcs = null;
		lock (_connectLock)
		{
			_pendingProxy = proxy;
			if (_steamClient.IsConnected)
			{
				// Wait on the disconnect, not the (already completed) connect TCS:
				// _connectedTcs was settled true by OnConnected, so awaiting it here
				// returns instantly and the reconnect races the socket teardown.
				disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				_pendingDisconnects.Add(disconnectTcs);
				settle = disconnectTcs.Task;
			}
			else
			{
				settle = Task.CompletedTask;
			}
		}

		if (!settle.IsCompleted)
		{
			// Drop the connection outside the lock: OnDisconnected takes
			// _connectLock to settle the TCS, so a synchronous callback here
			// would otherwise deadlock on the lock we just released.
			try
			{
				_steamClient.Disconnect();
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to drop the CM connection for a proxy switch");
			}
		}

		await SettleDisconnectAsync(settle, disconnectTcs, DisconnectSettleTimeout, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task DisconnectAsync()
	{
		if (_disposed)
		{
			return;
		}

		TaskCompletionSource<bool>? disconnectTcs = null;
		Task settle;
		lock (_connectLock)
		{
			if (_steamClient.IsConnected)
			{
				// Wait on the disconnect callback rather than a fixed sleep: the
				// callback settles this TCS as soon as the socket actually tears
				// down, so callers do not race a wedged connection with a magic
				// number. Same bounded mechanism as a proxy switch.
				disconnectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				_pendingDisconnects.Add(disconnectTcs);
				settle = disconnectTcs.Task;
			}
			else
			{
				settle = Task.CompletedTask;
			}
		}

		try
		{
			_steamClient.Disconnect();
		}
		catch
		{
			// Intentionally swallowed: the socket may already be dead (for example a
			// dropped CM connection), in which case Disconnect raises while the
			// disconnect is still the desired end state. Callers awaiting settle
			// below get the real outcome via the 10s ceiling.
		}

		await SettleDisconnectAsync(settle, disconnectTcs, DisconnectSettleTimeout).ConfigureAwait(false);
	}

	/// <summary>
	/// Bounded wait on the disconnect callback TCS. A wedged socket that never
	/// raises OnDisconnected surfaces as a timeout here; treat it as a completed
	/// best-effort teardown instead of throwing — the old fixed-delay contract
	/// never propagated this, and a caller (BotSession.DisconnectInternalAsync)
	/// would otherwise skip the Disconnected transition and strand in Disconnecting.
	/// </summary>
	private async Task SettleDisconnectAsync(
		Task settle,
		TaskCompletionSource<bool>? disconnectTcs,
		TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		if (settle.IsCompleted)
		{
			return;
		}

		try
		{
			await settle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			_logger.LogWarning("Disconnect timed out after {Timeout}ms; socket is wedged or already dead, treating teardown as complete", timeout.TotalMilliseconds);
			if (disconnectTcs != null)
			{
				lock (_connectLock)
				{
					// Do not let the orphaned TCS linger until the next OnDisconnected.
					_pendingDisconnects.Remove(disconnectTcs);
				}
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// A caller token cancellation surfaces as OperationCanceledException;
			// prune the orphaned TCS symmetrically with the timeout path so it does
			// not linger until the next OnDisconnected, then rethrow so the
			// cancellation still propagates to the awaiting caller.
			if (disconnectTcs != null)
			{
				lock (_connectLock)
				{
					_pendingDisconnects.Remove(disconnectTcs);
				}
			}
			throw;
		}
	}

}
