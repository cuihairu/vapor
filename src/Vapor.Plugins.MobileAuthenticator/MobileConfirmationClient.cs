using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.MobileAuthenticator;

/// <summary>A pending trade/market confirmation awaiting mobile approval.</summary>
public sealed record TradeConfirmation(
	ulong Id,
	ulong Nonce,
	ulong CreatorId,
	string? Headline,
	string? Summary,
	string? Type = null
);

public enum ConfirmationOperation
{
	Allow,
	Cancel
}

public sealed record MobileConfirmationListResult(
	bool Success,
	string? Error = null,
	IReadOnlyList<TradeConfirmation>? Confirmations = null
);

public sealed record MobileConfirmationResult(bool Success, string? Error = null);

/// <summary>
/// Talks to Steam's mobile confirmation endpoints (/mobileconf/*) on behalf of a logged-in
/// session, using confirmation hashes derived from the account's identity secret.
/// </summary>
public interface IMobileConfirmationClient
{
	Task<MobileConfirmationListResult> GetConfirmationsAsync(string identitySecret, CancellationToken cancellationToken);

	Task<MobileConfirmationResult> RespondAsync(
		string identitySecret,
		ulong confirmationId,
		ulong nonce,
		ConfirmationOperation operation,
		CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IMobileConfirmationClient"/> backed by a session's <see cref="SteamWebHandler"/>.
/// </summary>
public sealed class MobileConfirmationClient : IMobileConfirmationClient
{
	private static readonly Uri CommunityBase = new("https://steamcommunity.com");

	private readonly SteamWebHandler _webHandler;
	private readonly SteamTimeSynchronizer _timeSynchronizer;
	private readonly ILogger? _logger;

	public MobileConfirmationClient(
		SteamWebHandler webHandler,
		SteamTimeSynchronizer timeSynchronizer,
		ILogger? logger = null)
	{
		_webHandler = webHandler ?? throw new ArgumentNullException(nameof(webHandler));
		_timeSynchronizer = timeSynchronizer ?? throw new ArgumentNullException(nameof(timeSynchronizer));
		_logger = logger;
	}

	public async Task<MobileConfirmationListResult> GetConfirmationsAsync(string identitySecret, CancellationToken cancellationToken)
	{
		var steamId = ResolveOwnSteamId();
		if (steamId is null)
		{
			return new MobileConfirmationListResult(false, "Unable to determine own SteamID from session cookies");
		}

		var url = BuildUrl("getlist", identitySecret, steamId.Value, tag: "conf");
		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
		{
			return new MobileConfirmationListResult(false, $"Failed to fetch confirmations: HTTP {response.StatusCodeNumber}");
		}

		return ParseConfirmationsList(response.Body);
	}

	public async Task<MobileConfirmationResult> RespondAsync(
		string identitySecret,
		ulong confirmationId,
		ulong nonce,
		ConfirmationOperation operation,
		CancellationToken cancellationToken)
	{
		var steamId = ResolveOwnSteamId();
		if (steamId is null)
		{
			return new MobileConfirmationResult(false, "Unable to determine own SteamID from session cookies");
		}

		var op = operation == ConfirmationOperation.Allow ? "allow" : "cancel";
		var baseUrl = BuildUrl("ajaxop", identitySecret, steamId.Value, tag: op);
		var url = new Uri($"{baseUrl}&op={op}&cid={confirmationId}&ck={nonce}");

		var response = await _webHandler.GetAsync(url, null, cancellationToken).ConfigureAwait(false);

		if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Body))
		{
			return new MobileConfirmationResult(false, $"Failed to respond to confirmation: HTTP {response.StatusCodeNumber}");
		}

		return ParseOperationResult(response.Body);
	}

	private Uri BuildUrl(string endpoint, string identitySecret, ulong steamId, string tag)
	{
		var time = _timeSynchronizer.GetCurrentSteamTime();
		var hash = ConfirmationHashGenerator.Generate(identitySecret, time, tag);
		var deviceId = SteamDeviceId.FromSteamId(steamId);

		var query = string.Join('&', new[]
		{
			"p=" + Uri.EscapeDataString(deviceId),
			"a=" + steamId,
			"k=" + Uri.EscapeDataString(hash),
			"t=" + time,
			"m=react",
			"tag=" + Uri.EscapeDataString(tag)
		});

		return new Uri(CommunityBase, $"/mobileconf/{endpoint}?{query}");
	}

