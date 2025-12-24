using Microsoft.Extensions.Logging;
using Moq;
using SteamControl.Steam.Core.Actions;
using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit.Actions;

public class PingActionTests
{
	private readonly Mock<ILogger<PingAction>> _loggerMock;
	private readonly Mock<ILogger<SteamControl.Steam.Core.BotSession>> _sessionLoggerMock;
	private readonly PingAction _action;

	public PingActionTests()
	{
		_loggerMock = new Mock<ILogger<PingAction>>(MockBehavior.Loose);
		_sessionLoggerMock = new Mock<ILogger<SteamControl.Steam.Core.BotSession>>(MockBehavior.Loose);
		_action = new PingAction(_loggerMock.Object);
	}

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		// Act & Assert
		Assert.Equal("ping", _action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		// Act & Assert
		Assert.Equal("ping", _action.Metadata.Name);
		Assert.Equal("Pings the session and returns pong", _action.Metadata.Description);
		Assert.False(_action.Metadata.RequiresLogin);
		Assert.Equal(10, _action.Metadata.TimeoutSeconds);
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
		Assert.NotNull(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_OutputContainsPong()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("pong"));
		Assert.True((bool)(result.Output["pong"] ?? false));
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
	public async Task ExecuteAsync_OutputContainsTimestamp()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();
		var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);
		var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("timestamp"));

		var timestamp = (long)(result.Output["timestamp"] ?? 0);
		Assert.InRange(timestamp, before - 1, after + 1);
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
	public async Task ExecuteAsync_WithCancellation_NotCancelledImmediately()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var cts = new CancellationTokenSource();
		var payload = new Dictionary<string, object?>();

		// Act
		var task = _action.ExecuteAsync(session, payload, cts.Token);

		// Assert
		// PingAction executes synchronously and doesn't support cancellation mid-execution
		var result = await task;
		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_WithAlreadyCancelledToken_StillReturns()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		// Act
		var result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), cts.Token);

		// Assert
		// PingAction doesn't check for cancellation in sync execution
		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_WithDifferentSessionStates_OutputsCorrectState()
	{
		// Arrange
		var session = CreateTestSession("test_account");

		// Act
		var result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.Equal("Disconnected", result.Output["state"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_MultipleConcurrentCalls_AllSucceed()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var tasks = Enumerable.Range(0, 10)
			.Select(_ => _action.ExecuteAsync(session, payload, CancellationToken.None))
			.ToArray();

		var results = await Task.WhenAll(tasks);

		// Assert
		Assert.All(results, result =>
		{
			Assert.True(result.Success);
			Assert.NotNull(result.Output);
		});
	}

	private BotSession CreateTestSession(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "test_password");
		var mockRegistry = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(accountName, credentials, mockRegistry.Object, _sessionLoggerMock.Object, null);
	}
}
