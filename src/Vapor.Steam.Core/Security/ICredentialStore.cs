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

	/// <summary>
	/// Saves the Steam mobile authenticator shared secret (base64) for an account, used
	/// for local TOTP generation. Stored encrypted alongside the other credentials.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="sharedSecret">The base64-encoded shared secret.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task SaveSharedSecretAsync(string accountName, string sharedSecret, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the Steam mobile authenticator shared secret (base64) for an account, or
	/// null when none is stored.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<string?> GetSharedSecretAsync(string accountName, CancellationToken cancellationToken = default);

	/// <summary>
	/// Saves the Steam mobile authenticator identity secret (base64) for an account, used
	/// to sign mobile confirmation requests (trade/market confirmations). Stored encrypted
	/// alongside the other credentials; like the shared secret it must never leave the agent.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="identitySecret">The base64-encoded identity secret.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task SaveIdentitySecretAsync(string accountName, string identitySecret, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets the Steam mobile authenticator identity secret (base64) for an account, or
	/// null when none is stored.
	/// </summary>
	/// <param name="accountName">The account name.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task<string?> GetIdentitySecretAsync(string accountName, CancellationToken cancellationToken = default);
}
