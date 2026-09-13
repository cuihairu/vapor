using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;
using Vapor.Steam.Core.Web;

namespace Vapor.Plugins.MobileAuthenticator;

/// <summary>
/// "generate_totp": generates the current Steam mobile authenticator code from a
/// base64 shared secret. Uses the synced Steam time offset when available.
/// Payload: shared_secret (string, base64). Optional: time (unix seconds, for testing).
/// </summary>
public sealed class GenerateTotpAction : IAction
{
	private readonly SteamTimeSynchronizer _timeSynchronizer;

	public GenerateTotpAction(SteamTimeSynchronizer timeSynchronizer)
	{
		_timeSynchronizer = timeSynchronizer;
	}

	public string Name => "generate_totp";

	public ActionMetadata Metadata => new(
		Name,
		"Generate a Steam mobile authenticator TOTP code from a shared secret",
		RequiresLogin: false,
		TimeoutSeconds: 15);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var sharedSecret = PayloadReader.GetString(payload, "shared_secret") ?? PayloadReader.GetString(payload, "sharedSecret");
		if (string.IsNullOrWhiteSpace(sharedSecret))
		{
			return Task.FromResult(new ActionResult(false, "shared_secret is required", null));
		}

		var time = ReadTime(payload) ?? _timeSynchronizer.GetCurrentSteamTime();

		string code;
		try
		{
			code = SteamTotp.Generate(sharedSecret, time);
		}
		catch (ArgumentException ex)
		{
			return Task.FromResult(new ActionResult(false, ex.Message, null));
		}

		return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
		{
			["code"] = code,
			["seconds_remaining"] = SteamTotp.SecondsRemaining(time),
			["time"] = time,
			["time_synced"] = _timeSynchronizer.HasSynced
		}));
	}

	internal static long? ReadTime(IReadOnlyDictionary<string, object?> payload)
	{
		if (!PayloadReader.TryGetValue(payload, "time", out var value) || value is null)
		{
			return null;
		}

		return value switch
		{
			long l => l,
			int i => i,
			double d when d % 1 == 0 => (long)d,
			JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var parsed) => parsed,
			string s when long.TryParse(s, out var parsed) => parsed,
			_ => null
		};
	}
}

/// <summary>
/// "generate_confirmation_hash": computes the HMAC-SHA1 confirmation hash used by the
/// /mobileconf endpoints.
/// Payload: identity_secret (string, base64), tag (conf|details|allow|cancel). Optional: time.
/// </summary>
public sealed class GenerateConfirmationHashAction : IAction
{
	private readonly SteamTimeSynchronizer _timeSynchronizer;

	public GenerateConfirmationHashAction(SteamTimeSynchronizer timeSynchronizer)
	{
		_timeSynchronizer = timeSynchronizer;
	}

	public string Name => "generate_confirmation_hash";

	public ActionMetadata Metadata => new(
		Name,
		"Generate a mobile confirmation hash from an identity secret",
		RequiresLogin: false,
		TimeoutSeconds: 15);

	public Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var identitySecret = PayloadReader.GetString(payload, "identity_secret") ?? PayloadReader.GetString(payload, "identitySecret");
		if (string.IsNullOrWhiteSpace(identitySecret))
		{
			return Task.FromResult(new ActionResult(false, "identity_secret is required", null));
		}

		var tag = PayloadReader.GetString(payload, "tag") ?? "conf";
		if (!ConfirmationHashGenerator.KnownTags.Contains(tag))
		{
			return Task.FromResult(new ActionResult(false, $"tag must be one of: {string.Join(", ", ConfirmationHashGenerator.KnownTags)}", null));
		}

		var time = GenerateTotpAction.ReadTime(payload) ?? _timeSynchronizer.GetCurrentSteamTime();

		string hash;
		try
		{
			hash = ConfirmationHashGenerator.Generate(identitySecret, time, tag);
		}
		catch (ArgumentException ex)
		{
			return Task.FromResult(new ActionResult(false, ex.Message, null));
		}

		return Task.FromResult(new ActionResult(true, null, new Dictionary<string, object?>
		{
			["hash"] = hash,
			["tag"] = tag,
			["time"] = time,
			["time_synced"] = _timeSynchronizer.HasSynced
		}));
	}
}

