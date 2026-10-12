using System.Security.Cryptography;
using System.Text;

namespace Vapor.Agent;

/// <summary>
/// Verifies a declared package signature against the keys the operator pinned
/// on this agent. The signature is RSA-PSS over the SHA-256 digest string that
/// the operator pinned and the index advertises (ASCII bytes of the lowercase
/// hex digest), so what is checked is exactly what is pinned. The pinned key
/// set (<c>VAPOR_PLUGIN_TRUST_KEYS</c>) is the trust anchor — never the index.
///
/// <para>
/// There is no CA and no revocation list on purpose: "revoking" a publisher
/// key is removing it from the pin list, and the pin is operator-controlled,
/// so key rotation and revocation are config edits rather than infrastructure.
/// </para>
/// </summary>
public static class PluginSignatureVerifier
{
	/// <summary>Signature algorithm: RSA-PSS with SHA-256.</summary>
	public static readonly RSASignaturePadding SignaturePadding = RSASignaturePadding.Pss;

	/// <summary>
	/// <paramref name="hexDigest"/> is the lowercase package digest;
	/// <paramref name="publicKeyBase64"/>/<paramref name="signatureBase64"/> come
	/// from the catalog entry; <paramref name="trustedKeys"/> is this agent's
	/// pinned SubjectPublicKeyInfo keys. Never throws: every failure is a
	/// verdict plus a reason the operator can act on.
	/// </summary>
	public static (bool Ok, string Error) Verify(
		string hexDigest,
		string publicKeyBase64,
		string signatureBase64,
		IReadOnlyList<string>? trustedKeys)
	{
		if (trustedKeys is not { Count: > 0 })
		{
			return (false, "the catalog declares a package signature but this agent pins no trusted publisher keys (set VAPOR_PLUGIN_TRUST_KEYS)");
		}

		byte[] presentedKey;
		try
		{
			using var presented = RSA.Create();
			presented.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
			presentedKey = presented.ExportSubjectPublicKeyInfo();
		}
		catch (Exception ex) when (ex is FormatException or CryptographicException)
		{
			return (false, "the public key in the catalog entry is not a valid RSA SubjectPublicKeyInfo");
		}

		// Canonical SPKI bytes keep the comparison free of base64 formatting
		// differences; a pin that cannot be parsed can never match, so it is
		// skipped and the remaining pins still get a chance.
		var trusted = trustedKeys.Any(pin =>
		{
			try
			{
				using var pinned = RSA.Create();
				pinned.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pin), out _);
				return pinned.ExportSubjectPublicKeyInfo().SequenceEqual(presentedKey);
			}
			catch (Exception ex) when (ex is FormatException or CryptographicException)
			{
				return false;
			}
		});
		if (!trusted)
		{
			return (false, "the signing key in the catalog entry is not among this agent's pinned trusted publisher keys");
		}

		byte[] signature;
		try
		{
			signature = Convert.FromBase64String(signatureBase64);
		}
		catch (FormatException)
		{
			return (false, "the signature in the catalog entry is not valid base64");
		}

		using var signer = RSA.Create();
		signer.ImportSubjectPublicKeyInfo(presentedKey, out _);
		if (!signer.VerifyData(
			Encoding.ASCII.GetBytes(hexDigest), signature, HashAlgorithmName.SHA256, SignaturePadding))
		{
			return (false, "the package signature does not verify against the trusted signing key");
		}

		return (true, string.Empty);
	}

	/// <summary>Signs a lowercase hex digest (test/helper path for producing index entries).</summary>
	public static string Sign(string hexDigest, RSA signingKey) =>
		Convert.ToBase64String(signingKey.SignData(
			Encoding.ASCII.GetBytes(hexDigest), HashAlgorithmName.SHA256, SignaturePadding));
}
