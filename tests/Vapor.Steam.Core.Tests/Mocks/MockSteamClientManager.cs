using SteamKit2;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Mocks;

/// <summary>
/// Mock implementation of ISteamClientManager for testing.
/// </summary>
public sealed class MockSteamClientManager : ISteamClientManager
{
	private RedeemKeyResult? _redeemKeyResult;
	private TaskCompletionSource<bool>? _connectTcs;
	private bool _isConnected;

	public Task<SteamUser.LogOnDetails?> GetLogOnDetailsAsync(string accountName)
	{
		return Task.FromResult<SteamUser.LogOnDetails?>(new SteamUser.LogOnDetails
		{
			Username = accountName,
			Password = "mock_password"
		});
	}

	public Task UpdateLogOnDetailsAsync(string accountName, string? accessToken, string? refreshToken)
	{
		return Task.CompletedTask;
	}

	public SteamClient GetClient()
	{
		throw new NotImplementedException("Mock does not provide real SteamClient");
	}

	public Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		_connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		_isConnected = true;
		_connectTcs.TrySetResult(true);
		return Task.CompletedTask;
	}

	public Task DisconnectAsync()
	{
		_isConnected = false;
		return Task.CompletedTask;
	}

	public Task<bool> IsConnectedAsync()
	{
		return Task.FromResult(_isConnected);
	}

	public Task LoginAsync(string accountName, string password, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public void SetAuthCode(string accountName, string code) { }

	public void SetTwoFactorCode(string accountName, string code) { }

	public void RunCallbacks() { }

	public Task<RedeemKeyResult?> RedeemKeyAsync(string key, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<RedeemKeyResult?>(_redeemKeyResult);
	}

	public void PlayGames(HashSet<uint> appIds) { }

	public IReadOnlySet<uint> GetPlayingGames()
	{
		return new HashSet<uint>();
	}

	public Task<bool> RefreshAccessTokenAsync(string accountName, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(false);
	}

	/// <summary>
	/// Sets the result to return from RedeemKeyAsync.
	/// </summary>
	public void SetRedeemKeyResult(RedeemKeyResult result)
	{
		_redeemKeyResult = result;
	}

	/// <summary>
	/// Sets whether the mock is connected.
	/// </summary>
	public void SetConnected(bool connected)
	{
		_isConnected = connected;
	}
}
