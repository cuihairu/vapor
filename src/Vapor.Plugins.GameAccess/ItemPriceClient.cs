using System.Globalization;
using System.Text;
using System.Text.Json;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.GameAccess;

/// <summary>
/// Per-item valuation over the community market's <c>priceoverview</c> endpoint —
/// the item-level counterpart to the host's app-level <c>get_price</c> (both read
/// public Steam surfaces through the session's web handler and share the
/// <c>SteamCacheTtl.Price</c> tier in the calling actions).
/// </summary>
public interface IItemPriceClient
{
	Task<ItemPriceResult> GetItemPriceAsync(
		uint appId,
		string marketHashName,
		uint? currency,
		CancellationToken cancellationToken);
}

/// <summary>
/// A priceoverview result. Prices are reported verbatim exactly as Steam
/// returns them (currency symbol and locale decimal separators included) —
/// parsing them into numbers would guess at the locale, so aggregation across
/// stacks is left to the caller with the strings in hand.
/// </summary>
public sealed record ItemPriceResult(
	bool Success,
	string? LowestPrice,
	string? MedianPrice,
	string? Volume,
	string? Error);

public sealed class ItemPriceClient : IItemPriceClient
{
	private readonly Func<Uri, CancellationToken, Task<SteamWebResponse>> _getAsync;

	public ItemPriceClient(Func<Uri, CancellationToken, Task<SteamWebResponse>> getAsync)
	{
		_getAsync = getAsync ?? throw new ArgumentNullException(nameof(getAsync));
	}

	public ItemPriceClient(SteamWebHandler webHandler)
	{
		ArgumentNullException.ThrowIfNull(webHandler);
		_getAsync = (url, ct) => webHandler.GetAsync(url, null, ct);
	}

	public async Task<ItemPriceResult> GetItemPriceAsync(
		uint appId,
		string marketHashName,
		uint? currency,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrEmpty(marketHashName);
		cancellationToken.ThrowIfCancellationRequested();

		Uri url = BuildUrl(appId, marketHashName, currency);

		SteamWebResponse response;
		try
		{
			response = await _getAsync(url, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return new ItemPriceResult(false, null, null, null, $"request failed: {ex.Message}");
		}

		if (!response.IsSuccess)
		{
			return new ItemPriceResult(false, null, null, null,
				$"HTTP {(int)response.StatusCode}");
		}

		if (string.IsNullOrEmpty(response.Body))
		{
			return new ItemPriceResult(false, null, null, null, "empty response body");
		}

		return Parse(response.Body);
	}

	/// <summary>Shared cache key for a priceoverview lookup (both consumer actions).</summary>
	internal static string CacheKey(uint appId, string marketHashName, uint? currency) =>
		$"itemprice:{appId}:{currency?.ToString(CultureInfo.InvariantCulture) ?? "default"}:{marketHashName}";

	internal static Uri BuildUrl(uint appId, string marketHashName, uint? currency)
	{
		var sb = new StringBuilder("https://steamcommunity.com/market/priceoverview/?appid=");
		sb.Append(appId.ToString(CultureInfo.InvariantCulture));
		if (currency is not null)
		{
			sb.Append("&currency=");
			sb.Append(currency.Value.ToString(CultureInfo.InvariantCulture));
		}

		sb.Append("&market_hash_name=");
		sb.Append(Uri.EscapeDataString(marketHashName));
		return new Uri(sb.ToString());
	}

	internal static ItemPriceResult Parse(string body)
	{
		JsonDocument doc;
		try
		{
			doc = JsonDocument.Parse(body);
		}
		catch (JsonException)
		{
			return new ItemPriceResult(false, null, null, null, "malformed JSON response");
		}

		using (doc)
		{
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				return new ItemPriceResult(false, null, null, null, "unexpected JSON shape");
			}

			JsonElement root = doc.RootElement;
			if (!root.TryGetProperty("success", out JsonElement success) ||
				success.ValueKind != JsonValueKind.True)
			{
				// Steam answers {"success":false} when nothing is listed.
				return new ItemPriceResult(false, null, null, null, "not listed on the market");
			}

			return new ItemPriceResult(
				true,
				TryGetString(root, "lowest_price"),
				TryGetString(root, "median_price"),
				TryGetString(root, "volume"),
				null);
		}
	}

	private static string? TryGetString(JsonElement root, string name) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
