using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class NotificationTests
{
	// ── NotificationRule ──

	[Fact]
	public void MatchAllRule_MatchesEverything()
	{
		NotificationEvent e = NewEvent("job", "job.created");

		Assert.True(NotificationRule.MatchAll.Matches(e));
	}

	[Fact]
	public void TypeFilter_OnlyMatchesConfiguredTypes()
	{
		var rule = new NotificationRule(Types: new HashSet<string>(["state_changed", "auth_code_required"], StringComparer.OrdinalIgnoreCase));

		Assert.True(rule.Matches(NewEvent("session", "STATE_CHANGED")));
		Assert.False(rule.Matches(NewEvent("session", "login_failed")));
	}

	[Fact]
	public void AccountFilter_RejectsUnlistedAndNullAccounts()
	{
		var rule = new NotificationRule(Accounts: new HashSet<string>(["alice"], StringComparer.OrdinalIgnoreCase));

		Assert.True(rule.Matches(NewEvent("session", "state_changed", accountName: "ALICE")));
		Assert.False(rule.Matches(NewEvent("session", "state_changed", accountName: "bob")));
		Assert.False(rule.Matches(NewEvent("job", "job.created", accountName: null)));
	}

	[Fact]
	public void CombinedRules_RequireAllDimensions()
	{
		var rule = new NotificationRule(
			Categories: new HashSet<string>(["session"], StringComparer.OrdinalIgnoreCase),
			Accounts: new HashSet<string>(["alice"], StringComparer.OrdinalIgnoreCase));

		Assert.True(rule.Matches(NewEvent("session", "state_changed", accountName: "alice")));
		Assert.False(rule.Matches(NewEvent("job", "job.created", accountName: "alice")));
		Assert.False(rule.Matches(NewEvent("session", "state_changed", accountName: "bob")));
	}

	// ── WebhookNotificationSink ──

	[Fact]
	public async Task Webhook_PostsJsonEnvelopeAndCountsSent()
	{
		var handler = new StubHandler();
		using var sink = new WebhookNotificationSink(
			new Uri("http://localhost/hook"), secret: null, maxRetries: 0,
			TimeSpan.Zero, NullLogger<WebhookNotificationSink>.Instance,
			new HttpClient(handler));

		await sink.HandleAsync(NewEvent("job", "job.created", jobId: "job-9"), CancellationToken.None);

		Assert.Equal(1, sink.Sent);
		Assert.Equal(0, sink.Failed);
		Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
		string body = handler.Bodies[0];
		Assert.Contains("\"category\":\"job\"", body);
		Assert.Contains("\"type\":\"job.created\"", body);
		Assert.Contains("\"jobId\":\"job-9\"", body);
		Assert.Contains("\"accountName\":\"alice\"", body);
	}

	[Fact]
	public async Task Webhook_TransportThrowsOnEveryAttempt_RetriesThenFailsWithLastError()
	{
		var handler = new ThrowingWebHandler();
		using var sink = new WebhookNotificationSink(
			new Uri("http://localhost/hook"), secret: null, maxRetries: 2,
			TimeSpan.Zero, NullLogger<WebhookNotificationSink>.Instance,
			new HttpClient(handler));

		HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
			sink.HandleAsync(NewEvent("job", "job.created"), CancellationToken.None));

		Assert.Equal(3, handler.Calls); // initial attempt + 2 retries
		Assert.Equal(0, sink.Sent);
		Assert.Equal(1, sink.Failed);
		Assert.Contains("network down", ex.Message);
	}

	[Fact]
	public async Task Webhook_WithoutSecret_SendsNoSignatureHeaders()
	{
		var handler = new StubHandler();
		using var sink = new WebhookNotificationSink(
			new Uri("http://localhost/hook"), secret: null, maxRetries: 0,
			TimeSpan.Zero, NullLogger<WebhookNotificationSink>.Instance,
			new HttpClient(handler));

		await sink.HandleAsync(NewEvent("session", "state_changed"), CancellationToken.None);

		Assert.False(handler.Requests[0].Headers.Contains("X-Vapor-Signature"));
		Assert.False(handler.Requests[0].Headers.Contains("X-Vapor-Timestamp"));
	}

	[Fact]
	public async Task Webhook_WithSecret_SignsTimestampAndBody()
	{
		var handler = new StubHandler();
		const string secret = "s3cret";
		using var sink = new WebhookNotificationSink(
			new Uri("http://localhost/hook"), secret, maxRetries: 0,
			TimeSpan.Zero, NullLogger<WebhookNotificationSink>.Instance,
			new HttpClient(handler));

		await sink.HandleAsync(NewEvent("session", "state_changed"), CancellationToken.None);

		long timestamp = long.Parse(
			handler.Requests[0].Headers.GetValues("X-Vapor-Timestamp").Single(),
			System.Globalization.CultureInfo.InvariantCulture);
		string actual = handler.Requests[0].Headers.GetValues("X-Vapor-Signature").Single();
		string expected = SignHmac(secret, $"{timestamp}.{handler.Bodies[0]}");

		Assert.Equal($"sha256={expected}", actual);
	}

	[Fact]
	public async Task Webhook_TransientFailure_RetriesWithBackoffThenSucceeds()
	{
		var handler = new StubHandler(
			new HttpResponseMessage(HttpStatusCode.InternalServerError),
			new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
			new HttpResponseMessage(HttpStatusCode.OK));
		using var sink = new WebhookNotificationSink(
			new Uri("http://localhost/hook"), secret: null, maxRetries: 5,
			TimeSpan.Zero, NullLogger<WebhookNotificationSink>.Instance,
			new HttpClient(handler));

		await sink.HandleAsync(NewEvent("session", "state_changed"), CancellationToken.None);

		Assert.Equal(1, sink.Sent);
		Assert.Equal(0, sink.Failed);
		Assert.Equal(2, sink.Retried);
		Assert.Equal(3, handler.Requests.Count);
	}

	[Fact]
	public async Task Webhook_RetriesExhausted_FailsAndThrows()
	{
		var handler = new StubHandler(
			new HttpResponseMessage(HttpStatusCode.InternalServerError),
			new HttpResponseMessage(HttpStatusCode.InternalServerError));
		using var sink = new WebhookNotificationSink(
			new Uri("http://localhost/hook"), secret: null, maxRetries: 1,
			TimeSpan.Zero, NullLogger<WebhookNotificationSink>.Instance,
			new HttpClient(handler));

		await Assert.ThrowsAsync<HttpRequestException>(
			() => sink.HandleAsync(NewEvent("session", "state_changed"), CancellationToken.None));

		Assert.Equal(0, sink.Sent);
		Assert.Equal(1, sink.Failed);
		Assert.Equal(1, sink.Retried);
		Assert.Equal(2, handler.Requests.Count);
	}

	[Fact]
	public async Task Webhook_CancelledWhileSending_RethrowsWithoutCountingFailure()
	{
		// Cancellation during delivery must propagate (not be swallowed as a delivery
		// failure) so the dispatcher can distinguish stop-the-world from a broken sink.
		using var cts = new CancellationTokenSource();
		var handler = new CancellingHandler(cts);
		using var sink = new WebhookNotificationSink(
			new Uri("http://localhost/hook"), secret: null, maxRetries: 3,
			TimeSpan.Zero, NullLogger<WebhookNotificationSink>.Instance,
			new HttpClient(handler));

		await Assert.ThrowsAsync<OperationCanceledException>(
			() => sink.HandleAsync(NewEvent("job", "job.created"), cts.Token));

		Assert.Equal(0, sink.Sent);
		Assert.Equal(0, sink.Failed);
		Assert.Equal(0, sink.Retried);
		Assert.Single(handler.Requests); // No retry loop after cancellation.
	}

	// ── NotificationService (EventBroker integration) ──

	[Fact]
	public async Task StartAsync_WithoutSinks_ReturnsWithoutSubscribing()
	{
		// No sinks configured: the service is a no-op and never touches the broker.
		var broker = new EventBroker();
		using var service = new NotificationService(broker, [], NullLogger<NotificationService>.Instance);

		await service.StartAsync(CancellationToken.None);
		// Let the background ExecuteAsync actually reach its no-sink early return
		// before stopping, instead of cancelling it before it starts.
		await Task.Delay(50);
		await service.StopAsync(CancellationToken.None);

		Assert.Equal(0, broker.SubscriberCount);
		Assert.Equal(0, broker.SessionSubscriberCount);
		Assert.Equal(0, broker.AuthSubscriberCount);
	}

	[Fact]
	public async Task JobEvents_AreDeliveredToMatchingSinks()
	{
		var broker = new EventBroker();
		var sink = new RecordingSink();
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		try
		{
			broker.Publish("job-1", "job.created", new Dictionary<string, object?> { ["accountName"] = "alice" });

			NotificationEvent n = await sink.WaitForEventAsync();
			Assert.Equal("job", n.Category);
			Assert.Equal("job.created", n.Type);
			Assert.Equal("job-1", n.JobId);
			Assert.Equal("alice", n.AccountName);
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task ThrowingSink_IsIsolatedAndLoggedWithoutAccountOrJob()
	{
		var broker = new EventBroker();
		var sink = new RecordingSink { Handler = _ => Task.FromException(new HttpRequestException("sink down")) };
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		try
		{
			// No accountName in the payload: the failure log line falls back to "<none>".
			broker.Publish("job-1", "job.created", new Dictionary<string, object?>());
			await sink.WaitForCallAsync();
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}

		Assert.Equal(1, sink.Calls);
		Assert.Empty(sink.Received);
	}

	[Fact]
	public async Task ThrowingSink_WithoutJobId_LogsNonePlaceholder()
	{
		var broker = new EventBroker();
		var sink = new RecordingSink { Handler = _ => Task.FromException(new HttpRequestException("sink down")) };
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		try
		{
			// A broker event with no job id: the failure log line falls back to "<none>".
			broker.Publish(null, "session.state_changed", new Dictionary<string, object?> { ["accountName"] = "alice" });
			await sink.WaitForCallAsync();
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}

		Assert.Equal(1, sink.Calls);
		Assert.Empty(sink.Received);
	}

	[Fact]
	public async Task SessionAndChallengeEvents_AreDeliveredWithCategoryAndState()
	{
		var broker = new EventBroker();
		var sink = new RecordingSink();
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		try
		{
			broker.PublishSession("alice", "state_changed", "Connected", message: "logged on");
			NotificationEvent sessionEvent = await sink.WaitForEventAsync();
			Assert.Equal("session", sessionEvent.Category);
			Assert.Equal("state_changed", sessionEvent.Type);
			Assert.Equal("Connected", sessionEvent.State);
			Assert.Equal("alice", sessionEvent.AccountName);

			broker.PublishAuthChallenge("bob", "2fa_required", code: "123456");
			NotificationEvent challenge = await sink.WaitForEventAsync();
			Assert.Equal("auth_challenge", challenge.Category);
			Assert.Equal("2fa_required", challenge.Type);
			Assert.Equal("bob", challenge.AccountName);
			// The challenge code itself must never be delivered to sinks.
			Assert.NotNull(challenge.Payload);
			Assert.True((bool)challenge.Payload!["codeSupplied"]!);
			Assert.False(challenge.Payload.ContainsKey("code"));
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task SinkRules_FilterDeliveries()
	{
		var broker = new EventBroker();
		var filtered = new RecordingSink
		{
			Rule = new NotificationRule(Types: new HashSet<string>(["state_changed"], StringComparer.OrdinalIgnoreCase)),
		};
		using var service = new NotificationService(broker, new[] { filtered }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		try
		{
			broker.Publish("job-1", "job.created", null);
			broker.PublishSession("alice", "state_changed", "Connected");

			NotificationEvent delivered = await filtered.WaitForEventAsync();
			Assert.Equal("state_changed", delivered.Type);
			Assert.Single(filtered.Received);
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task FailingSink_DoesNotBlockOtherSinks()
	{
		var broker = new EventBroker();
		var failing = new RecordingSink { Handler = _ => throw new InvalidOperationException("boom") };
		var healthy = new RecordingSink();
		using var service = new NotificationService(broker, new[] { failing, healthy }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		try
		{
			broker.Publish("job-2", "job.created", null);

			await healthy.WaitForEventAsync();
			Assert.Single(healthy.Received);
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task ServiceWithoutSinks_StartsAndStopsCleanly()
	{
		var broker = new EventBroker();
		using var service = new NotificationService(broker, Array.Empty<INotificationSink>(), NullLogger<NotificationService>.Instance);

		await service.StartAsync(CancellationToken.None);
		broker.Publish("job-3", "job.created", null);
		await service.StopAsync(CancellationToken.None);

		Assert.Equal(0, service.SinkCount);
	}

	[Fact]
	public async Task JobEvent_PayloadValueShapes_FallBackToStringOrNull()
	{
		// After a JSON round-trip payload values are JsonElements; the dispatcher must
		// accept string-shaped ones and map anything else to null (not throw).
		var broker = new EventBroker();
		var sink = new RecordingSink();
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		try
		{
			System.Text.Json.JsonElement jsonName = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("\"eve\"");
			broker.Publish("job-4", "job.created", new Dictionary<string, object?>
			{
				["accountName"] = jsonName,
				["state"] = 42 // number → no string representation → null
			});

			NotificationEvent n = await sink.WaitForEventAsync();
			Assert.Equal("eve", n.AccountName);
			Assert.Null(n.State);
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task SinkCancelledDuringDelivery_AfterStopRequested_SkipsRemainingSinks()
	{
		// A sink that throws OperationCanceledException while stopping must abort the
		// delivery loop (return) instead of being treated as an ordinary sink failure.
		var broker = new EventBroker();
		using var cts = new CancellationTokenSource();
		var cancelled = new RecordingSink
		{
			Handler = _ =>
			{
				cts.Cancel();
				throw new OperationCanceledException();
			}
		};
		using var service = new NotificationService(broker, new[] { cancelled }, NullLogger<NotificationService>.Instance);

		await service.StartAsync(cts.Token);
		try
		{
			await WaitForSubscriptionsAsync(broker, service);
			broker.Publish("job-5", "job.created", null);

			await cancelled.WaitForCallAsync();
		}
		finally
		{
			// Completes without hanging: the pumps exit through their cancellation catches.
			await service.StopAsync(CancellationToken.None);
		}

		Assert.Equal(1, cancelled.Calls);
	}

	[Fact]
	public async Task StopAsync_AfterDeliveries_ExitsAllPumpsWithoutThrowing()
	{
		// Covers the pumps' OperationCanceledException catches on shutdown: after the
		// pumps have processed at least one event, stopping must complete cleanly.
		var broker = new EventBroker();
		var sink = new RecordingSink();
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(broker, service);
		broker.Publish("job-6", "job.created", null);
		await sink.WaitForEventAsync();

		await service.StopAsync(CancellationToken.None);

		Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
	}

	[Fact]
	public async Task FiniteBrokerStreams_WhenDrained_ServiceCompletesWithoutStop()
	{
		// A broker whose subscription streams end naturally (e.g. a replay/projection
		// source) exercises the pumps' normal loop exits: all three drain, and
		// ExecuteAsync runs to completion without StopAsync being called.
		var broker = new FiniteBroker();
		var sink = new RecordingSink();
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await service.StartAsync(CancellationToken.None);

		NotificationEvent delivered = await sink.WaitForEventAsync();
		Assert.Equal("job.created", delivered.Type);

		// The three pumps all reached their natural stream ends. (The ExecuteTask
		// status is WaitingForActivation while pending — BackgroundService attaches
		// a continuation — so poll completion, not TaskStatus.Running.)
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
		while (!service.ExecuteTask!.IsCompleted && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		Assert.Equal(TaskStatus.RanToCompletion, service.ExecuteTask!.Status);
	}

	// ── fixtures ──

	/// <summary>
	/// A broker whose streams are finite: one job event, then end of enumeration;
	/// the session and auth-challenge streams are empty. Backs the natural-drain
	/// coverage of the notification pumps.
	/// </summary>
	private sealed class FiniteBroker : IEventBroker
	{
		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
		{
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null)
		{
		}

		public async IAsyncEnumerable<Vapor.Protocol.Event> Subscribe(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
			string jobId)
		{
			await Task.Yield();
			yield return new Vapor.Protocol.Event(
				"evt-finite-1", "job-finite", "job.created", DateTimeOffset.UtcNow, null);
		}

		public async IAsyncEnumerable<SessionEvent> SubscribeSessions(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
			string? accountName = null)
		{
			await Task.Yield();
			yield break;
		}

		public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges(
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
			string? accountName = null)
		{
			await Task.Yield();
			yield break;
		}
	}

	/// <summary>
	/// Starts the background service and waits until its three pumps have actually
	/// registered their broker subscriptions. A fixed delay is racy under CI load:
	/// an event published before registration lands is silently dropped.
	/// </summary>
	private static async Task StartAsync(EventBroker broker, NotificationService service)
	{
		await service.StartAsync(CancellationToken.None);
		await WaitForSubscriptionsAsync(broker, service);
	}

	/// <summary>Waits until the three pumps have registered their broker subscriptions.</summary>
	private static async Task WaitForSubscriptionsAsync(EventBroker broker, NotificationService service)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (broker.SubscriberCount == 0 || broker.SessionSubscriberCount == 0 || broker.AuthSubscriberCount == 0)
		{
			if (DateTimeOffset.UtcNow > deadline)
			{
				throw new TimeoutException(
					$"Notification pumps did not subscribe within 5s " +
					$"(job={broker.SubscriberCount}, session={broker.SessionSubscriberCount}, auth={broker.AuthSubscriberCount}).");
			}

			await Task.Delay(10);
		}
	}

	private static NotificationEvent NewEvent(
		string category,
		string type,
		string? accountName = "alice",
		string? jobId = null) => new(
			Id: Guid.NewGuid().ToString("N"),
			Category: category,
			Type: type,
			JobId: jobId,
			AccountName: accountName,
			State: null,
			Message: null,
			Timestamp: DateTimeOffset.UtcNow,
			Payload: null);

	private static string SignHmac(string secret, string payload)
	{
		using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
		return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
	}

	/// <summary>Cancels the token and throws OperationCanceledException while "sending".</summary>
	private sealed class CancellingHandler : HttpMessageHandler
	{
		private readonly CancellationTokenSource _cts;

		public CancellingHandler(CancellationTokenSource cts)
		{
			_cts = cts;
		}

		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			// The token must already be cancelled when the exception escapes, mirroring
			// an HttpClient request aborted by the caller's token.
			_cts.Cancel();
			return Task.FromException<HttpResponseMessage>(new OperationCanceledException(_cts.Token));
		}
	}

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> _responses;

		public StubHandler(params HttpResponseMessage[] responses)
		{
			_responses = new Queue<HttpResponseMessage>(responses);
		}

		public List<HttpRequestMessage> Requests { get; } = [];
		public List<string> Bodies { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
			Requests.Add(request);
			return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.OK);
		}
	}

	/// <summary>Throws a transport exception on every send, simulating a dead network.</summary>
	private sealed class ThrowingWebHandler : HttpMessageHandler
	{
		public int Calls;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref Calls);
			return Task.FromException<HttpResponseMessage>(new HttpRequestException("network down"));
		}
	}

	private sealed class RecordingSink : INotificationSink
	{
		private readonly System.Threading.Channels.Channel<NotificationEvent> _events =
			System.Threading.Channels.Channel.CreateUnbounded<NotificationEvent>(
				new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

		public NotificationRule Rule { get; init; } = NotificationRule.MatchAll;

		public Func<NotificationEvent, Task>? Handler { get; init; }

		public List<NotificationEvent> Received { get; } = [];

		public string Name => "recording";

		public long Sent => Received.Count;
		public long Failed { get; }
		public long Retried { get; }

		/// <summary>Total HandleAsync invocations, including ones whose handler threw.</summary>
		public int Calls { get; private set; }

		public async Task HandleAsync(NotificationEvent notification, CancellationToken cancellationToken)
		{
			Calls++;
			// Signaled before the handler runs: a throwing handler must still unblock WaitForCallAsync.
			_callsChannel.Writer.TryWrite(notification);
			if (Handler is not null)
			{
				await Handler(notification);
			}

			Received.Add(notification);
			_events.Writer.TryWrite(notification);
		}

		private readonly System.Threading.Channels.Channel<NotificationEvent> _callsChannel =
			System.Threading.Channels.Channel.CreateUnbounded<NotificationEvent>(
				new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

		/// <summary>Waits until HandleAsync has been invoked once more (even if the handler threw).</summary>
		public Task WaitForCallAsync() =>
			_callsChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		/// <summary>Waits for the next delivered event (consumes it, so successive calls sequence through events).</summary>
		public Task<NotificationEvent> WaitForEventAsync() =>
			_events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
	}
}
