using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// "add_license": claims free Steam content on this account — the addlicense flow.
/// App IDs go through the client protocol (SteamKit free-license request, works for
/// free on demand apps); sub IDs go through the store checkout endpoint the website's
/// own "Add to account" button calls, which needs the session cookies the web handler
/// already carries. Both ID kinds are optional but at least one must be present.
/// </summary>
public sealed class AddLicenseAction : IAction
{
	private readonly ILogger<AddLicenseAction> _logger;
	private readonly Func<SteamWebHandler, ISteamStoreApiClient> _storeClientFactory;

	public AddLicenseAction(ILogger<AddLicenseAction> logger)
	{
		_logger = logger;
		_storeClientFactory = static webHandler => new SteamStoreApiClient(webHandler, NullLogger<SteamStoreApiClient>.Instance);
	}

	// Constructor for testing with a custom store client factory.
	internal AddLicenseAction(
		ILogger<AddLicenseAction> logger,
		Func<SteamWebHandler, ISteamStoreApiClient> storeClientFactory)
	{
		_logger = logger;
		_storeClientFactory = storeClientFactory;
	}

	public string Name => "add_license";

	public ActionMetadata Metadata => new(
		Name,
		"Claim free apps or store subs on this account (addlicense)",
		RequiresLogin: true,
		TimeoutSeconds: 120);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		List<uint> appIds = ParseIds(payload, "app_ids");
		List<uint> subIds = ParseIds(payload, "sub_ids");

		if (appIds.Count == 0 && subIds.Count == 0)
		{
			return new ActionResult(false, "either app_ids or sub_ids is required", null);
		}

		_logger.LogInformation(
			"Add license action for {AccountName}: {AppCount} apps, {SubCount} subs",
			session.AccountName, appIds.Count, subIds.Count);

		var output = new Dictionary<string, object?>
		{
			["app_ids"] = appIds,
			["sub_ids"] = subIds
		};
		bool overallSuccess = true;

		if (appIds.Count > 0)
		{
			var clientManager = session.SteamClientManager;
			if (clientManager == null)
			{
				return new ActionResult(false, "Steam client not available (app_ids need a connected client)", null);
			}

			var free = await clientManager.RequestFreeLicenseAsync(appIds, cancellationToken).ConfigureAwait(false);
			if (free == null)
			{
				output["apps_result"] = "no response from Steam";
				overallSuccess = false;
			}
			else
			{
				output["apps_result"] = free.Result.ToString();
				if (free.GrantedApps.Count > 0)
				{
					output["granted_app_ids"] = free.GrantedApps;
				}

				if (free.GrantedPackages.Count > 0)
				{
					output["granted_package_ids"] = free.GrantedPackages;
				}

				if (free.Result != SteamResult.OK)
				{
					overallSuccess = false;
				}
			}
		}

		if (subIds.Count > 0)
		{
			var webHandler = session.SteamWebHandler;
			if (webHandler == null)
			{
				return new ActionResult(false, "Steam web handler not available (sub_ids need a web session)", null);
			}

			var storeClient = _storeClientFactory(webHandler);
			var purchases = new List<Dictionary<string, object?>>();
			foreach (uint subId in subIds)
			{
				StorePurchaseResult? purchase = await storeClient.AddFreeLicenseAsync(subId, cancellationToken).ConfigureAwait(false);
				bool success = purchase?.Success == true;
				purchases.Add(new Dictionary<string, object?>
				{
					["id"] = subId,
					["success"] = success,
					["detail"] = purchase?.PurchaseResultDetail
				});

				if (!success)
				{
					overallSuccess = false;
				}
			}

			output["purchases"] = purchases;
		}

		return new ActionResult(overallSuccess, overallSuccess ? null : "one or more free-license requests failed", output);
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
				return TryParseString(je.GetString()!, out parsed);
			case string s:
				return TryParseString(s, out parsed);
			default:
				parsed = 0;
				return false;
		}
	}

	private static bool TryParseString(string value, out uint parsed)
	{
		// Both call sites pass non-null text (a JSON string element or a CLR string).
		return uint.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
	}
}
