using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Property-based invariants over the plugin-ecosystem primitives introduced in
/// §38 P4: the agent:{id} host-target convention (round-trip and exact-acceptance),
/// the install checksum normalizer (acceptance predicate, canonical form,
/// idempotence — over an alphabet-stretched 64-hex generator so the acceptance
/// region is densely sampled), and the index parser (field transparency plus
/// Ordinal sort for arbitrary catalogs, never returning an unsorted snapshot).
/// The malformed-root rejection arms stay with the example-based suite in
/// PluginCatalogServiceTests; here arbitrary input must either parse into a
/// sorted snapshot or throw only the two contracted exception types.
/// </summary>
public sealed class PluginEcosystemPropertyTests
{
	// ---------- HostTaskTarget ----------

	[Property]
	public Property For_RoundTripsThroughTryParse(string? agentId)
	{
		string target = HostTaskTarget.For(agentId!); // concat folds null into the prefix
		bool ok = HostTaskTarget.TryParseAgentId(target, out string parsed);

		// Only null/empty ids are unrepresentable: "agent:" fails the strict-length check.
		return (ok == !string.IsNullOrEmpty(agentId) && (!ok || parsed == agentId)).ToProperty();
	}

	[Property]
	public Property TryParse_AcceptsExactlyPrefixedAndNonEmpty(bool forceNull, string? target)
	{
		string? input = forceNull ? null : target;
		bool ok = HostTaskTarget.TryParseAgentId(input, out string parsed);
		bool expected = input is not null
			&& input.StartsWith(HostTaskTarget.Prefix, StringComparison.Ordinal)
			&& input.Length > HostTaskTarget.Prefix.Length;

		// A successful parse must compose back to the exact input — nested
		// "agent:agent:x" included, since the id is everything after one prefix.
		return (ok == expected && (!ok || HostTaskTarget.For(parsed) == input)).ToProperty();
	}

	// ---------- Sha256Normalizer ----------

	private const string HexAlphabet = "0123456789abcdefABCDEF";

	// Blank-ish paddings the trim step removes; indexed FsCheck-style via ushort.
	private static readonly string[] Whitespace = ["", " ", "\t", "\n", "  \t\r\n "];

	/// <summary>Stretches an arbitrary ushort pattern into a dense 64-char hex sample.</summary>
	private static string StretchTo64(ushort[]? pattern)
	{
		pattern ??= [];
		var chars = new char[64];
		for (int i = 0; i < chars.Length; i++)
		{
			chars[i] = HexAlphabet[pattern.Length == 0 ? 0 : pattern[i % pattern.Length] % HexAlphabet.Length];
		}

		return new string(chars);
	}

	[Property]
	public Property Normalize_AcceptsPaddedHex64_AndCanonicalizesToLower(ushort padLeft, ushort padRight, ushort[]? pattern)
	{
		string hex = StretchTo64(pattern);
		string input = Whitespace[padLeft % Whitespace.Length] + hex + Whitespace[padRight % Whitespace.Length];
		string? normalized = Sha256Normalizer.Normalize(input);

		return (normalized is not null
			&& string.Equals(normalized, hex.ToLowerInvariant(), StringComparison.Ordinal)
			&& normalized.Length == 64
			&& normalized.All(char.IsAsciiHexDigit)).ToProperty();
	}

	[Property]
	public Property Normalize_AcceptsExactlyTrimmed64Hex(string? input)
	{
		string? normalized = Sha256Normalizer.Normalize(input);
		if (input is null)
		{
			return (normalized is null).ToProperty();
		}

		string trimmed = input.Trim();
		bool shouldBeAccepted = trimmed.Length == 64 && trimmed.All(char.IsAsciiHexDigit);

		return ((normalized is not null) == shouldBeAccepted
			&& (normalized is null
				|| (string.Equals(normalized, trimmed.ToLowerInvariant(), StringComparison.Ordinal)
					&& normalized.All(char.IsAsciiHexDigit)))).ToProperty();
	}

	[Property]
	public Property Normalize_IsIdempotent(string input)
	{
		string? once = Sha256Normalizer.Normalize(input);
		if (once is null)
		{
			return true.ToProperty();
		}

		return (Sha256Normalizer.Normalize(once) == once).ToProperty();
	}

	// ---------- PluginCatalogService.ParseIndex ----------

