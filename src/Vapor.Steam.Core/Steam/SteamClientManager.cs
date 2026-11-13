using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SteamKit2;
using Vapor.Steam.Core.Security;
using System.Diagnostics.CodeAnalysis;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Steam;

/// <summary>
/// SteamKit2 logon/callback state machine. The callback pump and logon
/// transitions only run inside a live SteamKit2 network session, which unit
/// tests cannot drive (consumers mock <see cref="ISteamClientManager"/>);
/// excluded from coverage.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed partial class SteamClientManager : ISteamClientManager, IDisposable
{
	private sealed record LoginState(string AccountName, string Password)
	{
		public string? AccessToken { get; init; }
		public string? RefreshToken { get; init; }
		public SteamID? SteamId { get; init; }
		public string? AuthCode { get; init; }
		public string? TwoFactorCode { get; init; }
		public string? Proxy { get; init; }
		public TaskCompletionSource<SteamUser.LoggedOnCallback>? LoginTcs { get; init; }
	}

	private readonly SteamClient _steamClient;
	private readonly CallbackManager _callbackManager;
	private readonly ILogger<SteamClientManager> _logger;
	private readonly ICredentialStore? _credentialStore;
	private readonly ISteamAuthTokenProvider _steamAuthTokenProvider;
	private readonly SteamUserStatsProtocolHandler _userStatsProtocol = new();
	private readonly Cs2GcWaitRegistry _gcWaits = new();

	/// <summary>Tracks whether the CS2 game coordinator has welcomed this connection.</summary>
	private volatile bool _cs2GcSessionLive;
	private readonly ConcurrentDictionary<string, LoginState> _loginStates = new(StringComparer.OrdinalIgnoreCase);
	private readonly object _connectLock = new();
	private TaskCompletionSource<bool> _connectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
	// Each proxy switch that must tear the live connection down registers its own
	// settle TCS here; OnDisconnected completes every one of them so a reconnect
	// does not race the dying socket and a concurrent switch never orphans an
	// earlier caller's wait (which would otherwise only end via the 10s timeout).
	private readonly List<TaskCompletionSource<bool>> _pendingDisconnects = [];
	private string? _activeLoginAccountName;
	// Monotonic generation identifying which LoginAsync attempt owns the routing
	// slot. LoggedOnCallback carries no request identity, so the exit path
	// compares generations (not account names) before clearing, and OnLoggedOn
	// fences late responses of earlier cancelled logins off the active login by
	// the slotted account's stored SteamId.
	private long _loginGeneration;
	private long _activeLoginGeneration;
	// Serializes LoginAsync: a LoggedOnCallback is routed through the single
	// _activeLoginAccountName slot, so only one logon may be in flight at a time.
	private readonly SemaphoreSlim _loginGate = new(1, 1);

	// The exit proxy the next CM connection must use. The SteamKit HTTP client
	// factory reads this when the CM WebSocket (and any WebAPI/CDN) invoker is
	// built; SetAccountProxyAsync writes it before the caller's connect.
	private string? _pendingProxy;
	internal TimeSpan DisconnectSettleTimeout { get; set; } = TimeSpan.FromSeconds(10);
	private bool _disposed;

	public SteamClientManager(ILogger<SteamClientManager> logger, ICredentialStore? credentialStore = null)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_steamClient = CreateSteamClient();
		_callbackManager = new CallbackManager(_steamClient);
		_steamAuthTokenProvider = new SteamAuthTokenProvider(_steamClient);
		// SteamKit2 auto-registers its own SteamGameCoordinator handler on
		// construction (re-adding one throws); the CS2 GC path below resolves it
		// via GetHandler<SteamGameCoordinator>() and only subscribes to its
		// MessageCallback. The user-stats protocol handler is Vapor's own.
		_steamClient.AddHandler(_userStatsProtocol);

		SubscribeCallbacks();
	}

	internal SteamClientManager(
		ILogger<SteamClientManager> logger,
		ICredentialStore? credentialStore,
		ISteamAuthTokenProvider steamAuthTokenProvider)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_steamClient = CreateSteamClient();
		_callbackManager = new CallbackManager(_steamClient);
		_steamAuthTokenProvider = steamAuthTokenProvider;
		// SteamKit2 auto-registers its own SteamGameCoordinator handler on
		// construction (re-adding one throws); the CS2 GC path below resolves it
		// via GetHandler<SteamGameCoordinator>() and only subscribes to its
		// MessageCallback. The user-stats protocol handler is Vapor's own.
		_steamClient.AddHandler(_userStatsProtocol);

		SubscribeCallbacks();
	}

	/// <summary>
	/// SteamKit2's CM socket has no proxy support for its TCP/UDP transports, but
	/// the WebSocket transport is built on the HTTP client factory — so the client
	/// runs WebSocket-only and every CM connection (plus SteamKit's own WebAPI/CDN
	/// calls) is built through <see cref="CreateProxiedHttpClient"/>, which applies
	/// the pending per-account proxy. socks5 endpoints resolve remotely at the
	/// proxy, keeping the local resolver out of the account's traffic path.
	/// </summary>
	private SteamClient CreateSteamClient() =>
		new(SteamConfiguration.Create(builder => builder
			.WithProtocolTypes(ProtocolTypes.WebSocket)
			.WithHttpClientFactory(CreateProxiedHttpClient)));

	private HttpClient CreateProxiedHttpClient(HttpClientPurpose purpose)
	{
		var proxy = _pendingProxy;

		// CA2000 suppressed: ownership of both handler and client transfers to the
		// returned HttpClient; SteamKit2 disposes the invoker with the connection.
#pragma warning disable CA2000
		var handler = new SocketsHttpHandler
		{
			ConnectTimeout = TimeSpan.FromSeconds(15)
		};

		if (proxy != null)
		{
			handler.Proxy = ProxyOptions.Parse(proxy, "proxy").ToWebProxy();
			handler.UseProxy = true;
			_logger.LogInformation("Building {Purpose} transport through {Proxy}", purpose, ProxyOptions.Parse(proxy, "proxy").ToString());
		}

		return new HttpClient(handler)
		{
			// The CM WebSocket is a long-lived socket; only the handshake goes
			// through this client. WebAPI/CDN purposes keep a finite timeout.
			Timeout = purpose == HttpClientPurpose.CMWebSocket ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(60)
		};
#pragma warning restore CA2000
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
			// Intentionally swallowed: Dispose must never throw even if the CM
			// socket is already gone and SteamKit2 raises from Disconnect.
		}

		_loginStates.Clear();
		_loginGate.Dispose();
	}
}
