using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>SSE stream branch tests: job events, session events, event-type normalization.</summary>
public sealed partial class ProgramBranchCoverageTests
{
	[Fact]
	public async Task JobEventsStream_UnknownJob_Returns404Json()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.GetAsync("/v1/jobs/does-not-exist/events");

		Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
		Assert.Contains("job not found", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task JobEventsStream_DeliversPublishedEvents()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage created = await client.PostAsJsonAsync("/v1/jobs", new { action = "ping", targets = new[] { "alice" } });
		Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
		string jobId;
		using (JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
		{
			jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString()!;
		}

		using var stream = await client.GetStreamAsync($"/v1/jobs/{jobId}/events");
		using var reader = new StreamReader(stream, Encoding.UTF8);

		Assert.Equal("event: ready", await reader.ReadLineAsync());
		Assert.Equal("data: {}", await reader.ReadLineAsync());
		Assert.True(string.IsNullOrEmpty(await reader.ReadLineAsync()));

		await WaitForJobSubscriptionAsync(factory.Events);
		factory.Events.Publish(jobId, "task.started", new Dictionary<string, object?> { ["taskId"] = "t-1" });

		Assert.Equal("event: task.started", await reader.ReadLineAsync());
		string data = await reader.ReadLineAsync() ?? string.Empty;
		Assert.Contains("\"taskId\":\"t-1\"", data);
	}

	// ── session + challenge endpoints ─────────────────────────────────────────
	[Fact]
	public async Task SessionEventsStream_DeliversEventsPostedThroughTheApi()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using var stream = await client.GetStreamAsync("/v1/sessions/events?accountName=alice");
		using var reader = new StreamReader(stream, Encoding.UTF8);

		Assert.Equal("event: ready", await reader.ReadLineAsync());
		Assert.Equal("data: {}", await reader.ReadLineAsync());
		Assert.True(string.IsNullOrEmpty(await reader.ReadLineAsync()));
		await factory.Events.WaitForSessionSubscriptionAsync();

		using var agentClient = factory.CreateClient();
		agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");
		using HttpResponseMessage post = await agentClient.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "Connected",
			state = "Connected"
		});
		Assert.Equal(HttpStatusCode.OK, post.StatusCode);

		Assert.Equal("event: session.connected", await reader.ReadLineAsync());
		string data = await reader.ReadLineAsync() ?? string.Empty;
		Assert.Contains("\"accountName\":\"alice\"", data);
	}

	[Fact]
	public async Task SessionEvents_EventTypeNormalization_Variants()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Missing event type falls back to state_changed.
		using HttpResponseMessage missing = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			state = "Connected"
		});

		// Mixed-case unknown type goes through the snake-case converter.
		using HttpResponseMessage snake = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "Two-Factor Ready",
			state = "Waiting"
		});

		// Remaining mapped names.
		using HttpResponseMessage disconnected = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "Disconnected",
			state = "Disconnected"
		});
		using HttpResponseMessage twoFactor = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "TwoFactorCodeNeeded",
			state = "ConnectingWait2FA"
		});

		Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
		Assert.Equal(HttpStatusCode.OK, snake.StatusCode);
		Assert.Equal(HttpStatusCode.OK, disconnected.StatusCode);
		Assert.Equal(HttpStatusCode.OK, twoFactor.StatusCode);

		// The tracker keeps the last normalized type per account.
		using HttpResponseMessage sessions = await client.GetAsync("/v1/sessions?account=alice");
		string body = await sessions.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("2fa_required", doc.RootElement.GetProperty("sessions")[0].GetProperty("eventType").GetString());
	}

	[Fact]
	public async Task SessionEvents_MissingAccountName_Returns400()
	{
		await using BranchFactory factory = new();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			eventType = "StateChanged",
			state = "Connected"
		});

		Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
		Assert.Contains("accountName is required", await resp.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task SessionEvents_MixedCaseEventType_FallsBackToSnakeCase()
	{
		await using BranchFactory factory = new();
		using HttpClient client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage resp = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "TwoFactor Ready",
			state = "Connecting",
			message = "hi"
		});

		Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
		AuditEntry? entry = await PollAuditAsync(factory, "session.event.received");
		Assert.NotNull(entry);
	}

	private static async Task WaitForJobSubscriptionAsync(BranchEventBroker broker)
	{
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (broker.ActiveJobSubscriptions == 0)
		{
			if (DateTimeOffset.UtcNow > deadline)
			{
				throw new TimeoutException("job SSE subscription did not start within 5s");
			}

			await Task.Delay(10);
		}
	}

	/// <summary>Event broker with live job and session channels (unlike the shared
	/// recording broker, whose session stream is intentionally empty).</summary>
	internal sealed class BranchEventBroker : IEventBroker
	{
		private readonly object _gate = new();
		private readonly List<(Channel<Event> Channel, string JobId)> _jobSubscriptions = [];
		private readonly List<(Channel<SessionEvent> Channel, string? AccountName)> _sessionSubscriptions = [];
		private readonly TaskCompletionSource _sessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _jobReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public int ActiveJobSubscriptions
		{
			get { lock (_gate) { return _jobSubscriptions.Count; } }
		}

		public List<Event> Published { get; } = [];

		public Task WaitForJobSubscriptionAsync() => _jobReady.Task;
		public Task WaitForSessionSubscriptionAsync() => _sessionReady.Task;

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
		{
			var evt = new Event(Guid.NewGuid().ToString("N"), jobId, type, DateTimeOffset.UtcNow, payload);
			List<ChannelWriter<Event>> writers;
			lock (_gate)
			{
				writers = _jobSubscriptions
					.Where(sub => sub.JobId == "*" || string.Equals(sub.JobId, jobId, StringComparison.Ordinal))
					.Select(sub => sub.Channel.Writer)
					.ToList();
			}

			lock (_gate)
			{
				Published.Add(evt);
			}

			foreach (ChannelWriter<Event> writer in writers)
			{
				writer.TryWrite(evt);
			}
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
			var evt = new SessionEvent(Guid.NewGuid().ToString("N"), accountName, eventType, state, message, DateTimeOffset.UtcNow);
			List<ChannelWriter<SessionEvent>> writers;
			lock (_gate)
			{
				writers = _sessionSubscriptions
					.Where(sub => sub.AccountName == null || string.Equals(sub.AccountName, accountName, StringComparison.OrdinalIgnoreCase))
					.Select(sub => sub.Channel.Writer)
					.ToList();
			}

			foreach (ChannelWriter<SessionEvent> writer in writers)
			{
				writer.TryWrite(evt);
			}
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null, int attempt = 1)
		{
		}

		public async IAsyncEnumerable<Event> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken, string jobId)
		{
			var channel = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
			lock (_gate)
			{
				_jobSubscriptions.Add((channel, jobId));
				_jobReady.TrySetResult();
			}

			try
			{
				await foreach (Event evt in channel.Reader.ReadAllAsync(cancellationToken))
				{
					yield return evt;
				}
			}
			finally
			{
				lock (_gate)
				{
					for (int i = _jobSubscriptions.Count - 1; i >= 0; i--)
					{
						if (ReferenceEquals(_jobSubscriptions[i].Channel, channel))
						{
							_jobSubscriptions.RemoveAt(i);
						}
					}
				}

				channel.Writer.TryComplete();
			}
		}

		public async IAsyncEnumerable<SessionEvent> SubscribeSessions([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			var channel = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
			lock (_gate)
			{
				_sessionSubscriptions.Add((channel, accountName));
				_sessionReady.TrySetResult();
			}

			try
			{
				await foreach (SessionEvent evt in channel.Reader.ReadAllAsync(cancellationToken))
				{
					yield return evt;
				}
			}
			finally
			{
				lock (_gate)
				{
					for (int i = _sessionSubscriptions.Count - 1; i >= 0; i--)
					{
						if (ReferenceEquals(_sessionSubscriptions[i].Channel, channel))
						{
							_sessionSubscriptions.RemoveAt(i);
						}
					}
				}

				channel.Writer.TryComplete();
			}
		}

		public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			await Task.CompletedTask;
			yield break;
		}
	}

}
