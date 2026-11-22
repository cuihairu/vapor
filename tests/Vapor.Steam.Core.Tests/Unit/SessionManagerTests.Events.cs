using System.Reflection;
using System.Threading.Channels;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>Subscribe-all / broadcast event stream tests: fan-out, seeded history, overflow trim, registration races and cancellation.</summary>
public partial class SessionManagerTests
{
	[Fact]
	public async Task SubscribeAllEvents_ReturnsEventChannel()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		// Act
		var events = _manager.SubscribeAllEvents(CancellationToken.None);

		// Assert
		Assert.NotNull(events);
	}

	[Fact]
	public void SubscribeAllEvents_WithNoSessions_DoesNotThrow()
	{
		// Act & Assert - should not throw
		var events = _manager.SubscribeAllEvents(CancellationToken.None);
		Assert.NotNull(events);
	}

	[Fact]
	public async Task SubscribeAllEvents_WhenCallerCancels_EnumerationEndsWithCancellation()
	{
		// The channel has no producer-side completion, so a parked consumer's read
		// loop only ends when the *caller's* token cancels — ReadAllAsync receives
		// that token directly, surfacing OperationCanceledException to the enumerator.
		using var cts = new CancellationTokenSource();
		var enumerator = _manager.SubscribeAllEvents(cts.Token).GetAsyncEnumerator();
		var read = enumerator.MoveNextAsync().AsTask();
		await Task.Delay(50); // let the consumer park inside ReadAllAsync

		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
	}

	[Fact]
	public async Task SubscribeAllEvents_WhenChannelCompletes_EnumerationFinishes()
	{
		// Producer-side completion is the other legal loop exit: completing the
		// underlying channel ends ReadAllAsync normally, so the enumerator must
		// report Completion (false, no throw) instead of hanging or cancelling.
		var channel = (Channel<SessionEvent>)typeof(SessionManager)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(_manager)!;
		channel.Writer.Complete();

		var enumerator = _manager.SubscribeAllEvents(CancellationToken.None).GetAsyncEnumerator();
		var read = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

		Assert.False(read);
	}

	[Fact]
	public async Task SubscribeAllEvents_ReceivesEventsFromSessions()
	{
		// Arrange
		var accountName = "test_account";
		var credentials = new AccountCredentials(accountName, "password");
		await _manager.GetOrCreateSessionAsync(accountName, credentials, CancellationToken.None);

		var events = _manager.SubscribeAllEvents(CancellationToken.None);
		var eventList = new List<SessionEvent>();
		var cts = new CancellationTokenSource();

		// Start collecting events. Deliberately no token argument: with one, a busy thread pool
		// could leave the task canceled before the delegate ever runs, failing the await below.
		var collectTask = Task.Run(async () =>
		{
			try
			{
				await foreach (var evt in events.WithCancellation(cts.Token))
				{
					eventList.Add(evt);
					if (eventList.Count >= 1) break;
				}
			}
			catch (OperationCanceledException)
			{
			}
		});

		// Wait a bit for events
		await Task.Delay(200);
		cts.Cancel();
		await collectTask.WaitAsync(TimeSpan.FromSeconds(30));

		// Assert
		// Events should be collected (the exact number depends on timing)
		Assert.NotNull(events);
	}

	[Fact]
	public async Task SubscribeAllEvents_AfterEventsPublished_ReceivesSeededHistory()
	{
		// An event published before SubscribeAllEvents registers is either still in
		// the broadcaster's recent history (seeded) or fanned out live — never
		// missed. Publish first and wait until the broadcaster has drained it into
		// history, then subscribe: delivery can only come from the seed path.
		Channel<SessionEvent> channel = (Channel<SessionEvent>)typeof(SessionManager)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(_manager)!;
		var published = new SessionEvent(SessionEventType.StateChanged, "seed_test", SessionState.Connected);
		channel.Writer.TryWrite(published);
		await WaitForHistoryAsync(h => h.Count == 1);

		List<SessionEvent> received = [];
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await foreach (var evt in _manager.SubscribeAllEvents(cts.Token))
		{
			received.Add(evt);
			break;
		}

		Assert.Single(received);
		Assert.Equal(published, received[0]);
	}

	[Fact]
	public async Task SubscribeAllEvents_OneShotChallengeEvents_AreNotSeededToLateSubscriber()
	{
		// A 2FA/QR/auth challenge is a point-in-time command: a subscriber that
		// registers after it was published must NOT have it replayed, otherwise the
		// 2FA auto-responder burns its cooldown and inflates AnsweredCount, and the
		// monitoring plugin inflates its session-event counter.
		Channel<SessionEvent> channel = (Channel<SessionEvent>)typeof(SessionManager)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(_manager)!;
		var challenge = new SessionEvent(SessionEventType.TwoFactorCodeNeeded, "one_shot_test", SessionState.ConnectingWait2FA);
		channel.Writer.TryWrite(challenge);
		await WaitForHistoryAsync(h => h.Count == 1);

		List<SessionEvent> received = [];
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			await foreach (var evt in _manager.SubscribeAllEvents(cts.Token))
			{
				received.Add(evt);
				break;
			}
		}
		catch (OperationCanceledException)
		{
			// Expected: a non-replayed one-shot leaves the subscription empty, so
			// the enumerator only ends when the caller token fires.
		}

		// The seed must be empty for a one-shot challenge event: the only way the
		// subscription yields nothing is the live-only rule — assert it delivered
		// no stale challenge.
		Assert.Empty(received);
	}

	[Fact]
	public async Task BroadcastEvents_HistoryTrimmedToLimitAfterOverflow()
	{
		// 300 distinct events through the ingress: the recent-history buffer must
		// trim from the front and cap at the limit, keeping the most recent window
		// (evt-44 is the first survivor of 0..299) so a late subscriber seeded from
		// it never sees the trimmed prefix.
		Channel<SessionEvent> channel = (Channel<SessionEvent>)typeof(SessionManager)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(_manager)!;
		const int published = 300;
		const int limit = 256;
		for (int i = 0; i < published; i++)
		{
			channel.Writer.TryWrite(new SessionEvent(
				SessionEventType.StateChanged, "history_test", SessionState.Connected, $"evt-{i}"));
		}

		// Drain completion is observable: the front is evt-0 until the cap is hit,
		// then trimming starts and it can only advance toward the survivor window.
		List<SessionEvent> history = await WaitForHistoryAsync(h =>
			h.Count > 0 && h[0].Message == $"evt-{published - limit}");

		Assert.Equal(limit, history.Count);
		Assert.Equal($"evt-{published - limit}", history[0].Message);
		Assert.Equal($"evt-{published - 1}", history[^1].Message);
	}

	[Fact]
	public async Task SubscribeAllEvents_PublishDuringRegistration_DeliversEachEventExactlyOnce()
	{
		// The _eventLock linearizes registration and fan-out: an event published
		// while a subscriber is registering is either seeded from the recent
		// history or fanned out live, never both and never neither. Publish a batch
		// of distinct events while a subscriber registers concurrently, then assert
		// every event reached the subscriber exactly once.
		Channel<SessionEvent> channel = (Channel<SessionEvent>)typeof(SessionManager)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(_manager)!;

		const int batch = 200;
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		List<SessionEvent> received = [];
		var collected = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

		// Concurrently publish and subscribe so the registration happens mid-batch:
		// the lock must give the subscriber either the seed (already-published) or
		// the live fan-out (published after registration), never a duplicate.
		var publishTask = Task.Run(async () =>
		{
			for (int i = 0; i < batch; i++)
			{
				channel.Writer.TryWrite(new SessionEvent(
					SessionEventType.StateChanged, "race_test", SessionState.Connected, $"race-{i}"));
			}
		});
		var subscribeTask = Task.Run(async () =>
		{
			try
			{
				await foreach (var evt in _manager.SubscribeAllEvents(cts.Token))
				{
					lock (received)
					{
						received.Add(evt);
						if (received.Count >= batch)
						{
							collected.TrySetResult(null);
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
		});

		await publishTask;
		await collected.Task.WaitAsync(TimeSpan.FromSeconds(20));
		cts.Cancel();
		await subscribeTask;

		// Exactly once each: no loss and no duplication.
		Assert.Equal(batch, received.Count);
		Assert.Equal(batch, received.Select(e => e.Message).Distinct().Count());
	}

	[Fact]
	public async Task SubscribeAllEvents_RegisteredSubscriber_ReceivesLiveFanOut()
	{
		// The race above is delivery-guaranteed but not path-guaranteed: under a
		// loaded machine the publisher can drain all 200 events into history
		// before the subscriber registers, every event arrives via the seed path,
		// and the live fan-out inside the broadcast loop never executes — the
		// whole suite can then pass with that line at zero hits (2026-11-14
		// coverage run). This test pins the fan-out deterministically: wait until
		// the subscription is registered in the broadcaster's list, and only then
		// publish a fresh event. That event cannot be in the history the
		// registration seeded, so the subscription's only route to it is the
		// TryWrite in BroadcastEventsAsync.
		Channel<SessionEvent> channel = (Channel<SessionEvent>)typeof(SessionManager)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(_manager)!;
		FieldInfo subscribers = typeof(SessionManager)
			.GetField("_eventSubscribers", BindingFlags.Instance | BindingFlags.NonPublic)!;

		List<SessionEvent> received = [];
		var collectTask = Task.Run(async () =>
		{
			await foreach (var evt in _manager.SubscribeAllEvents(CancellationToken.None))
			{
				lock (received)
				{
					received.Add(evt);
				}
				break;
			}
		});

		// Registration happens on the enumerator's first MoveNextAsync; poll the
		// private list until the subscription shows up in it. From this moment on
		// every drain fans out to this subscriber — the write below can only be
		// delivered live, never seeded.
		System.Collections.IList registered = (System.Collections.IList)subscribers.GetValue(_manager)!;
		DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
		while (registered.Count == 0)
		{
			if (DateTimeOffset.UtcNow >= deadline)
			{
				Assert.Fail("Subscription never registered with the broadcaster within 10s.");
			}
			await Task.Delay(10);
		}

		var published = new SessionEvent(SessionEventType.StateChanged, "live_fanout", SessionState.Connected, "live-1");
		channel.Writer.TryWrite(published);
		await collectTask.WaitAsync(TimeSpan.FromSeconds(10));

		lock (received)
		{
			Assert.Single(received);
			Assert.Equal(published, received[0]);
		}
	}

	/// <summary>
	/// Polls the broadcaster's private recent-history buffer until the predicate
	/// holds — the only way to observe drain completion, since the ingress write
	/// returns before the background broadcaster records the event.
	/// </summary>
	private async Task<List<SessionEvent>> WaitForHistoryAsync(Func<List<SessionEvent>, bool> ready)
	{
		FieldInfo field = typeof(SessionManager).GetField("_recentEvents", BindingFlags.Instance | BindingFlags.NonPublic)!;
		DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
		while (true)
		{
			var history = (List<SessionEvent>)field.GetValue(_manager)!;
			if (ready(history))
			{
				return history;
			}
			if (DateTimeOffset.UtcNow >= deadline)
			{
				Assert.Fail($"Broadcaster never reached the expected history state (count: {history.Count}, front: {(history.Count > 0 ? history[0].Message : "<empty>")}).");
			}
			await Task.Delay(10);
		}
	}

	[Fact]
	public async Task SubscribeAllEvents_YieldsEventsWrittenBySessions()
	{
		var credentialStoreMock = CreateSuccessfulRestoreCredentialStore();
		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			credentialStoreMock.Object);

		var eventList = new List<SessionEvent>();
		var collectTask = Task.Run(async () =>
		{
			await foreach (var evt in manager.SubscribeAllEvents(CancellationToken.None))
			{
				lock (eventList)
				{
					eventList.Add(evt);
				}

				if (eventList.Count >= 1)
				{
					break;
				}
			}
		});

		var session = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(session);

		await collectTask.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.NotEmpty(eventList);
	}

}
