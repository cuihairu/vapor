using Microsoft.Extensions.Logging;

namespace Vapor.Steam.Core.Actions;

public sealed class PlayGamesAction : IAction
{
	private readonly ILogger<PlayGamesAction> _logger;

	public PlayGamesAction(ILogger<PlayGamesAction> logger)
	{
		_logger = logger;
	}

	public string Name => "play_games";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Play, stop or idle games on Steam",
		RequiresLogin: true,
			TimeoutSeconds: 30
	);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		string? gamesInput = PayloadReader.GetString(payload, "games");
		string? action = PayloadReader.GetString(payload, "action");

		if (string.IsNullOrEmpty(gamesInput) && string.IsNullOrEmpty(action))
		{
			return Task.FromResult<ActionResult>(new ActionResult(false, "Either 'games' or 'action' parameter is required", null));
		}

		// Default to playing games
		var actionValue = action ?? "play";

		return actionValue.ToLowerInvariant() switch
		{
			"play" => PlayGamesAsync(session, gamesInput),
			"stop" => StopAllGamesAsync(session),
			"idle" => StopAllGamesAsync(session),
			_ => Task.FromResult<ActionResult>(new ActionResult(false, $"Unknown action: {actionValue}", null))
		};
	}

	private Task<ActionResult> PlayGamesAsync(BotSession session, string? gamesInput)
	{
		var gamesToPlay = PlayGamesPayloadParser.ParseGamesInput(gamesInput);
		if (gamesToPlay.Count == 0)
		{
			return Task.FromResult<ActionResult>(new ActionResult(false, "No games specified", null));
		}

		_logger.LogInformation("Playing {Count} games: {Games}", gamesToPlay.Count, string.Join(", ", gamesToPlay));

		var output = new Dictionary<string, object?>
		{
			["action"] = "play",
			["games"] = gamesToPlay,
			["account"] = session.AccountName
		};

		// TODO: Integrate with Steam client to actually play the games
		return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
	}

	private Task<ActionResult> StopAllGamesAsync(BotSession session)
	{
		_logger.LogInformation("Stopping all games for {AccountName}", session.AccountName);

		var output = new Dictionary<string, object?>
		{
			["action"] = "stop",
			["account"] = session.AccountName
		};

		// TODO: Integrate with Steam client to actually stop games
		return Task.FromResult<ActionResult>(new ActionResult(true, null, output));
	}
}

internal static class PlayGamesPayloadParser
{
	internal static HashSet<uint> ParseGamesInput(string? input)
	{
		var games = new HashSet<uint>();

		if (string.IsNullOrWhiteSpace(input))
		{
			return games;
		}

		// Support multiple formats:
		// Single AppID: "12345"
		// Multiple AppIDs: "12345,67890"
		// GameID format: "id/12345"
		// Combined: "12345,id/67890,76543"

		var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		foreach (var part in parts)
		{
			var trimmedPart = part.Trim();

			// Handle "id/..." format
			if (trimmedPart.StartsWith("id/", StringComparison.OrdinalIgnoreCase))
			{
				var gameID = trimmedPart[3..];
				if (uint.TryParse(gameID, out uint gameId))
				{
					games.Add(gameId);
				}
			}
			// Handle plain AppID
			else if (uint.TryParse(trimmedPart, out uint appId))
			{
				games.Add(appId);
			}
		}

		return games;
	}
}
