using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;

namespace Vapor.Plugins.MobileAuthenticator;

/// <summary>
/// Steam mobile authenticator plugin. Contributes actions for TOTP code generation,
/// trade confirmation hashing/listing/responding, and Steam server time synchronization.
/// </summary>
public sealed class MobileAuthenticatorPlugin : IActionPlugin
{
	private SteamTimeSynchronizer? _timeSynchronizer;
	private ILoggerFactory? _loggerFactory;

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
			QueryServerTimeAsync,
			logger: _loggerFactory.CreateLogger<SteamTimeSynchronizer>());
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
	}

	/// <summary>
	/// Default server-time query: POSTs to Steam's ITwoFactorService/QueryTime endpoint and
	/// parses the server_time field from the response.
	/// </summary>
	internal static async Task<long> QueryServerTimeAsync(CancellationToken cancellationToken)
	{
		using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		using var content = new FormUrlEncodedContent(new Dictionary<string, string>());
		using var response = await httpClient.PostAsync(SteamTimeSynchronizer.QueryTimeEndpoint, content, cancellationToken).ConfigureAwait(false);

		response.EnsureSuccessStatusCode();

		var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		using var doc = JsonDocument.Parse(body);

		if (doc.RootElement.TryGetProperty("response", out var responseElem)
			&& responseElem.TryGetProperty("server_time", out var serverTimeElem)
			&& long.TryParse(serverTimeElem.GetString(), out var serverTime))
		{
			return serverTime;
		}

		throw new InvalidOperationException("Steam QueryTime response did not contain response.server_time");
	}
}
