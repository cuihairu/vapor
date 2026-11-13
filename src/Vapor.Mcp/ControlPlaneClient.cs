using System.Net.Http.Headers;
using ModelContextProtocol;

namespace Vapor.Mcp;

/// <summary>
/// Thin HTTP face over the running control plane's REST API. This is a proxy,
/// not a second query implementation: every tool call resolves through the
/// control plane's existing endpoints and therefore through the same service
/// layer and auth checks the dashboard uses.
/// </summary>
public sealed class ControlPlaneClient : IDisposable
{
	private const int MaxErrorBodyLength = 400;

	private readonly HttpClient _http;

	public ControlPlaneClient(string baseUrl, string adminApiKey, HttpClient? http = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(adminApiKey);
		_http = http ?? new HttpClient { BaseAddress = new Uri(baseUrl) };
		_http.BaseAddress ??= new Uri(baseUrl);
		_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminApiKey);
	}

	/// <summary>GET the path and return the raw body; non-success becomes an <see cref="McpException"/>.</summary>
	public Task<string> GetAsync(string path, CancellationToken cancellationToken = default)
		=> SendAsync(() => _http.GetAsync(new Uri(path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cancellationToken), cancellationToken);

	/// <summary>POST the path with an empty body and return the raw body; non-success becomes an <see cref="McpException"/>.</summary>
	public Task<string> PostAsync(string path, CancellationToken cancellationToken = default)
		=> SendAsync(
			() =>
			{
				// CA2000 suppressed: HttpClient.SendAsync disposes the request message.
#pragma warning disable CA2000
				var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
#pragma warning restore CA2000
				return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			},
			cancellationToken);

	/// <summary>
	/// Wraps transport-level failures (control plane down, wrong base URL) into
	/// an <see cref="McpException"/> so the client sees the actionable message
	/// instead of the SDK's generic error text.
	/// </summary>
	private async Task<string> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
	{
		HttpResponseMessage response;
		try
		{
			response = await send().ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			throw new McpException(
				$"cannot reach the control plane at {_http.BaseAddress} ({ex.GetType().Name}: {ex.Message}); check that it is running and that Vapor_MCP_BASE_URL points at it",
				ex);
		}

		using (response)
		{
			return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Append only the non-blank parameters as a query string.</summary>
	public static string WithQuery(string path, params (string Key, string? Value)[] parameters)
	{
		IEnumerable<string> pairs = parameters
			.Where(p => !string.IsNullOrWhiteSpace(p.Value))
			.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!.Trim())}");
		string query = string.Join("&", pairs);
		return query.Length == 0 ? path : $"{path}?{query}";
	}

	private async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		string body;
		try
		{
			body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			throw new McpException($"control plane returned {(int)response.StatusCode} {response.StatusCode} with an unreadable body");
		}

		if (response.IsSuccessStatusCode)
		{
			return body;
		}

		string detail = body.Length > 0 ? $": {Truncate(body)}" : string.Empty;
		string hint = (int)response.StatusCode == 401
			? " (check Vapor_MCP_ADMIN_API_KEY against the control plane's Vapor_ADMIN_API_KEY)"
			: string.Empty;
		throw new McpException($"control plane returned {(int)response.StatusCode} {response.StatusCode}{hint}{detail}");
	}

	private static string Truncate(string body)
		=> body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength] + "…";

	public void Dispose() => _http.Dispose();
}