/// <summary>
/// "sync_steam_time": queries Steam's QueryTime endpoint and stores the local/server clock
/// offset. Subsequent TOTP and confirmation hash generations use the synced time.
/// </summary>
public sealed class SyncSteamTimeAction : IAction
{
	private readonly SteamTimeSynchronizer _timeSynchronizer;
	private readonly ILogger<SyncSteamTimeAction> _logger;

	public SyncSteamTimeAction(SteamTimeSynchronizer timeSynchronizer, ILogger<SyncSteamTimeAction> logger)
	{
		_timeSynchronizer = timeSynchronizer;
		_logger = logger;
	}

	public string Name => "sync_steam_time";

	public ActionMetadata Metadata => new(
		Name,
		"Synchronize the local clock offset against Steam server time",
		RequiresLogin: false,
		TimeoutSeconds: 30);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		try
		{
			await _timeSynchronizer.SyncAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ActionResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Steam time sync failed");
			return new ActionResult(false, $"Steam time sync failed: {ex.Message}", null);
		}

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["offset_seconds"] = _timeSynchronizer.OffsetSeconds,
			["steam_time"] = _timeSynchronizer.GetCurrentSteamTime(),
			["synced_at"] = _timeSynchronizer.LastSyncedAt?.ToString("O")
		});
	}
}

/// <summary>
/// "get_trade_confirmations": lists pending mobile trade/market confirmations for the
/// session account. Requires identity_secret.
/// </summary>
public sealed class GetTradeConfirmationsAction : IAction
{
	private readonly ILogger<GetTradeConfirmationsAction> _logger;
	private readonly Func<BotSession, IMobileConfirmationClient> _clientFactory;

	public GetTradeConfirmationsAction(ILogger<GetTradeConfirmationsAction> logger, SteamTimeSynchronizer timeSynchronizer)
	{
		_logger = logger;
		_clientFactory = session => new MobileConfirmationClient(session.SteamWebHandler!, timeSynchronizer, logger);
	}

	internal GetTradeConfirmationsAction(
		ILogger<GetTradeConfirmationsAction> logger,
		Func<BotSession, IMobileConfirmationClient> clientFactory)
	{
		_logger = logger;
		_clientFactory = clientFactory;
	}

	public string Name => "get_trade_confirmations";

	public ActionMetadata Metadata => new(
		Name,
		"List pending mobile trade/market confirmations",
		RequiresLogin: true,
		TimeoutSeconds: 30);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var identitySecret = PayloadReader.GetString(payload, "identity_secret") ?? PayloadReader.GetString(payload, "identitySecret");
		if (string.IsNullOrWhiteSpace(identitySecret))
		{
			return new ActionResult(false, "identity_secret is required", null);
		}

		if (session.SteamWebHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		MobileConfirmationListResult result;
		try
		{
			result = await _clientFactory(session).GetConfirmationsAsync(identitySecret, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ActionResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to fetch trade confirmations for {AccountName}", session.AccountName);
			return new ActionResult(false, ex.Message, null);
		}

		if (!result.Success)
		{
			return new ActionResult(false, result.Error, null);
		}

		var confirmations = result.Confirmations ?? [];
		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["count"] = confirmations.Count,
			["confirmations"] = confirmations.Select(c => new Dictionary<string, object?>
			{
				["id"] = c.Id.ToString(),
				["nonce"] = c.Nonce.ToString(),
				["creator_id"] = c.CreatorId.ToString(),
				["headline"] = c.Headline,
				["summary"] = c.Summary
			}).ToList()
		});
	}
}

/// <summary>
/// "respond_trade_confirmation": accepts (allow) or cancels a pending mobile confirmation.
/// Payload: identity_secret, confirmation_id, nonce, operation (allow|cancel, default allow).
/// </summary>
public sealed class RespondTradeConfirmationAction : IAction
{
	private readonly ILogger<RespondTradeConfirmationAction> _logger;
	private readonly Func<BotSession, IMobileConfirmationClient> _clientFactory;

	public RespondTradeConfirmationAction(ILogger<RespondTradeConfirmationAction> logger, SteamTimeSynchronizer timeSynchronizer)
	{
		_logger = logger;
		_clientFactory = session => new MobileConfirmationClient(session.SteamWebHandler!, timeSynchronizer, logger);
	}

	internal RespondTradeConfirmationAction(
		ILogger<RespondTradeConfirmationAction> logger,
		Func<BotSession, IMobileConfirmationClient> clientFactory)
	{
		_logger = logger;
		_clientFactory = clientFactory;
	}

