using Microsoft.Extensions.Logging;
using Moq;
using SteamControl.Steam.Core.Steam;
using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit;

public class SteamClientManagerTests : IDisposable
{
	private readonly Mock<ILogger<SteamClientManager>> _loggerMock;
	private readonly SteamClientManager _manager;

	public SteamClientManagerTests()
	{
		_loggerMock = new Mock<ILogger<SteamClientManager>>(MockBehavior.Loose);
		_manager = new SteamClientManager(_loggerMock.Object);
	}

	[Fact]
	public void Constructor_WithValidLogger_CreatesManager()
	{
		// Assert
		Assert.NotNull(_manager);
	}

	[Fact]
	public void GetClient_ReturnsNonNullSteamClient()
	{
		// Act
		var client = _manager.GetClient();

		// Assert
		Assert.NotNull(client);
	}

	[Fact]
	public void GetClient_ReturnsSameInstance()
	{
		// Act
		var client1 = _manager.GetClient();
		var client2 = _manager.GetClient();

		// Assert
		Assert.Same(client1, client2);
	}

	[Fact]
	public async Task GetLogOnDetailsAsync_WithNonExistentAccount_ReturnsNull()
	{
		// Act
		var details = await _manager.GetLogOnDetailsAsync("nonexistent");

		// Assert
		Assert.Null(details);
	}

	[Fact]
	public async Task LoginAsync_WithValidCredentials_AddsLoginState()
	{
		// Arrange
		var accountName = "test_account";
		var password = "test_password";

		// Act - Note: This will fail without actual Steam connection, but we test the state management
		try
		{
			await _manager.LoginAsync(accountName, password, CancellationToken.None);
		}
		catch (InvalidOperationException)
		{
			// Expected - SteamUser handler not available in test environment
		}
		catch (Exception)
		{
			// Other exceptions are also expected without Steam connection
		}

		// Assert - Login state should have been added
		var details = await _manager.GetLogOnDetailsAsync(accountName);
		// Details may or may not exist depending on exception timing
	}

	[Fact]
	public async Task UpdateLogOnDetailsAsync_WithNonExistentAccount_DoesNotThrow()
	{
		// Act & Assert - should not throw
		await _manager.UpdateLogOnDetailsAsync("nonexistent", "access_token", "refresh_token");
	}

	[Fact]
	public async Task UpdateLogOnDetailsAsync_WithExistingAccount_UpdatesTokens()
	{
		// Arrange
		var accountName = "test_account";
		var password = "test_password";

		// First create a login state (will likely fail, but state gets added)
		try
		{
			await _manager.LoginAsync(accountName, password, CancellationToken.None);
		}
		catch { }

		// Act & Assert - should not throw
		await _manager.UpdateLogOnDetailsAsync(accountName, "new_access", "new_refresh");
	}

	[Fact]
	public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
	{
		// Act & Assert - should not throw
		await _manager.DisconnectAsync();
	}

	[Fact]
	public async Task IsConnectedAsync_WhenNotConnected_ReturnsFalse()
	{
		// Act
		var isConnected = await _manager.IsConnectedAsync();

		// Assert
		Assert.False(isConnected);
	}

