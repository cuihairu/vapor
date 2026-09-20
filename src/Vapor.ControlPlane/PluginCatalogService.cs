using System.Text.Json;

namespace Vapor.ControlPlane;

/// <summary>
/// Fetches and caches the plugin index — a JSON document naming installable
/// plugins with their package URL and SHA-256 checksum:
/// `{ "plugins": [ { "id", "name", "version", "apiVersion", "description",
/// "url", "sha256", "trust", "permissions" } ] }`. The index is the only
/// plugin-related state the ControlPlane holds; packages themselves are pulled
/// by the agents directly from the package URL.
/// </summary>
public sealed class PluginCatalogService
{
	internal static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

	private readonly HttpClient _http;
	private readonly ILogger _logger;
	private readonly Func<string?> _indexUrl;
	private readonly object _gate = new();
	private volatile PluginCatalog? _cached;

	public PluginCatalogService(HttpClient http, ILogger logger, Func<string?> indexUrl)
	{
		_http = http;
		_logger = logger;
		_indexUrl = indexUrl;
	}

	/// <summary>Current catalog: from cache when fresh, otherwise (re)fetched.</summary>
	public async Task<PluginCatalog> GetCatalogAsync(CancellationToken cancellationToken)
	{
		string? url = _indexUrl();
		if (string.IsNullOrWhiteSpace(url))
		{
			return PluginCatalog.NotConfigured();
		}

		PluginCatalog? cached = _cached;
		if (cached is { } fresh && fresh.Configured && DateTimeOffset.UtcNow - fresh.FetchedAt < CacheLifetime && string.Equals(fresh.Source, url, StringComparison.Ordinal))
		{
			return fresh;
		}

		string json;
		try
		{
			json = await _http.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Plugin index fetch from {Url} failed", url);
			return new PluginCatalog(Configured: true, Source: url, FetchedAt: DateTimeOffset.UtcNow, Entries: [], Error: $"index fetch failed: {ex.Message}");
		}

		PluginCatalog catalog;
		try
		{
			catalog = ParseIndex(json, url);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Plugin index at {Url} is not a valid catalog", url);
			return new PluginCatalog(true, url, DateTimeOffset.UtcNow, [], $"index is not a valid catalog: {ex.Message}");
		}

		lock (_gate)
		{
			_cached = catalog;
		}

		return catalog;
	}

	/// <summary>Drops the cached index so the next read refetches (tests / admin refresh).</summary>
	public void Invalidate() => _cached = null;

	internal static PluginCatalog ParseIndex(string json, string url)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
		{
			throw new FormatException("expected an object with a 'plugins' array");
		}

		var entries = new List<PluginIndexEntry>();
		foreach (var item in plugins.EnumerateArray())
		{
			entries.Add(ParseEntry(item));
		}

		return new PluginCatalog(true, url, DateTimeOffset.UtcNow, entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(), null);
	}

	private static PluginIndexEntry ParseEntry(JsonElement item)
	{
		string Get(string name)
		{
			if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
			{
				throw new FormatException($"index entry is missing the '{name}' string");
			}

			return value.GetString()!;
		}

		var permissions = new List<string>();
		if (item.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
		{
			permissions.AddRange(perms.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!));
		}

		return new PluginIndexEntry(
			Id: Get("id"),
			Name: Get("name"),
			Version: Get("version"),
			ApiVersion: Get("apiVersion"),
			Url: Get("url"),
			Sha256: Get("sha256").ToLowerInvariant(),
			Description: item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
			Trust: item.TryGetProperty("trust", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
			Permissions: permissions
		);
	}
}

/// <summary>A snapshot of the plugin index.</summary>
public sealed record PluginCatalog(
	bool Configured,
	string? Source,
	DateTimeOffset FetchedAt,
	IReadOnlyList<PluginIndexEntry> Entries,
	string? Error)
{
	public static PluginCatalog NotConfigured() => new(false, null, default, [], null);
}

/// <summary>One installable plugin as declared by the index.</summary>
public sealed record PluginIndexEntry(
	string Id,
	string Name,
	string Version,
	string ApiVersion,
	string Url,
	string Sha256,
	string? Description,
	string? Trust,
	IReadOnlyList<string> Permissions);
