using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Steam;

namespace Vapor.Plugins.MobileAuthenticator;

/// <summary>
/// Steam mobile authenticator plugin. Contributes actions for TOTP code generation,
/// trade confirmation hashing/listing/responding, and Steam server time synchronization.
/// </summary>
public sealed class MobileAuthenticatorPlugin : IActionPlugin
{
	private SteamTimeSynchronizer? _timeSynchronizer;
	private ILoggerFactory? _loggerFactory;
	private Vapor.Steam.Core.Security.ICredentialStore? _credentialStore;

	public PluginInfo Info { get; } = new(
		Id: "vapor.mobile-authenticator",
		Name: "Vapor Mobile Authenticator",
		Version: new Version(1, 0, 0),
		ApiVersion: PluginApi.Current,
		Description: "Steam mobile authenticator: TOTP, trade confirmations and time sync");

	public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
	{
		_loggerFactory = context.Host.LoggerFactory;
		_timeSynchronizer = new SteamTimeSynchronizer(
			SteamTimeSynchronizer.QuerySteamServerTimeAsync,
			logger: _loggerFactory.CreateLogger<SteamTimeSynchronizer>());
		_credentialStore = context.Host.Services.GetService(typeof(Vapor.Steam.Core.Security.ICredentialStore)) as Vapor.Steam.Core.Security.ICredentialStore;
		return Task.CompletedTask;
	}

	public Task ShutdownAsync(CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	public IEnumerable<IAction> GetActions()
	{
		if (_timeSynchronizer is null || _loggerFactory is null)
		{
			throw new InvalidOperationException("Plugin has not been initialized");
		}

		yield return new GenerateTotpAction(_timeSynchronizer);
		yield return new GenerateConfirmationHashAction(_timeSynchronizer);
		yield return new SyncSteamTimeAction(_timeSynchronizer, _loggerFactory.CreateLogger<SyncSteamTimeAction>());
		yield return new GetTradeConfirmationsAction(_loggerFactory.CreateLogger<GetTradeConfirmationsAction>(), _timeSynchronizer);
		yield return new RespondTradeConfirmationAction(_loggerFactory.CreateLogger<RespondTradeConfirmationAction>(), _timeSynchronizer);
		yield return new SaveSharedSecretAction(_loggerFactory.CreateLogger<SaveSharedSecretAction>(), _credentialStore);
	}
}
