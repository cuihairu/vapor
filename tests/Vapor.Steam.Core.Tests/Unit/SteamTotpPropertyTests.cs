using System.Buffers.Binary;
using System.Security.Cryptography;
using FsCheck;
using FsCheck.Xunit;
using Vapor.Steam.Core.Steam;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Property-based tests over the Steam authenticator TOTP math. Generate is
/// pinned against a locally written RFC 6238 oracle (HMAC-SHA1 over the
/// 30-second window, dynamic truncation, Steam alphabet projection) — the
/// property asserts the implementation equals the spec for arbitrary secrets
/// and times — plus the output shape (five chars from the unambiguous Steam
/// alphabet) and SecondsRemaining's periodicity. DecodeSecret round-trips
/// every valid base64 payload and rejects blanks/invalid input by parameter
/// name.
/// </summary>
public class SteamTotpPropertyTests
{
	// The 26-symbol transcription-safe alphabet (no 0/1/I/L/O); duplicated
	// here so the oracle stays independent of SteamTotp internals.
	private const string SteamAlphabet = "23456789BCDFGHJKMNPQRTVWXY";

	// Inputs no base64 decoder accepts: blank (guarded), malformed text, and
	// a length that is not a multiple of four. ("ZZZZ" would be VALID base64.)
	private static readonly string?[] BadSecrets = [null, "", "   ", "!!not-base64!!", "abc", "===="];

	// The RFC 6238 oracle: exactly what the spec prescribes for SHA-1 with a
	// 30-second step, plus the Steam alphabet projection. Deliberately
	// independent of SteamTotp's code paths.
	private static string OracleCode(byte[] secret, long unixTimeSeconds)
	{
		Span<byte> timeBytes = stackalloc byte[8];
		BinaryPrimitives.WriteInt64BigEndian(timeBytes, unixTimeSeconds / 30);

		Span<byte> hash = stackalloc byte[20];
		HMACSHA1.HashData(secret, timeBytes, hash);

		var offset = hash[19] & 0x0F;
		uint codepoint =
			((uint)(hash[offset] & 0x7F) << 24) |
			(uint)(hash[offset + 1] << 16) |
			(uint)(hash[offset + 2] << 8) |
			hash[offset + 3];

		var chars = new char[SteamTotp.CodeLength];
		for (var i = 0; i < SteamTotp.CodeLength; i++)
		{
			chars[i] = SteamAlphabet[(int)(codepoint % (uint)SteamAlphabet.Length)];
			codepoint /= (uint)SteamAlphabet.Length;
		}

		return new string(chars);
	}

	private static string EncodedSecret(byte[] secret) => Convert.ToBase64String(secret);

	[Property]
	public void Generate_MatchesRfc6238Oracle(byte[] secret, ulong window)
	{
		if (secret.Length == 0)
		{
			return; // empty secret is rejected; covered by the example tests
		}

		var time = (long)window * SteamTotp.TimeStepSeconds;

		Assert.Equal(OracleCode(secret, time), SteamTotp.Generate(EncodedSecret(secret), time));
	}

	[Property]
	public void Generate_OutputIsDeterministic_FiveUnambiguousChars(byte[] secret, long unixTimeSeconds)
	{
		if (secret.Length == 0)
		{
			return;
		}

		var encoded = EncodedSecret(secret);
		var code = SteamTotp.Generate(encoded, unixTimeSeconds);

		Assert.Equal(code, SteamTotp.Generate(encoded, unixTimeSeconds));
		Assert.Equal(SteamTotp.CodeLength, code.Length);
		// Character-set membership implies transcription safety: the Steam
		// alphabet omits 0/1/I/L/O.
		Assert.All(code, c => Assert.Contains(c, SteamAlphabet));
	}

	[Property]
	public void Generate_SameWindowAnyOffset_ProducesSameCode(byte[] secret, PositiveInt window, int offsetIdx)
	{
		if (secret.Length == 0)
		{
			return;
		}

		var encoded = EncodedSecret(secret);
		var baseTime = (long)window.Get * SteamTotp.TimeStepSeconds;
		var anyOffset = (uint)offsetIdx % SteamTotp.TimeStepSeconds;

		// Both points fall inside [baseTime, baseTime + 30).
		Assert.Equal(
			SteamTotp.Generate(encoded, baseTime + SteamTotp.TimeStepSeconds - 1),
			SteamTotp.Generate(encoded, baseTime + anyOffset));
	}

	[Property]
	public void SecondsRemaining_Periodic_AndWithinDomain(long time)
	{
		if (time > long.MaxValue - SteamTotp.TimeStepSeconds)
		{
			return; // keep time + step overflow-free
		}

		var remaining = SteamTotp.SecondsRemaining(time);

		Assert.InRange(remaining, 1, SteamTotp.TimeStepSeconds);
		Assert.Equal(remaining, SteamTotp.SecondsRemaining(time + SteamTotp.TimeStepSeconds));
	}

	[Property]
	public void DecodeSecret_RoundTripsValidBase64(byte[] secret)
	{
		if (secret.Length == 0)
		{
			return; // encodes to "" which the blank guard rejects
		}

		Assert.Equal(secret, SteamTotp.DecodeSecret(EncodedSecret(secret), "p"));
	}

	[Property]
	public void DecodeSecret_RejectsBadInput_WithParamName(ushort probeIdx)
	{
		var ex = Assert.Throws<ArgumentException>(
			() => SteamTotp.DecodeSecret(BadSecrets[(uint)probeIdx % BadSecrets.Length], "p"));

		Assert.Equal("p", ex.ParamName);
	}
}