	public string Name => "respond_trade_confirmation";

	public ActionMetadata Metadata => new(
		Name,
		"Accept or cancel a pending mobile trade/market confirmation",
		RequiresLogin: true,
		TimeoutSeconds: 30);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var identitySecret = PayloadReader.GetString(payload, "identity_secret") ?? PayloadReader.GetString(payload, "identitySecret");
		if (string.IsNullOrWhiteSpace(identitySecret))
		{
			return new ActionResult(false, "identity_secret is required", null);
		}

		if (!TryGetUInt64(payload, "confirmation_id", out var confirmationId))
		{
			return new ActionResult(false, "confirmation_id is required and must be a positive integer", null);
		}

		if (!TryGetUInt64(payload, "nonce", out var nonce))
		{
			return new ActionResult(false, "nonce is required and must be a positive integer", null);
		}

		var operationText = PayloadReader.GetString(payload, "operation") ?? "allow";
		var operation = operationText.Equals("cancel", StringComparison.OrdinalIgnoreCase)
			? ConfirmationOperation.Cancel
			: operationText.Equals("allow", StringComparison.OrdinalIgnoreCase)
				? ConfirmationOperation.Allow
				: (ConfirmationOperation?)null;

		if (operation is null)
		{
			return new ActionResult(false, "operation must be 'allow' or 'cancel'", null);
		}

		if (session.SteamWebHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		MobileConfirmationResult result;
		try
		{
			result = await _clientFactory(session)
				.RespondAsync(identitySecret, confirmationId, nonce, operation.Value, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ActionResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to respond to confirmation {ConfirmationId} for {AccountName}", confirmationId, session.AccountName);
			return new ActionResult(false, ex.Message, null);
		}

		if (!result.Success)
		{
			return new ActionResult(false, result.Error, null);
		}

		_logger.LogInformation(
			"Confirmation {ConfirmationId} {Operation} for {AccountName}",
			confirmationId, operation.Value == ConfirmationOperation.Allow ? "allowed" : "canceled", session.AccountName);

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["confirmation_id"] = confirmationId.ToString(),
			["operation"] = operation.Value == ConfirmationOperation.Allow ? "allow" : "cancel"
		});
	}

	private static bool TryGetUInt64(IReadOnlyDictionary<string, object?> payload, string key, out ulong value)
	{
		value = 0;

		if (!PayloadReader.TryGetValue(payload, key, out var raw) || raw is null)
		{
			return false;
		}

		switch (raw)
		{
			case ulong u:
				value = u;
				return true;
			case long l when l > 0:
				value = (ulong)l;
				return true;
			case int i when i > 0:
				value = (ulong)i;
				return true;
			case double d when d > 0 && d % 1 == 0:
				value = (ulong)d;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } e:
				return e.TryGetUInt64(out value);
			case string s:
				return ulong.TryParse(s, out value);
			default:
				return false;
		}
	}
}

/// <summary>
/// "save_shared_secret": stores the account's Steam mobile authenticator shared secret
/// (base64) in the agent's encrypted credential store so 2FA challenges can be answered
/// locally. Payload: shared_secret (string, base64).
/// </summary>
public sealed class SaveSharedSecretAction : IAction
{
	private readonly ILogger<SaveSharedSecretAction> _logger;
	private readonly ICredentialStore? _credentialStore;

	public SaveSharedSecretAction(ILogger<SaveSharedSecretAction> logger, ICredentialStore? credentialStore = null)
	{
		_logger = logger;
		_credentialStore = credentialStore;
	}

	public string Name => "save_shared_secret";

	public ActionMetadata Metadata => new(
		Name,
		"Store the account's mobile authenticator shared secret for automatic 2FA answers",
		RequiresLogin: false,
		TimeoutSeconds: 10);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		if (_credentialStore is null)
		{
			return new ActionResult(false, "credential store is not available to this plugin", null);
		}

		var sharedSecret = PayloadReader.GetString(payload, "shared_secret") ?? PayloadReader.GetString(payload, "sharedSecret");
		if (string.IsNullOrWhiteSpace(sharedSecret))
		{
			return new ActionResult(false, "shared_secret is required", null);
		}

		try
		{
			await _credentialStore.SaveSharedSecretAsync(session.AccountName, sharedSecret, cancellationToken).ConfigureAwait(false);
		}
		catch (ArgumentException ex)
		{
			return new ActionResult(false, ex.Message, null);
		}

