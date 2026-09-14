using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class EventBrokerTests
{
	[Fact]
	public async Task GlobalSubscriberReceivesSystemEventWithoutJobId()
	{
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Task<Protocol.Event> next = ReadNextEventAsync(broker, "*", cts.Token);

		broker.Publish(null, "agent.connected", new Dictionary<string, object?>
		{
			["agentId"] = "agent-1",
			["region"] = "local"
		});

		Protocol.Event evt = await next;

		Assert.Null(evt.JobId);
		Assert.Equal("agent.connected", evt.Type);
		Assert.NotNull(evt.Payload);
		Assert.Equal("agent-1", evt.Payload!["agentId"]?.ToString());
	}

	[Fact]
	public async Task JobSubscriberOnlyReceivesMatchingJobEvents()
	{
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Task<Protocol.Event> next = ReadNextEventAsync(broker, "job-1", cts.Token);

		broker.Publish("job-2", "task.finished", new Dictionary<string, object?> { ["taskId"] = "task-2" });
		broker.Publish("job-1", "task.finished", new Dictionary<string, object?> { ["taskId"] = "task-1" });

		Protocol.Event evt = await next;

		Assert.Equal("job-1", evt.JobId);
		Assert.Equal("task.finished", evt.Type);
		Assert.NotNull(evt.Payload);
		Assert.Equal("task-1", evt.Payload!["taskId"]?.ToString());
	}

	[Fact]
	public async Task GlobalSubscriberReceivesJobAndSystemEvents()
	{
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Task<List<Protocol.Event>> collect = CollectEventsAsync(broker, "*", 2, cts.Token);

		broker.Publish("job-1", "job.created", new Dictionary<string, object?> { ["action"] = "ping" });
		broker.Publish(null, "agent.disconnected", new Dictionary<string, object?> { ["agentId"] = "agent-2" });

		List<Protocol.Event> events = await collect;

		Assert.Collection(events,
			first =>
			{
				Assert.Equal("job.created", first.Type);
				Assert.Equal("job-1", first.JobId);
			},
			second =>
			{
				Assert.Equal("agent.disconnected", second.Type);
				Assert.Null(second.JobId);
			});
	}

	// ── session / auth-challenge streams ──

	[Fact]
	public async Task SessionSubscriber_AccountSpecificKey_ReceivesOnlyThatAccountsEvents()
	{
		// Drives the account-specific publish branch (distinct from the "*" global branch).
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Task<SessionEvent> next = ReadNextSessionEventAsync(broker, "alice", cts.Token);

		broker.PublishSession("bob", "state_changed", "Connected");
		broker.PublishSession("alice", "state_changed", "LoginFailed", message: "bad password");

		SessionEvent evt = await next;

		Assert.Equal("alice", evt.AccountName);
		Assert.Equal("LoginFailed", evt.State);
		Assert.Equal("bad password", evt.Message);
	}

	[Fact]
	public async Task AuthChallengeSubscriber_AccountSpecificKey_ReceivesOnlyThatAccountsEvents()
	{
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Task<AuthChallengeEvent> next = ReadNextAuthChallengeAsync(broker, "alice", cts.Token);

		broker.PublishAuthChallenge("bob", "email");
		broker.PublishAuthChallenge("alice", "2fa_required", code: "123456");

		AuthChallengeEvent evt = await next;

		Assert.Equal("alice", evt.AccountName);
		Assert.Equal("2fa_required", evt.ChallengeType);
		Assert.Equal("123456", evt.Code);
	}

	[Fact]
	public async Task SubscribeSessions_SurvivesAcrossDeliveredEvents_AndCleansUpOnCancel()
	{
		// The loop must keep iterating after a delivery (draining back to the wait)
		// and the finally block must deregister the channel once cancellation ends it.
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

		List<SessionEvent> received = await CollectSessionEventsAsync(broker, accountName: null, 2, cts, publish: n =>
			broker.PublishSession(n == 0 ? "alice" : "bob", "state_changed", "Connected"));

		Assert.Equal(2, received.Count);

		// The enumerator ran to completion, so the finally cleanup must have run.
		Assert.Equal(0, broker.SessionSubscriberCount);
	}

	[Fact]
	public async Task SubscribeAuthChallenges_SurvivesAcrossDeliveredEvents_AndCleansUpOnCancel()
	{
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

		List<AuthChallengeEvent> received = await CollectAuthChallengeEventsAsync(broker, accountName: null, 2, cts, publish: n =>
			broker.PublishAuthChallenge(n == 0 ? "alice" : "bob", "email"));

		Assert.Equal(2, received.Count);
		Assert.Equal(0, broker.AuthSubscriberCount);
	}

	[Fact]
	public async Task SubscribeSessions_CancelledBeforeFirstWait_ExitsWithoutEvents()
	{
		// A token that is already cancelled ends the stream through the normal
		// (non-exception) path: loop body never runs, cleanup still executes.
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		await foreach (SessionEvent _ in broker.SubscribeSessions(cts.Token, "alice"))
		{
			Assert.Fail("No event was published, nothing should be delivered.");
		}

		Assert.Equal(0, broker.SessionSubscriberCount);
	}

	private static async Task<SessionEvent> ReadNextSessionEventAsync(EventBroker broker, string key, CancellationToken cancellationToken)
	{
		await foreach (SessionEvent evt in broker.SubscribeSessions(cancellationToken, key))
		{
			return evt;
		}

		throw new InvalidOperationException("Expected at least one session event.");
	}

	private static async Task<AuthChallengeEvent> ReadNextAuthChallengeAsync(EventBroker broker, string key, CancellationToken cancellationToken)
	{
		await foreach (AuthChallengeEvent evt in broker.SubscribeAuthChallenges(cancellationToken, key))
		{
			return evt;
		}

		throw new InvalidOperationException("Expected at least one auth challenge event.");
	}

	/// <summary>
	/// Waits for the subscription to be registered, then publishes the requested
	/// number of events one at a time (each after the previous one was consumed),
	/// canceling the token so the enumerator exits through its cleanup path.
	/// </summary>
	private static async Task<List<SessionEvent>> CollectSessionEventsAsync(
		EventBroker broker,
		string? accountName,
		int count,
		CancellationTokenSource cts,
		Action<int> publish)
	{
		List<SessionEvent> events = [];
		Func<Task> pump = async () =>
		{
			await foreach (SessionEvent evt in broker.SubscribeSessions(cts.Token, accountName))
			{
				events.Add(evt);
				if (events.Count == count)
				{
					cts.Cancel();
				}
			}
		};

		Task pumpTask = pump();
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		for (int i = 0; i < count; i++)
		{
			while (broker.SessionSubscriberCount == 0 && DateTimeOffset.UtcNow < deadline)
			{
				await Task.Delay(10);
			}

			publish(i);
		}

		await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
		return events;
	}

	private static async Task<List<AuthChallengeEvent>> CollectAuthChallengeEventsAsync(
		EventBroker broker,
		string? accountName,
		int count,
		CancellationTokenSource cts,
		Action<int> publish)
	{
		List<AuthChallengeEvent> events = [];
		Func<Task> pump = async () =>
		{
			await foreach (AuthChallengeEvent evt in broker.SubscribeAuthChallenges(cts.Token, accountName))
			{
				events.Add(evt);
				if (events.Count == count)
				{
					cts.Cancel();
				}
			}
		};

		Task pumpTask = pump();
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		for (int i = 0; i < count; i++)
		{
			while (broker.AuthSubscriberCount == 0 && DateTimeOffset.UtcNow < deadline)
			{
				await Task.Delay(10);
			}

			publish(i);
		}

		await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
		return events;
	}

	private static async Task<Protocol.Event> ReadNextEventAsync(EventBroker broker, string key, CancellationToken cancellationToken)
	{
		await foreach (Protocol.Event evt in broker.Subscribe(cancellationToken, key))
		{
			return evt;
		}

		throw new InvalidOperationException("Expected at least one event.");
	}

	private static async Task<List<Protocol.Event>> CollectEventsAsync(EventBroker broker, string key, int count, CancellationToken cancellationToken)
	{
		List<Protocol.Event> events = [];
		await foreach (Protocol.Event evt in broker.Subscribe(cancellationToken, key))
		{
			events.Add(evt);
			if (events.Count == count)
			{
				return events;
			}
		}

		throw new InvalidOperationException($"Expected {count} events but stream completed early.");
	}
}
