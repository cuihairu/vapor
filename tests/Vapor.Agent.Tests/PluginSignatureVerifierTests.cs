using System.Security.Cryptography;
using Xunit;
using Vapor.Agent;

namespace Vapor.Agent.Tests;

/// <summary>
/// The declared-signature path is the origin check that turns "digest pinned by
/// the operator" into "package signed by a key the operator pinned". The
/// convention is exact: RSA-PSS over the ASCII bytes of the lowercase hex
/// digest the operator pinned.
/// </summary>
public sealed class PluginSignatureVerifierTests
{
	private static readonly string Digest = new string('a', 64);

	[Fact]
	public void Verify_ValidSignatureFromPinnedKey_Succeeds()
	{
		using var key = RSA.Create(2048);
		var pin = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
		var signature = PluginSignatureVerifier.Sign(Digest, key);

		var (ok, error) = PluginSignatureVerifier.Verify(Digest, pin, signature, [pin]);

		Assert.True(ok);
		Assert.Equal(string.Empty, error);
	}

	[Fact]
	public void Verify_TamperedDigest_Fails()
	{
		using var key = RSA.Create(2048);
		var pin = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
		var signature = PluginSignatureVerifier.Sign(Digest, key);

		// Signing the pinned digest and verifying a different one is the
		// substituted-package case the signature exists to catch.
		var (ok, error) = PluginSignatureVerifier.Verify("b".PadRight(64, '0'), pin, signature, [pin]);

		Assert.False(ok);
		Assert.Contains("does not verify", error);
	}

	[Fact]
	public void Verify_UnpinnedKey_Fails()
	{
		using var signer = RSA.Create(2048);
		using var pinnedKey = RSA.Create(2048);
		var pin = Convert.ToBase64String(pinnedKey.ExportSubjectPublicKeyInfo());
		var foreignKey = Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());
		var signature = PluginSignatureVerifier.Sign(Digest, signer);

		var (ok, error) = PluginSignatureVerifier.Verify(Digest, foreignKey, signature, [pin]);

		Assert.False(ok);
		Assert.Contains("not among this agent's pinned", error);
	}

	[Fact]
	public void Verify_MalformedPresentedKey_Fails()
	{
		using var key = RSA.Create(2048);
		var pin = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
		var signature = PluginSignatureVerifier.Sign(Digest, key);

		var (ok, error) = PluginSignatureVerifier.Verify(Digest, "not-a-key", signature, [pin]);

		Assert.False(ok);
		Assert.Contains("not a valid RSA SubjectPublicKeyInfo", error);
	}

	[Fact]
	public void Verify_MalformedSignatureBase64_Fails()
	{
		using var key = RSA.Create(2048);
		var pin = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

		var (ok, error) = PluginSignatureVerifier.Verify(Digest, pin, "!!not-base64!!", [pin]);

		Assert.False(ok);
		Assert.Contains("not valid base64", error);
	}

	[Fact]
	public void Verify_MissingPins_FailsClosed()
	{
		using var key = RSA.Create(2048);
		var pin = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
		var signature = PluginSignatureVerifier.Sign(Digest, key);

		var nullPins = PluginSignatureVerifier.Verify(Digest, pin, signature, null);
		var emptyPins = PluginSignatureVerifier.Verify(Digest, pin, signature, []);

		// Declared signature plus no pin list is fail-closed, never a skip: the
		// operator asked for an origin claim the agent cannot make.
		Assert.False(nullPins.Ok);
		Assert.Contains("pins no trusted publisher keys", nullPins.Error);
		Assert.False(emptyPins.Ok);
		Assert.Contains("pins no trusted publisher keys", emptyPins.Error);
	}

	[Fact]
	public void Verify_UnparseablePinsAreSkipped_OtherPinStillMatches()
	{
		using var key = RSA.Create(2048);
		var pin = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
		var signature = PluginSignatureVerifier.Sign(Digest, key);

		// Garbage base64 and valid base64 that is not an SPKI document are both
		// skipped so one bad line in the pin list cannot break the rest.
		var (ok, error) = PluginSignatureVerifier.Verify(Digest, pin, signature, ["!", Convert.ToBase64String([1, 2, 3]), pin]);

		Assert.True(ok);
		Assert.Equal(string.Empty, error);
	}
}
