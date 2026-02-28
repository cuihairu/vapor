namespace Vapor.Steam.Core.Security;

/// <summary>
/// Stored access token with expiration.
/// </summary>
public sealed record StoredAccessToken(
	string Token,
	DateTimeOffset ExpiresAt
);

/// <summary>
/// Interface for storing and retrieving Steam credentials.
/// </summary>
public interface ICredentialStore
{
	/// <summary>
	/// Saves the refresh token for an account.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="refreshToken">The refresh token.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task SaveRefreshTokenAsync(string accountName, string refreshToken, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the refresh token for an account.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The refresh token, or null if not found.</returns>
	Task<string?> GetRefreshTokenAsync(string accountName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Saves the access token for an account.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="accessToken">The access token with expiration.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task SaveAccessTokenAsync(string accountName, StoredAccessToken accessToken, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the access token for an account.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The access token, or null if not found/expired.</returns>
	Task<StoredAccessToken?> GetAccessTokenAsync(string accountName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Revokes (removes) all credentials for an account.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task RevokeCredentialsAsync(string accountName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks if an account has stored credentials.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<bool> HasCredentialsAsync(string accountName, CancellationToken cancellationToken = default);
}
