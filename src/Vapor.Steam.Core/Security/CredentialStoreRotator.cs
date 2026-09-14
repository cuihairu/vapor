using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Security;

/// <summary>
/// Result of a credential store key rotation.
/// </summary>
public sealed record CredentialRotationResult(
	int TotalAccounts,
	int RotatedAccounts,
	IReadOnlyList<string> FailedAccounts,
	bool DryRun
)
{
	public bool Success => FailedAccounts.Count == 0;
}

/// <summary>
/// Re-encrypts a <see cref="FileCredentialStore"/> data file from an old encryption key to a new one.
/// Supports v2 (encrypted) and v1 (plain) input formats; always writes v2 with the new key.
/// </summary>
public static class CredentialStoreRotator
{
	private const int CurrentFormatVersion = 2;
	private const string EncryptedValuePrefix = "gcm:";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	/// <param name="storePath">Path to credentials.json.</param>
	/// <param name="oldKey">Raw key material used to encrypt the current file.</param>
	/// <param name="newKey">Raw key material to re-encrypt with.</param>
	/// <param name="logger">Optional logger.</param>
	/// <param name="dryRun">When true, only validates decryption and does not write.</param>
	public static CredentialRotationResult Rotate(
		string storePath,
		byte[] oldKey,
		byte[] newKey,
		ILogger? logger = null,
		bool dryRun = false)
	{
		ArgumentException.ThrowIfNullOrEmpty(storePath);
		ArgumentNullException.ThrowIfNull(oldKey);
		ArgumentNullException.ThrowIfNull(newKey);

		if (!File.Exists(storePath))
		{
			throw new FileNotFoundException("Credential store not found", storePath);
		}

		string json = File.ReadAllText(storePath);
		using var document = JsonDocument.Parse(json);

		Dictionary<string, StoredAccount>? accounts;
		if (document.RootElement.ValueKind == JsonValueKind.Object &&
			document.RootElement.TryGetProperty("accounts", out var accountsElement) &&
			accountsElement.ValueKind == JsonValueKind.Object)
		{
			accounts = accountsElement.Deserialize<Dictionary<string, StoredAccount>>(JsonOptions);
		}
		else
		{
			// v1 legacy format: root is the account map.
			accounts = document.RootElement.Deserialize<Dictionary<string, StoredAccount>>(JsonOptions);
		}

		var rotated = new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
		List<string> failed = [];

		foreach (var (accountName, creds) in accounts ?? new Dictionary<string, StoredAccount>())
		{
			try
			{
				rotated[accountName] = new StoredAccount
				{
					RefreshToken = ReEncrypt(creds.RefreshToken, oldKey, newKey),
					RefreshTokenUpdatedAt = creds.RefreshTokenUpdatedAt,
					AccessToken = ReEncrypt(creds.AccessToken, oldKey, newKey),
					AccessTokenExpiresAt = creds.AccessTokenExpiresAt
				};
			}
			catch (CredentialRotationException)
			{
				failed.Add(accountName);
				logger?.LogError("Failed to rotate credentials for account {AccountName}", accountName);
			}
		}

		if (dryRun)
		{
			return new CredentialRotationResult(rotated.Count + failed.Count, rotated.Count, failed, DryRun: true);
		}

		if (failed.Count > 0)
		{
			// Partial rotation would silently drop accounts that failed to decrypt.
			// Keep the original file intact and let the operator resolve the failures first.
			logger?.LogError(
				"Rotation aborted: {Failed} of {Total} accounts failed to decrypt with the old key; store left unchanged",
				failed.Count,
				rotated.Count + failed.Count);
			return new CredentialRotationResult(rotated.Count + failed.Count, rotated.Count, failed, DryRun: false);
		}

		var store = new RotatedStoreFile
		{
			Version = CurrentFormatVersion,
			Accounts = rotated
		};

		// Backup, then atomically replace.
		string backupPath = storePath + ".bak.pre-rotate";
		File.Copy(storePath, backupPath, overwrite: true);

		string tempPath = storePath + ".tmp";
		File.WriteAllText(tempPath, JsonSerializer.Serialize(store, JsonOptions));
		File.Move(tempPath, storePath, overwrite: true);

		logger?.LogInformation(
			"Rotated credential store: {Rotated} accounts re-encrypted, {Failed} failed, backup at {BackupPath}",
			rotated.Count,
			failed.Count,
			backupPath);

		return new CredentialRotationResult(rotated.Count + failed.Count, rotated.Count, failed, DryRun: false);
	}

	private static string? ReEncrypt(string? value, byte[] oldKey, byte[] newKey)
	{
		if (string.IsNullOrEmpty(value))
		{
			return null;
		}

		string? plain;
		if (value.StartsWith(EncryptedValuePrefix, StringComparison.Ordinal))
		{
			plain = VaporCryptoHelper.DecryptWithKey(oldKey, value).ConfigureAwait(false).GetAwaiter().GetResult();
			if (plain == null)
			{
				throw new CredentialRotationException("decryption with old key failed");
			}
		}
		else
		{
			// v1 plain-text value.
			plain = value;
		}

		// Encryption with a normalized key cannot fail, so this never returns null.
		return VaporCryptoHelper.EncryptWithKey(newKey, plain);
	}

	private sealed class RotatedStoreFile
	{
		public int Version { get; set; } = CurrentFormatVersion;
		public Dictionary<string, StoredAccount> Accounts { get; set; } = new();
	}

	private sealed class StoredAccount
	{
		public string? RefreshToken { get; set; }
		public DateTimeOffset? RefreshTokenUpdatedAt { get; set; }
		public string? AccessToken { get; set; }
		public DateTimeOffset? AccessTokenExpiresAt { get; set; }
	}

	private sealed class CredentialRotationException(string message) : Exception(message);
}
