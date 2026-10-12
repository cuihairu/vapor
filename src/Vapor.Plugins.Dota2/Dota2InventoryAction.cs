using System.Globalization;
using Vapor.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.Dota2;

/// <summary>
/// "dota2_inventory": reads the Dota 2 inventory (app 570 / context 2) through
/// the shared paginator and reports the tradability split an operator needs
/// before sending anything anywhere: items ready to trade now, items under
/// Steam's market trade cooldown (with the unlock time whenever Steam reports
/// one), and items whose flag says not tradable with no unlock time. The
/// steam id defaults to the session's own (resolved from its web cookies).
/// </summary>
public sealed class Dota2InventoryAction : IAction
{
	/// <summary>Upper bound on unavailable items listed per bucket in the output.</summary>
	internal const int MaxListedUnavailable = 50;

	private readonly Dota2Plugin _plugin;
	private readonly ILogger<Dota2InventoryAction> _logger;
	private readonly Func<SteamWebHandler, ISteamTradeClient> _tradeClientFactory;

	public Dota2InventoryAction(Dota2Plugin plugin)
		: this(plugin, NullLogger<Dota2InventoryAction>.Instance)
	{
	}

	// Constructor for testing with a custom trade client factory.
	internal Dota2InventoryAction(
		Dota2Plugin plugin,
		ILogger<Dota2InventoryAction> logger,
		Func<SteamWebHandler, ISteamTradeClient>? tradeClientFactory = null)
	{
		_plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
		_logger = logger;
		_tradeClientFactory = tradeClientFactory ?? plugin.TradeClientFactory;
	}

	public string Name => "dota2_inventory";

	public ActionMetadata Metadata => new(
		Name,
		"Reads a Dota 2 inventory (app 570 / context 2) and reports the tradability split: items ready to trade now, items under Steam's 7-day market trade cooldown (unlock times when reported), and items not tradable. The steam_id defaults to the session's own.",
		RequiresLogin: true,
		TimeoutSeconds: 60
	)
	{ Safety = ActionSafety.ReadOnly };

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(payload);

		SteamWebHandler? webHandler = session.SteamWebHandler;
		ulong steamId;
		string? steamIdParam = PayloadReader.GetString(payload, "steam_id");
		if (!string.IsNullOrEmpty(steamIdParam))
		{
			if (!ulong.TryParse(steamIdParam, out steamId))
			{
				return new ActionResult(false, "invalid steam_id parameter", null);
			}
		}
		else
		{
			// Default to the session's own SteamID; without a web session there is
			// nothing to resolve it from.
			if (webHandler is null)
			{
				return new ActionResult(false, "steam_id parameter is required when no web session is available", null);
			}

			ulong? resolved = webHandler.TryResolveOwnSteamId();
			if (resolved is null)
			{
				return new ActionResult(false, "steam_id parameter is required when the session cookies do not carry a SteamID (is the session logged on?)", null);
			}

			steamId = resolved.Value;
		}

		if (webHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		try
		{
			var tradeClient = _tradeClientFactory(webHandler);
			(List<InventoryItem> items, string? error) = await SteamInventoryPaginator.FetchAsync(
				tradeClient, steamId, Dota2Plugin.AppId, Dota2Plugin.InventoryContextId, _logger, cancellationToken).ConfigureAwait(false);
			if (error is not null)
			{
				return new ActionResult(false, error, null);
			}

			(DateTimeOffset now, List<Dictionary<string, object?>> cooldownListed, int cooldownTotal) = CollectCooldownItems(items);
			(List<Dictionary<string, object?>> lockedListed, int lockedTotal) = CollectLockedItems(items, now);
			int readyCount = items.Count - cooldownTotal - lockedTotal;

			_logger.LogInformation(
				"Read Dota 2 inventory for SteamID {SteamId}: {Total} item(s), {Ready} tradable, {Cooldown} in cooldown, {Locked} locked",
				steamId, items.Count, readyCount, cooldownTotal, lockedTotal);

			var output = new Dictionary<string, object?>
			{
				["mode"] = "readonly",
				["steam_id"] = steamId.ToString(CultureInfo.InvariantCulture),
				["app_id"] = Dota2Plugin.AppId,
				["context_id"] = Dota2Plugin.InventoryContextId,
				["total_count"] = items.Count,
				["ready_count"] = readyCount,
				["cooldown_count"] = cooldownTotal,
				["locked_count"] = lockedTotal,
				["checked_at"] = now.ToString("O", CultureInfo.InvariantCulture),
				["cooldown_items"] = cooldownListed,
				["locked_items"] = lockedListed
			};

			return new ActionResult(true, null, output);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to read Dota 2 inventory for {SteamId}", steamId);
			return new ActionResult(false, ex.Message, null);
		}
	}

	/// <summary>
	/// Items with a future tradability date — the 7-day market cooldown bucket,
	/// with the unlock time carried verbatim. Returned capped; the total count
	/// keeps the output honest when the cap clips.
	/// </summary>
	private static (DateTimeOffset Now, List<Dictionary<string, object?>> Listed, int Total) CollectCooldownItems(
		IReadOnlyList<InventoryItem> items)
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		List<Dictionary<string, object?>> listed = [];
		int total = 0;

		foreach (InventoryItem item in items)
		{
			if (item.TradabilityDate is not DateTimeOffset unlock || unlock <= now)
			{
				continue;
			}

			total++;
			if (listed.Count < MaxListedUnavailable)
			{
				listed.Add(new Dictionary<string, object?>
				{
					["asset_id"] = item.AssetId.ToString(CultureInfo.InvariantCulture),
					["name"] = item.MarketHashName ?? item.MarketName ?? item.Name,
					["cooldown_until"] = unlock.ToString("O", CultureInfo.InvariantCulture)
				});
			}
		}

		return (now, listed, total);
	}


	/// <summary>
	/// Items flagged not tradable with no future unlock date — the "never
	/// trades or no unlock time reported" bucket. Returned capped at
	/// <see cref="MaxListedUnavailable"/> (the count is recoverable from
	/// locked_count minus what the list shows).
	/// </summary>
	private static (List<Dictionary<string, object?>> Listed, int Total) CollectLockedItems(
		IReadOnlyList<InventoryItem> items,
		DateTimeOffset now)
	{
		List<Dictionary<string, object?>> listed = [];
		int total = 0;

		foreach (InventoryItem item in items)
		{
			// Cooldown items (future unlock date) belong to the other bucket even
			// when their raw flag already reads not-tradable; locked here means
			// "no future unlock time reported".
			if (item.Tradable
				|| (item.TradabilityDate is DateTimeOffset unlock && unlock > now))
			{
				continue;
			}

			total++;
			if (listed.Count < MaxListedUnavailable)
			{
				listed.Add(new Dictionary<string, object?>
				{
					["asset_id"] = item.AssetId.ToString(CultureInfo.InvariantCulture),
					["name"] = item.MarketHashName ?? item.MarketName ?? item.Name
				});
			}
		}

		return (listed, total);
	}
}
