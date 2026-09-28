using System.Text;
using System.Text.Json;

namespace Vapor.Plugins.GameData;

/// <summary>
/// Minimal key-authed client for the official Steam Web API
/// (<c>api.steampowered.com</c>). Read-only GETs only; the API key is
/// operator-supplied agent-side (plugin configuration or environment) and
/// never leaves the agent — the control plane never sees it.
/// </summary>
internal sealed class SteamWebApiClient : IDisposable
{
	/// <summary>Web API base; everything this client does is a GET under it.</summary>
	internal const string BaseUrl = "https://api.steampowered.com/";

	/// <summary>Default request timeout — read-only analytics never needs long.</summary>
	internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

	private readonly string _apiKey;
	private readonly HttpClient _client;

	/// <summary>Creates the client for an operator-supplied API key.</summary>
	public SteamWebApiClient(string apiKey)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
		_apiKey = apiKey;

		// CA2000 suppressed: ownership of the handler transfers to this
		// instance; Dispose releases handler and client together.
#pragma warning disable CA2000
		_client = new HttpClient(new SocketsHttpHandler
		{
			ConnectTimeout = Timeout,
			UseCookies = false
		})
		{
			Timeout = Timeout
		};
#pragma warning restore CA2000
	}

	/// <summary>Test hook: injects the transport handler instead of a live socket pool.</summary>
	internal SteamWebApiClient(string apiKey, HttpMessageHandler handler)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
		_apiKey = apiKey;

		// CA2000 suppressed: ownership transfers to this instance.
#pragma warning disable CA2000
		_client = new HttpClient(handler) { Timeout = Timeout };
#pragma warning restore CA2000
	}

	/// <summary>
	/// GETs <c>{BaseUrl}{path}?key=...&amp;query...</c> and parses the JSON
	/// body. Throws <see cref="HttpRequestException"/> on a non-success status
	/// and <see cref="JsonException"/> on a malformed body; the caller owns the
	/// returned document.
	/// </summary>
	public async Task<JsonDocument> GetJsonAsync(
		string path,
		IReadOnlyList<(string Name, string Value)> query,
		CancellationToken cancellationToken)
	{
		var url = new StringBuilder(BaseUrl).Append(path).Append("?key=").Append(Uri.EscapeDataString(_apiKey));
		foreach ((string name, string value) in query)
		{
			url.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
		}

		using HttpResponseMessage response = await _client
			.GetAsync(new Uri(url.ToString(), UriKind.Absolute), cancellationToken)
			.ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"Steam Web API returned {(int)response.StatusCode} {response.StatusCode} for {path}");
		}

		string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		return JsonDocument.Parse(body);
	}

	/// <summary>Releases the client and its handler.</summary>
	public void Dispose()
	{
		_client.Dispose();
	}
}
