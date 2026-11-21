using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Steam;

internal partial class Program
{
	/// <summary>
	/// Keeps the Steam time offset fresh for TOTP generation: syncs once at startup and
	/// then hourly (clock drift is slow, and a stale offset of a few seconds is harmless
	/// within the 30s TOTP window).
	/// </summary>
	static async Task RunSteamTimeSyncAsync(SteamTimeSynchronizer timeSynchronizer, ILogger logger, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await timeSynchronizer.SyncAsync(cancellationToken);
				logger.LogDebug("Steam time sync: offset {OffsetSeconds}s", timeSynchronizer.OffsetSeconds);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Steam time sync failed; keeping the previous offset");
			}

			try
			{
				await Task.Delay(TimeSpan.FromHours(1), cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
		}
	}
}
