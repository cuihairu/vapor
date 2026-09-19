using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Vapor.Steam.Core.Security;
using Xunit;

namespace Vapor.KeyRotation.Tests;

/// <summary>
/// The rotation math itself lives in Vapor.Steam.Core's CredentialStoreRotator
/// (covered there); this suite pins the CLI shell around it — argument parsing,
/// key-spec formats, exit codes and the console report — by calling
/// Program.Main and the private helpers directly.
///
/// Every test runs in one collection: Console.SetOut/SetError are process-wide
/// statics, so console-redirecting tests must not interleave.
/// </summary>
[Collection("vapor-keyrotation-cli")]
public sealed class ProgramCliTests : IDisposable
{
	private const string EnvKeyName = "VAPOR_KR_TESTS_KEY_ENV";

	private static readonly byte[] OldKey = new byte[32];
	private static readonly byte[] NewKey = new byte[32];

	static ProgramCliTests()
	{
		OldKey.AsSpan().Fill((byte)1);
		NewKey.AsSpan().Fill((byte)2);
	}

	private readonly string _storePath =
		Path.Combine(Path.GetTempPath(), "vapor-keyrotation-tests-" + Guid.NewGuid().ToString("N") + ".json");

	public void Dispose()
	{
		foreach (string suffix in new[] { "", ".bak.pre-rotate", ".tmp", ".keyfile" })
		{
			try
			{
				File.Delete(_storePath + suffix);
			}
			catch (IOException)
			{
			}
		}

		Environment.SetEnvironmentVariable(EnvKeyName, null);
	}

	[Fact]
	public void Help_LongFlag_ReturnsZeroAndPrintsUsage()
	{
		var (code, stdout, _) = RunMain("--help");

		Assert.Equal(0, code);
		Assert.Contains("Vapor key rotation tool", stdout);
		Assert.Contains("--dry-run", stdout);
	}

	[Fact]
	public void Help_ShortFlag_ReturnsZeroAndPrintsUsage()
	{
		var (code, stdout, _) = RunMain("-h");

		Assert.Equal(0, code);
		Assert.Contains("Usage:", stdout);
	}

	[Theory]
	[MemberData(nameof(MissingArgumentCases))]
	public void MissingRequiredArgument_ReturnsTwoAndPrintsUsage(string[] args)
	{
		var (code, stdout, _) = RunMain(args);

		Assert.Equal(2, code);
		Assert.Contains("Usage:", stdout);
	}

	public static TheoryData<string[]> MissingArgumentCases => new()
	{
		Array.Empty<string>(),
		new[] { "--store", "credentials.json" },
		new[] { "--store", "credentials.json", "--old-key", "base64:AAAA" },
	};

	[Fact]
	public void UnknownArgument_ReturnsTwoAndNamesTheArgument()
	{
		var (code, _, stderr) = RunMain("--wat");

		Assert.Equal(2, code);
		Assert.Contains("Unknown argument: --wat", stderr);
	}

	[Fact]
	public void InvalidBase64Key_ReturnsTwoWithParserError()
	{
		var (code, _, stderr) = RunMain(
			"--store", _storePath,
			"--old-key", "base64:!!!not base64!!!",
			"--new-key", KeySpec(NewKey));

		Assert.Equal(2, code);
		Assert.Contains("invalid base64", stderr);
	}

	[Fact]
	public void IdenticalKeys_ReturnsTwo()
	{
		var (code, _, stderr) = RunMain(
			"--store", _storePath,
			"--old-key", KeySpec(OldKey),
			"--new-key", KeySpec(OldKey));

		Assert.Equal(2, code);
		Assert.Contains("must differ", stderr);
	}

	[Fact]
	public void StoreFileMissing_ReturnsOneWithError()
	{
		string missing = _storePath + ".does-not-exist";

		var (code, _, stderr) = RunMain(
			"--store", missing,
			"--old-key", KeySpec(OldKey),
			"--new-key", KeySpec(NewKey));

		Assert.Equal(1, code);
		Assert.Contains("ERROR", stderr);
	}

	[Fact]
	public void DryRunOnValidStore_ReturnsZeroAndLeavesFileUnchanged()
	{
		WriteStore(("alpha", "refresh-alpha", OldKey), ("beta", "refresh-beta", OldKey));
		string before = File.ReadAllText(_storePath);

		var (code, stdout, _) = RunMain(
			"--store", _storePath,
			"--old-key", KeySpec(OldKey),
			"--new-key", KeySpec(NewKey),
			"--dry-run");

		Assert.Equal(0, code);
		Assert.Contains("Total accounts:   2", stdout);
		Assert.Contains("Rotated accounts: 2", stdout);
		Assert.Contains("Failed accounts:  0", stdout);
		Assert.Contains("dry-run (file unchanged)", stdout);
		Assert.Equal(before, File.ReadAllText(_storePath));
		Assert.False(File.Exists(_storePath + ".bak.pre-rotate"), "dry-run must not write a backup");
	}

