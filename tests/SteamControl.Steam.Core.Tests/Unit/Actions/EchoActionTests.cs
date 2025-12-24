using Microsoft.Extensions.Logging;
using Moq;
using SteamControl.Steam.Core.Actions;
using Xunit;

namespace SteamControl.Steam.Core.Tests.Unit.Actions;

public class EchoActionTests
{
	private readonly Mock<ILogger<EchoAction>> _loggerMock;
	private readonly Mock<ILogger<SteamControl.Steam.Core.BotSession>> _sessionLoggerMock;
	private readonly EchoAction _action;

	public EchoActionTests()
	{
		_loggerMock = new Mock<ILogger<EchoAction>>(MockBehavior.Loose);
		_sessionLoggerMock = new Mock<ILogger<SteamControl.Steam.Core.BotSession>>(MockBehavior.Loose);
		_action = new EchoAction(_loggerMock.Object);
	}

	[Fact]
	public void Name_ReturnsCorrectName()
	{
		// Act & Assert
		Assert.Equal("echo", _action.Name);
	}

	[Fact]
	public void Metadata_HasCorrectValues()
	{
		// Act & Assert
		Assert.Equal("echo", _action.Metadata.Name);
		Assert.Equal("Echos back the provided payload", _action.Metadata.Description);
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
	}

	[Fact]
	public async Task ExecuteAsync_WithEmptyPayload_EchosEmptyPayload()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		Assert.True(result.Output.ContainsKey("echo"));
		var echoed = result.Output["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.Empty(echoed);
	}

	[Fact]
	public async Task ExecuteAsync_WithSingleValue_EchosCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["message"] = "Hello, World!"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var echoed = result.Output["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.True(echoed.ContainsKey("message"));
		Assert.Equal("Hello, World!", echoed["message"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_WithMultipleValues_EchosAllCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["string"] = "test",
			["number"] = 42,
			["boolean"] = true,
			["null"] = null
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var echoed = result.Output["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.Equal(4, echoed.Count);
		Assert.Equal("test", echoed["string"]?.ToString());
		Assert.Equal(42, (int)(echoed["number"] ?? 0));
		Assert.True((bool)(echoed["boolean"] ?? false));
		Assert.Null(echoed["null"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithNestedDictionary_EchosCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var nested = new Dictionary<string, object?>
		{
			["nested_key"] = "nested_value"
		};
		var payload = new Dictionary<string, object?>
		{
			["outer"] = nested
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var echoed = result.Output["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.True(echoed.ContainsKey("outer"));
	}

	[Fact]
	public async Task ExecuteAsync_WithArrayValues_EchosCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["array"] = new[] { 1, 2, 3, 4, 5 }
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var echoed = result.Output["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.True(echoed.ContainsKey("array"));
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
	public async Task ExecuteAsync_WithSpecialCharactersInPayload_EchosCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["emoji"] = "😀🎉",
			["unicode"] = "中文测试",
			["special"] = "!@#$%^&*()"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var echoed = result.Output["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.Equal(3, echoed.Count);
	}

	[Fact]
	public async Task ExecuteAsync_WithLargePayload_EchosCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>();
		for (int i = 0; i < 100; i++)
		{
			payload[$"key_{i}"] = $"value_{i}";
		}

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.NotNull(result.Output);
		var echoed = result.Output["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.Equal(100, echoed.Count);
	}

	[Fact]
	public async Task ExecuteAsync_PreservesOriginalPayload()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["key"] = "original_value"
		};

		// Act
		await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.Equal("original_value", payload["key"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_MultipleCalls_DoNotInterfere()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload1 = new Dictionary<string, object?> { ["id"] = 1 };
		var payload2 = new Dictionary<string, object?> { ["id"] = 2 };

		// Act
		var result1 = await _action.ExecuteAsync(session, payload1, CancellationToken.None);
		var result2 = await _action.ExecuteAsync(session, payload2, CancellationToken.None);

		// Assert
		var echoed1 = result1.Output!["echo"] as IReadOnlyDictionary<string, object?>;
		var echoed2 = result2.Output!["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.Equal("1", echoed1!["id"]?.ToString());
		Assert.Equal("2", echoed2!["id"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_WithNullPayloadValues_EchosCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["null_key"] = null,
			["valid_key"] = "value"
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		var echoed = result.Output!["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.True(echoed.ContainsKey("null_key"));
		Assert.Null(echoed["null_key"]);
		Assert.Equal("value", echoed["valid_key"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_WithNumericTypes_EchosCorrectly()
	{
		// Arrange
		var session = CreateTestSession("test_account");
		var payload = new Dictionary<string, object?>
		{
			["int"] = 42,
			["long"] = 9000000000L,
			["double"] = 3.14,
			["decimal"] = 123.45m
		};

		// Act
		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		// Assert
		Assert.True(result.Success);
		var echoed = result.Output!["echo"] as IReadOnlyDictionary<string, object?>;
		Assert.NotNull(echoed);
		Assert.Equal(4, echoed.Count);
	}

	private BotSession CreateTestSession(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "test_password");
		var mockRegistry = new Mock<IActionRegistry>(MockBehavior.Loose);
		return new BotSession(accountName, credentials, mockRegistry.Object, _sessionLoggerMock.Object, null);
	}
}
