using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Plugins.MobileAuthenticator;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

/// <summary>
/// Edge-path coverage for the authenticator actions: payload value coercion,
/// cancellation and unexpected-exception handling, and credential-store failures.
/// </summary>
public class AuthenticatorActionEdgeCaseTests
{
	private const string SharedSecret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";
	private const string IdentitySecret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
	private const ulong TestCreatorId = 43591234567890UL;

	private static SteamTimeSynchronizer CreateSynchronizer(long? serverTime = null) =>
		new(_ => Task.FromResult(serverTime ?? 0L), new FixedTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));

	private static JsonElement JsonNumber(string number)
	{
		using var doc = JsonDocument.Parse(number);
		return doc.RootElement.Clone();
	}

	private static CancellationToken CanceledToken()
	{
		var cts = new CancellationTokenSource();
		cts.Cancel();
		return cts.Token;
	}

	private static ConfirmTradeOfferAction CreateConfirmOfferAction(StubMobileConfirmationClient client)
	{
		var store = new StubCredentialStore();
		store.SaveIdentitySecretAsync("test_account", IdentitySecret).GetAwaiter().GetResult();
		return new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, store, _ => client);
	}

	// --- generate_totp: payload "time" coercion ---

	[Fact]
	public void ReadTime_CoercesSupportedPayloadValueTypes()
	{
		Assert.Equal(59L, GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = 59L }));
		Assert.Equal(59L, GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = 59 }));
		Assert.Equal(59L, GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = 59.0 }));
		Assert.Equal(59L, GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = JsonNumber("59") }));
		Assert.Equal(59L, GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = "59" }));
	}

	[Fact]
	public void ReadTime_UnsupportedOrNonIntegralValues_YieldNull()
	{
		Assert.Null(GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = true }));
		Assert.Null(GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = "not-a-number" }));
		Assert.Null(GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = 59.5 }));
		Assert.Null(GenerateTotpAction.ReadTime(new Dictionary<string, object?> { ["time"] = JsonNumber("59.5") }));
		Assert.Null(GenerateTotpAction.ReadTime(new Dictionary<string, object?>()));
	}

	[Fact]
	public async Task GenerateTotp_JsonElementTime_ProducesSameCodeAsLong()
	{
		var action = new GenerateTotpAction(CreateSynchronizer());

		var fromJson = await action.ExecuteAsync(
			null!,
			new Dictionary<string, object?> { ["shared_secret"] = SharedSecret, ["time"] = JsonNumber("59") },
			CancellationToken.None);

		Assert.True(fromJson.Success);
		Assert.Equal("PV9M4", fromJson.Output!["code"]);
		Assert.Equal(59L, fromJson.Output["time"]);
	}

	// --- generate_confirmation_hash: invalid secret reaching the generator ---

	[Fact]
	public async Task GenerateConfirmationHash_InvalidSecret_FailsWithDecodeError()
	{
		var action = new GenerateConfirmationHashAction(CreateSynchronizer());
		var payload = new Dictionary<string, object?> { ["identity_secret"] = "!!bad!!" };

		var result = await action.ExecuteAsync(null!, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("base64", result.Error);
	}

	// --- sync_steam_time: cancellation ---

	[Fact]
	public async Task SyncSteamTime_Canceled_ReturnsCanceled()
	{
		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromCanceled<long>(CanceledToken()));
		var action = new SyncSteamTimeAction(synchronizer, NullLogger<SyncSteamTimeAction>.Instance);

		var result = await action.ExecuteAsync(null!, new Dictionary<string, object?>(), CanceledToken());

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	// --- get_trade_confirmations: client cancellation and unexpected exceptions ---

	[Fact]
	public async Task GetTradeConfirmations_CanceledDuringFetch_ReturnsCanceled()
	{
		var stub = new StubMobileConfirmationClient { ListException = new OperationCanceledException(CanceledToken()) };
		var action = new GetTradeConfirmationsAction(NullLogger<GetTradeConfirmationsAction>.Instance, _ => stub);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret };

		var result = await action.ExecuteAsync(session, payload, CanceledToken());

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task GetTradeConfirmations_UnexpectedClientException_PropagatesMessage()
	{
		var stub = new StubMobileConfirmationClient { ListException = new InvalidOperationException("list blew up") };
		var action = new GetTradeConfirmationsAction(NullLogger<GetTradeConfirmationsAction>.Instance, _ => stub);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("list blew up", result.Error);
	}

	// --- respond_trade_confirmation: payload validation gaps ---

	[Fact]
	public async Task RespondTradeConfirmation_MissingIdentitySecret_Fails()
	{
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => new StubMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["confirmation_id"] = "123", ["nonce"] = "456" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("identity_secret", result.Error);
	}

	[Fact]
	public async Task RespondTradeConfirmation_MissingNonce_Fails()
	{
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => new StubMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret, ["confirmation_id"] = "123" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("nonce", result.Error);
	}

	[Fact]
	public async Task RespondTradeConfirmation_NoWebHandler_Fails()
	{
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => new StubMobileConfirmationClient());
		using var session = TestSession.Create(withWebHandler: false);
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = "123",
			["nonce"] = "456"
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error);
	}

	[Fact]
	public async Task RespondTradeConfirmation_CanceledDuringRespond_ReturnsCanceled()
	{
		var stub = new StubMobileConfirmationClient { RespondException = new OperationCanceledException(CanceledToken()) };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => stub);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = "123",
			["nonce"] = "456"
		};

		var result = await action.ExecuteAsync(session, payload, CanceledToken());

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task RespondTradeConfirmation_UnexpectedClientException_PropagatesMessage()
	{
		var stub = new StubMobileConfirmationClient { RespondException = new InvalidOperationException("respond blew up") };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => stub);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = "123",
			["nonce"] = "456"
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("respond blew up", result.Error);
	}

	[Theory]
	[InlineData(123UL)]
	[InlineData(123L)]
	[InlineData(123)]
	[InlineData(123.0)]
	public async Task RespondTradeConfirmation_NumericConfirmationIdVariants_AreAccepted(object? confirmationId)
	{
		var stub = new StubMobileConfirmationClient { OperationResult = new MobileConfirmationResult(true) };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => stub);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = confirmationId,
			["nonce"] = "456"
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal((123UL, 456UL, ConfirmationOperation.Allow), stub.LastRespond);
	}

	[Fact]
	public async Task RespondTradeConfirmation_JsonNumberConfirmationId_IsAccepted()
	{
		var stub = new StubMobileConfirmationClient { OperationResult = new MobileConfirmationResult(true) };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => stub);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = JsonNumber("123"),
			["nonce"] = JsonNumber("456")
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal((123UL, 456UL, ConfirmationOperation.Allow), stub.LastRespond);
	}

	[Fact]
	public async Task RespondTradeConfirmation_NonNumericConfirmationId_Fails()
	{
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => new StubMobileConfirmationClient());
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = true,
			["nonce"] = "456"
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("confirmation_id is required", result.Error);
	}

	// --- save_shared_secret / save_identity_secret: store rejections ---

	[Fact]
	public async Task SaveSharedSecret_StoreRejectsSecret_ReturnsStoreError()
	{
		var store = new StubCredentialStore { OnSaveSharedSecret = new ArgumentException("secret must be valid base64") };
		var action = new SaveSharedSecretAction(NullLogger<SaveSharedSecretAction>.Instance, store);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["shared_secret"] = SharedSecret },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("secret must be valid base64", result.Error);
	}

	[Fact]
	public async Task SaveIdentitySecret_StoreRejectsSecret_ReturnsStoreError()
	{
		var store = new StubCredentialStore { OnSaveIdentitySecret = new ArgumentException("identity secret must be valid base64") };
		var action = new SaveIdentitySecretAction(NullLogger<SaveIdentitySecretAction>.Instance, store);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("identity secret must be valid base64", result.Error);
	}

	// --- confirm_trade_offer: validation gaps ---

	[Fact]
	public async Task ConfirmTradeOffer_InvalidOperation_Fails()
	{
		var action = CreateConfirmOfferAction(new StubMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890", ["operation"] = "bogus" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("operation must be 'allow' or 'cancel'", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_MissingCredentialStore_Fails()
	{
		var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, (ICredentialStore?)null, _ => new StubMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("credential store is not available", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_ZeroTradeOfferId_Fails()
	{
		var action = CreateConfirmOfferAction(new StubMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = "0" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("trade_offer_id is required", result.Error);
	}

	[Theory]
	[InlineData(55UL)]
	[InlineData(55L)]
	[InlineData(55)]
	[InlineData(55.0)]
	public async Task ConfirmTradeOffer_NumericTradeOfferIdVariants_AreAccepted(object? tradeOfferId)
	{
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(111UL, 222UL, 55UL, "Trade with someone", "You give: item")
			}),
			OperationResult = new MobileConfirmationResult(true)
		};
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = tradeOfferId },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal((111UL, 222UL, ConfirmationOperation.Allow), stub.LastRespond);
	}

	[Fact]
	public async Task ConfirmTradeOffer_JsonNumberTradeOfferId_IsAccepted()
	{
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(111UL, 222UL, 55UL, "Trade with someone", "You give: item")
			}),
			OperationResult = new MobileConfirmationResult(true)
		};
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = JsonNumber("55") },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("55", result.Output!["trade_offer_id"]);
	}

	[Fact]
	public async Task ConfirmTradeOffer_NonNumericTradeOfferId_Fails()
	{
		var action = CreateConfirmOfferAction(new StubMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = true },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("trade_offer_id is required", result.Error);
	}

	// --- confirm_trade_offer: listing/respond cancellation and failures ---

	[Fact]
	public async Task ConfirmTradeOffer_CanceledDuringListing_ReturnsCanceled()
	{
		var stub = new StubMobileConfirmationClient { ListException = new OperationCanceledException(CanceledToken()) };
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = TestCreatorId.ToString() },
			CanceledToken());

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_UnexpectedListingException_PropagatesMessage()
	{
		var stub = new StubMobileConfirmationClient { ListException = new InvalidOperationException("listing blew up") };
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = TestCreatorId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("listing blew up", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_ListingFailure_FailsWithSteamError()
	{
		var stub = new StubMobileConfirmationClient { ListResult = new MobileConfirmationListResult(false, "Invalid authenticator") };
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = TestCreatorId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("Invalid authenticator", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_ListingFailureWithoutSteamError_UsesFallbackMessage()
	{
		var stub = new StubMobileConfirmationClient { ListResult = new MobileConfirmationListResult(false, null) };
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = TestCreatorId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("failed to list trade confirmations", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_CanceledDuringRespond_ReturnsCanceled()
	{
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(111UL, 222UL, TestCreatorId, "Trade with someone", "You give: item")
			}),
			RespondException = new OperationCanceledException(CanceledToken())
		};
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = TestCreatorId.ToString() },
			CanceledToken());

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_UnexpectedRespondException_PropagatesMessage()
	{
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(111UL, 222UL, TestCreatorId, "Trade with someone", "You give: item")
			}),
			RespondException = new InvalidOperationException("respond blew up")
		};
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = TestCreatorId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("respond blew up", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_RespondFailure_FailsWithSteamError()
	{
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(111UL, 222UL, TestCreatorId, "Trade with someone", "You give: item")
			}),
			OperationResult = new MobileConfirmationResult(false, null)
		};
		var action = CreateConfirmOfferAction(stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = TestCreatorId.ToString() },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("failed to respond to the confirmation", result.Error);
	}

	// --- confirm_all_confirmations: validation gaps and per-item exceptions ---

	[Fact]
	public async Task ConfirmAll_MissingCredentialStore_Fails()
	{
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, (ICredentialStore?)null, _ => new StubMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("credential store is not available", result.Error);
	}

	[Fact]
	public async Task ConfirmAll_NoWebHandler_Fails()
	{
		var store = new StubCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => new StubMobileConfirmationClient());
		using var session = TestSession.Create(withWebHandler: false);

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error);
	}

	[Fact]
	public async Task ConfirmAll_CanceledDuringListing_ReturnsCanceled()
	{
		var store = new StubCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var stub = new StubMobileConfirmationClient { ListException = new OperationCanceledException(CanceledToken()) };
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CanceledToken());

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task ConfirmAll_UnexpectedListingException_PropagatesMessage()
	{
		var store = new StubCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var stub = new StubMobileConfirmationClient { ListException = new InvalidOperationException("listing blew up") };
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("listing blew up", result.Error);
	}

	[Fact]
	public async Task ConfirmAll_CanceledDuringRespond_ReturnsCanceled()
	{
		var store = new StubCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		using var cts = new CancellationTokenSource();
		var token = cts.Token;
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(1UL, 11UL, 100UL, "Trade", "item", "trade")
			}),
			// The batch checks the token before each respond; cancel inside the stub so
			// the OCE filter (token already canceled) matches once the exception surfaces.
			OnRespond = () =>
			{
				cts.Cancel();
				throw new OperationCanceledException(token);
			}
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), token);

		Assert.False(result.Success);
		Assert.Equal("canceled", result.Error);
	}

	[Fact]
	public async Task ConfirmAll_RespondException_IsReportedPerItem()
	{
		var store = new StubCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(1UL, 11UL, 100UL, "Trade", "item", "trade"),
				new TradeConfirmation(2UL, 22UL, 101UL, "Market", "sale", "market")
			}),
			RespondException = new InvalidOperationException("respond blew up")
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		// The batch itself succeeds — per-item exceptions are reported in the output.
		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total"]);
		Assert.Equal(0, result.Output["succeeded"]);
		Assert.Equal(2, result.Output["failed"]);

		var results = (List<Dictionary<string, object?>>)result.Output["results"]!;
		Assert.All(results, entry =>
		{
			Assert.Equal(false, entry["succeeded"]);
			Assert.Equal("respond blew up", entry["error"]);
		});
		Assert.Equal(2, stub.RespondCalls);
	}

	[Fact]
	public async Task ConfirmAll_AllowAndCancelOperationNames_AppearInOutput()
	{
		var store = new StubCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(1UL, 11UL, 100UL, "Trade", "item", "trade")
			}),
			OperationResult = new MobileConfirmationResult(false, null)
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["operation"] = "CANCEL" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("cancel", result.Output!["operation"]);
		Assert.Equal(1, result.Output["failed"]);
		var entry = Assert.Single((List<Dictionary<string, object?>>)result.Output["results"]!);
		Assert.Equal("trade", entry["type"]);
	}

	[Fact]
	public async Task ConfirmAll_BlankTypeFilter_BehavesLikeAll()
	{
		var store = new StubCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var stub = new StubMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(1UL, 11UL, 100UL, "Trade", "item", "trade"),
				new TradeConfirmation(2UL, 22UL, 101UL, "Market", "sale", "market")
			})
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => stub);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["type"] = "  ALL  " },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total"]);
		Assert.Equal(2, stub.RespondCalls);
	}

	// --- test doubles ---

	private sealed class FixedTimeProvider : TimeProvider
	{
		private readonly DateTimeOffset _now;

		public FixedTimeProvider(DateTimeOffset now) => _now = now;

		public override DateTimeOffset GetUtcNow() => _now;
	}

	private sealed class StubMobileConfirmationClient : IMobileConfirmationClient
	{
		public MobileConfirmationListResult ListResult { get; set; } = new(true, null, []);
		public Exception? ListException { get; set; }
		public Action? OnList { get; set; }
		public MobileConfirmationResult OperationResult { get; set; } = new(true);
		public Exception? RespondException { get; set; }
		public Action? OnRespond { get; set; }
		public int ListCalls { get; private set; }
		public int RespondCalls { get; private set; }
		public string? LastIdentitySecret { get; private set; }
		public (ulong Id, ulong Nonce, ConfirmationOperation Op) LastRespond { get; private set; }

		public Task<MobileConfirmationListResult> GetConfirmationsAsync(string identitySecret, CancellationToken cancellationToken)
		{
			LastIdentitySecret = identitySecret;
			ListCalls++;
			OnList?.Invoke();
			if (ListException is not null)
			{
				throw ListException;
			}

			return Task.FromResult(ListResult);
		}

		public Task<MobileConfirmationResult> RespondAsync(
				string identitySecret,
				ulong confirmationId,
				ulong nonce,
				ConfirmationOperation operation,
				CancellationToken cancellationToken)
		{
			LastIdentitySecret = identitySecret;
			RespondCalls++;
			OnRespond?.Invoke();
			if (RespondException is not null)
			{
				throw RespondException;
			}

			LastRespond = (confirmationId, nonce, operation);
			return Task.FromResult(OperationResult);
		}
	}

	/// <summary>In-memory credential store with optional failure hooks for the save paths.</summary>
	private sealed class StubCredentialStore : ICredentialStore
	{
		public Dictionary<string, string> IdentitySecrets { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Exception? OnSaveSharedSecret { get; set; }
		public Exception? OnSaveIdentitySecret { get; set; }

		public Task SaveRefreshTokenAsync(string accountName, string refreshToken, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<string?> GetRefreshTokenAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);

		public Task SaveAccessTokenAsync(string accountName, StoredAccessToken accessToken, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<StoredAccessToken?> GetAccessTokenAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.FromResult<StoredAccessToken?>(null);

		public Task RevokeCredentialsAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<bool> HasCredentialsAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.FromResult(false);

		public Task SaveSharedSecretAsync(string accountName, string sharedSecret, CancellationToken cancellationToken = default)
			=> OnSaveSharedSecret is not null ? Task.FromException(OnSaveSharedSecret) : Task.CompletedTask;

		public Task<string?> GetSharedSecretAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);

		public Task SaveIdentitySecretAsync(string accountName, string identitySecret, CancellationToken cancellationToken = default)
		{
			if (OnSaveIdentitySecret is not null)
			{
				return Task.FromException(OnSaveIdentitySecret);
			}

			IdentitySecrets[accountName] = identitySecret;
			return Task.CompletedTask;
		}

		public Task<string?> GetIdentitySecretAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.FromResult(IdentitySecrets.TryGetValue(accountName, out string? secret) ? secret : null);
		public Task SaveProxyAsync(string accountName, string? proxy, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;

		public Task<string?> GetProxyAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.FromResult<string?>(null);
	}
}
