using Microsoft.Extensions.Logging;
using Moq;
using Vapor.Steam.Core.Actions;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

public sealed class PlayGamesActionTests : IDisposable
{
	private readonly Mock<ILogger<PlayGamesAction>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<ILogger<BotSession>> _sessionLoggerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];
	private readonly PlayGamesAction _action;

	public PlayGamesActionTests()
	{
		_action = new PlayGamesAction(_loggerMock.Object);
	}

	[Fact]
	public void Name_AndMetadata_AreCorrect()
	{
		Assert.Equal("play_games", _action.Name);
		Assert.Equal("play_games", _action.Metadata.Name);
		Assert.True(_action.Metadata.RequiresLogin);
		Assert.Equal(30, _action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_WhenActionAndGamesMissing_ReturnsFailure()
	{
		var session = CreateSession("acct-1");

		var result = await _action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("required", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ExecuteAsync_Play_WithValidGames_ReturnsParsedGames()
	{
		var session = CreateSession("acct-1");
		var payload = new Dictionary<string, object?>
		{
			["action"] = "play",
			["games"] = "730, id/570,730,not-a-number"
		};

		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal("play", result.Output["action"]?.ToString());

		var games = Assert.IsAssignableFrom<IEnumerable<uint>>(result.Output["games"]);
		Assert.Equal([570u, 730u], games.OrderBy(v => v).ToArray());
	}

	[Fact]
	public async Task ExecuteAsync_Play_WithInvalidGames_ReturnsFailure()
	{
		var session = CreateSession("acct-1");
		var payload = new Dictionary<string, object?>
		{
			["action"] = "play",
			["games"] = "abc,id/xyz"
		};

		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("No games specified", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_Stop_WorksWithoutGames()
	{
		var session = CreateSession("acct-1");
		var payload = new Dictionary<string, object?> { ["action"] = "stop" };

		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal("stop", result.Output["action"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_Idle_UsesStopSemantics()
	{
		var session = CreateSession("acct-1");
		var payload = new Dictionary<string, object?> { ["action"] = "idle" };

		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal("stop", result.Output["action"]?.ToString());
	}

	[Fact]
	public async Task ExecuteAsync_UnknownAction_ReturnsFailure()
	{
		var session = CreateSession("acct-1");
		var payload = new Dictionary<string, object?> { ["action"] = "unknown" };

		var result = await _action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Unknown action", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
	}

	private BotSession CreateSession(string accountName)
	{
		var credentials = new AccountCredentials(accountName, "password");
		var registry = new Mock<IActionRegistry>(MockBehavior.Loose);
		var session = new BotSession(accountName, credentials, registry.Object, _sessionLoggerMock.Object, null);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}

public sealed class PlayGamesPayloadParserTests
{
	[Fact]
	public void ParseGamesInput_SingleAppId_ReturnsSingleGame()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("730");

		Assert.Single(result);
		Assert.Contains(730u, result);
	}

	[Fact]
	public void ParseGamesInput_MultipleAppIds_ReturnsAllGames()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("730,570,440");

		Assert.Equal(3, result.Count);
		Assert.Contains(730u, result);
		Assert.Contains(570u, result);
		Assert.Contains(440u, result);
	}

	[Fact]
	public void ParseGamesInput_IdFormat_ReturnsParsedGame()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("id/730");

		Assert.Single(result);
		Assert.Contains(730u, result);
	}

	[Fact]
	public void ParseGamesInput_MixedFormat_ReturnsAllValidGames()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("730,id/570,440,id/220");

		Assert.Equal(4, result.Count);
		Assert.Contains(730u, result);
		Assert.Contains(570u, result);
		Assert.Contains(440u, result);
		Assert.Contains(220u, result);
	}

	[Fact]
	public void ParseGamesInput_WithSpaces_ParsesCorrectly()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("730, 570 , 440");

		Assert.Equal(3, result.Count);
		Assert.Contains(730u, result);
		Assert.Contains(570u, result);
		Assert.Contains(440u, result);
	}

	[Fact]
	public void ParseGamesInput_DuplicateAppIds_ReturnsUniqueSet()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("730,570,730,id/570");

		Assert.Equal(2, result.Count);
		Assert.Contains(730u, result);
		Assert.Contains(570u, result);
	}

	[Fact]
	public void ParseGamesInput_EmptyString_ReturnsEmptySet()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("");

		Assert.Empty(result);
	}

	[Fact]
	public void ParseGamesInput_NullInput_ReturnsEmptySet()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput(null);

		Assert.Empty(result);
	}

	[Fact]
	public void ParseGamesInput_WhitespaceOnly_ReturnsEmptySet()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("   ");

		Assert.Empty(result);
	}

	[Fact]
	public void ParseGamesInput_InvalidEntries_IgnoresInvalid()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("730,invalid,abc,id/xyz,570");

		Assert.Equal(2, result.Count);
		Assert.Contains(730u, result);
		Assert.Contains(570u, result);
	}

	[Fact]
	public void ParseGamesInput_AllInvalid_ReturnsEmptySet()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("abc,def,id/xyz");

		Assert.Empty(result);
	}

	[Fact]
	public void ParseGamesInput_CaseInsensitiveIdFormat_ParsesCorrectly()
	{
		var result = PlayGamesPayloadParser.ParseGamesInput("ID/730,Id/570");

		Assert.Equal(2, result.Count);
		Assert.Contains(730u, result);
		Assert.Contains(570u, result);
	}
}
