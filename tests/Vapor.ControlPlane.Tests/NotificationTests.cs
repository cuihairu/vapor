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

	// ── NotificationService (EventBroker integration) ──

	[Fact]
	public async Task JobEvents_AreDeliveredToMatchingSinks()
	{
		var broker = new EventBroker();
		var sink = new RecordingSink();
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(service);
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
	public async Task SessionAndChallengeEvents_AreDeliveredWithCategoryAndState()
	{
		var broker = new EventBroker();
		var sink = new RecordingSink();
		using var service = new NotificationService(broker, new[] { sink }, NullLogger<NotificationService>.Instance);

		await StartAsync(service);
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

		await StartAsync(service);
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

		await StartAsync(service);
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

	// ── fixtures ──

	/// <summary>Starts the background service and waits until its pumps have subscribed.</summary>
	private static async Task StartAsync(NotificationService service)
	{
		await service.StartAsync(CancellationToken.None);
		await Task.Delay(100);
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

		public async Task HandleAsync(NotificationEvent notification, CancellationToken cancellationToken)
		{
			if (Handler is not null)
			{
				await Handler(notification);
			}

			Received.Add(notification);
			_events.Writer.TryWrite(notification);
		}

		/// <summary>Waits for the next delivered event (consumes it, so successive calls sequence through events).</summary>
		public Task<NotificationEvent> WaitForEventAsync() =>
			_events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
	}
}