	[Fact]
	public void RotateApplied_ReturnsZeroWritesBackupAndNewKeyDecrypts()
	{
		WriteStore(("alpha", "refresh-alpha", OldKey));

		var (code, stdout, _) = RunMain(
			"--store", _storePath,
			"--old-key", KeySpec(OldKey),
			"--new-key", KeySpec(NewKey));

		Assert.Equal(0, code);
		Assert.Contains("Total accounts:   1", stdout);
		Assert.Contains("Rotated accounts: 1", stdout);
		Assert.Contains("applied (backup written as *.bak.pre-rotate)", stdout);

		Assert.True(File.Exists(_storePath + ".bak.pre-rotate"));
		using var doc = JsonDocument.Parse(File.ReadAllText(_storePath));
		string rotated = doc.RootElement.GetProperty("accounts").GetProperty("alpha")
			.GetProperty("refreshToken").GetString()!;
		Assert.Equal("refresh-alpha", VaporCryptoHelper.DecryptWithKey(NewKey, rotated));
	}

	[Fact]
	public void UndecryptableAccount_ReturnsOneAndReportsFailure()
	{
		// "bad" was encrypted with a third key the old key cannot decrypt, so
		// rotation must refuse the whole store instead of dropping the account.
		WriteStore(("good", "refresh-good", OldKey), ("bad", "refresh-bad", CreateKey(3)));

		var (code, stdout, stderr) = RunMain(
			"--store", _storePath,
			"--old-key", KeySpec(OldKey),
			"--new-key", KeySpec(NewKey));

		Assert.Equal(1, code);
		Assert.Contains("Total accounts:   2", stdout);
		Assert.Contains("aborted (store unchanged; resolve failed accounts first)", stdout);
		Assert.Contains("FAILED: bad", stderr);
	}

	[Fact]
	public void MalformedStore_ReturnsOneWithError()
	{
		File.WriteAllText(_storePath, "not json at all");

		var (code, _, stderr) = RunMain(
			"--store", _storePath,
			"--old-key", KeySpec(OldKey),
			"--new-key", KeySpec(NewKey));

		Assert.Equal(1, code);
		Assert.Contains("ERROR", stderr);
	}

	// ---- private static helpers, exercised via reflection ----

	[Theory]
	[InlineData("base64:")]
	[InlineData("BASE64:")]
	public void ParseKeySpec_Base64_DecodesAndIgnoresPrefixCase(string prefix)
	{
		byte[] key = ParseKeySpec(prefix + Convert.ToBase64String(NewKey));

		Assert.Equal(NewKey, key);
	}

	[Fact]
	public void ParseKeySpec_InvalidBase64_ThrowsArgumentError()
	{
		var ex = Assert.Throws<ArgumentException>(
			() => ParseKeySpec("base64:%%%"));

		Assert.Contains("invalid base64", ex.Message);
	}

	[Fact]
	public void ParseKeySpec_FileMissing_ThrowsWithResolvedPath()
	{
		string missing = Path.Combine(Path.GetTempPath(), "vapor-kr-missing-" + Guid.NewGuid().ToString("N"));

		var ex = Assert.Throws<ArgumentException>(
			() => ParseKeySpec("file:" + missing));

		Assert.Contains("key file not found", ex.Message);
		Assert.Contains(missing, ex.Message);
	}

	[Fact]
	public void ParseKeySpec_FileWithBase64Content_DecodesWhenLongEnough()
	{
		string path = WriteKeyFile(Convert.ToBase64String(NewKey));

		Assert.Equal(NewKey, ParseKeySpec("file:" + path));
	}

	[Fact]
	public void ParseKeySpec_FileWithShortBase64Content_FallsBackToRawText()
	{
		// "AAAA" decodes to 3 bytes — under the 32-byte floor — so the tool
		// must treat the file as plain text instead of a decoded key.
		string path = WriteKeyFile("AAAA");

		Assert.Equal(Encoding.UTF8.GetBytes("AAAA"), ParseKeySpec("file:" + path));
	}

