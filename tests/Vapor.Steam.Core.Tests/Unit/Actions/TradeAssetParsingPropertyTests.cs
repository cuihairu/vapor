using System.Globalization;
using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Models;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>
/// Property-based tests over the trade-asset payload parsing
/// (ParseTradeAssets / ParseSingleAsset). The parser is a defensive shell
/// around four TryParses with documented fallback defaults; the properties
/// pin the oracle semantics — any boxed value parses exactly when the
/// matching TryParse would, unreadable values fall back to the CS:GO
/// defaults, entries without a usable asset id are dropped, and filtering
/// preserves input order. InvariantCulture strings stand in for the JSON
/// number forms that arrive over the wire.
/// </summary>
public sealed class TradeAssetParsingPropertyTests
{
	private const uint DefaultAppId = 730;
	private const ulong DefaultContextId = 2;
	private const int DefaultAmount = 1;

	// Values no TryParse accepts: empty, non-numeric text, letter-prefixed
	// digits, scientific notation, and values beyond each target width.
	private static readonly object?[] UnreadableForUnsigned = ["", "abc", "7x", "1E+20", "-1", "4294967296"];
	private static readonly object?[] UnreadableForLong = ["", "abc", "7x", "1E+20", "-1", "18446744073709551616"];
	private static readonly object?[] UnreadableForInt = ["", "abc", "7x", "1E+20", "2147483648"];

	// asset_id forms that must drop the entry: missing key, null, zero,
	// unreadable text, and overflow.
	private static readonly object?[] UnusableAssetIds = [null, (ulong)0, "0", "", "abc", "18446744073709551616"];

	private static Dictionary<string, object?> Dict(object? appId, object? contextId, object? assetId, object? amount)
	{
		var item = new Dictionary<string, object?>();
		if (appId is not null)
		{
			item["app_id"] = appId;
		}

		if (contextId is not null)
		{
			item["context_id"] = contextId;
		}

		if (assetId is not null)
		{
			item["asset_id"] = assetId;
		}

		if (amount is not null)
		{
			item["amount"] = amount;
		}

		return item;
	}

	private static void AssertExact(TradeAsset asset, uint appId, ulong contextId, ulong assetIdRaw, int amount)
	{
		Assert.Equal(appId, asset.AppId);
		Assert.Equal(contextId, asset.ContextId);
		Assert.Equal(assetIdRaw, asset.AssetId);
		Assert.Equal(amount, asset.Amount);
	}

	// --- exact numeric forms ---

	[Property]
	public void SingleAsset_BoxedNumericForms_ParseExactly(uint appId, ulong contextId, ulong assetId, PositiveInt amount)
	{
		if (assetId == 0)
		{
			return; // full-range generator; zero drops the entry and is covered below
		}

		var parsed = SendTradeOfferAction.ParseSingleAsset(Dict(appId, contextId, assetId, amount.Get));

		Assert.NotNull(parsed);
		AssertExact(parsed, appId, contextId, assetId, amount.Get);
	}

	[Property]
	public void SingleAsset_InvariantStringForms_ParseExactly(uint appId, ulong contextId, ulong assetId, PositiveInt amount)
	{
		if (assetId == 0)
		{
			return;
		}

		var parsed = SendTradeOfferAction.ParseSingleAsset(Dict(
			appId.ToString(CultureInfo.InvariantCulture),
			contextId.ToString(CultureInfo.InvariantCulture),
			assetId.ToString(CultureInfo.InvariantCulture),
			amount.Get.ToString(CultureInfo.InvariantCulture)));

		Assert.NotNull(parsed);
		AssertExact(parsed, appId, contextId, assetId, amount.Get);
	}

	// --- fallback defaults ---

	[Property]
	public void SingleAsset_MissingFields_KeepCsgoDefaults(ulong assetId, PositiveInt amount)
	{
		if (assetId == 0)
		{
			return;
		}

		// Dict only inserts non-null fields: app_id, context_id and amount are
		// absent here, so their CS:GO defaults must survive.
		var parsed = SendTradeOfferAction.ParseSingleAsset(Dict(null, null, assetId, amount.Get));

		Assert.NotNull(parsed);
		AssertExact(parsed, DefaultAppId, DefaultContextId, assetId, amount.Get);
	}

