using Microsoft.Extensions.Logging;
using Moq;
using SteamControl.Steam.Core.Actions;
using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit.Actions;

public class IdleActionTests
{
	private readonly Mock<ILogger<IdleAction>> _loggerMock;
	private readonly Mock<ILogger<SteamControl.Steam.Core.BotSession>> _sessionLoggerMock;
	private readonly IdleAction _action;

	public IdleActionTests()
	{
		_loggerMock = new Mock<ILogger<IdleAction>>(MockBehavior.Loose);
		_sessionLoggerMock = new Mock<ILogger<SteamControl.Steam.Core.BotSession>>(MockBehavior.Loose);
		_action = new IdleAction(_loggerMock.Object);
	}

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		// Act & Assert
		Assert.Equal("idle", _action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		// Act & Assert
		Assert.Equal("idle", _action.Metadata.Name);
		Assert.Equal("Idles the session (simulates being online)", _action.Metadata.Description);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(300, _action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_ReturnsSuccessResult()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		Assert.Null(result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WithDefaultDuration_Uses60Seconds()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("duration"));
		Assert.Equal(60, (int)(result.Output["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_WithCustomDuration_UsesCustomDuration()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = 120
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.Equal(120, (int)(result.Output["duration"] ?? 0));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(30)]
	[InlineData(60)]
	[InlineData(120)]
	[InlineData(300)]
	[InlineData(600)]
	[InlineData(3600)]
	public async Task ExecuteAsync_WithVariousDurations_OutputsCorrectDuration(int duration)
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = duration
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal(duration, (int)(result.Output!["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_WithNegativeDuration_OutputsNegativeValue()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = -30
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal(-30, (int)(result.Output!["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_WithVeryLargeDuration_OutputsCorrectValue()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = 86400 // 24 hours
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal(86400, (int)(result.Output!["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_OutputContainsActionName()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("action"));
		Assert.Equal("idle", result.Output["action"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_OutputContainsSessionState()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("state"));
	}

	[Fact]
	public async Task ExecuteAsync_WithStringDuration_DoesNotParseToInt()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = "60"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		// String "60" is not an int, so default 60 is used
		Assert.NotNull(result.Output);
		Assert.Equal(60, (int)(result.Output["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_WithNullDuration_UsesDefault()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = null
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal(60, (int)(result.Output!["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_WithZeroDuration_OutputsZero()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = 0
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal(0, (int)(result.Output!["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_DoesNotModifyPayload()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = 120,
			["extra"] = "preserve_me"
		};

		// Act
		await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal(120, payload["duration"]);
		Assert.Equal("preserve_me", payload["extra"]);
	}

	[Fact]
	public async Task ExecuteAsync_MultipleConcurrentCalls_DoNotInterfere()
	{
		// Arrange
		var session = CreateTestSession("test_account");

		// Act
		var task1 = _action.ExecuteAsync(session, new Dictionary<string, object?> { ["duration"] = 30 }, CancellationToken.None);
		var task2 = _action.ExecuteAsync(session, new Dictionary<string, object?> { ["duration"] = 60 }, CancellationToken.None);
		var task3 = _action.ExecuteAsync(session, new Dictionary<string, object?> { ["duration"] = 90 }, CancellationToken.None);

		var (result1, result2, result3) = await (task1, task2, task3);

		// Assert
		Assert.Equal(30, (int)(result1.Output!["duration"] ?? 0));
		Assert.Equal(60, (int)(result2.Output!["duration"] ?? 0));
		Assert.Equal(90, (int)(result3.Output!["duration"] ?? 0));
	}

	[Fact]
	public async Task ExecuteAsync_WithDoubleDuration_IgnoresDecimal()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["duration"] = 90.5
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		// 90.5 is not an int, so default 60 is used
		Assert.Equal(60, (int)(result.Output!["duration"] ?? 0));
	}

	private BotSession CreateTestSession(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "test_password");
		var mockRegistry = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(accountName, credentials, mockRegistry.Object, _sessionLoggerMock.Object, null);
	}
}
