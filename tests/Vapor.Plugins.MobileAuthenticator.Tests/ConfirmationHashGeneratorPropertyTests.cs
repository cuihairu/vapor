using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Vapor.Plugins.MobileAuthenticator;
using Xunit;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

/// <summary>
/// Property-based tests over the mobileconf confirmation hash. Generate is
/// pinned against a locally written HMAC-SHA1 oracle (8-byte big-endian time
/// followed by the UTF-8 tag bytes, base64-encoded) — the property asserts
/// the implementation equals that construction for arbitrary secrets, times
/// and known tags — plus determinism, tag distinctness, and the failure
/// contract (blank tags and invalid secrets throw ArgumentException naming
/// the offending parameter; secret validation runs before the tag check).
/// </summary>
public class ConfirmationHashGeneratorPropertyTests
{
	private static readonly string[] KnownTags = ["conf", "details", "allow", "cancel"];

	// Blank tags the guard must reject.
	private static readonly string?[] BlankTags = [null, "", "   "];

	// The oracle: deliberately independent of ConfirmationHashGenerator.
	private static string OracleHash(byte[] secret, long unixTimeSeconds, string tag)
	{
		var tagBytes = Encoding.UTF8.GetBytes(tag);
		var buffer = new byte[8 + tagBytes.Length];
		BinaryPrimitives.WriteInt64BigEndian(buffer, unixTimeSeconds);
		tagBytes.CopyTo(buffer, 8);

		return Convert.ToBase64String(HMACSHA1.HashData(secret, buffer));
	}

	[Property]
	public void Generate_MatchesHmacOracle(byte[] secret, long unixTimeSeconds, ushort tagIdx)
	{
		if (secret.Length == 0)
		{
			return; // empty secret encodes to "" and is rejected; example-tested
		}

		var tag = KnownTags[(uint)tagIdx % KnownTags.Length];
		var encoded = Convert.ToBase64String(secret);

		Assert.Equal(
			OracleHash(secret, unixTimeSeconds, tag),
			ConfirmationHashGenerator.Generate(encoded, unixTimeSeconds, tag));
	}

	[Property]
	public void Generate_IsDeterministic_AndKnownTagsDiffer(byte[] secret, long unixTimeSeconds)
	{
		if (secret.Length == 0)
		{
			return;
		}

		var encoded = Convert.ToBase64String(secret);
		var byTag = KnownTags.ToDictionary(
			t => t,
			t => ConfirmationHashGenerator.Generate(encoded, unixTimeSeconds, t));

		foreach (var (tag, hash) in byTag)
		{
			Assert.Equal(hash, ConfirmationHashGenerator.Generate(encoded, unixTimeSeconds, tag));
		}

		// Four distinct tags over the same input yield four distinct hashes.
		Assert.Equal(KnownTags.Length, byTag.Values.Distinct().Count());
	}

	[Property]
	public void Generate_RejectsInvalidInput_WithParamName(byte[] secret, ushort probeIdx)
	{
		if (secret.Length == 0)
		{
			// DecodeSecret runs before the tag guard, so the secret's own
			// parameter name surfaces for an empty encoded secret.
			var secretEx = Assert.Throws<ArgumentException>(
				() => ConfirmationHashGenerator.Generate("", 0, "conf"));
			Assert.Equal("identitySecretBase64", secretEx.ParamName);
			return;
		}

		var encoded = Convert.ToBase64String(secret);
		var tagEx = Assert.Throws<ArgumentException>(
			() => ConfirmationHashGenerator.Generate(encoded, 0, BlankTags[(uint)probeIdx % BlankTags.Length]!));
		Assert.Equal("tag", tagEx.ParamName);
	}
}
