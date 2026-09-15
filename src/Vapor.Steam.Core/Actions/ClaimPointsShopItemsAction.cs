using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "claim_points_shop_items": redeems points shop reward definitions on this
/// account — the points-shop flavor of add_license. Mirroring the ASF "RP"
/// command, only free definitions (point_cost == 0) are claimable by default:
/// a paid definition rejects the whole batch unless force=true is passed.
/// Definitions are looked up first (so a mistyped id fails before anything is
/// redeemed), then redeemed one by one; a single failure does not abort the
/// rest and every outcome is reported back.
/// </summary>
public sealed class ClaimPointsShopItemsAction : IAction
{
	private readonly ILogger<ClaimPointsShopItemsAction> _logger;

	public ClaimPointsShopItemsAction(ILogger<ClaimPointsShopItemsAction> logger)
	{
		_logger = logger;
	}

	public string Name => "claim_points_shop_items";

	public ActionMetadata Metadata => new(
		Name,
		"Redeem points shop reward definitions (free ones by default; force=true for paid ones)",
		RequiresLogin: true,
		TimeoutSeconds: 120);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		List<uint> definitionIds = ParseIds(payload, "definition_ids");
		if (definitionIds.Count == 0)
		{
			return new ActionResult(false, "definition_ids is required", null);
		}

		bool force = PayloadReader.GetBool(payload, "force") ?? false;

		var clientManager = session.SteamClientManager;
		if (clientManager == null)
		{
			return new ActionResult(false, "Steam client not available", null);
		}

		_logger.LogInformation(
			"Claim points shop items for {AccountName}: {Count} definitions (force: {Force})",
			session.AccountName, definitionIds.Count, force);

		if (!force)
		{
			// Validate the whole batch before redeeming anything: a mistyped id or a
			// paid definition must not half-apply. (ASF RP behaves the same way.)
			IReadOnlyList<PointsShopItemInfo>? items = await clientManager
				.QueryPointsShopItemsAsync(definitionIds, cancellationToken).ConfigureAwait(false);
			if (items == null)
			{
				return new ActionResult(false, "no response from Steam (definition lookup)", null);
			}

			Dictionary<uint, PointsShopItemInfo> byId = items
				.GroupBy(item => item.DefId)
				.ToDictionary(group => group.Key, group => group.First());

			foreach (uint definitionId in definitionIds)
			{
				if (!byId.TryGetValue(definitionId, out PointsShopItemInfo? info))
				{
					return new ActionResult(false, $"definition {definitionId} not found on Steam", null);
				}

				if (info.PointCost != 0)
				{
					return new ActionResult(
						false,
						$"definition {definitionId} costs {info.PointCost} points (pass force=true to redeem paid items)",
						null);
				}
			}
		}

		var results = new List<Dictionary<string, object?>>();
		foreach (uint definitionId in definitionIds)
		{
			RedeemPointsResult? redeem = await clientManager
				.RedeemPointsShopItemAsync(definitionId, cancellationToken).ConfigureAwait(false);

			if (redeem == null)
			{
				results.Add(new Dictionary<string, object?>
				{
					["defid"] = definitionId,
					["success"] = false,
					["result"] = "no response from Steam"
				});
				continue;
			}

			var entry = new Dictionary<string, object?>
			{
				["defid"] = definitionId,
				["success"] = redeem.Result == SteamResult.OK,
				["result"] = redeem.Result.ToString()
			};
			if (redeem.CommunityItemId != 0)
			{
				// 64-bit item id: keep it a string so JSON consumers don't lose precision.
				entry["community_item_id"] = redeem.CommunityItemId.ToString(CultureInfo.InvariantCulture);
			}

			results.Add(entry);
		}

		var output = new Dictionary<string, object?>
		{
			["force"] = force,
			["requested"] = definitionIds.Count,
			["succeeded"] = results.Count(r => (bool)r["success"]!),
			["failed"] = results.Count(r => !(bool)r["success"]!),
			["results"] = results
		};

		// Best-effort balance refresh; failing to read it must not shadow the
		// redemption outcomes.
		PointsShopSummary? after = await clientManager.GetPointsShopSummaryAsync(cancellationToken).ConfigureAwait(false);
		if (after != null)
		{
			output["points_after"] = after.Points;
		}

		bool overall = (int)output["failed"]! == 0;
		return new ActionResult(overall, overall ? null : "one or more point shop redemptions failed", output);
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
