using SteamKit2;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Utilities;
using SteamKit2.Internal;
using SteamKit2.WebUI.Internal;

namespace Vapor.Steam.Core.Steam;

/// <summary>Store surface: redeem keys, free licenses and the points shop.</summary>
public sealed partial class SteamClientManager
{
	public async Task<RedeemKeyResult?> RedeemKeyAsync(string key, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(key);

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot redeem key: Steam client not connected");
			return null;
		}

		var requestId = Guid.NewGuid().ToString("N")[..12];
		var stopwatch = ValueStopwatch.StartNew();

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			var request = new CStore_RegisterCDKey_Request
			{
				activation_code = key,
				is_request_from_client = true
			};

			_logger.LogInformation("Redeeming key: {Key} (RequestId: {RequestId})", MaskKey(key), requestId);

			var asyncJob = unifiedMessages.SendMessage<CStore_RegisterCDKey_Request, CStore_RegisterCDKey_Response>(
				"Store#RegisterCDKey",
				request
			);

			// Set timeout
			asyncJob.Timeout = TimeSpan.FromSeconds(60);

			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Key redemption timed out (RequestId: {RequestId})", requestId);
				return new RedeemKeyResult(MapResult(EResult.Timeout), requestId, stopwatch.ElapsedMilliseconds);
			}

			_logger.LogInformation(
				"Key redemption result: {Result} (RequestId: {RequestId}, Duration: {Duration}ms)",
				response.Result,
				requestId,
				stopwatch.ElapsedMilliseconds
			);

			var parsedReceipt = ParseRedeemReceipt(response.Body);

			return new RedeemKeyResult(
				MapResult(response.Result),
				requestId,
				stopwatch.ElapsedMilliseconds,
				parsedReceipt?.GrantedAppIds,
				parsedReceipt?.GrantedPackageIds,
				parsedReceipt?.ReceiptDetails
			);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to redeem key (RequestId: {RequestId})", requestId);
			return new RedeemKeyResult(MapResult(EResult.Fail), requestId, stopwatch.ElapsedMilliseconds);
		}
	}

	public async Task<FreeLicenseResult?> RequestFreeLicenseAsync(IReadOnlyCollection<uint> appIds, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(appIds);

		if (appIds.Count == 0)
		{
			return new FreeLicenseResult(SteamResult.OK, [], []);
		}

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot request free license: Steam client not connected");
			return null;
		}

		try
		{
			var steamApps = _steamClient.GetHandler<SteamApps>()
				?? throw new InvalidOperationException("SteamApps handler not available");

			var asyncJob = steamApps.RequestFreeLicense(appIds);
			asyncJob.Timeout = TimeSpan.FromSeconds(60);
			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Free license request timed out for {Count} apps", appIds.Count);
				return new FreeLicenseResult(SteamResult.Timeout, [], []);
			}

			_logger.LogInformation(
				"Free license request result: {Result} (granted {GrantedApps} apps / {GrantedPackages} packages for {Count} requested apps)",
				response.Result,
				response.GrantedApps.Count,
				response.GrantedPackages.Count,
				appIds.Count);

			return new FreeLicenseResult(
				MapResult(response.Result),
				response.GrantedApps.ToList(),
				response.GrantedPackages.ToList());
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to request free license for {Count} apps", appIds.Count);
			return new FreeLicenseResult(SteamResult.Fail, [], []);
		}
	}

	public async Task<PointsShopSummary?> GetPointsShopSummaryAsync(CancellationToken cancellationToken = default)
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot fetch points shop summary: Steam client not connected");
			return null;
		}

		if (_steamClient.SteamID is not { } steamId)
		{
			_logger.LogWarning("Cannot fetch points shop summary: not logged on");
			return null;
		}

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			var asyncJob = unifiedMessages.SendMessage<CLoyaltyRewards_GetSummary_Request, CLoyaltyRewards_GetSummary_Response>(
				"LoyaltyRewards#GetSummary",
				new CLoyaltyRewards_GetSummary_Request { steamid = steamId });
			asyncJob.Timeout = TimeSpan.FromSeconds(60);
			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Points shop summary request timed out");
				return null;
			}

			if (response.Result != EResult.OK)
			{
				_logger.LogWarning("Points shop summary request failed: {Result}", response.Result);
				return null;
			}

			return response.Body.summary is { } summary
				? new PointsShopSummary(summary.points, summary.points_earned, summary.points_spent)
				: null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to fetch points shop summary");
			return null;
		}
	}

	public async Task<IReadOnlyList<PointsShopItemInfo>?> QueryPointsShopItemsAsync(IReadOnlyCollection<uint> definitionIds, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(definitionIds);

		if (definitionIds.Count == 0)
		{
			return [];
		}

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot query points shop items: Steam client not connected");
			return null;
		}

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			var request = new CLoyaltyRewards_QueryRewardItems_Request();
			request.definitionids.AddRange(definitionIds.Distinct());

			var result = new List<PointsShopItemInfo>();
			while (true)
			{
				var asyncJob = unifiedMessages.SendMessage<CLoyaltyRewards_QueryRewardItems_Request, CLoyaltyRewards_QueryRewardItems_Response>(
					"LoyaltyRewards#QueryRewardItems",
					request);
				asyncJob.Timeout = TimeSpan.FromSeconds(60);
				var response = await asyncJob.ToTask().ConfigureAwait(false);

				if (response == null || response.Result != EResult.OK)
				{
					_logger.LogWarning("Points shop item query failed: {Result}", response?.Result.ToString() ?? "no response");
					return null;
				}

				result.AddRange(response.Body.definitions.Select(ToItemInfo));

				// Normally comparing counts suffices, but guard against Steam looping
				// the same cursor forever all the same (ASF's bulletproofing).
				if (result.Count >= response.Body.total_count ||
					string.IsNullOrEmpty(response.Body.next_cursor) ||
					request.cursor == response.Body.next_cursor)
				{
					return result;
				}

				request.cursor = response.Body.next_cursor;
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to query points shop items for {Count} definitions", definitionIds.Count);
			return null;
		}
	}

	public async Task<RedeemPointsResult?> RedeemPointsShopItemAsync(uint definitionId, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfZero(definitionId);

		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(SteamClientManager));
		}

		if (!_steamClient.IsConnected)
		{
			_logger.LogWarning("Cannot redeem points shop item: Steam client not connected");
			return null;
		}

		try
		{
			var unifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>()
				?? throw new InvalidOperationException("SteamUnifiedMessages handler not available");

			// expected_points_cost stays 0 (unchecked), mirroring the ASF RP command:
			// the caller validates the price up front, so no racing-price failure mode.
			var asyncJob = unifiedMessages.SendMessage<CLoyaltyRewards_RedeemPoints_Request, CLoyaltyRewards_RedeemPoints_Response>(
				"LoyaltyRewards#RedeemPoints",
				new CLoyaltyRewards_RedeemPoints_Request { defid = definitionId });
			asyncJob.Timeout = TimeSpan.FromSeconds(60);
			var response = await asyncJob.ToTask().ConfigureAwait(false);

			if (response == null)
			{
				_logger.LogWarning("Points shop redemption timed out for definition {DefId}", definitionId);
				return new RedeemPointsResult(SteamResult.Timeout, 0);
			}

			_logger.LogInformation(
				"Points shop redemption for definition {DefId}: {Result} (community item {CommunityItemId})",
				definitionId,
				response.Result,
				response.Body.communityitemid);

			return new RedeemPointsResult(MapResult(response.Result), response.Body.communityitemid);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to redeem points shop definition {DefId}", definitionId);
			return new RedeemPointsResult(SteamResult.Fail, 0);
		}
	}

	private static PointsShopItemInfo ToItemInfo(LoyaltyRewardDefinition definition) => new(
		definition.defid,
		definition.appid,
		definition.type,
		string.IsNullOrWhiteSpace(definition.internal_description) ? null : definition.internal_description,
		definition.point_cost,
		definition.active,
		definition.timestamp_free_until);

}
