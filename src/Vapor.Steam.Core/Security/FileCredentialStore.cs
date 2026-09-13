using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Security;

/// <summary>
/// File-based credential store with encryption-at-rest.
/// Stores credentials in ~/.vapor/credentials.json using versioned format v2:
/// tokens are encrypted with AES-GCM via <see cref="VaporCryptoHelper"/>.
/// Legacy v1 files (plain account map) are migrated transparently on first load.
/// Writes are atomic (temp file + replace) and backed up to a .bak file used for corruption recovery.
/// </summary>
public sealed class FileCredentialStore : ICredentialStore, IDisposable
{
	private const int CurrentFormatVersion = 2;
	private const string EncryptedValuePrefix = "gcm:";
	private const string BackupFileExtension = ".bak";
	private const string TempFileExtension = ".tmp";

	private static readonly UnixFileMode CredentialFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	private readonly ILogger<FileCredentialStore> _logger;
	private readonly string _credentialsPath;
	private readonly string _backupPath;
	private readonly SemaphoreSlim _lock = new(1, 1);
	private readonly JsonSerializerOptions _jsonOptions;
	private Dictionary<string, AccountCredentials> _credentials = new();
	private bool _loaded;
	private bool _disposed;

	public FileCredentialStore(ILogger<FileCredentialStore> logger, string? dataDirectory = null)
	{
		_logger = logger;

		// Default to ~/.vapor/credentials.json
		dataDirectory ??= Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			".vapor"
		);

		Directory.CreateDirectory(dataDirectory);
		_credentialsPath = Path.Combine(dataDirectory, "credentials.json");
		_backupPath = _credentialsPath + BackupFileExtension;