	[Fact]
	public void ParseKeySpec_FileWithPlainTextContent_ReturnsUtf8Bytes()
	{
		string path = WriteKeyFile("plain-text-key-material-that-is-long-enough!");

		Assert.Equal(
			Encoding.UTF8.GetBytes("plain-text-key-material-that-is-long-enough!"),
			ParseKeySpec("file:" + path));
	}

	[Fact]
	public void ParseKeySpec_Env_ReturnsTrimmedUtf8Bytes()
	{
		Environment.SetEnvironmentVariable(EnvKeyName, "env-key-material-0123456789abcdef");

		Assert.Equal(
			Encoding.UTF8.GetBytes("env-key-material-0123456789abcdef"),
			ParseKeySpec("env:" + EnvKeyName));
	}

	[Fact]
	public void ParseKeySpec_EnvUnset_ThrowsArgumentError()
	{
		Environment.SetEnvironmentVariable(EnvKeyName, null);

		var ex = Assert.Throws<ArgumentException>(() => ParseKeySpec("env:" + EnvKeyName));

		Assert.Contains("environment variable", ex.Message);
	}

	[Fact]
	public void ParseKeySpec_PlainText_FallsBackToUtf8Bytes()
	{
		Assert.Equal(
			Encoding.UTF8.GetBytes("raw text key"),
			ParseKeySpec("raw text key"));
	}

	[Fact]
	public void ExpandPath_TildeSlash_PrefersHome()
	{
		string expanded = InvokePrivate<string>("ExpandPath", "~/vapor-store.json");

		Assert.Equal(
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "vapor-store.json"),
			expanded);
	}

	[Fact]
	public void ExpandPath_TildeBackslash_PrefersHome()
	{
		string expanded = InvokePrivate<string>("ExpandPath", "~\\vapor-store.json");

		Assert.Equal(
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "vapor-store.json"),
			expanded);
	}

	[Fact]
	public void ExpandPath_OtherPath_ReturnsFullPath()
	{
		Assert.Equal(Path.GetFullPath("relative/store.json"), InvokePrivate<string>("ExpandPath", "relative/store.json"));
	}

	[Fact]
	public void GetValue_ReturnsNextArgumentAndAdvancesIndex()
	{
		var args = new object?[] { new[] { "--store", "c.json" }, 0, "--store" };

		string value = InvokePrivate<string>("GetValue", args);

		Assert.Equal("c.json", value);
		Assert.Equal(1, args[1]);
	}

	// GetValue's missing-value arm calls Environment.Exit(2), which would kill
	// the test host; the arm stays structurally unreachable in-process and is
	// covered only by manual CLI runs.

	// ---- fixtures & helpers ----

	private (int Code, string StdOut, string StdErr) RunMain(params string[] args)
	{
		TextWriter originalOut = Console.Out;
		TextWriter originalErr = Console.Error;
		using var stdout = new StringWriter();
		using var stderr = new StringWriter();
		Console.SetOut(stdout);
		Console.SetError(stderr);
		try
		{
			return (Program.Main(args), stdout.ToString(), stderr.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalErr);
		}
	}

	private byte[] ParseKeySpec(string spec) => InvokePrivate<byte[]>("ParseKeySpec", spec, "--old-key");

	private T InvokePrivate<T>(string method, params object?[] args)
	{
		MethodInfo? info = typeof(Program).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(info);
		try
		{
			return (T)info.Invoke(null, args)!;
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			// Surface the real exception so Assert.Throws can match it.
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw; // unreachable
		}
	}

	private static byte[] CreateKey(byte fill)
	{
		var key = new byte[32];
		key.AsSpan().Fill(fill);
		return key;
	}

	private void WriteStore(params (string Account, string RefreshToken, byte[] Key)[] accounts)
	{
		var fileAccounts = new Dictionary<string, object?>();
		foreach (var (account, refreshToken, key) in accounts)
		{
			fileAccounts[account] = new Dictionary<string, object?>
			{
				["refreshToken"] = VaporCryptoHelper.EncryptWithKey(key, refreshToken),
				["accessToken"] = null,
			};
		}

		var store = new Dictionary<string, object?>
		{
			["version"] = 2,
			["accounts"] = fileAccounts,
		};
		File.WriteAllText(_storePath, JsonSerializer.Serialize(store));
	}

	private string WriteKeyFile(string content)
	{
		string path = _storePath + ".keyfile";
		File.WriteAllText(path, content);
		return path;
	}

	/// <summary>CLI arguments need the explicit key-spec prefix; the bare
	/// base64 body would be treated as plain-text key material.</summary>
	private static string KeySpec(byte[] key) => "base64:" + Convert.ToBase64String(key);
}