		_logger.LogInformation("Stored shared secret for {AccountName}", session.AccountName);
		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["account"] = session.AccountName,
			["stored"] = true
		});
	}
}

/// <summary>
/// "save_identity_secret": stores the account's Steam mobile authenticator identity
/// secret (base64) in the agent's encrypted credential store so trade/market
/// confirmations can be answered locally. The secret never leaves the agent —
/// confirmation tasks are dispatched without it and the store is read at execution time.
/// Payload: identity_secret (string, base64).
/// </summary>
public sealed class SaveIdentitySecretAction : IAction
{
	private readonly ILogger<SaveIdentitySecretAction> _logger;
	private readonly ICredentialStore? _credentialStore;

	public SaveIdentitySecretAction(ILogger<SaveIdentitySecretAction> logger, ICredentialStore? credentialStore = null)
	{
		_logger = logger;
		_credentialStore = credentialStore;
	}

	public string Name => "save_identity_secret";

	public ActionMetadata Metadata => new(
		Name,
		"Store the account's mobile authenticator identity secret for local trade confirmation signing",
		RequiresLogin: false,
		TimeoutSeconds: 10);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		if (_credentialStore is null)
		{
			return new ActionResult(false, "credential store is not available to this plugin", null);
		}

		var identitySecret = PayloadReader.GetString(payload, "identity_secret") ?? PayloadReader.GetString(payload, "identitySecret");
		if (string.IsNullOrWhiteSpace(identitySecret))
		{
			return new ActionResult(false, "identity_secret is required", null);
		}

		try
		{
			await _credentialStore.SaveIdentitySecretAsync(session.AccountName, identitySecret, cancellationToken).ConfigureAwait(false);
		}
		catch (ArgumentException ex)
		{
			return new ActionResult(false, ex.Message, null);
		}

		_logger.LogInformation("Stored identity secret for {AccountName}", session.AccountName);
		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["account"] = session.AccountName,
			["stored"] = true
		});
	}
}

/// <summary>
/// "confirm_trade_offer": completes the mobile confirmation step of a trade offer using
/// the identity secret stored in the agent's credential store. Lists pending confirmations,
/// matches the one whose creator id equals the trade offer id (retrying briefly — Steam
/// lags a few seconds between accepting an offer and publishing its confirmation), and
/// responds with the requested operation. The identity secret is never accepted through
/// the payload and never appears in task records; only the credential store is consulted.
/// </summary>
public sealed class ConfirmTradeOfferAction : IAction
{
	// Steam publishes a trade confirmation a few seconds after the offer is accepted;
	// poll a few times before giving up. Internal-static so tests can shrink them.
	internal static TimeSpan MatchPollInterval = TimeSpan.FromMilliseconds(250);
	internal static int MatchPollAttempts = 6;

	private readonly ILogger<ConfirmTradeOfferAction> _logger;
	private readonly ICredentialStore? _credentialStore;
	private readonly Func<BotSession, IMobileConfirmationClient> _clientFactory;

