using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Security;

/// <summary>
/// File-based credential store.
/// Stores credentials in ~/.vapor/credentials.json
/// </summary>
public sealed class FileCredentialStore : ICredentialStore, IDisposable
{
	private readonly ILogger<FileCredentialStore> _logger;
	private readonly string _credentialsPath;
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

			if (File.Exists(_credentialsPath))
			{
				try
				{
					string json = await File.ReadAllTextAsync(_credentialsPath, cancellationToken).ConfigureAwait(false);
					_credentials = JsonSerializer.Deserialize<Dictionary<string, AccountCredentials>>(json, _jsonOptions)
						?? new Dictionary<string, AccountCredentials>();
					_logger.LogDebug("Loaded credentials for {Count} accounts", _credentials.Count);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to load credentials file, starting fresh");
					_credentials = new Dictionary<string, AccountCredentials>();
				}
			}

			_loaded = true;
		}
		finally
		{
			_lock.Release();
		}
	}

	private async Task SaveToFileAsync(CancellationToken cancellationToken)
	{
		try
		{
			string json = JsonSerializer.Serialize(_credentials, _jsonOptions);
			await File.WriteAllTextAsync(_credentialsPath, json, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to save credentials file");
		}
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
	}
}