	[Property]
	public void SingleAsset_UnreadableFields_FallBackToDefaults(uint appIdx, ulong ctxIdx, ulong amtIdx, ulong assetId, PositiveInt amount)
	{
		if (assetId == 0)
		{
			return;
		}

		var parsed = SendTradeOfferAction.ParseSingleAsset(Dict(
			UnreadableForUnsigned[appIdx % (uint)UnreadableForUnsigned.Length],
			UnreadableForLong[ctxIdx % (uint)UnreadableForLong.Length],
			assetId,
			UnreadableForInt[amtIdx % (uint)UnreadableForInt.Length]));

		Assert.NotNull(parsed);
		AssertExact(parsed, DefaultAppId, DefaultContextId, assetId, DefaultAmount);
	}

	// --- entry dropping ---

	[Property]
	public void SingleAsset_UnusableAssetId_DropsEntry(uint appId, ulong contextId, int amount, int probeIdx)
	{
		var parsed = SendTradeOfferAction.ParseSingleAsset(Dict(
			appId, contextId, UnusableAssetIds[(uint)probeIdx % UnusableAssetIds.Length], amount));

		Assert.Null(parsed);
	}

	// --- container shapes & total function ---

	[Property]
	public void ParseTradeAssets_AnyContainerShape_NeverThrows_AndKeepsOnlyNonZeroAssets(NonNull<string> key, int modeIdx, string?[] values)
	{
		int mode = (int)((uint)modeIdx % 4);
		object? container = mode switch
		{
			// missing key entirely
			0 => null,
			// array of dictionaries
			1 => (object?)values.Select(v => Dict(null, null, v, null)).ToList(),
			// array of objects with mixed element kinds
			2 => (object?)values.Cast<object?>().Select((v, i) => i % 2 == 0 ? (object?)Dict(null, null, v, null) : v).ToList(),
			// bare scalar — no list container matches
			_ => (object?)"not-a-list",
		};

		var payload = new Dictionary<string, object?> { [key.Get] = container };

		var parsed = SendTradeOfferAction.ParseTradeAssets(payload, key.Get);
		Assert.NotNull(parsed);
		Assert.All(parsed, asset => Assert.NotEqual(0ul, asset.AssetId));

		// An absent key yields the empty list, not an error.
		Assert.Empty(SendTradeOfferAction.ParseTradeAssets(payload, key.Get + "-missing"));
	}

	[Property]
	public void ParseTradeAssets_MixedEntries_KeepsOnlyValid_InInputOrder(
		uint[] appIds, ulong[] contextIds, ulong[] assetIds, PositiveInt[] amounts, bool[] valid)
	{
		int n = Math.Min(Math.Min(appIds.Length, contextIds.Length), Math.Min(Math.Min(assetIds.Length, amounts.Length), valid.Length));
		if (n == 0)
		{
			return;
		}

		var expectedAppIds = new List<uint>(n);
		var expectedAssetIds = new List<ulong>(n);
		var items = new List<object?>(n);
		for (int i = 0; i < n; i++)
		{
			bool keep = valid[i] && assetIds[i] != 0;
			items.Add(keep
				? Dict(appIds[i], contextIds[i], assetIds[i], amounts[i].Get)
				: Dict(appIds[i], contextIds[i], assetIds[i] == 0 ? assetIds[i] : null, amounts[i].Get));
			if (keep)
			{
				expectedAppIds.Add(appIds[i]);
				expectedAssetIds.Add(assetIds[i]);
			}
		}

		var payload = new Dictionary<string, object?> { ["take"] = items };
		var parsed = SendTradeOfferAction.ParseTradeAssets(payload, "take");

		Assert.Equal(expectedAssetIds, parsed.Select(a => a.AssetId));
		Assert.Equal(expectedAppIds, parsed.Select(a => a.AppId));
	}
}
