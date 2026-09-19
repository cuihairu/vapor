using System.Globalization;

namespace Vapor.Steam.Core.Web;

/// <summary>
/// Fetches the account's Web API key from the developer page
/// (<c>steamcommunity.com/dev/apikey</c>) using the logged-on web session.
/// Shared by the clients that call key-gated Web API endpoints
/// (IEconService, ISteamUser, IPlayerService).
/// </summary>
internal static class SteamWebApiKeyFetcher
{
	/// <summary>
	/// Returns the Web API key, or null when the page is unavailable or does
	/// not reveal a key (e.g. the account never generated one). The key never
	/// appears in logs.
	/// </summary>
	public static async Task<string?> FetchAsync(SteamWebHandler webHandler, CancellationToken cancellationToken)
	{
		var url = "https://steamcommunity.com/dev/apikey";
		var response = await webHandler.GetAsync(new Uri(url), null, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccess || string.IsNullOrEmpty(response.Body))
		{
			return null;
		}

		// Parse the API key from the response body.
		const string keyPattern = "<p>Key: ";
		var keyIndex = response.Body.IndexOf(keyPattern, StringComparison.OrdinalIgnoreCase);
		if (keyIndex >= 0)
		{
			var startIndex = keyIndex + keyPattern.Length;
			var endIndex = response.Body.IndexOf("</p>", startIndex, StringComparison.OrdinalIgnoreCase);
			if (endIndex > startIndex)
			{
				var key = response.Body[startIndex..endIndex].Trim();
				if (!string.IsNullOrEmpty(key) && key.Length > 20)
				{
					return key;
				}
			}
		}

		return null;
	}
}
