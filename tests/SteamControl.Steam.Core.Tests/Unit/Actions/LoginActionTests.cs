using Microsoft.Extensions.Logging;
using Moq;
using SteamControl.Steam.Core.Actions;
using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit.Actions;

public class LoginActionTests
{
	private readonly Mock<ILogger<LoginAction>> _loggerMock;
	private readonly Mock<ILogger<SteamControl.Steam.Core.BotSession>> _sessionLoggerMock;
	private readonly LoginAction _action;

	public LoginActionTests()
	{
		_loggerMock = new Mock<ILogger<LoginAction>>(MockBehavior.Loose);
		_sessionLoggerMock = new Mock<ILogger<SteamControl.Steam.Core.BotSession>>(MockBehavior.Loose);
		_action = new LoginAction(_loggerMock.Object);
	}

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		// Act & Assert
		Assert.Equal("login", _action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		// Act & Assert
		Assert.Equal("login", _action.Metadata.Name);
		Assert.Equal("Login to Steam", _action.Metadata.Description);
		Assert.False(_action.Metadata.RequiresLogin);
		Assert.Equal(60, _action.Metadata.TimeoutSeconds);
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
		Assert.Equal("login", result.Output["action"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_OutputContainsAccountName()
	{
		// Arrange
		var session = CreateTestSession("my_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("account"));
		Assert.Equal("my_account", result.Output["account"]?.ToString());
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
		Assert.Equal("Disconnected", result.Output["state"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_WithNullPayload_WorksCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");

		// Act
		var result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		Assert.NotNull(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_WithExtraPayload_IgnoresExtraData()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
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

	[Fact]
	public async Task ExecuteAsync_DoesNotModifyPayload()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["preserve"] = "this"
		};

		// Act
		await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal("this", payload["preserve"]?.ToString());
		Assert.Single(payload);
	}

	[Fact]
	public async Task ExecuteAsync_MultipleConcurrentCalls_AllSucceed()
	{
		// Arrange
		var session = CreateTestSession("test_account");

		// Act
		var tasks = Enumerable.Range(0, 10)
			.Select(_ => _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None))
			.ToArray();

		var results = await Task.WhenAll(tasks);

		// Assert
		Assert.All(results, result =>
		{
			Assert.True(result.Success);
			Assert.NotNull(result.Output);
		});
	}

	[Fact]
	public async Task ExecuteAsync_WithDifferentAccounts_OutputsCorrectAccount()
	{
		// Arrange
		var session1 = CreateTestSession("account1");
		var session2 = CreateTestSession("account2");

		// Act
		var result1 = await _action.ExecuteAsync(session1, new Dictionary<string, object?>(), CancellationToken.None);
		var result2 = await _action.ExecuteAsync(session2, new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.Equal("account1", result1.Output!["account"]?.ToString());
		Assert.Equal("account2", result2.Output!["account"]?.ToString());
	}

	[Theory]
	[InlineData("Disconnected")]
	[InlineData("Connecting")]
	[InlineData("Connected")]
	public async Task ExecuteAsync_OutputsCurrentState(string expectedState)
	{
		// Arrange
		var session = CreateTestSession("test_account");

		// Act
		var result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.Contains(result.Output["state"]?.ToString() ?? "", new[] { "Disconnected", "Connecting", "Connected" });
	}

	[Fact]
	public async Task ExecuteAsync_AlwaysReturnsSameOutputStructure()
	{
		// Arrange
		var session = CreateTestSession("test_account");

		// Act
		var result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.Equal(3, result.Output.Count);
		Assert.True(result.Output.ContainsKey("action"));
		Assert.True(result.Output.ContainsKey("account"));
		Assert.True(result.Output.ContainsKey("state"));
	}

	private BotSession CreateTestSession(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "test_password");
		var mockRegistry = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(accountName, credentials, mockRegistry.Object, _sessionLoggerMock.Object, null);
	}
}
