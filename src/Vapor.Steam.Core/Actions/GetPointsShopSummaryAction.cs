using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "get_points_shop_summary": reads the account's points shop balance and,
/// when definition_ids are given, the reward definitions behind those ids —
/// the discovery feed for claim_points_shop_items (free claimables are the
/// ones with point_cost 0). free_only filters the item list client-side;
/// items_total still reports how many definitions Steam returned.
/// </summary>
public sealed class GetPointsShopSummaryAction : IAction
{
	private readonly ILogger<GetPointsShopSummaryAction> _logger;

	public GetPointsShopSummaryAction(ILogger<GetPointsShopSummaryAction> logger)
	{
		_logger = logger;
	}

	public string Name => "get_points_shop_summary";

	public ActionMetadata Metadata => new(
		Name,
		"Read the account's points shop balance and reward definitions",
		RequiresLogin: true,
		TimeoutSeconds: 60);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		List<uint> definitionIds = ParseIds(payload, "definition_ids");
		bool freeOnly = PayloadReader.GetBool(payload, "free_only") ?? false;

		var clientManager = session.SteamClientManager;
		if (clientManager == null)
		{
			return new ActionResult(false, "Steam client not available", null);
		}

		PointsShopSummary? summary = await clientManager.GetPointsShopSummaryAsync(cancellationToken).ConfigureAwait(false);
		if (summary == null)
		{
			return new ActionResult(false, "no response from Steam", null);
		}

		_logger.LogInformation(
			"Points shop summary for {AccountName}: {Points} points",
			session.AccountName, summary.Points);

		var output = new Dictionary<string, object?>
		{
			["points"] = summary.Points,
			["points_earned"] = summary.PointsEarned,
			["points_spent"] = summary.PointsSpent
		};

		if (definitionIds.Count > 0)
		{
			IReadOnlyList<PointsShopItemInfo>? items = await clientManager
				.QueryPointsShopItemsAsync(definitionIds, cancellationToken).ConfigureAwait(false);
			if (items == null)
			{
				return new ActionResult(false, "no response from Steam (definition lookup)", output);
			}

			output["items"] = items
				.Where(item => !freeOnly || item.PointCost == 0)
				.Select(ItemToDict)
				.ToList();
			output["items_total"] = items.Count;
		}

		return new ActionResult(true, null, output);
	}

	internal static Dictionary<string, object?> ItemToDict(PointsShopItemInfo item)
	{
		var dict = new Dictionary<string, object?>
		{
			["defid"] = item.DefId,
			["app_id"] = item.AppId,
			["point_cost"] = item.PointCost,
			["active"] = item.Active
		};

		if (item.Type != 0)
		{
			dict["type"] = item.Type;
		}

		if (item.InternalDescription is not null)
		{
			dict["description"] = item.InternalDescription;
		}

		if (item.FreeUntilTimestamp != 0)
		{
			dict["free_until"] = item.FreeUntilTimestamp;
		}

		return dict;
	}

	private static List<uint> ParseIds(IReadOnlyDictionary<string, object?> payload, string key)
	{
		var result = new List<uint>();

		if (!PayloadReader.TryGetValue(payload, key, out var raw) || raw is null)
		{
			return result;
		}

		if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
		{
			foreach (var element in array.EnumerateArray())
			{
				if (TryParseUInt32(element, out uint id) && id > 0)
				{
					result.Add(id);
				}
			}
		}
		else if (raw is System.Collections.IEnumerable list and not string)
		{
			foreach (var item in list)
			{
				if (item != null && TryParseUInt32(item, out uint id) && id > 0)
				{
					result.Add(id);
				}
			}
		}
		else if (TryParseUInt32(raw, out uint single) && single > 0)
		{
			result.Add(single);
		}

		return result.Distinct().ToList();
	}

	private static bool TryParseUInt32(object? value, out uint parsed)
	{
		switch (value)
		{
			case uint u:
				parsed = u;
				return true;
			case int i when i > 0:
				parsed = (uint)i;
				return true;
			case long l when l > 0 && l <= uint.MaxValue:
				parsed = (uint)l;
				return true;
			case double d when d > 0 && d <= uint.MaxValue && Math.Floor(d) == d:
				parsed = (uint)d;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetUInt32(out parsed):
				return true;
			case JsonElement { ValueKind: JsonValueKind.String } je:
				return uint.TryParse(je.GetString()!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
			case string s:
				return uint.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
			default:
				parsed = 0;
				return false;
		}
	}
}
