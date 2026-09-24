using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Utilities;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Property-based invariants (FsCheck) over the redaction helpers — the
/// control-character sanitizer and the key-based/URI-credential redaction.
/// The example tests in SensitiveDataRedactorTests pin known payloads; these
/// sweep the arbitrary-string domain log lines actually arrive from (caller-
/// controlled values, hostile casing, half-formed JSON). The sanitizer laws
/// (strips exactly the control characters, idempotent) and the redaction
/// laws (sensitive key in any casing variant → "&lt;redacted&gt;", benign
/// values round-trip, inline proxy credentials never survive) together pin
/// "never leaks, never mangles" over the free domain.
/// </summary>
public sealed class SensitiveDataRedactorPropertyTests
{
	private static readonly string[] SensitiveKeyWords =
	{
		"password", "pass", "accesstoken", "refreshtoken", "token", "apikey",
		"api_key", "authorization", "authcode", "twofactorcode", "code", "key",
		"secret", "proxy",
	};

	private static readonly string[] BenignKeyWords =
	{
		"name", "host", "region", "count", "agent", "account", "state", "job",
	};

	private static string[] CasingVariants(string word)
	{
		// NormalizeKey keeps only letters/digits lowercased, so upper-casing
		// and interleaving punctuation must both resolve back to the same
		// vocabulary word — the variants property tests lean on that.
		var upper = word.ToUpperInvariant();
		var separated = string.Join("_", word.Select(c => c.ToString()));
		return [word, upper, separated];
	}

	// Index mapping that accepts every int including MinValue (Math.Abs on
	// the raw index would overflow exactly on that boundary sample).
	private static int Ring(int index, int count) => ((index % count) + count) % count;

	// ---------- SanitizeLogValue: strips exactly the control characters ----------

	[Property]
	public Property SanitizeLogValue_OutputNeverContainsControlCharacters(NonNull<string> raw)
	{
		string output = SensitiveDataRedactor.SanitizeLogValue(raw.Get);

		return output.All(c => !char.IsControl(c)).ToProperty();
	}

	[Property]
	public Property SanitizeLogValue_IsIdempotent(NonNull<string> raw)
	{
		string once = SensitiveDataRedactor.SanitizeLogValue(raw.Get);
		string twice = SensitiveDataRedactor.SanitizeLogValue(once);

		return (once == twice).ToProperty();
	}

	[Property]
	public Property SanitizeLogValue_ControlFreeInputIsIdentity(NonNull<string> raw)
	{
		string input = raw.Get;
		if (input.Any(char.IsControl))
		{
			// Hostile inputs are governed by the two properties above; this
			// one pins the "well-formed input passes through unchanged" side.
			return true.ToProperty();
		}

		return (SensitiveDataRedactor.SanitizeLogValue(input) == input).ToProperty();
	}

	// ---------- Redact: robustness and the two scrubbing paths ----------

	[Property]
	public Property Redact_NeverThrowsOnArbitraryInput(NonNull<string> raw)
	{
		// Redact sits on the log path, so caller-controlled garbage (fragments
		// that look like JSON, wild casing, proxy-ish text) must never surface
		// an exception. Returns non-null by contract; the call itself is the assert.
		SensitiveDataRedactor.Redact(raw.Get);

		return true.ToProperty();
	}

	[Property]
	public Property Redact_ScrubsInlineProxyCredentials(int schemeIndex, NonEmptyString credRaw, NonNegativeInt hostSeed)
	{
		string scheme = (new[] { "http", "https", "socks5" })[Ring(schemeIndex, 3)];
		string cred = new(credRaw.Get
			.Where(c => !char.IsWhiteSpace(c) && c is not ('@' or '/' or '"' or ','))
			.ToArray());
		if (cred.Length == 0)
		{
			// The credential pattern requires at least one character; an empty
			// segment is not an inline-credential URI at all.
			return true.ToProperty();
		}

		string input = $"{scheme}://{cred}@h{hostSeed.Get}.internal";
		string output = SensitiveDataRedactor.Redact(input);

		// The precise claim: the scheme://cred@ segment is gone and the masked
		// placeholder took its place. A bare Contains(cred) is too weak a
		// witness — short credentials (a single letter) also occur inside the
		// placeholder or the host name without being leaked.
		return (output.Contains($"{scheme}://<redacted>@")
			&& !output.Contains($"{scheme}://{cred}@")).ToProperty();
	}

	[Property]
	public Property Redact_JsonSensitiveKeyValuesBecomeRedactedInAnyKeyVariant(int keyIndex, int variantIndex)
	{
		string word = SensitiveKeyWords[Ring(keyIndex, SensitiveKeyWords.Length)];
		string[] variants = CasingVariants(word);
		string key = variants[Ring(variantIndex, variants.Length)];

		string input = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = "s3cret-value" });
		string output = SensitiveDataRedactor.Redact(input);

		using var document = JsonDocument.Parse(output);
		return (document.RootElement.GetProperty(key).GetString() == "<redacted>").ToProperty();
	}

	[Property]
	public Property Redact_JsonBenignStringValuesRoundTrip(int keyIndex, NonNegativeInt valueSeed)
	{
		string key = BenignKeyWords[Ring(keyIndex, BenignKeyWords.Length)];
		string value = $"v{valueSeed.Get}";
		string input = JsonSerializer.Serialize(new Dictionary<string, string> { [key] = value });

		string output = SensitiveDataRedactor.Redact(input);

		// Benign, identifier-shaped values carry diagnostics — redaction must
		// not mangle them (proxy scrubbing only ever fires on the URI form).
		using var document = JsonDocument.Parse(output);
		return (document.RootElement.GetProperty(key).GetString() == value).ToProperty();
	}

	[Property]
	public Property RedactValue_DecidesExactlyOnKeySensitivity(int sensitiveIndex, int benignIndex, NonNegativeInt valueSeed)
	{
		string sensitiveKey = SensitiveKeyWords[Ring(sensitiveIndex, SensitiveKeyWords.Length)];
		string benignKey = BenignKeyWords[Ring(benignIndex, BenignKeyWords.Length)];
		string value = $"plain{valueSeed.Get}";

		return (SensitiveDataRedactor.RedactValue(sensitiveKey, value) == "<redacted>"
			&& SensitiveDataRedactor.RedactValue(benignKey, value) == value).ToProperty();
	}
}