	public ConfirmTradeOfferAction(ILogger<ConfirmTradeOfferAction> logger, ICredentialStore? credentialStore, SteamTimeSynchronizer timeSynchronizer)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_clientFactory = session => new MobileConfirmationClient(session.SteamWebHandler!, timeSynchronizer, logger);
	}

	// Constructor for testing with a custom client factory
	internal ConfirmTradeOfferAction(
		ILogger<ConfirmTradeOfferAction> logger,
		ICredentialStore? credentialStore,
		Func<BotSession, IMobileConfirmationClient> clientFactory)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_clientFactory = clientFactory;
	}

	public string Name => "confirm_trade_offer";

	public ActionMetadata Metadata => new(
		Name,
		"Approve or cancel the pending mobile confirmation of a trade offer (uses the stored identity secret)",
		RequiresLogin: true,
		TimeoutSeconds: 60);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		if (!TryGetUInt64(payload, "trade_offer_id", out var tradeOfferId) || tradeOfferId == 0)
		{
			return new ActionResult(false, "trade_offer_id is required and must be a positive integer", null);
		}

		var operationText = PayloadReader.GetString(payload, "operation") ?? "allow";
		var operation = operationText.Equals("cancel", StringComparison.OrdinalIgnoreCase)
			? ConfirmationOperation.Cancel
			: operationText.Equals("allow", StringComparison.OrdinalIgnoreCase)
				? ConfirmationOperation.Allow
				: (ConfirmationOperation?)null;

		if (operation is null)
		{
			return new ActionResult(false, "operation must be 'allow' or 'cancel'", null);
		}

		if (_credentialStore is null)
		{
			return new ActionResult(false, "credential store is not available to this plugin", null);
		}

		if (session.SteamWebHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		string? identitySecret = await _credentialStore.GetIdentitySecretAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(identitySecret))
		{
			return new ActionResult(false, $"no identity secret is stored for '{session.AccountName}'; dispatch save_identity_secret first (the secret stays agent-side)", null);
		}

		var client = _clientFactory(session);
		TradeConfirmation? match = null;

		for (int attempt = 0; attempt < MatchPollAttempts && match is null; attempt++)
		{
			if (attempt > 0)
			{
				await Task.Delay(MatchPollInterval, cancellationToken).ConfigureAwait(false);
			}

			MobileConfirmationListResult listing;
			try
			{
				listing = await client.GetConfirmationsAsync(identitySecret, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return new ActionResult(false, "canceled", null);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to list trade confirmations for {AccountName}", session.AccountName);
				return new ActionResult(false, ex.Message, null);
			}

			if (!listing.Success)
			{
				return new ActionResult(false, listing.Error ?? "failed to list trade confirmations", null);
			}

			match = listing.Confirmations?.FirstOrDefault(c => c.CreatorId == tradeOfferId);
		}

		if (match is null)
		{
			return new ActionResult(false, $"no pending mobile confirmation for trade offer {tradeOfferId} (it may have expired, already been handled, or not surfaced yet)", null);
		}

		MobileConfirmationResult response;
		try
		{
			response = await client.RespondAsync(identitySecret, match.Id, match.Nonce, operation.Value, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ActionResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to respond to confirmation {ConfirmationId} for {AccountName}", match.Id, session.AccountName);
			return new ActionResult(false, ex.Message, null);
		}

		if (!response.Success)
		{
			return new ActionResult(false, response.Error ?? "failed to respond to the confirmation", null);
		}

		_logger.LogInformation(
			"Confirmation {ConfirmationId} {Operation} for trade offer {TradeOfferId} ({AccountName})",
			match.Id, operation.Value == ConfirmationOperation.Allow ? "allowed" : "canceled", tradeOfferId, session.AccountName);

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["trade_offer_id"] = tradeOfferId.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["confirmation_id"] = match.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
			["operation"] = operation.Value == ConfirmationOperation.Allow ? "allow" : "cancel",
			["confirmed"] = operation.Value == ConfirmationOperation.Allow
		});
	}

	private static bool TryGetUInt64(IReadOnlyDictionary<string, object?> payload, string key, out ulong value)
	{
		value = 0;

		if (!PayloadReader.TryGetValue(payload, key, out var raw) || raw is null)
		{
			return false;
		}

		switch (raw)
		{
			case ulong u:
				value = u;
				return true;
			case long l when l > 0:
				value = (ulong)l;
				return true;
			case int i when i > 0:
				value = (ulong)i;
				return true;
			case double d when d > 0 && d % 1 == 0:
				value = (ulong)d;
				return true;
			case JsonElement { ValueKind: JsonValueKind.Number } e:
				return e.TryGetUInt64(out value);
			case string s:
				return ulong.TryParse(s, out value);
			default:
				return false;
		}
	}
}

/// <summary>
/// "confirm_all_confirmations": responds to every pending mobile confirmation in one
/// shot (the Watt/ASF "accept all" flow), optionally restricted to a type
/// ("trade" or "market"; default all). Uses the identity secret stored in the agent's
/// credential store — never a payload-supplied one. Each confirmation is answered
/// individually so one failure does not abort the batch; the per-item results are
/// reported back in the output.
/// </summary>
public sealed class ConfirmAllConfirmationsAction : IAction
{
	private readonly ILogger<ConfirmAllConfirmationsAction> _logger;
	private readonly ICredentialStore? _credentialStore;
	private readonly Func<BotSession, IMobileConfirmationClient> _clientFactory;

