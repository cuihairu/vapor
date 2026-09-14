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
	public void Remove_NotRegisteredPlugin_ReturnsFalse()
	{
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);

		Assert.False(dispatcher.Remove(new RecordingEventPlugin()));
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

	[Fact]
	public async Task Dispose_Twice_IsIdempotent()
	{
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);
		dispatcher.Add(new RecordingEventPlugin());
		dispatcher.Start(new FakeSessionManager());

		await dispatcher.DisposeAsync();
		await dispatcher.DisposeAsync();

		Assert.Equal(0, dispatcher.SubscriberCount);
	}

	[Fact]
	public async Task Pump_EventSourceCompletes_DisposeDrainsCleanly()
	{
		// An event source whose enumerator ends on its own lets the pump finish without
		// cancellation; disposal afterwards must not hang or throw.
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);
		var plugin = new RecordingEventPlugin();
		dispatcher.Add(plugin);
		dispatcher.Start(new CompletingSessionManager());

		await plugin.WaitForEventAsync();
		await dispatcher.DisposeAsync();

		Assert.Single(plugin.Events);
		Assert.Equal(0, dispatcher.SubscriberCount);
	}

	[Fact]
	public async Task Pump_EventSourceThrows_IsSwallowed()
	{
		// A crashing event source must take down neither the pump task nor the dispatcher;
		// the failure is logged and the pump stops quietly.
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);
		dispatcher.Add(new RecordingEventPlugin());
		dispatcher.Start(new ThrowingSessionManager());

		await Task.Delay(100);
		await dispatcher.DisposeAsync();

		Assert.Equal(0, dispatcher.SubscriberCount);
	}

	[Fact]
	public async Task Dispatch_DisposedDuringDelivery_StopsBeforeNextSubscriber()
	{
		// Disposal while the pump sits inside a subscriber handler cancels the token, so
		// the dispatch loop must return before reaching the remaining subscribers.
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondCalls = 0;
		dispatcher.Add(new GatedHandlerPlugin(entered, release, throwOperationCanceled: false));
		dispatcher.Add(new GatedHandlerPlugin(entered, release, throwOperationCanceled: false, onEvent: _ => secondCalls++));

		var manager = new FakeSessionManager();
		dispatcher.Start(manager);
		await manager.PublishAsync(new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected, "msg"));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		// Dispose cancels the pump token while the first handler is still parked.
		var dispose = dispatcher.DisposeAsync();
		await Task.Delay(100);
		release.SetResult();
		await dispose;

		Assert.Equal(0, secondCalls);
	}

	[Fact]
	public async Task Dispatch_HandlerThrowsOperationCanceledAfterDispose_StopsDelivery()
	{
		// A handler reacting to disposal with OperationCanceledException is a clean stop,
		// not a failure: the loop returns without logging an error or reaching the next
		// subscriber.
		var dispatcher = new PluginEventDispatcher(NullLoggerFactory.Instance);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondCalls = 0;
		dispatcher.Add(new GatedHandlerPlugin(entered, release, throwOperationCanceled: true));
		dispatcher.Add(new GatedHandlerPlugin(entered, release, throwOperationCanceled: false, onEvent: _ => secondCalls++));

		var manager = new FakeSessionManager();
		dispatcher.Start(manager);
		await manager.PublishAsync(new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected, "msg"));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var dispose = dispatcher.DisposeAsync();
		await Task.Delay(100);
		release.SetResult();
		await dispose;

		Assert.Equal(0, secondCalls);
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

	/// <summary>
	/// Subscriber that signals arrival, parks until released and then optionally reacts to
	/// disposal by throwing OperationCanceledException — used to freeze the pump mid-dispatch.
	/// </summary>
	private sealed class GatedHandlerPlugin(
		TaskCompletionSource entered,
		TaskCompletionSource release,
		bool throwOperationCanceled,
		Action<SessionEvent>? onEvent = null) : IEventPlugin
	{
		public PluginInfo Info { get; } = new(
			Id: "test.gated-handler",
			Name: "Gated Handler Plugin",
			Version: new Version(1, 0, 0),
			ApiVersion: PluginApi.Current);

		public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task OnSessionEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
		{
			entered.TrySetResult();
			release.Task.Wait();
			if (throwOperationCanceled)
			{
				throw new OperationCanceledException();
			}

			onEvent?.Invoke(sessionEvent);
			return Task.CompletedTask;
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

	/// <summary>Event source that yields one event and then completes on its own.</summary>
	private sealed class CompletingSessionManager : ISessionManager
	{
		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public IReadOnlyList<BotSession> ListSessions() => [];

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public async IAsyncEnumerable<SessionEvent> SubscribeAllEvents(
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			yield return new SessionEvent(SessionEventType.StateChanged, "acct", SessionState.Connected, "only");
			// Let the enumeration end naturally instead of waiting for cancellation.
			await Task.CompletedTask;
		}
	}

	/// <summary>Event source whose subscription itself crashes with a non-cancellation error.</summary>
	private sealed class ThrowingSessionManager : ISessionManager
	{
		public Task<BotSession> GetOrCreateSessionAsync(string accountName, AccountCredentials credentials, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task<BotSession?> GetSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public Task RemoveSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public IReadOnlyList<BotSession> ListSessions() => [];

		public void SetEventCallback(SessionEventDelegate? callback)
		{
		}

		public Task<BotSession?> TryRestoreSessionAsync(string accountName, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException();

		public IAsyncEnumerable<SessionEvent> SubscribeAllEvents(CancellationToken cancellationToken = default) =>
			throw new InvalidOperationException("event source exploded");
	}
}