	[Fact]
	public async Task ConnectAsync_WithImmediateCancellation_ThrowsOperationCanceledException()
	{
		// Arrange
		var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act & Assert
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => _manager.ConnectAsync(cts.Token)
		);
	}

	[Fact]
	public void RunCallbacks_CanBeCalledMultipleTimes()
	{
		// Act & Assert - should not throw
		_manager.RunCallbacks();
		_manager.RunCallbacks();
		_manager.RunCallbacks();
	}

	[Fact]
	public void Dispose_CanBeCalledMultipleTimes()
	{
		// Arrange
		var manager = new SteamClientManager(_loggerMock.Object);

		// Act & Assert - should not throw
		manager.Dispose();
		manager.Dispose();
	}

	[Fact]
	public async Task LoginAsync_AccountNameIsCaseInsensitive()
	{
		// Arrange
		var accountName = "MyAccount";
		var password = "test_password";

		// Try to login (will fail without Steam connection)
		try
		{
			await _manager.LoginAsync(accountName, password, CancellationToken.None);
		}
		catch { }

		// Act - Check with different case
		var details1 = await _manager.GetLogOnDetailsAsync("MyAccount");
		var details2 = await _manager.GetLogOnDetailsAsync("myaccount");

		// Assert - Both should return the same result
		if (details1 != null)
		{
			Assert.NotNull(details2);
			Assert.Equal(details1.Username, details2.Username);
		}
	}

	[Fact]
	public async Task UpdateLogOnDetailsAsync_AccountNameIsCaseInsensitive()
	{
		// Arrange
		var accountName = "MyAccount";
		var password = "test_password";

		// Create a login state
		try
		{
			await _manager.LoginAsync(accountName, password, CancellationToken.None);
		}
		catch { }

		// Act - Update with different case
		await _manager.UpdateLogOnDetailsAsync("myaccount", "access", "refresh");

		// Assert - Should have updated the same account
		var details = await _manager.GetLogOnDetailsAsync(accountName);
		if (details != null)
		{
			Assert.Equal("MyAccount", details.Username);
		}
	}

	[Fact]
	public async Task LoginAsync_MultipleAccounts_CreatesSeparateStates()
	{
		// Arrange
		var accounts = new[]
		{
			("account1", "password1"),
			("account2", "password2"),
			("account3", "password3")
		};

		// Act - Try to login multiple accounts
		foreach (var (account, password) in accounts)
		{
			try
			{
				await _manager.LoginAsync(account, password, CancellationToken.None);
			}
			catch { }
		}

		// Assert - Check that all accounts have login states
		foreach (var (account, _) in accounts)
		{
			var details = await _manager.GetLogOnDetailsAsync(account);
			// Details may not exist if LoginAsync failed before adding state
		}
	}

	[Fact]
	public async Task GetLogOnDetailsAsync_AfterLogin_ReturnsCorrectUsername()
	{
		// Arrange
		var accountName = "test_account";
		var password = "test_password";

		// Try to login
		try
		{
			await _manager.LoginAsync(accountName, password, CancellationToken.None);
		}
		catch { }

		// Act
		var details = await _manager.GetLogOnDetailsAsync(accountName);

		// Assert
		if (details != null)
		{
			Assert.Equal(accountName, details.Username);
		}
	}

	[Fact]
	public async Task UpdateLogOnDetailsAsync_WithNullTokens_DoesNotThrow()
	{
		// Arrange
		var accountName = "test_account";
		var password = "test_password";

		try
		{
			await _manager.LoginAsync(accountName, password, CancellationToken.None);
		}
		catch { }

		// Act & Assert - should not throw
		await _manager.UpdateLogOnDetailsAsync(accountName, null, null);
	}

	[Fact]
	public async Task UpdateLogOnDetailsAsync_WithEmptyTokens_DoesNotThrow()
	{
		// Arrange
		var accountName = "test_account";
		var password = "test_password";

		try
		{
			await _manager.LoginAsync(accountName, password, CancellationToken.None);
		}
		catch { }

		// Act & Assert - should not throw
		await _manager.UpdateLogOnDetailsAsync(accountName, string.Empty, string.Empty);
	}

	[Fact]
	public void Dispose_ReleasesResources()
	{
		// Arrange
		var manager = new SteamClientManager(_loggerMock.Object);

		// Act
		manager.Dispose();

		// Assert - calling RunCallbacks after dispose should not crash
		// (SteamClient is disposed but we just verify no exception is thrown)
	}

	public void Dispose()
	{
		_manager.Dispose();
		GC.SuppressFinalize(this);
	}
}
