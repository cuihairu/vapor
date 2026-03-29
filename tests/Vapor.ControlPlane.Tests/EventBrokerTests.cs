using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class EventBrokerTests {
	[Fact]
	public async Task GlobalSubscriberReceivesSystemEventWithoutJobId() {
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Task<Protocol.Event> next = ReadNextEventAsync(broker, "*", cts.Token);

		broker.Publish(null, "agent.connected", new Dictionary<string, object?> {
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
	public async Task JobSubscriberOnlyReceivesMatchingJobEvents() {
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
	public async Task GlobalSubscriberReceivesJobAndSystemEvents() {
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

		Task<List<Protocol.Event>> collect = CollectEventsAsync(broker, "*", 2, cts.Token);

		broker.Publish("job-1", "job.created", new Dictionary<string, object?> { ["action"] = "ping" });
		broker.Publish(null, "agent.disconnected", new Dictionary<string, object?> { ["agentId"] = "agent-2" });

		List<Protocol.Event> events = await collect;

		Assert.Collection(events,
			first => {
				Assert.Equal("job.created", first.Type);
				Assert.Equal("job-1", first.JobId);
			},
			second => {
				Assert.Equal("agent.disconnected", second.Type);
				Assert.Null(second.JobId);
			});
	}

	private static async Task<Protocol.Event> ReadNextEventAsync(EventBroker broker, string key, CancellationToken cancellationToken) {
		await foreach (Protocol.Event evt in broker.Subscribe(cancellationToken, key)) {
			return evt;
		}

		throw new InvalidOperationException("Expected at least one event.");
	}

	private static async Task<List<Protocol.Event>> CollectEventsAsync(EventBroker broker, string key, int count, CancellationToken cancellationToken) {
		List<Protocol.Event> events = [];
		await foreach (Protocol.Event evt in broker.Subscribe(cancellationToken, key)) {
			events.Add(evt);
			if (events.Count == count) {
				return events;
			}
		}

		throw new InvalidOperationException($"Expected {count} events but stream completed early.");
	}
}