		_jsonOptions = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};
	}

	public async Task SaveRefreshTokenAsync(string accountName, string refreshToken, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);
		ArgumentException.ThrowIfNullOrEmpty(refreshToken);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_credentials.TryGetValue(accountName, out var creds))
			{
				creds = new AccountCredentials();
				_credentials[accountName] = creds;
			}

			creds.RefreshToken = refreshToken;
			creds.RefreshTokenUpdatedAt = DateTimeOffset.UtcNow;

			await SaveToFileAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogDebug("Saved refresh token for {AccountName}", accountName);
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task<string?> GetRefreshTokenAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return _credentials.TryGetValue(accountName, out var creds)
				? creds.RefreshToken
				: null;
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task SaveAccessTokenAsync(string accountName, StoredAccessToken accessToken, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);
		ArgumentNullException.ThrowIfNull(accessToken);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_credentials.TryGetValue(accountName, out var creds))
			{
				creds = new AccountCredentials();
				_credentials[accountName] = creds;
			}

			creds.AccessToken = accessToken.Token;
			creds.AccessTokenExpiresAt = accessToken.ExpiresAt;

			await SaveToFileAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogDebug("Saved access token for {AccountName}", accountName);
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task<StoredAccessToken?> GetAccessTokenAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_credentials.TryGetValue(accountName, out var creds)
				&& creds.AccessToken != null
				&& creds.AccessTokenExpiresAt > DateTimeOffset.UtcNow)
			{
				return new StoredAccessToken(creds.AccessToken, creds.AccessTokenExpiresAt.Value);
			}

			return null;
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task RevokeCredentialsAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_credentials.Remove(accountName))
			{
				await SaveToFileAsync(cancellationToken).ConfigureAwait(false);
				_logger.LogInformation("Revoked credentials for {AccountName}", accountName);
			}
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task<bool> HasCredentialsAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return _credentials.ContainsKey(accountName) &&
				   _credentials[accountName].RefreshToken != null;
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task SaveSharedSecretAsync(string accountName, string sharedSecret, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);
		ArgumentException.ThrowIfNullOrEmpty(sharedSecret);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_credentials.TryGetValue(accountName, out var creds))
			{
				creds = new AccountCredentials();
				_credentials[accountName] = creds;
			}

			creds.SharedSecret = sharedSecret.Trim();

			await SaveToFileAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogDebug("Saved shared secret for {AccountName}", accountName);
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task<string?> GetSharedSecretAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return _credentials.TryGetValue(accountName, out var creds)
				? creds.SharedSecret
				: null;
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task SaveIdentitySecretAsync(string accountName, string identitySecret, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);
		ArgumentException.ThrowIfNullOrEmpty(identitySecret);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!_credentials.TryGetValue(accountName, out var creds))
			{
				creds = new AccountCredentials();
				_credentials[accountName] = creds;
			}

			creds.IdentitySecret = identitySecret.Trim();

			await SaveToFileAsync(cancellationToken).ConfigureAwait(false);
			_logger.LogDebug("Saved identity secret for {AccountName}", accountName);
		}
		finally
		{
			_lock.Release();
		}
	}

	public async Task<string?> GetIdentitySecretAsync(string accountName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(accountName);

		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return _credentials.TryGetValue(accountName, out var creds)
				? creds.IdentitySecret
				: null;
		}
		finally
		{
			_lock.Release();
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_lock.Dispose();
		_disposed = true;
	}

	private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
	{
		if (_loaded)
		{
			return;
		}

		await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_loaded)
			{
				return;
			}

			bool migratedFromV1 = false;

			if (File.Exists(_credentialsPath))
			{
				try
				{
					(_credentials, migratedFromV1) = await ReadStoreFileAsync(_credentialsPath, cancellationToken).ConfigureAwait(false);
					_logger.LogDebug("Loaded credentials for {Count} accounts", _credentials.Count);
				}
				catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
				{
					_logger.LogError(ex, "Credentials file is corrupted, attempting backup recovery");

					if (File.Exists(_backupPath))
					{
						try
						{
							(_credentials, migratedFromV1) = await ReadStoreFileAsync(_backupPath, cancellationToken).ConfigureAwait(false);
							_logger.LogWarning("Recovered credentials for {Count} accounts from backup", _credentials.Count);

							// Restore the backup as the live file so subsequent writes are consistent.
							File.Copy(_backupPath, _credentialsPath, overwrite: true);
						}
						catch (Exception backupEx) when (backupEx is JsonException or IOException or UnauthorizedAccessException)
						{
							_logger.LogError(backupEx, "Backup recovery failed, starting fresh");
							_credentials = new Dictionary<string, AccountCredentials>();
						}
					}
					else
					{
						_logger.LogError("No backup available, starting fresh");
						_credentials = new Dictionary<string, AccountCredentials>();
					}
				}
			}

			CheckAndTightenFilePermissions();

			if (migratedFromV1 && _credentials.Count > 0)
			{
				// Persist the migrated (encrypted) v2 format right away.
				await SaveToFileAsync(cancellationToken).ConfigureAwait(false);
				_logger.LogInformation("Migrated credential store from v1 to v2 (encrypted) for {Count} accounts", _credentials.Count);
			}

			_loaded = true;
		}
		finally
		{
			_lock.Release();
		}
	}

	/// <summary>
	/// Reads a store file, supporting both v2 (versioned + encrypted) and v1 (legacy plain map) formats.
	/// Returns the decrypted credentials and whether a v1 → v2 migration is required.
	/// </summary>
	private async Task<(Dictionary<string, AccountCredentials> Credentials, bool MigratedFromV1)> ReadStoreFileAsync(string path, CancellationToken cancellationToken)
	{
		string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
		using var document = JsonDocument.Parse(json);

		Dictionary<string, AccountCredentials>? credentials;
		bool isV1 = false;

		if (document.RootElement.ValueKind == JsonValueKind.Object &&
			document.RootElement.TryGetProperty("version", out var versionElement) &&
			versionElement.TryGetInt32(out int version))
		{
			if (version > CurrentFormatVersion)
			{
				throw new InvalidOperationException($"Credential store format version {version} is newer than supported {CurrentFormatVersion}");
			}

			if (!document.RootElement.TryGetProperty("accounts", out var accountsElement) ||
				accountsElement.ValueKind != JsonValueKind.Object)
			{
				throw new InvalidOperationException("Credential store file is missing the accounts object");
			}

			credentials = accountsElement.Deserialize<Dictionary<string, AccountCredentials>>(_jsonOptions);
		}
		else
		{
			// Legacy v1: root object is the account map itself, tokens stored in plain text.
			credentials = document.RootElement.Deserialize<Dictionary<string, AccountCredentials>>(_jsonOptions);
			isV1 = true;
		}

		var result = new Dictionary<string, AccountCredentials>();
		foreach (var (accountName, creds) in credentials ?? new Dictionary<string, AccountCredentials>())
		{
			result[accountName] = new AccountCredentials
			{
				RefreshToken = await DecryptValueAsync(creds.RefreshToken, accountName, nameof(creds.RefreshToken), cancellationToken).ConfigureAwait(false),
				RefreshTokenUpdatedAt = creds.RefreshTokenUpdatedAt,
				AccessToken = await DecryptValueAsync(creds.AccessToken, accountName, nameof(creds.AccessToken), cancellationToken).ConfigureAwait(false),
				AccessTokenExpiresAt = creds.AccessTokenExpiresAt,
				SharedSecret = await DecryptValueAsync(creds.SharedSecret, accountName, nameof(creds.SharedSecret), cancellationToken).ConfigureAwait(false),
				IdentitySecret = await DecryptValueAsync(creds.IdentitySecret, accountName, nameof(creds.IdentitySecret), cancellationToken).ConfigureAwait(false)
			};
		}

		return (result, isV1);
	}

	private static async Task<string?> DecryptValueAsync(string? value, string accountName, string fieldName, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (string.IsNullOrEmpty(value))
		{
			return null;
		}

		if (!value.StartsWith(EncryptedValuePrefix, StringComparison.Ordinal))
		{
			// Legacy plain-text value (v1 file).
			return value;
		}

		string? decrypted = await VaporCryptoHelper.Decrypt(ECryptoMethod.AES, value).ConfigureAwait(false);
		if (decrypted == null)
		{
			throw new InvalidOperationException(
				$"Failed to decrypt {fieldName} for account '{accountName}'. The configured encryption key does not match the one used to write the store.");
		}

		return decrypted;
	}

	private async Task SaveToFileAsync(CancellationToken cancellationToken)
	{
		var accounts = new Dictionary<string, AccountCredentials>(StringComparer.Ordinal);
		foreach (var (accountName, creds) in _credentials)
		{
			string? encryptedRefreshToken = EncryptValue(creds.RefreshToken, accountName, nameof(creds.RefreshToken));
			string? encryptedAccessToken = EncryptValue(creds.AccessToken, accountName, nameof(creds.AccessToken));
			string? encryptedSharedSecret = EncryptValue(creds.SharedSecret, accountName, nameof(creds.SharedSecret));
			string? encryptedIdentitySecret = EncryptValue(creds.IdentitySecret, accountName, nameof(creds.IdentitySecret));

			accounts[accountName] = new AccountCredentials
			{
				RefreshToken = encryptedRefreshToken,
				RefreshTokenUpdatedAt = creds.RefreshTokenUpdatedAt,
				AccessToken = encryptedAccessToken,
				AccessTokenExpiresAt = creds.AccessTokenExpiresAt,
				SharedSecret = encryptedSharedSecret,
				IdentitySecret = encryptedIdentitySecret
			};
		}

		var store = new CredentialStoreFile
		{
			Version = CurrentFormatVersion,
			Accounts = accounts
		};

		string json = JsonSerializer.Serialize(store, _jsonOptions);

		try
		{
			// Backup current file before replacing it.
			if (File.Exists(_credentialsPath))
			{
				File.Copy(_credentialsPath, _backupPath, overwrite: true);
			}

			// Atomic write: write to temp file, then replace.
			string tempPath = _credentialsPath + TempFileExtension;
			await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
			ApplyFilePermissions(tempPath);
			File.Move(tempPath, _credentialsPath, overwrite: true);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to save credentials file");
		}
	}

	private static string? EncryptValue(string? value, string accountName, string fieldName)
	{
		if (string.IsNullOrEmpty(value))
		{
			return null;
		}

		string? encrypted = VaporCryptoHelper.Encrypt(ECryptoMethod.AES, value);
		if (encrypted == null)
		{
			// Fail secure: refuse to write plain-text secrets to disk.
			throw new InvalidOperationException($"Failed to encrypt {fieldName} for account '{accountName}'");
		}

		return encrypted;
	}

	private void CheckAndTightenFilePermissions()
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		TightenPermissions(_credentialsPath);
		TightenPermissions(_backupPath);
	}

	private void ApplyFilePermissions(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			File.SetUnixFileMode(path, CredentialFileMode);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to restrict permissions on {Path}", path);
		}
	}

	private void TightenPermissions(string path)
	{
		if (OperatingSystem.IsWindows() || !File.Exists(path))
		{
			return;
		}

		try
		{
			UnixFileMode current = File.GetUnixFileMode(path);
			if ((current & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
			{
				_logger.LogWarning(
					"Credentials file {Path} has overly permissive permissions ({Mode}), tightening to owner-only access",
					path,
					current);
				File.SetUnixFileMode(path, CredentialFileMode);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to check permissions on {Path}", path);
		}
	}

	/// <summary>
	/// On-disk representation of the versioned credential store (v2).
	/// </summary>
	private sealed class CredentialStoreFile
	{
		public int Version { get; set; } = CurrentFormatVersion;
		public Dictionary<string, AccountCredentials> Accounts { get; set; } = new();
	}

	/// <summary>
	/// Internal representation of stored account credentials.
	/// </summary>
	internal sealed class AccountCredentials
	{
		public string? RefreshToken { get; set; }
		public DateTimeOffset? RefreshTokenUpdatedAt { get; set; }
		public string? AccessToken { get; set; }
		public DateTimeOffset? AccessTokenExpiresAt { get; set; }
		public string? SharedSecret { get; set; }
		public string? IdentitySecret { get; set; }
	}
}