	/// <summary>Serializes arbitrary entries into a well-formed index document.</summary>
	private static string WriteIndexJson(IReadOnlyList<PluginIndexEntry> entries)
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();
			writer.WritePropertyName("plugins");
			writer.WriteStartArray();
			foreach (var entry in entries)
			{
				writer.WriteStartObject();
				writer.WriteString("id", entry.Id);
				writer.WriteString("name", entry.Name);
				writer.WriteString("version", entry.Version);
				writer.WriteString("apiVersion", entry.ApiVersion);
				writer.WriteString("url", entry.Url);
				writer.WriteString("sha256", entry.Sha256);
				if (entry.Description is not null)
				{
					writer.WriteString("description", entry.Description);
				}

				if (entry.Trust is not null)
				{
					writer.WriteString("trust", entry.Trust);
				}

				if (entry.Permissions.Count > 0)
				{
					writer.WritePropertyName("permissions");
					writer.WriteStartArray();
					foreach (var permission in entry.Permissions)
					{
						writer.WriteStringValue(permission);
					}

					writer.WriteEndArray();
				}

				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static bool SameEntry(PluginIndexEntry a, PluginIndexEntry b) =>
		a.Id == b.Id
		&& a.Name == b.Name
		&& a.Version == b.Version
		&& a.ApiVersion == b.ApiVersion
		&& a.Url == b.Url
		&& a.Sha256 == b.Sha256
		&& a.Description == b.Description
		&& a.Trust == b.Trust
		&& a.Permissions.SequenceEqual(b.Permissions);

	/// <summary>Co-indexes into a parallel array, tolerating empty and null-bearing samples.</summary>
	private static string At(string?[] source, int i) =>
		source.Length == 0 ? string.Empty : source[i % source.Length] ?? string.Empty;

	private static string[] AtList(string?[][] source, int i) =>
		source.Length == 0 ? [] : [.. (source[i % source.Length] ?? []).Select(p => p ?? string.Empty)];

	[Property]
	public Property ParseIndex_PreservesFields_NormalizesSha_AndSortsByIdOrdinal(
		bool withDescription,
		bool withTrust,
		string?[] ids,
		string?[] names,
		string?[] versions,
		string?[] apiVersions,
		string?[] urls,
		string?[] shas,
		string?[] descriptions,
		string?[] trusts,
		string?[][] permissions)
	{
		// FsCheck cannot synthesize IReadOnlyList<string>, so entries are built
		// from parallel arrays (ids drives the entry count; empty arrays fall back).
		PluginIndexEntry[] entries = [.. Enumerable.Range(0, ids.Length).Select(i => new PluginIndexEntry(
			Id: At(ids, i),
			Name: At(names, i),
			Version: At(versions, i),
			ApiVersion: At(apiVersions, i),
			Url: At(urls, i),
			Sha256: At(shas, i),
			Description: withDescription ? At(descriptions, i) : null,
			Trust: withTrust ? At(trusts, i) : null,
			Permissions: AtList(permissions, i)))];

		// Expected side: what the parser contract says each field becomes —
		// optional strings survive verbatim, the checksum is lowercased.
		var expected = entries
			.Select(e => e with { Sha256 = e.Sha256.ToLowerInvariant() })
			.OrderBy(e => e.Id, StringComparer.Ordinal)
			.ToList();

		PluginCatalog catalog = PluginCatalogService.ParseIndex(WriteIndexJson(entries), "https://index.example/plugins.json");

		return (catalog.Configured
			&& catalog.Source == "https://index.example/plugins.json"
			&& catalog.Entries.Count == expected.Count
			&& catalog.Entries.Zip(expected, SameEntry).All(matched => matched)
			&& catalog.Entries.SequenceEqual(catalog.Entries.OrderBy(e => e.Id, StringComparer.Ordinal))).ToProperty();
	}

	[Property]
	public Property ParseIndex_NeverReturnsUnsorted_AndRejectsOnlyWithContractedExceptions(string? json)
	{
		PluginCatalog catalog;
		try
		{
			catalog = PluginCatalogService.ParseIndex(json!, "https://index.example/plugins.json");
		}
		catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException)
		{
			return true.ToProperty();
		}

		return catalog.Entries.SequenceEqual(catalog.Entries.OrderBy(e => e.Id, StringComparer.Ordinal)).ToProperty();
	}
}
