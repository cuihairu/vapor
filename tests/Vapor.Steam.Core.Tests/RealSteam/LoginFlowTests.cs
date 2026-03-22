using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;
using Xunit;
using Xunit.Abstractions;

namespace Vapor.Steam.Core.Tests.RealSteam;

/// <summary>
/// Tests for the Steam login flow with real Steam connections.
/// These tests require actual Steam credentials and are skipped by default.
/// </summary>
[Trait("Category", "RealSteam")]
public sealed class LoginFlowTests
{
	private readonly ITestOutputHelper _output;

	public LoginFlowTests(ITestOutputHelper output)
	{
		_output = output;
	}

	[Fact]
	public async Task LoginAsync_ValidCredentials_ReturnsSuccess()
	{
		// Skip if environment variables not set
		var accountName = Environment.GetEnvironmentVariable("STEAM_TEST_ACCOUNT");
		var password = Environment.GetEnvironmentVariable("STEAM_TEST_PASSWORD");

		if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(password))
		{
			_output.WriteLine("Skipping test: STEAM_TEST_ACCOUNT or STEAM_TEST_PASSWORD not set");
			return; // Skip test silently
		}

		var manager = new SteamClientManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<SteamClientManager>.Instance);

		try
		{
			// Act
			await manager.ConnectAsync();
			await manager.LoginAsync(accountName, password);

			// Assert - if we get here without exception, login succeeded
			_output.WriteLine("Login succeeded");
		}
		finally
		{
			await manager.DisconnectAsync();
			manager.Dispose();
		}
	}
}