	private ulong? ResolveOwnSteamId()
	{
		var cookies = _webHandler.GetAllCookies();

		foreach (var cookieName in new[] { "steamLoginSecure", "steamLogin", "steamlogin[secure]", "steamlogin" })
		{
			if (!cookies.TryGetValue(cookieName, out var value) || string.IsNullOrEmpty(value))
			{
				continue;
			}

			// Cookie format: "<steamid>%7C%7C<token>" (URL-encoded pipe separators).
			var steamIdPart = value.Split("%7C%7C")[0].Split('|')[0];
			if (ulong.TryParse(steamIdPart, out var steamId) && steamId >= 76561197960265728UL)
			{
				return steamId;
			}
		}

		return null;
	}

	internal static MobileConfirmationListResult ParseConfirmationsList(string body)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (!root.TryGetProperty("success", out var successElem) || !successElem.GetBoolean())
			{
				var message = root.TryGetProperty("message", out var msgElem) ? msgElem.GetString() : null;
				return new MobileConfirmationListResult(false, message ?? "Steam rejected the confirmation list request");
			}

			var confirmations = new List<TradeConfirmation>();

			if (root.TryGetProperty("conf", out var confElem) && confElem.ValueKind == JsonValueKind.Array)
			{
				foreach (var entry in confElem.EnumerateArray())
				{
					var id = GetUInt64(entry, "id");
					var nonce = GetUInt64(entry, "nonce");
					var creatorId = GetUInt64(entry, "creator_id");

					if (id is null || nonce is null)
					{
						continue;
					}

					confirmations.Add(new TradeConfirmation(
						Id: id.Value,
						Nonce: nonce.Value,
						CreatorId: creatorId ?? 0,
						Headline: entry.TryGetProperty("headline", out var headlineElem) ? headlineElem.GetString() : null,
						Summary: entry.TryGetProperty("summary", out var summaryElem) ? summaryElem.GetString() : null,
						Type: entry.TryGetProperty("type", out var typeElem) ? ParseConfirmationType(typeElem) : null));
				}
			}

			return new MobileConfirmationListResult(true, null, confirmations);
		}
		catch (JsonException ex)
		{
			return new MobileConfirmationListResult(false, $"Failed to parse confirmation list: {ex.Message}");
		}
	}

	internal static MobileConfirmationResult ParseOperationResult(string body)
	{
		try
		{
			using var doc = JsonDocument.Parse(body);
			var root = doc.RootElement;

			if (root.TryGetProperty("success", out var successElem) && successElem.GetBoolean())
			{
				return new MobileConfirmationResult(true);
			}

			var message = root.TryGetProperty("message", out var msgElem) ? msgElem.GetString() : null;
			return new MobileConfirmationResult(false, message ?? "Steam rejected the confirmation operation");
		}
		catch (JsonException ex)
		{
			return new MobileConfirmationResult(false, $"Failed to parse confirmation operation result: {ex.Message}");
		}
	}

	// Steam encodes the confirmation type as an integer enum: 1 = generic,
	// 2 = trade offer, 3 = market listing. Normalize to lowercase names so the
	// batch action's "type" filter reads naturally; unknown codes pass through.
	private static string? ParseConfirmationType(JsonElement element)
	{
		return element.ValueKind switch
		{
			JsonValueKind.Number when element.TryGetInt32(out var code) => code switch
			{
				1 => "generic",
				2 => "trade",
				3 => "market",
				_ => code.ToString(System.Globalization.CultureInfo.InvariantCulture)
			},
			JsonValueKind.String => element.GetString()!.Trim().ToLowerInvariant(), // STJ: GetString() is non-null for a String token — the ?. null arm would be an unreachable probe
			_ => null
		};
	}

	private static ulong? GetUInt64(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var prop))
		{
			return null;
		}

		return prop.ValueKind switch
		{
			JsonValueKind.String when ulong.TryParse(prop.GetString(), out var parsed) => parsed,
			JsonValueKind.Number when prop.TryGetUInt64(out var parsed) => parsed,
			_ => null
		};
	}
}
