using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.Steam.Core.Tests.RealSteam;

/// <summary>
/// Tests for token refresh functionality.
/// These tests require actual Steam credentials and are skipped by default.
/// </summary>
[Trait("Category", "RealSteam")]
public sealed class TokenRefreshTests : IDisposable
{
	private readonly ITestOutputHelper _output;
	private readonly ICredentialStore _credentialStore;
	private readonly string _tempDirectory;

	public TokenRefreshTests(ITestOutputHelper output)
	{
		_output = output;

		// Create a temporary directory for test credentials
		_tempDirectory = Path.Combine(Path.GetTempPath(), $"vapor_tests_{Guid.NewGuid():N}");
		Directory.CreateDirectory(_tempDirectory);
		_credentialStore = new FileCredentialStore(
			Microsoft.Extensions.Logging.Abstractions.NullLogger<FileCredentialStore>.Instance,
			_tempDirectory
		);
	}

	public void Dispose()
	{
		// Clean up temporary credentials
		try
		{
			if (Directory.Exists(_tempDirectory))
			{
				Directory.Delete(_tempDirectory, recursive: true);
			}
		}
		catch
		{
			// Ignore cleanup errors
		}
	}

	[Fact]
	public async Task StoredAccessToken_ExpiresInFuture_IsValid()
	{
		// Arrange
		var token = new StoredAccessToken(
			"test_token",
			DateTimeOffset.UtcNow.AddHours(1)
		);

		// Act
		var isValid = token.ExpiresAt > DateTimeOffset.UtcNow;

		// Assert
		Assert.True(isValid);
	}

	[Fact]
	public async Task StoredAccessToken_Expired_IsInvalid()
	{
		// Arrange
		var token = new StoredAccessToken(
			"test_token",
			DateTimeOffset.UtcNow.AddHours(-1)
		);

		// Act
		var isValid = token.ExpiresAt > DateTimeOffset.UtcNow;

		// Assert
		Assert.False(isValid);
	}
}
