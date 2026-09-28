using System.Text.Json;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

// Joins the wait-window collection: these tests retune the static
// StorageCollectRunner.WaitWindow/PollInterval knobs exactly like
// AccountApiTests tunes the AccountTaskRunner ones (see the collection's note
// — interleaved classes can read each other's shrunk windows mid-wait).
[Collection(AccountTaskWaitWindowCollection.Name)]
public sealed class StorageCollectRunnerTests
{
	private const string StorageSteamId = "76561197960265728";

	[Fact]
	public void SelectFarmSources_KeepsOnlyEnabledNonOfflineFarms()
	{
		var sources = StorageCollectRunner.SelectFarmSources(
		[
			new AccountSpec("plain", Enabled: true, AccountDesiredState.Online),
			new AccountSpec("farm-idle", Enabled: true, AccountDesiredState.Farm),
			new AccountSpec("disabled", Enabled: false, AccountDesiredState.Online),
			new AccountSpec("logged-out", Enabled: true, AccountDesiredState.Offline),
			new AccountSpec("warehouse", Enabled: true, AccountDesiredState.Offline, Role: AccountRole.Storage, SteamId: StorageSteamId)
		]);

		// An Offline-declared account is deliberately logged out — there is
		// nothing to loot from it; storage accounts are targets, not sources.
		Assert.Equal(new[] { "plain", "farm-idle" }, sources.Select(a => a.AccountName).ToArray());
	}

	[Fact]
	public void OutputReaders_AcceptPrimitiveAndElementValues()
	{
		// The SQL store round-trip always hands back JsonElement values, so the
		// in-memory primitive arms are covered here by direct invocation.
		Dictionary<string, object?> all = new()
		{
			["boolTrue"] = true,
			["boolFalse"] = false,
			["str"] = "abc",
			["int"] = 5,
			["longSmall"] = 7L,
			["longHuge"] = (long)int.MaxValue + 1,
			["nullValue"] = null
		};

		Assert.True(StorageCollectRunner.OutputFlag(all, "boolTrue"));
		Assert.False(StorageCollectRunner.OutputFlag(all, "boolFalse"));
		Assert.True(StorageCollectRunner.OutputFlag(
			new Dictionary<string, object?> { ["flag"] = JsonSerializer.SerializeToElement(true) }, "flag"));
		Assert.False(StorageCollectRunner.OutputFlag(
			new Dictionary<string, object?> { ["flag"] = JsonSerializer.SerializeToElement(false) }, "flag"));
		Assert.True(StorageCollectRunner.OutputFlag(
			new Dictionary<string, object?> { ["flag"] = JsonSerializer.SerializeToElement("true") }, "flag"));
		Assert.False(StorageCollectRunner.OutputFlag(
			new Dictionary<string, object?> { ["flag"] = JsonSerializer.SerializeToElement("yes") }, "flag"));
		Assert.False(StorageCollectRunner.OutputFlag(
			new Dictionary<string, object?> { ["flag"] = JsonSerializer.SerializeToElement(1) }, "flag"));
		Assert.False(StorageCollectRunner.OutputFlag(all, "missing"));
		Assert.False(StorageCollectRunner.OutputFlag(all, "nullValue"));
		Assert.False(StorageCollectRunner.OutputFlag(null!, "flag"));

		Assert.Equal("abc", StorageCollectRunner.OutputString(all, "str"));
		Assert.Equal("x", StorageCollectRunner.OutputString(
			new Dictionary<string, object?> { ["k"] = JsonSerializer.SerializeToElement("x") }, "k"));
		Assert.Equal("5", StorageCollectRunner.OutputString(all, "int"));
		Assert.Null(StorageCollectRunner.OutputString(all, "missing"));
		Assert.Null(StorageCollectRunner.OutputString(all, "nullValue"));
		Assert.Null(StorageCollectRunner.OutputString(null!, "k"));

		Assert.Equal(5, StorageCollectRunner.OutputInt(all, "int"));
		Assert.Equal(7, StorageCollectRunner.OutputInt(all, "longSmall"));
		Assert.Null(StorageCollectRunner.OutputInt(all, "longHuge"));
		Assert.Equal(42, StorageCollectRunner.OutputInt(
			new Dictionary<string, object?> { ["n"] = JsonSerializer.SerializeToElement(42) }, "n"));
		Assert.Null(StorageCollectRunner.OutputInt(
			new Dictionary<string, object?> { ["n"] = JsonSerializer.SerializeToElement(1.5) }, "n"));
		Assert.Null(StorageCollectRunner.OutputInt(all, "str"));
		Assert.Null(StorageCollectRunner.OutputInt(all, "nullValue"));
		Assert.Null(StorageCollectRunner.OutputInt(all, "missing"));
		Assert.Null(StorageCollectRunner.OutputInt(null!, "n"));
	}