	public ConfirmAllConfirmationsAction(ILogger<ConfirmAllConfirmationsAction> logger, ICredentialStore? credentialStore, SteamTimeSynchronizer timeSynchronizer)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_clientFactory = session => new MobileConfirmationClient(session.SteamWebHandler!, timeSynchronizer, logger);
	}

	// Constructor for testing with a custom client factory
	internal ConfirmAllConfirmationsAction(
		ILogger<ConfirmAllConfirmationsAction> logger,
		ICredentialStore? credentialStore,
		Func<BotSession, IMobileConfirmationClient> clientFactory)
	{
		_logger = logger;
		_credentialStore = credentialStore;
		_clientFactory = clientFactory;
	}

	public string Name => "confirm_all_confirmations";

	public ActionMetadata Metadata => new(
		Name,
		"Respond to all pending mobile confirmations (optionally filtered by type) using the stored identity secret",
		RequiresLogin: true,
		TimeoutSeconds: 120);

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var operationText = PayloadReader.GetString(payload, "operation") ?? "allow";
		var operation = operationText.Equals("cancel", StringComparison.OrdinalIgnoreCase)
			? ConfirmationOperation.Cancel
			: operationText.Equals("allow", StringComparison.OrdinalIgnoreCase)
				? ConfirmationOperation.Allow
				: (ConfirmationOperation?)null;

		if (operation is null)
		{
			return new ActionResult(false, "operation must be 'allow' or 'cancel'", null);
		}

		var typeFilter = PayloadReader.GetString(payload, "type")?.Trim().ToLowerInvariant();
		if (typeFilter is not (null or "" or "all" or "trade" or "market"))
		{
			return new ActionResult(false, "type must be 'all', 'trade' or 'market'", null);
		}

		if (_credentialStore is null)
		{
			return new ActionResult(false, "credential store is not available to this plugin", null);
		}

		if (session.SteamWebHandler is null)
		{
			return new ActionResult(false, "Steam web handler not available", null);
		}

		string? identitySecret = await _credentialStore.GetIdentitySecretAsync(session.AccountName, cancellationToken).ConfigureAwait(false);
		if (string.IsNullOrWhiteSpace(identitySecret))
		{
			return new ActionResult(false, $"no identity secret is stored for '{session.AccountName}'; dispatch save_identity_secret first (the secret stays agent-side)", null);
		}

		var client = _clientFactory(session);
		MobileConfirmationListResult listing;
		try
		{
			listing = await client.GetConfirmationsAsync(identitySecret, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return new ActionResult(false, "canceled", null);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to list trade confirmations for {AccountName}", session.AccountName);
			return new ActionResult(false, ex.Message, null);
		}

		if (!listing.Success)
		{
			return new ActionResult(false, listing.Error ?? "failed to list trade confirmations", null);
		}

		IEnumerable<TradeConfirmation> targets = listing.Confirmations ?? [];
		if (typeFilter is not (null or "" or "all"))
		{
			targets = targets.Where(c => string.Equals(c.Type, typeFilter, StringComparison.OrdinalIgnoreCase));
		}

		var results = new List<Dictionary<string, object?>>();
		int succeeded = 0;

		foreach (TradeConfirmation confirmation in targets.ToList())
		{
			cancellationToken.ThrowIfCancellationRequested();

			MobileConfirmationResult response;
			try
			{
				response = await client.RespondAsync(identitySecret, confirmation.Id, confirmation.Nonce, operation.Value, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return new ActionResult(false, "canceled", null);
			}
			catch (Exception ex)
			{
				response = new MobileConfirmationResult(false, ex.Message);
			}

			if (response.Success)
			{
				succeeded++;
			}

			var entry = new Dictionary<string, object?>
			{
				["confirmation_id"] = confirmation.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
				["type"] = confirmation.Type,
				["succeeded"] = response.Success
			};
			if (!response.Success)
			{
				entry["error"] = response.Error;
			}

			results.Add(entry);
		}

		int total = results.Count;
		_logger.LogInformation(
			"Batch confirmation {Operation}: {Succeeded}/{Total} succeeded for {AccountName}",
			operation.Value == ConfirmationOperation.Allow ? "allow" : "cancel", succeeded, total, session.AccountName);

		return new ActionResult(true, null, new Dictionary<string, object?>
		{
			["operation"] = operation.Value == ConfirmationOperation.Allow ? "allow" : "cancel",
			["total"] = total,
			["succeeded"] = succeeded,
			["failed"] = total - succeeded,
			["results"] = results
		});
	}
}
