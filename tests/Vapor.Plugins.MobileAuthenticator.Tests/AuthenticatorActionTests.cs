using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Plugins.MobileAuthenticator;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

namespace Vapor.Plugins.MobileAuthenticator.Tests;

public class AuthenticatorActionTests
{
	private const string SharedSecret = "MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=";
	private const string IdentitySecret = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

	private static SteamTimeSynchronizer CreateSynchronizer(long? serverTime = null) =>
		new(_ => Task.FromResult(serverTime ?? 0L), new FixedTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));

	[Fact]
	public async Task GenerateTotp_WithExplicitTime_ReturnsKnownCode()
	{
		var action = new GenerateTotpAction(CreateSynchronizer());
		var payload = new Dictionary<string, object?> { ["shared_secret"] = SharedSecret, ["time"] = 59L };

		var result = await action.ExecuteAsync(null!, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("PV9M4", result.Output!["code"]);
		Assert.Equal(1, result.Output["seconds_remaining"]);
		Assert.False((bool)result.Output["time_synced"]!);
	}

	[Fact]
	public async Task GenerateTotp_MissingSecret_Fails()
	{
		var action = new GenerateTotpAction(CreateSynchronizer());

		var result = await action.ExecuteAsync(null!, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("shared_secret", result.Error);
	}

	[Fact]
	public async Task GenerateTotp_InvalidSecret_Fails()
	{
		var action = new GenerateTotpAction(CreateSynchronizer());
		var payload = new Dictionary<string, object?> { ["shared_secret"] = "!!bad!!" };

		var result = await action.ExecuteAsync(null!, payload, CancellationToken.None);

		Assert.False(result.Success);
	}

	[Fact]
	public async Task GenerateTotp_UsesSynchronizedTime()
	{
		var local = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var synchronizer = new SteamTimeSynchronizer(
			_ => Task.FromResult(59L),
			new FixedTimeProvider(local));
		await synchronizer.SyncAsync();

		var action = new GenerateTotpAction(synchronizer);
		var payload = new Dictionary<string, object?> { ["sharedSecret"] = SharedSecret };

		var result = await action.ExecuteAsync(null!, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("PV9M4", result.Output!["code"]);
		Assert.True((bool)result.Output["time_synced"]!);
	}

	[Fact]
	public async Task GenerateConfirmationHash_ReturnsKnownHash()
	{
		var action = new GenerateConfirmationHashAction(CreateSynchronizer());
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["tag"] = "allow",
			["time"] = 1610000000L
		};

		var result = await action.ExecuteAsync(null!, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("zJsKoS9LykHHgQG+K6g0aHCd45o=", result.Output!["hash"]);
		Assert.Equal("allow", result.Output["tag"]);
	}

	[Fact]
	public async Task GenerateConfirmationHash_DefaultsToConfTag()
	{
		var action = new GenerateConfirmationHashAction(CreateSynchronizer());
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret, ["time"] = 1610000000L };

		var result = await action.ExecuteAsync(null!, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("conf", result.Output!["tag"]);
	}

	[Fact]
	public async Task GenerateConfirmationHash_UnknownTag_Fails()
	{
		var action = new GenerateConfirmationHashAction(CreateSynchronizer());
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret, ["tag"] = "bogus" };

		var result = await action.ExecuteAsync(null!, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("tag", result.Error);
	}

	[Fact]
	public async Task GenerateConfirmationHash_MissingSecret_Fails()
	{
		var action = new GenerateConfirmationHashAction(CreateSynchronizer());

		var result = await action.ExecuteAsync(null!, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("identity_secret", result.Error);
	}

	[Fact]
	public async Task SyncSteamTime_Succeeds()
	{
		var local = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromResult(local.ToUnixTimeSeconds() + 5), new FixedTimeProvider(local));
		var action = new SyncSteamTimeAction(synchronizer, NullLogger<SyncSteamTimeAction>.Instance);

		var result = await action.ExecuteAsync(null!, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(5L, result.Output!["offset_seconds"]);
		Assert.True(synchronizer.HasSynced);
	}

	[Fact]
	public async Task SyncSteamTime_QueryFailure_ReturnsError()
	{
		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromException<long>(new HttpRequestException("down")));
		var action = new SyncSteamTimeAction(synchronizer, NullLogger<SyncSteamTimeAction>.Instance);

		var result = await action.ExecuteAsync(null!, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("Steam time sync failed", result.Error);
	}

	[Fact]
	public async Task SaveSharedSecret_StoresSecretForSessionAccount()
	{
		var store = new FakeCredentialStore();
		var action = new SaveSharedSecretAction(NullLogger<SaveSharedSecretAction>.Instance, store);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["shared_secret"] = "MTIzNDU2" }, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("MTIzNDU2", store.Secrets["test_account"]);
		Assert.True((bool)result.Output!["stored"]!);
	}

	[Fact]
	public async Task SaveSharedSecret_MissingPayload_Fails()
	{
		var action = new SaveSharedSecretAction(NullLogger<SaveSharedSecretAction>.Instance, new FakeCredentialStore());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("shared_secret", result.Error);
	}

	[Fact]
	public async Task SaveSharedSecret_UnavailableCredentialStore_Fails()
	{
		var action = new SaveSharedSecretAction(NullLogger<SaveSharedSecretAction>.Instance, credentialStore: null);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?> { ["shared_secret"] = "MTIzNDU2" }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("credential store", result.Error);
	}

	[Fact]
	public async Task GetTradeConfirmations_ReturnsParsedConfirmations()
	{
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(123UL, 456UL, 789UL, "Trade with someone", "You give: item")
			})
		};

		var action = new GetTradeConfirmationsAction(NullLogger<GetTradeConfirmationsAction>.Instance, _ => fakeClient);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["count"]);
		Assert.Equal(IdentitySecret, fakeClient.LastIdentitySecret);
	}

	[Fact]
	public async Task GetTradeConfirmations_MissingSecret_Fails()
	{
		var action = new GetTradeConfirmationsAction(NullLogger<GetTradeConfirmationsAction>.Instance, _ => new FakeMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("identity_secret", result.Error);
	}

	[Fact]
	public async Task GetTradeConfirmations_NoWebHandler_Fails()
	{
		var action = new GetTradeConfirmationsAction(NullLogger<GetTradeConfirmationsAction>.Instance, _ => new FakeMobileConfirmationClient());
		using var session = TestSession.Create(withWebHandler: false);
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error);
	}

	[Fact]
	public async Task GetTradeConfirmations_ClientFailure_PropagatesError()
	{
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(false, "Steam rejected the confirmation list request")
		};

		var action = new GetTradeConfirmationsAction(NullLogger<GetTradeConfirmationsAction>.Instance, _ => fakeClient);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rejected", result.Error);
	}

	[Fact]
	public async Task RespondTradeConfirmation_Allow_Succeeds()
	{
		var fakeClient = new FakeMobileConfirmationClient { OperationResult = new MobileConfirmationResult(true) };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => fakeClient);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = "123",
			["nonce"] = "456",
			["operation"] = "allow"
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("allow", result.Output!["operation"]);
		Assert.Equal((123UL, 456UL, ConfirmationOperation.Allow), fakeClient.LastRespond);
	}

	[Fact]
	public async Task RespondTradeConfirmation_Cancel_Succeeds()
	{
		var fakeClient = new FakeMobileConfirmationClient { OperationResult = new MobileConfirmationResult(true) };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => fakeClient);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = 123L,
			["nonce"] = 456L,
			["operation"] = "cancel"
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal((123UL, 456UL, ConfirmationOperation.Cancel), fakeClient.LastRespond);
	}

	[Theory]
	[InlineData("bogus")]
	public async Task RespondTradeConfirmation_InvalidOperation_Fails(string operation)
	{
		var fakeClient = new FakeMobileConfirmationClient { OperationResult = new MobileConfirmationResult(true) };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => fakeClient);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = "123",
			["nonce"] = "456",
			["operation"] = operation
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("operation", result.Error);
	}

	[Fact]
	public async Task RespondTradeConfirmation_MissingConfirmationId_Fails()
	{
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => new FakeMobileConfirmationClient());
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret, ["nonce"] = "456" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("confirmation_id", result.Error);
	}

	[Fact]
	public async Task RespondTradeConfirmation_ClientFailure_PropagatesError()
	{
		var fakeClient = new FakeMobileConfirmationClient { OperationResult = new MobileConfirmationResult(false, "bad confirmation") };
		var action = new RespondTradeConfirmationAction(NullLogger<RespondTradeConfirmationAction>.Instance, _ => fakeClient);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?>
		{
			["identity_secret"] = IdentitySecret,
			["confirmation_id"] = "123",
			["nonce"] = "456"
		};

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("bad confirmation", result.Error);
	}

	[Fact]
	public async Task SaveIdentitySecret_StoresSecretForSessionAccount()
	{
		var store = new FakeCredentialStore();
		var action = new SaveIdentitySecretAction(NullLogger<SaveIdentitySecretAction>.Instance, store);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(IdentitySecret, store.IdentitySecrets["test_account"]);
	}

	[Fact]
	public async Task SaveIdentitySecret_MissingPayload_Fails()
	{
		var action = new SaveIdentitySecretAction(NullLogger<SaveIdentitySecretAction>.Instance, new FakeCredentialStore());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("identity_secret", result.Error);
	}

	[Fact]
	public async Task SaveIdentitySecret_UnavailableCredentialStore_Fails()
	{
		var action = new SaveIdentitySecretAction(NullLogger<SaveIdentitySecretAction>.Instance, credentialStore: null);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["identity_secret"] = IdentitySecret },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("credential store", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_Allow_Succeeds()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(111UL, 222UL, 43591234567890UL, "Trade with someone", "You give: item")
			}),
			OperationResult = new MobileConfirmationResult(true)
		};
		var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();
		var payload = new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890" };

		var result = await action.ExecuteAsync(session, payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("43591234567890", result.Output!["trade_offer_id"]);
		Assert.Equal("111", result.Output["confirmation_id"]);
		Assert.Equal(true, result.Output["confirmed"]);
		Assert.Equal(IdentitySecret, fakeClient.LastIdentitySecret);
		Assert.Equal((111UL, 222UL, ConfirmationOperation.Allow), fakeClient.LastRespond);
	}

	[Fact]
	public async Task ConfirmTradeOffer_Cancel_Succeeds()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(111UL, 222UL, 43591234567890UL, "Trade with someone", "You give: item")
			})
		};
		var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890", ["operation"] = "cancel" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("cancel", result.Output!["operation"]);
		Assert.Equal(false, result.Output["confirmed"]);
		Assert.Equal((111UL, 222UL, ConfirmationOperation.Cancel), fakeClient.LastRespond);
	}

	[Fact]
	public async Task ConfirmTradeOffer_MatchesAfterRetry()
	{
		ConfirmTradeOfferAction.MatchPollInterval = TimeSpan.FromMilliseconds(1);
		try
		{
			var store = new FakeCredentialStore();
			await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
			var fakeClient = new FakeMobileConfirmationClient
			{
				ListResultQueue = new Queue<MobileConfirmationListResult>(new[]
				{
					new MobileConfirmationListResult(true, null, Array.Empty<TradeConfirmation>()),
					new MobileConfirmationListResult(true, null, new[] { new TradeConfirmation(55UL, 66UL, 43591234567890UL, null, null) })
				}),
				OperationResult = new MobileConfirmationResult(true)
			};
			var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, store, _ => fakeClient);
			using var session = TestSession.Create();

			var result = await action.ExecuteAsync(
				session,
				new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890" },
				CancellationToken.None);

			Assert.True(result.Success);
			Assert.Equal("55", result.Output!["confirmation_id"]);
			Assert.Equal(2, fakeClient.ListCalls);
		}
		finally
		{
			ConfirmTradeOfferAction.MatchPollInterval = TimeSpan.FromMilliseconds(250);
		}
	}

	[Fact]
	public async Task ConfirmTradeOffer_NoMatchAfterPolling_Fails()
	{
		ConfirmTradeOfferAction.MatchPollInterval = TimeSpan.FromMilliseconds(1);
		ConfirmTradeOfferAction.MatchPollAttempts = 3;
		try
		{
			var store = new FakeCredentialStore();
			await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
			var fakeClient = new FakeMobileConfirmationClient();
			var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, store, _ => fakeClient);
			using var session = TestSession.Create();

			var result = await action.ExecuteAsync(
				session,
				new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890" },
				CancellationToken.None);

			Assert.False(result.Success);
			Assert.Contains("no pending mobile confirmation", result.Error);
			Assert.Equal(3, fakeClient.ListCalls);
		}
		finally
		{
			ConfirmTradeOfferAction.MatchPollInterval = TimeSpan.FromMilliseconds(250);
			ConfirmTradeOfferAction.MatchPollAttempts = 6;
		}
	}

	[Fact]
	public async Task ConfirmTradeOffer_NoStoredSecret_Fails()
	{
		var fakeClient = new FakeMobileConfirmationClient();
		var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, new FakeCredentialStore(), _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("save_identity_secret", result.Error);
		Assert.Equal(0, fakeClient.ListCalls);
	}

	[Fact]
	public async Task ConfirmTradeOffer_InvalidTradeOfferId_Fails()
	{
		var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, new FakeCredentialStore(), _ => new FakeMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("trade_offer_id", result.Error);
	}

	[Fact]
	public async Task ConfirmTradeOffer_NoWebHandler_Fails()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var action = new ConfirmTradeOfferAction(NullLogger<ConfirmTradeOfferAction>.Instance, store, _ => new FakeMobileConfirmationClient());
		using var session = TestSession.Create(withWebHandler: false);

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["trade_offer_id"] = "43591234567890" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("web handler", result.Error);
	}

	[Fact]
	public async Task ConfirmAll_Allow_ConfirmsEverything()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(1UL, 11UL, 100UL, "Trade", "item", "trade"),
				new TradeConfirmation(2UL, 22UL, 101UL, "Market", "sale", "market"),
				new TradeConfirmation(3UL, 33UL, 102UL, "Trade2", "item", "trade")
			})
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("allow", result.Output!["operation"]);
		Assert.Equal(3, result.Output["total"]);
		Assert.Equal(3, result.Output["succeeded"]);
		Assert.Equal(0, result.Output["failed"]);
		Assert.Equal(3, fakeClient.RespondCalls);
		Assert.Equal([(1UL, 11UL, ConfirmationOperation.Allow), (2UL, 22UL, ConfirmationOperation.Allow), (3UL, 33UL, ConfirmationOperation.Allow)], fakeClient.Responds);
		Assert.Equal(IdentitySecret, fakeClient.LastIdentitySecret);
	}

	[Fact]
	public async Task ConfirmAll_TypeFilter_OnlyRespondsToMatchingType()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(1UL, 11UL, 100UL, "Trade", "item", "trade"),
				new TradeConfirmation(2UL, 22UL, 101UL, "Market", "sale", "market"),
				new TradeConfirmation(3UL, 33UL, 102UL, "Trade2", "item", "trade")
			})
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["type"] = "market" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(1, result.Output!["total"]);
		Assert.Equal(1, result.Output["succeeded"]);
		Assert.Single((List<Dictionary<string, object?>>)result.Output["results"]!, r => Equals(r["confirmation_id"], "2"));
		Assert.Equal([(2UL, 22UL, ConfirmationOperation.Allow)], fakeClient.Responds);
	}

	[Fact]
	public async Task ConfirmAll_PartialFailure_ReportsEachItem()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(1UL, 11UL, 100UL, "Trade", "item", "trade"),
				new TradeConfirmation(2UL, 22UL, 101UL, "Market", "sale", "market")
			}),
			OperationResults =
			{
				[2UL] = new MobileConfirmationResult(false, "Steam rejected the operation")
			}
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		// The batch itself succeeds — per-item failures are reported in the output.
		Assert.True(result.Success);
		Assert.Equal(2, result.Output!["total"]);
		Assert.Equal(1, result.Output["succeeded"]);
		Assert.Equal(1, result.Output["failed"]);

		var results = (List<Dictionary<string, object?>>)result.Output["results"]!;
		var failed = Assert.Single(results, r => Equals(r["succeeded"], false));
		Assert.Equal("2", failed["confirmation_id"]);
		Assert.Equal("Steam rejected the operation", failed["error"]);
	}

	[Fact]
	public async Task ConfirmAll_Cancel_UsesCancelOperation()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(true, null, new[]
			{
				new TradeConfirmation(7UL, 77UL, 107UL, "Trade", "item", "trade")
			})
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["operation"] = "cancel" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal("cancel", result.Output!["operation"]);
		Assert.Equal([(7UL, 77UL, ConfirmationOperation.Cancel)], fakeClient.Responds);
	}

	[Fact]
	public async Task ConfirmAll_EmptyList_SucceedsWithZeroTotals()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient();
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Equal(0, result.Output!["total"]);
		Assert.Equal(0, result.Output["succeeded"]);
		Assert.Equal(0, fakeClient.RespondCalls);
	}

	[Fact]
	public async Task ConfirmAll_ListingFailure_Fails()
	{
		var store = new FakeCredentialStore();
		await store.SaveIdentitySecretAsync("test_account", IdentitySecret);
		var fakeClient = new FakeMobileConfirmationClient
		{
			ListResult = new MobileConfirmationListResult(false, "Steam rejected the confirmation list request")
		};
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, store, _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("rejected the confirmation list", result.Error);
		Assert.Equal(0, fakeClient.RespondCalls);
	}

	[Fact]
	public async Task ConfirmAll_NoStoredSecret_Fails()
	{
		var fakeClient = new FakeMobileConfirmationClient();
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, new FakeCredentialStore(), _ => fakeClient);
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("save_identity_secret", result.Error);
		Assert.Equal(0, fakeClient.ListCalls);
	}

	[Fact]
	public async Task ConfirmAll_InvalidOperation_Fails()
	{
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, new FakeCredentialStore(), _ => new FakeMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["operation"] = "bogus" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("operation must be 'allow' or 'cancel'", result.Error);
	}

	[Fact]
	public async Task ConfirmAll_InvalidType_Fails()
	{
		var action = new ConfirmAllConfirmationsAction(NullLogger<ConfirmAllConfirmationsAction>.Instance, new FakeCredentialStore(), _ => new FakeMobileConfirmationClient());
		using var session = TestSession.Create();

		var result = await action.ExecuteAsync(
			session,
			new Dictionary<string, object?> { ["type"] = "bogus" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("type must be 'all', 'trade' or 'market'", result.Error);
	}

	private sealed class FixedTimeProvider : TimeProvider
	{
		private readonly DateTimeOffset _now;

		public FixedTimeProvider(DateTimeOffset now) => _now = now;

		public override DateTimeOffset GetUtcNow() => _now;
	}

	private sealed class FakeMobileConfirmationClient : IMobileConfirmationClient
	{
		public MobileConfirmationListResult ListResult { get; set; } = new(true, null, []);
		public Queue<MobileConfirmationListResult>? ListResultQueue { get; set; }
		public int ListCalls { get; private set; }
		public MobileConfirmationResult OperationResult { get; set; } = new(true);
		public Dictionary<ulong, MobileConfirmationResult> OperationResults { get; } = new();
		public int RespondCalls { get; private set; }
		public List<(ulong Id, ulong Nonce, ConfirmationOperation Op)> Responds { get; } = new();
		public string? LastIdentitySecret { get; private set; }
		public (ulong Id, ulong Nonce, ConfirmationOperation Op) LastRespond { get; private set; }

		public Task<MobileConfirmationListResult> GetConfirmationsAsync(string identitySecret, CancellationToken cancellationToken)
		{
			LastIdentitySecret = identitySecret;
			ListCalls++;
			return Task.FromResult(ListResultQueue is { Count: > 0 } ? ListResultQueue.Dequeue() : ListResult);
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
			Responds.Add((confirmationId, nonce, operation));
			LastRespond = (confirmationId, nonce, operation);
			return Task.FromResult(OperationResults.TryGetValue(confirmationId, out var result) ? result : OperationResult);
		}
	}

	private sealed class FakeCredentialStore : ICredentialStore
	{
		public Dictionary<string, string> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, string> IdentitySecrets { get; } = new(StringComparer.OrdinalIgnoreCase);

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
		{
			Secrets[accountName] = sharedSecret;
			return Task.CompletedTask;
		}

		public Task<string?> GetSharedSecretAsync(string accountName, CancellationToken cancellationToken = default)
			=> Task.FromResult(Secrets.TryGetValue(accountName, out string? secret) ? secret : null);

		public Task SaveIdentitySecretAsync(string accountName, string identitySecret, CancellationToken cancellationToken = default)
		{
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