	[Fact]
	public async Task WaitTerminalAsync_MissingTaskRow_NeverCountsAsTerminal()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(200);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			using var store = new SqliteJobStore(":memory:");
			JobWithTasks job = await store.CreateJob(
				new CreateJobRequest("ping", null, ["alice"], null, null), CancellationToken.None);

			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			_ = Task.Run(async () =>
			{
				JobTask? claimed = await store.ClaimNextQueuedTask("us-east", cts.Token);
				await store.SetTaskResult(
					new TaskResult(claimed!.Id, true, null, null, DateTimeOffset.UtcNow), cts.Token);
			}, CancellationToken.None);

			Dictionary<string, JobTask>? state = await StorageCollectRunner.WaitTerminalAsync(
				store, [(job.Job.Id, "ghost"), (job.Job.Id, "alice")], cts.Token);

			// A watch with no task row can never settle, so the wait runs to the
			// window close and reports null — it must not report partial state.
			Assert.Null(state);
		}
		finally
		{
			RestoreKnobs();
		}
	}

	[Fact]
	public async Task WaitTerminalAsync_WindowClosesWithTasksInFlight_ReturnsNull()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(150);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			using var store = new SqliteJobStore(":memory:");
			JobWithTasks job = await store.CreateJob(
				new CreateJobRequest("ping", null, ["alice"], null, null), CancellationToken.None);

			Dictionary<string, JobTask>? state = await StorageCollectRunner.WaitTerminalAsync(
				store, [(job.Job.Id, "alice")], CancellationToken.None);

			// The task stays queued forever — the window must close, not spin.
			Assert.Null(state);
		}
		finally
		{
			RestoreKnobs();
		}
	}

	[Fact]
	public async Task WaitTerminalAsync_CanceledTaskCountsAsTerminal()
	{
		using var store = new SqliteJobStore(":memory:");
		JobWithTasks job = await store.CreateJob(
			new CreateJobRequest("ping", null, ["alice"], null, null), CancellationToken.None);
		await store.CancelJob(job.Job.Id, CancellationToken.None);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		Dictionary<string, JobTask>? state = await StorageCollectRunner.WaitTerminalAsync(
			store, [(job.Job.Id, "alice")], cts.Token);

		Assert.NotNull(state);
		Assert.Equal(JobTaskStatus.Canceled, state!["alice"].Status);
	}

	[Fact]
	public async Task DispatchCollectAsync_WindowClosesBeforeLootSettles_ReturnsPendingRun()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(150);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			using var store = new SqliteJobStore(":memory:");

			CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
				store, StorageSpec(), [Farm("alice")], null, null, CancellationToken.None);

			// No agent answers: the run stays observable via the job id and the
			// next collect simply re-loots (offers expire, items stay put).
			Assert.False(run.Completed);
			Assert.Empty(run.Tasks);
		}
		finally
		{
			RestoreKnobs();
		}
	}

	[Fact]
	public async Task DispatchCollectAsync_HappyPath_ChainsConfirmationsPerSource()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Task responder = Task.Run(
			() => RespondCollectAsync(
				store,
				new Dictionary<string, LootOutcome>(StringComparer.OrdinalIgnoreCase)
				{
					["alice"] = LootOutcome.Ok(3, "offer-alice", requiresConfirmation: true),
					["bob"] = LootOutcome.Ok(3, "offer-bob", requiresConfirmation: true)
				},
				ConfirmBehavior.Answer,
				cts.Token),
			CancellationToken.None);

		CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
			store, StorageSpec(), [Farm("alice"), Farm("bob")], "batch move", [753], cts.Token);

		Assert.True(run.Completed);
		Assert.Equal(2, run.Tasks.Count);
		foreach (string target in new[] { "alice", "bob" })
		{
			CollectTaskResult result = run.Tasks.Single(t => t.Account == target);
			Assert.Equal(JobTaskStatus.Finished, result.Status);
			Assert.Null(result.Error);
			Assert.Equal($"offer-{target}", result.TradeOfferId);
			Assert.Equal(3, result.ItemCount);
			Assert.True(result.Confirmed);
			Assert.NotEmpty(result.ConfirmJobId!);
			Assert.Null(result.ConfirmError);
		}

		// The loot task carries the storage partner address declared on the spec.
		string lootJobId = (await store.ListJobs(50, null, cts.Token))
			.Single(j => j.Action == AccountTaskRunner.LootInventoryAction).Id;
		JobWithTasks lootJob = await store.GetJob(lootJobId, cts.Token);
		Assert.Equal(StorageSteamId, PayloadValue(lootJob.Tasks[0].Payload, "partner_steam_id"));
		Assert.Equal("batch move", PayloadValue(lootJob.Tasks[0].Payload, "message"));
		Assert.Contains("753", PayloadValue(lootJob.Tasks[0].Payload, "app_ids"), StringComparison.Ordinal);

		// Each confirmation is its own auditable job, linked back to the run.
		List<Job> confirmJobs = (await store.ListJobs(50, null, cts.Token))
			.Where(j => j.Action == AccountTaskRunner.ConfirmTradeOfferAction)
			.ToList();
		Assert.Equal(2, confirmJobs.Count);
		foreach (Job confirmJob in confirmJobs)
		{
			Assert.Equal(lootJobId, confirmJob.Meta!["parent_job"]);
			Assert.Equal(StorageCollectRunner.CollectOrigin, confirmJob.Meta["origin"]);
			Assert.Equal("warehouse", confirmJob.Meta["storage"]);
		}
	}

	[Fact]
	public async Task DispatchCollectAsync_MixedOutcomes_ConfirmOnlyFinishedSourcesWithOffers()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Task responder = Task.Run(
			() => RespondCollectAsync(
				store,
				new Dictionary<string, LootOutcome>(StringComparer.OrdinalIgnoreCase)
				{
					["alice"] = LootOutcome.Ok(2, "offer-alice", requiresConfirmation: true),
					["bob"] = LootOutcome.Ok(0, null, requiresConfirmation: false),
					["carol"] = LootOutcome.Failed("nothing tradable to loot")
				},
				ConfirmBehavior.Answer,
				cts.Token),
			CancellationToken.None);

		CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
			store, StorageSpec(), [Farm("alice"), Farm("bob"), Farm("carol")], null, null, cts.Token);

		Assert.True(run.Completed);
		CollectTaskResult alice = run.Tasks.Single(t => t.Account == "alice");
		Assert.True(alice.Confirmed);

		// Nothing to confirm: no confirm job, no confirmed value.
		CollectTaskResult bob = run.Tasks.Single(t => t.Account == "bob");
		Assert.Equal(JobTaskStatus.Finished, bob.Status);
		Assert.Null(bob.Confirmed);
		Assert.Null(bob.ConfirmJobId);

		// A failed source is reported per-account, never as a blanket failure.
		CollectTaskResult carol = run.Tasks.Single(t => t.Account == "carol");
		Assert.Equal(JobTaskStatus.Failed, carol.Status);
		Assert.Equal("nothing tradable to loot", carol.Error);
		Assert.Null(carol.Confirmed);
	}

	[Fact]
	public async Task DispatchCollectAsync_RequiredConfirmationWithoutOfferId_SkipsConfirmJob()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Task responder = Task.Run(
			() => RespondCollectAsync(
				store,
				new Dictionary<string, LootOutcome>(StringComparer.OrdinalIgnoreCase)
				{
					// Steam wants a confirmation but the output lost the offer id —
					// there is nothing addressable to confirm, so none is chained.
					["alice"] = LootOutcome.Ok(1, null, requiresConfirmation: true)
				},
				ConfirmBehavior.Answer,
				cts.Token),
			CancellationToken.None);

		CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
			store, StorageSpec(), [Farm("alice")], null, null, cts.Token);

		Assert.True(run.Completed);
		CollectTaskResult alice = Assert.Single(run.Tasks);
		Assert.Null(alice.TradeOfferId);
		Assert.Null(alice.Confirmed);
		Assert.Null(alice.ConfirmJobId);
	}

	[Fact]
	public async Task DispatchCollectAsync_ConfirmWindowCloses_LootStillReportsCompleted()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(600);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(25);
		try
		{
			using var store = new SqliteJobStore(":memory:");
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			Task responder = Task.Run(
				() => RespondCollectAsync(
					store,
					new Dictionary<string, LootOutcome>(StringComparer.OrdinalIgnoreCase)
					{
						["alice"] = LootOutcome.Ok(2, "offer-alice", requiresConfirmation: true)
					},
					ConfirmBehavior.Ignore,
					cts.Token),
				CancellationToken.None);

			CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
				store, StorageSpec(), [Farm("alice")], null, null, cts.Token);

			// The loot leg settled inside the window; its confirmation is still
			// in flight, so the run completes with the confirm leg unreported.
			Assert.True(run.Completed);
			CollectTaskResult alice = Assert.Single(run.Tasks);
			Assert.Null(alice.Confirmed);
			Assert.Null(alice.ConfirmJobId);
		}
		finally
		{
			RestoreKnobs();
		}
	}

	[Fact]
	public async Task DispatchCollectAsync_ConfirmFailure_IsReportedNotSilent()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Task responder = Task.Run(
			() => RespondCollectAsync(
				store,
				new Dictionary<string, LootOutcome>(StringComparer.OrdinalIgnoreCase)
				{
					["alice"] = LootOutcome.Ok(2, "offer-alice", requiresConfirmation: true)
				},
				ConfirmBehavior.Fail,
				cts.Token),
			CancellationToken.None);

		CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
			store, StorageSpec(), [Farm("alice")], null, null, cts.Token);

		Assert.True(run.Completed);
		CollectTaskResult alice = Assert.Single(run.Tasks);
		Assert.False(alice.Confirmed);
		Assert.Equal("steam rejected the confirmation", alice.ConfirmError);
	}

	[Fact]
	public async Task DispatchCollectAsync_ConfirmCanceled_DerivesTheOutcomeFromStatus()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Task responder = Task.Run(
			() => RespondCollectAsync(
				store,
				new Dictionary<string, LootOutcome>(StringComparer.OrdinalIgnoreCase)
				{
					["alice"] = LootOutcome.Ok(2, "offer-alice", requiresConfirmation: true)
				},
				ConfirmBehavior.Cancel,
				cts.Token),
			CancellationToken.None);

		CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
			store, StorageSpec(), [Farm("alice")], null, null, cts.Token);

		Assert.True(run.Completed);
		CollectTaskResult alice = Assert.Single(run.Tasks);
		Assert.False(alice.Confirmed);
		Assert.Equal($"confirmation ended as {JobTaskStatus.Canceled}", alice.ConfirmError);
	}

	[Fact]
	public async Task DispatchCollectAsync_ConfirmationSettledWithoutConfirmation_ReportsNotConfirmed()
	{
		using var store = new SqliteJobStore(":memory:");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		Task responder = Task.Run(
			() => RespondCollectAsync(
				store,
				new Dictionary<string, LootOutcome>(StringComparer.OrdinalIgnoreCase)
				{
					["alice"] = LootOutcome.Ok(2, "offer-alice", requiresConfirmation: true)
				},
				ConfirmBehavior.AnswerUnconfirmed,
				cts.Token),
			CancellationToken.None);

		CollectRun run = await StorageCollectRunner.DispatchCollectAsync(
			store, StorageSpec(), [Farm("alice")], null, null, cts.Token);

		// The confirmation task itself succeeded but Steam never actually
		// confirmed: reported as not confirmed, with no error to show.
		Assert.True(run.Completed);
		CollectTaskResult alice = Assert.Single(run.Tasks);
		Assert.False(alice.Confirmed);
		Assert.Null(alice.ConfirmError);
		Assert.NotNull(alice.ConfirmJobId);
	}

	[Fact]
	public async Task BuildSnapshotAsync_LinksConfirmationsAndSkipsForeignJobs()
	{
		using var store = new SqliteJobStore(":memory:");
		var ct = CancellationToken.None;

		// Foreign jobs the snapshot must ignore entirely — a loot action is not
		// enough on its own, the collect origin in the job meta is the marker.
		await StageTaskAsync(store, "ping", "stray", null, null, null, success: true, ct);
		await StageTaskAsync(store, "ping", "stray2", new Dictionary<string, string> { ["origin"] = "accounts-api" }, null, null, success: true, ct);
		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "dave", new Dictionary<string, string> { ["origin"] = "manual-loot" }, null, null, success: true, ct);
		await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "erin", new Dictionary<string, string> { ["origin"] = "manual-loot" }, null, null, success: true, ct);

		// alice: loot offer A with a matched confirmation (found past a target
		// mismatch and an offer mismatch — both candidates stay unmatched).
		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "alice", CollectMeta(storage: "warehouse"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "offer-A", ["item_count"] = 5 }, null, success: true, ct);
		await Task.Delay(5);
		Job confirmForCarol = await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "carol",
			CollectMeta(storage: "warehouse"), null, new Dictionary<string, object?> { ["trade_offer_id"] = "offer-A" }, success: true, ct);
		await Task.Delay(5);
		Job confirmWrongOffer = await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "alice",
			CollectMeta(storage: "warehouse"), null, new Dictionary<string, object?> { ["trade_offer_id"] = "offer-B" }, success: true, ct);
		await Task.Delay(5);
		Job confirmMatched = await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "alice",
			CollectMeta(storage: "warehouse"), new Dictionary<string, object?> { ["confirmed"] = true },
			new Dictionary<string, object?> { ["trade_offer_id"] = "offer-A" }, success: true, ct);

		// bob: loot with no offer in the output, and its loot job carries no
		// storage meta (defensive read) — the entry degrades gracefully.
		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "bob", CollectMeta(storage: null),
			new Dictionary<string, object?> { ["item_count"] = 1 }, null, success: true, ct);

		// carol: a failed loot — error surfaces, no offer to match.
		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "carol", CollectMeta(storage: "warehouse"),
			null, null, success: false, ct);

		IReadOnlyList<CollectSnapshotEntry> entries = await StorageCollectRunner.BuildSnapshotAsync(store, ct);

		Assert.Equal(3, entries.Count);
		CollectSnapshotEntry alice = entries.Single(e => e.Account == "alice");
		Assert.Equal("warehouse", alice.Storage);
		Assert.Equal("offer-A", alice.TradeOfferId);
		Assert.Equal(5, alice.ItemCount);
		Assert.Equal("finished", alice.Status);
		Assert.True(alice.Confirmed);
		Assert.Equal(confirmMatched.Id, alice.ConfirmJobId);
		Assert.NotEqual(confirmForCarol.Id, alice.ConfirmJobId);
		Assert.NotEqual(confirmWrongOffer.Id, alice.ConfirmJobId);

		CollectSnapshotEntry bob = entries.Single(e => e.Account == "bob");
		Assert.Null(bob.Storage);
		Assert.Null(bob.TradeOfferId);
		Assert.Null(bob.Confirmed);
		Assert.Null(bob.ConfirmJobId);

		CollectSnapshotEntry carol = entries.Single(e => e.Account == "carol");
		Assert.Equal("failed", carol.Status);
		Assert.Contains("nothing tradable", carol.Error, StringComparison.Ordinal);
		Assert.Equal("warehouse", carol.Storage);
	}

	[Fact]
	public async Task BuildSnapshotAsync_MostRecentLootWinsPerAccount()
	{
		using var store = new SqliteJobStore(":memory:");
		var ct = CancellationToken.None;

		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "alice", CollectMeta(storage: "warehouse"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "offer-OLD", ["item_count"] = 1 }, null, success: true, ct);
		await Task.Delay(5);
		Job oldConfirm = await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "alice",
			CollectMeta(storage: "warehouse"), null, new Dictionary<string, object?> { ["trade_offer_id"] = "offer-OLD" }, success: true, ct);
		await Task.Delay(5);
		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "alice", CollectMeta(storage: "warehouse"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "offer-NEW", ["item_count"] = 9 }, null, success: true, ct);
		await Task.Delay(5);
		Job newConfirm = await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "alice",
			CollectMeta(storage: "warehouse"), null, new Dictionary<string, object?> { ["trade_offer_id"] = "offer-NEW" }, success: true, ct);

		IReadOnlyList<CollectSnapshotEntry> entries = await StorageCollectRunner.BuildSnapshotAsync(store, ct);

		// ListJobs is newest first: the later collect supersedes the earlier
		// one, including which confirmation gets linked.
		CollectSnapshotEntry entry = Assert.Single(entries);
		Assert.Equal("offer-NEW", entry.TradeOfferId);
		Assert.Equal(9, entry.ItemCount);
		Assert.Equal(newConfirm.Id, entry.ConfirmJobId);
		Assert.NotEqual(oldConfirm.Id, entry.ConfirmJobId);
	}

	[Fact]
	public async Task BuildSnapshotAsync_ScanCapsBoundTheDetailFetches()
	{
		using var store = new SqliteJobStore(":memory:");
		var ct = CancellationToken.None;

		for (int i = 1; i <= StorageCollectRunner.SnapshotJobDetail + 1; i++)
		{
			await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, $"t{i:00}", CollectMeta(storage: "warehouse"),
				new Dictionary<string, object?> { ["trade_offer_id"] = $"offer-{i:00}" }, null, success: true, ct);
			await Task.Delay(5);
			await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, $"u{i:00}", CollectMeta(storage: "warehouse"),
				null, new Dictionary<string, object?> { ["trade_offer_id"] = $"offer-u{i:00}" }, success: true, ct);
			await Task.Delay(5);
		}

		IReadOnlyList<CollectSnapshotEntry> entries = await StorageCollectRunner.BuildSnapshotAsync(store, ct);

		// Only the newest SnapshotJobDetail loot jobs are unpacked — the oldest
		// collect (t01) falls out of the per-account snapshot.
		Assert.Equal(StorageCollectRunner.SnapshotJobDetail, entries.Count);
		Assert.DoesNotContain(entries, e => e.Account == "t01");
		Assert.Contains(entries, e => e.Account == "t02");
		Assert.Contains(entries, e => e.Account == $"t{StorageCollectRunner.SnapshotJobDetail:00}");
	}

	[Fact]
	public async Task BuildSnapshotAsync_UnusualMetaAndConfirmationShapesDegradeGracefully()
	{
		using var store = new SqliteJobStore(":memory:");
		var ct = CancellationToken.None;

		// Meta that carries no origin key at all, and a collect-origin job whose
		// action is neither leg — both must be skipped, not misread as a collect.
		await StageTaskAsync(store, "ping", "noise", new Dictionary<string, string> { ["storage"] = "warehouse" }, null, null, success: true, ct);
		await StageTaskAsync(store, "unrelated", "noise2", CollectMeta(storage: "warehouse"), null, null, success: true, ct);

		// frank: a settled confirmation that never reported the flag (the Steam
		// plugin could not confirm) — the entry must say "not confirmed", not
		// guess, and must not report a confirmation error either.
		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "frank", CollectMeta(storage: "warehouse"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "offer-F" }, null, success: true, ct);
		await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "frank", CollectMeta(storage: "warehouse"),
			null, new Dictionary<string, object?> { ["trade_offer_id"] = "offer-F" }, success: true, ct);

		// grace: a confirmation that failed outright — the error is surfaced and
		// the confirmed flag is false.
		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "grace", CollectMeta(storage: "warehouse"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "offer-G" }, null, success: true, ct);
		await StageTaskAsync(store, AccountTaskRunner.ConfirmTradeOfferAction, "grace", CollectMeta(storage: "warehouse"),
			null, new Dictionary<string, object?> { ["trade_offer_id"] = "offer-G" }, success: false, ct);

		IReadOnlyList<CollectSnapshotEntry> entries = await StorageCollectRunner.BuildSnapshotAsync(store, ct);

		Assert.DoesNotContain(entries, e => e.Account is "noise" or "noise2");
		CollectSnapshotEntry frank = entries.Single(e => e.Account == "frank");
		Assert.False(frank.Confirmed);
		Assert.Null(frank.ConfirmError);
		CollectSnapshotEntry grace = entries.Single(e => e.Account == "grace");
		Assert.False(grace.Confirmed);
		Assert.Contains("nothing tradable", grace.ConfirmError!, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildSnapshotAsync_CanceledConfirmation_DerivesTheOutcomeFromStatus()
	{
		using var store = new SqliteJobStore(":memory:");
		var ct = CancellationToken.None;

		await StageTaskAsync(store, AccountTaskRunner.LootInventoryAction, "alice", CollectMeta(storage: "warehouse"),
			new Dictionary<string, object?> { ["trade_offer_id"] = "offer-A" }, null, success: true, ct);

		// The confirmation was cancelled before any agent answered it: no error
		// text was ever recorded, so the snapshot derives one from the status.
		JobWithTasks confirm = await store.CreateJob(
			new CreateJobRequest(
				AccountTaskRunner.ConfirmTradeOfferAction,
				null,
				["alice"],
				new Dictionary<string, object?> { ["trade_offer_id"] = "offer-A" },
				CollectMeta(storage: "warehouse")),
			ct);
		await store.CancelJob(confirm.Job.Id, ct);

		IReadOnlyList<CollectSnapshotEntry> entries = await StorageCollectRunner.BuildSnapshotAsync(store, ct);

		CollectSnapshotEntry entry = Assert.Single(entries);
		Assert.Equal("finished", entry.Status);
		Assert.False(entry.Confirmed);
		Assert.Equal($"confirmation ended as {JobTaskStatus.Canceled}", entry.ConfirmError);
	}

	private static Dictionary<string, string> CollectMeta(string? storage)
	{
		var meta = new Dictionary<string, string> { ["origin"] = StorageCollectRunner.CollectOrigin };
		if (storage is not null)
		{
			meta["storage"] = storage;
		}

		return meta;
	}

	/// <summary>Stages one single-target job and settles its task via the normal claim/result path.</summary>
	private static async Task<Job> StageTaskAsync(
		SqliteJobStore store,
		string action,
		string target,
		Dictionary<string, string>? meta,
		Dictionary<string, object?>? output,
		Dictionary<string, object?>? payload,
		bool success,
		CancellationToken ct)
	{
		JobWithTasks job = await store.CreateJob(
			new CreateJobRequest(action, null, [target], payload, meta), ct);
		JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
		Assert.NotNull(claimed);
		await store.SetTaskResult(
			new TaskResult(claimed!.Id, success, success ? null : "loot failed: nothing tradable to loot", success ? output : null, DateTimeOffset.UtcNow),
			ct);
		return job.Job;
	}

	private static AccountSpec StorageSpec() => new(
		"warehouse", Enabled: true, AccountDesiredState.Offline,
		Role: AccountRole.Storage, SteamId: StorageSteamId);

	private static AccountSpec Farm(string name) => new(name, Enabled: true, AccountDesiredState.Farm);

	private static string PayloadValue(IReadOnlyDictionary<string, object?>? payload, string key)
	{
		Assert.NotNull(payload);
		Assert.True(payload!.ContainsKey(key));
		return payload[key]!.ToString()!;
	}

	private static void RestoreKnobs()
	{
		StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(150);
		StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(500);
	}

	private sealed record LootOutcome(bool Success, Dictionary<string, object?>? Output, string? Error)
	{
		internal static LootOutcome Ok(int itemCount, string? offerId, bool requiresConfirmation) => new(
			true,
			new Dictionary<string, object?>
			{
				["item_count"] = itemCount,
				["trade_offer_id"] = offerId,
				["requires_mobile_confirmation"] = requiresConfirmation
			},
			null);

		internal static LootOutcome Failed(string error) => new(false, null, error);
	}

	private enum ConfirmBehavior
	{
		/// <summary>Confirm and report <c>confirmed=true</c>.</summary>
		Answer,

		/// <summary>Confirm but report no confirmation flag (Steam stayed silent).</summary>
		AnswerUnconfirmed,

		/// <summary>Attempt the confirmation and report failure.</summary>
		Fail,

		/// <summary>Never touch the confirmation tasks (they stay queued).</summary>
		Ignore,

		/// <summary>Cancel the confirmation job as soon as it appears.</summary>
		Cancel
	}

	/// <summary>
	/// Plays the agent side of a collect run: answers every <c>loot_inventory</c>
	/// task with its target's staged outcome, then handles chained
	/// <c>confirm_trade_offer</c> tasks per the configured behaviour. The offer
	/// id is staged in the loot output when confirmation is required.
	/// </summary>
	private static async Task RespondCollectAsync(
		IJobStore store,
		IReadOnlyDictionary<string, LootOutcome> lootOutcomes,
		ConfirmBehavior confirmBehavior,
		CancellationToken ct)
	{
		bool confirmCanceled = false;
		while (!ct.IsCancellationRequested)
		{
			JobTask? claimed = await store.ClaimNextQueuedTask("us-east", ct);
			if (claimed is null)
			{
				await Task.Delay(25, ct);
				continue;
			}

			if (claimed.Action == AccountTaskRunner.LootInventoryAction)
			{
				LootOutcome outcome = lootOutcomes[claimed.Target];
				await store.SetTaskResult(
					new TaskResult(claimed.Id, outcome.Success, outcome.Error, outcome.Success ? outcome.Output : null, DateTimeOffset.UtcNow),
					ct);
				continue;
			}

			if (claimed.Action != AccountTaskRunner.ConfirmTradeOfferAction)
			{
				continue;
			}

			switch (confirmBehavior)
			{
				case ConfirmBehavior.Answer:
					await store.SetTaskResult(
						new TaskResult(
							claimed.Id, true, null,
							new Dictionary<string, object?>
							{
								["confirmed"] = true,
								["trade_offer_id"] = claimed.Payload?.TryGetValue("trade_offer_id", out object? id) == true ? id : null
							},
							DateTimeOffset.UtcNow),
						ct);
					break;
				case ConfirmBehavior.AnswerUnconfirmed:
					await store.SetTaskResult(
						new TaskResult(
							claimed.Id, true, null,
							new Dictionary<string, object?>
							{
								["trade_offer_id"] = claimed.Payload?.TryGetValue("trade_offer_id", out object? silentId) == true ? silentId : null
							},
							DateTimeOffset.UtcNow),
						ct);
					break;
				case ConfirmBehavior.Fail:
					await store.SetTaskResult(
						new TaskResult(claimed.Id, false, "steam rejected the confirmation", null, DateTimeOffset.UtcNow),
						ct);
					break;
				case ConfirmBehavior.Ignore:
					// Leaves the task queued — the window logic sees it in flight.
					break;
				case ConfirmBehavior.Cancel:
					// The claimed confirm task must go back to cancellable state:
					// cancel its whole job (the run's confirm leg disappears).
					if (!confirmCanceled)
					{
						await store.CancelJob(claimed.JobId, ct);
						confirmCanceled = true;
					}

					break;
			}
		}
	}
}
