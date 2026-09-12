using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Plugins.Core.Tests;

public sealed class PluginEventDispatcherTests
{
	[Fact]
	public async Task Events_AreDeliveredToAllSubscribers()
	{
		await using var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);

		var first = new RecordingEventPlugin();
		var second = new RecordingEventPlugin();
		dispatcher.Add(first);
		dispatcher.Add(second);

		var manager = new FakeSessionManager();
		dispatcher.Start(manager);

		await manager.PublishAsync(new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected, "msg"));
		await first.WaitForEventAsync();
		await second.WaitForEventAsync();

		Assert.Equal(2, dispatcher.SubscriberCount);
		Assert.Equal(SessionState.Connected, first.Events[0].NewState);
	}

	[Fact]
	public async Task FailingSubscriber_DoesNotAffectOthers()
	{
		await using var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);

		var throwing = new RecordingEventPlugin(throwOnEvent: true);
		var healthy = new RecordingEventPlugin();
		dispatcher.Add(throwing);
		dispatcher.Add(healthy);

		var manager = new FakeSessionManager();
		dispatcher.Start(manager);

		await manager.PublishAsync(new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected, "msg"));
		await healthy.WaitForEventAsync();

		Assert.Empty(throwing.Events);
		Assert.Single(healthy.Events);
	}

	[Fact]
	public async Task RemovedSubscriber_StopsReceivingEvents()
	{
		await using var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);

		var plugin = new RecordingEventPlugin();
		dispatcher.Add(plugin);

		var manager = new FakeSessionManager();
		dispatcher.Start(manager);

		await manager.PublishAsync(new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected, "first"));
		await plugin.WaitForEventAsync();

		Assert.True(dispatcher.Remove(plugin));
		Assert.Equal(0, dispatcher.SubscriberCount);

		await manager.PublishAsync(new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Disconnected, "second"));
		await Task.Delay(100);
		Assert.Single(plugin.Events);
	}

	[Fact]
	public void Start_IsIdempotent()
	{
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);

		var manager = new FakeSessionManager();
		dispatcher.Start(manager);
		dispatcher.Start(manager);

		Assert.Equal(0, dispatcher.SubscriberCount);
	}

	[Fact]
	public async Task Add_IsIdempotentByReference()
	{
		await using var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);

		var plugin = new RecordingEventPlugin();
		dispatcher.Add(plugin);
		dispatcher.Add(plugin);

		Assert.Equal(1, dispatcher.SubscriberCount);
	}

	[Fact]
	public async Task Dispose_StopsDeliveryAndClearsSubscribers()
	{
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);

		var plugin = new RecordingEventPlugin();
		dispatcher.Add(plugin);
		dispatcher.Start(new FakeSessionManager());

		await dispatcher.DisposeAsync();

		Assert.Equal(0, dispatcher.SubscriberCount);
		Assert.Throws<ObjectDisposedException>(() => dispatcher.Start(new FakeSessionManager()));
	}

	private sealed class RecordingEventPlugin(bool throwOnEvent = false) : IEventPlugin
	{
		private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public List<SessionEvent> Events { get; } = [];

		public PluginInfo Info { get; } = new(
			Id: "test.event-plugin",
			Name: "Test Event Plugin",
			Version: new Version(1, 0, 0),
			ApiVersion: PluginApi.Current);

		public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task OnSessionEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
		{
			if (throwOnEvent)
			{
				throw new InvalidOperationException("handler failure");
			}

			Events.Add(sessionEvent);
			_received.TrySetResult();
			return Task.CompletedTask;
		}

		public async Task WaitForEventAsync()
		{
			await _received.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
	}

	private sealed class FakeSessionManager : ISessionManager
	{
		private readonly Channel<SessionEvent> _channel = Channel.CreateUnbounded<SessionEvent>();

		public Task PublishAsync(SessionEvent evt)
		{
			_channel.Writer.TryWrite(evt);
			return Task.CompletedTask;
		}

		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public IReadOnlyList<BotSession> ListSessions() => [];

		public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents(
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await foreach (SessionEvent evt in _channel.Reader.ReadAllAsync(cancellationToken))
			{
				yield return evt;
			}
		}

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();
	}
}
