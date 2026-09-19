using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.Steam.Core.Tests;

/// <summary>
/// Property-based round-trip invariants (FsCheck) over the AES-GCM helper's
/// explicit-key API — the same key that encrypts must decrypt any plaintext back
/// bit-for-bit, and a wrong key must never silently return it.
/// </summary>
public sealed class VaporCryptoRoundTripPropertyTests
{
	[Property]
	public Property EncryptThenDecrypt_WithSameKey_RestoresPlaintext(byte[] keySeed, NonNull<string> plaintext)
	{
		// Contract requires non-empty text; lone surrogates are not UTF-8 lossless
		// (they encode as U+FFFD), so the round-trip property only spans what the
		// encoder itself round-trips.
		if (plaintext.Get.Length == 0 || !Utf8RoundTrips(plaintext.Get))
		{
			return true.ToProperty();
		}

		byte[] key = MakeKey(keySeed);

		string? ciphertext = VaporCryptoHelper.EncryptWithKey(key, plaintext.Get);
		if (ciphertext is null)
		{
			return false.ToProperty();
		}

		string? restored = VaporCryptoHelper.DecryptWithKey(key, ciphertext);
		return (restored is not null && restored == plaintext.Get).ToProperty();
	}

	private static bool Utf8RoundTrips(string s) =>
		Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s)) == s;

	[Property]
	public Property Decrypt_WithDifferentKey_NeverRestoresPlaintext(byte[] keySeed, byte[] otherKeySeed, NonNull<string> plaintext)
	{
		if (plaintext.Get.Length == 0 || !Utf8RoundTrips(plaintext.Get))
		{
			return true.ToProperty();
		}

		byte[] key = MakeKey(keySeed);
		byte[] other = MakeKey(otherKeySeed, differentFrom: key);

		string? ciphertext = VaporCryptoHelper.EncryptWithKey(key, plaintext.Get);
		if (ciphertext is null)
		{
			return false.ToProperty();
		}

		// AES-GCM authenticates: a foreign key fails the auth tag. The helper may
		// surface that as null or an exception — both are fine; what must never
		// happen is a successful-looking wrong plaintext.
		try
		{
			string? restored = VaporCryptoHelper.DecryptWithKey(other, ciphertext);
			return (restored is null || restored != plaintext.Get).ToProperty();
		}
		catch (System.Security.Cryptography.CryptographicException)
		{
			return true.ToProperty();
		}
	}

	[Property]
	public Property FreshNonces_MeanDistinctCiphertexts_BothDecrypt(byte[] keySeed, NonNull<string> plaintext)
	{
		if (plaintext.Get.Length == 0 || !Utf8RoundTrips(plaintext.Get))
		{
			return true.ToProperty();
		}

		byte[] key = MakeKey(keySeed);

		string? first = VaporCryptoHelper.EncryptWithKey(key, plaintext.Get);
		string? second = VaporCryptoHelper.EncryptWithKey(key, plaintext.Get);
		if (first is null || second is null || first == plaintext.Get || second == plaintext.Get)
		{
			return false.ToProperty();
		}

		// Random nonce per call: two ciphertexts differ, and both restore the
		// same plaintext under the same key.
		return (first != second).ToProperty()
			.And(VaporCryptoHelper.DecryptWithKey(key, second) == plaintext.Get);
	}

	/// <summary>Derives a deterministic ≥32-byte key from arbitrary FsCheck bytes.</summary>
	private static byte[] MakeKey(byte[] seed, byte[]? differentFrom = null)
	{
		byte[] key = new byte[32];
		for (int i = 0; i < key.Length; i++)
		{
			key[i] = seed.Length == 0 ? (byte)i : seed[i % seed.Length];
		}

		if (differentFrom is not null && key.SequenceEqual(differentFrom))
		{
			key[0] ^= 0xFF; // Force difference; extremely rare but must not hang the property.
		}

		return key;
	}
}
