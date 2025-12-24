using Microsoft.Extensions.Logging;
using Moq;
using SteamControl.Steam.Core.Actions;
using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit.Actions;

public class RedeemKeyActionTests
{
	private readonly Mock<ILogger<RedeemKeyAction>> _loggerMock;
	private readonly Mock<ILogger<SteamControl.Steam.Core.BotSession>> _sessionLoggerMock;
	private readonly RedeemKeyAction _action;

	public RedeemKeyActionTests()
	{
		_loggerMock = new Mock<ILogger<RedeemKeyAction>>(MockBehavior.Loose);
		_sessionLoggerMock = new Mock<ILogger<SteamControl.Steam.Core.BotSession>>(MockBehavior.Loose);
		_action = new RedeemKeyAction(_loggerMock.Object);
	}

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		// Act & Assert
		Assert.Equal("redeem_key", _action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		// Act & Assert
		Assert.Equal("redeem_key", _action.Metadata.Name);
		Assert.Equal("Redeem a Steam product key", _action.Metadata.Description);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(60, _action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_WithValidKey_ReturnsSuccess()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "AAAAA-BBBBB-CCCCC"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		Assert.Null(result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WithNullKey_ReturnsFailure()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = null
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Equal("key is required", result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_WithMissingKey_ReturnsFailure()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.False(result.Success);
		Assert.Equal("key is required", result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_WithEmptyKey_ReturnsSuccess()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = ""
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		Assert.NotNull(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_OutputContainsMaskedKey()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "AAAAA-BBBBB-CCCCC"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("key"));
		var maskedKey = result.Output["key"]?.ToString() ?? "";
		Assert.DoesNotContain("BBBBB", maskedKey);
		Assert.StartsWith("AAAAA", maskedKey);
		Assert.EndsWith("CCCCC", maskedKey);
	}

	[Fact]
	public async Task ExecuteAsync_WithShortKey_MasksEntireKey()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "ABC123"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var maskedKey = result.Output["key"]?.ToString() ?? "";
		Assert.Equal("******", maskedKey);
	}

	[Fact]
	public async Task ExecuteAsync_WithEightCharKey_MasksEntireKey()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "ABCD1234"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var maskedKey = result.Output["key"]?.ToString() ?? "";
		Assert.Equal("********", maskedKey);
	}

	[Fact]
	public async Task ExecuteAsync_WithNineCharKey_PreservesFirstAndLast()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "ABCD1234E"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var maskedKey = result.Output["key"]?.ToString() ?? "";
		Assert.StartsWith("A", maskedKey);
		Assert.EndsWith("E", maskedKey);
		Assert.Equal('*', maskedKey[1]);
		Assert.Equal('*', maskedKey[^2]);
	}

	[Theory]
	[InlineData("A-B-C-D-E-F")]
	[InlineData("12345-67890-ABCDE")]
	[InlineData("XXXXX-YYYYY-ZZZZZ-OOOOO")]
	public async Task ExecuteAsync_WithStandardFormat_MasksMiddle(string key)
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = key
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var maskedKey = result.Output["key"]?.ToString() ?? "";
		Assert.NotEqual(key, maskedKey);
	}

	[Fact]
	public async Task ExecuteAsync_OutputContainsActionName()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "AAAAA-BBBBB-CCCCC"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("action"));
		Assert.Equal("redeem_key", result.Output["action"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_OutputContainsSessionState()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "AAAAA-BBBBB-CCCCC"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("state"));
	}

	[Fact]
	public async Task ExecuteAsync_DoesNotModifyOriginalKey()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "AAAAA-BBBBB-CCCCC"
		};
		var originalKey = payload["key"]?.ToString();

		// Act
		await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal(originalKey, payload["key"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_MultipleCalls_MaskEachKeyCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");

		// Act
		var result1 = await _action.ExecuteAsync(session,
			new Dictionary<string, object?> { ["key"] = "AAAAA-BBBBB-CCCCC" }, CancellationToken.None);
		var result2 = await _action.ExecuteAsync(session,
			new Dictionary<string, object?> { ["key"] = "DDDDD-EEEEE-FFFFF" }, CancellationToken.None);

		// Assert
		var masked1 = result1.Output!["key"]?.ToString() ?? "";
		var masked2 = result2.Output!["key"]?.ToString() ?? "";
		Assert.StartsWith("AAAAA", masked1);
		Assert.StartsWith("DDDDD", masked2);
		Assert.EndsWith("CCCCC", masked1);
		Assert.EndsWith("FFFFF", masked2);
	}

	[Fact]
	public async Task ExecuteAsync_WithExtraPayloadFields_IgnoresThem()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "AAAAA-BBBBB-CCCCC",
			["extra"] = "ignored",
			["data"] = 12345
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.False(result.Output.ContainsKey("extra"));
		Assert.False(result.Output.ContainsKey("data"));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("\t")]
	[InlineData("\n")]
	public async Task ExecuteAsync_WithWhitespaceKey_ReturnsSuccess(string key)
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = key
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_WithVeryLongKey_MasksCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var longKey = "AAAAAAAAAA" + new string('B', 100) + "CCCCCCCCCC";
		var payload = new Dictionary<string, object?>
		{
			["key"] = longKey
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		var maskedKey = result.Output!["key"]?.ToString() ?? "";
		Assert.StartsWith("AAAAAAAAAA", maskedKey);
		Assert.EndsWith("CCCCCCCCCC", maskedKey);
		Assert.DoesNotContain("BBBBBBB", maskedKey);
	}

	private BotSession CreateTestSession(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "test_password");
		var mockRegistry = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(accountName, credentials, mockRegistry.Object, _sessionLoggerMock.Object, null);
	}
}
