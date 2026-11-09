using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Models;

using System.Diagnostics.CodeAnalysis;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// Handles Steam trade-related Web API operations.
/// </summary>
/// <summary>
/// SteamKit2 trade-offer session shell. Every code path drives a live Steam
/// network session, which cannot be exercised in unit tests (the E2E suite
/// exercises the flows around this shell instead); excluded from coverage.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed partial class SteamTradeClient : ISteamTradeClient, IDisposable
{
	/// <summary>Environment variable overriding the Steam Community base URL (mock support, 41-4).</summary>
	public const string InventoryBaseUrlEnvironmentVariable = "VAPOR_STEAM_INVENTORY_BASE_URL";

	private const string DefaultInventoryBaseUrl = "https://steamcommunity.com";

	private readonly SteamWebHandler _webHandler;
	private readonly ILogger<SteamTradeClient> _logger;
	private readonly JsonSerializerOptions _jsonOptions;
	private readonly string _inventoryBaseUrl;
	private bool _disposed;

	private const uint SteamAppId = 730; // Default to CS2 for inventory
	private const ulong DefaultContextId = 2; // Default inventory context

	public SteamTradeClient(SteamWebHandler webHandler, ILogger<SteamTradeClient> logger, string? inventoryBaseUrl = null)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_logger = logger;
		_jsonOptions = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true,
			NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
		};

		// Base URL resolution (41-4 mock): explicit config (constructor) wins,
		// then the environment variable, then the real Steam Community host. The
		// address is never hard-coded at the call site — a local mock can stand in
		// for steamcommunity.com by setting either knob, so the agent keeps its
		// production default while tests point at a loopback listener.
		string? resolved = !string.IsNullOrWhiteSpace(inventoryBaseUrl)
			? inventoryBaseUrl
			: Environment.GetEnvironmentVariable(InventoryBaseUrlEnvironmentVariable);
		_inventoryBaseUrl = string.IsNullOrWhiteSpace(resolved)
			? DefaultInventoryBaseUrl
			: resolved.Trim().TrimEnd('/');
	}

	/// <summary>The Steam Community base URL this client issues inventory requests against.</summary>
	internal string InventoryBaseUrl => _inventoryBaseUrl;

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamTradeClient));
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
	}
}
