using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Http.Json;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class ControlPlaneApiTests
{
	[Fact]
	public async Task Healthz_ReturnsOk()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/healthz");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Metrics_ExposesTaskStatusAndAgentGauges()
	{
		await using TestFactory factory = CreateFactory();
		using var client = factory.CreateClient();

		// Seed the store fake so the gauge reflects a queued task.
		((FakeJobStore)factory.Services.GetRequiredService<IJobStore>()).QueuedTasks = 1;

		using HttpResponseMessage response = await client.GetAsync("/metrics");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("text/plain", response.Content.Headers.ContentType!.ToString());
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("# TYPE vapor_controlplane_tasks_by_status gauge", body);
		Assert.Contains("vapor_controlplane_tasks_by_status{status=\"Queued\"} 1", body);
		Assert.Contains("vapor_controlplane_tasks_by_status{status=\"Running\"} 0", body);
		Assert.Contains("vapor_controlplane_agents_connected 0", body);
		Assert.Contains("vapor_controlplane_dispatch_failures_total{reason=\"no_capable_agent\"} 0", body);
		Assert.Contains("vapor_controlplane_dispatch_failures_total{reason=\"enqueue_failed\"} 0", body);
		Assert.Contains("vapor_controlplane_dispatch_failures_total{reason=\"attempts_exhausted\"} 0", body);
	}

	[Fact]
	public async Task AdminConfig_RequiresAuthorization()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/config");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task AdminConfig_ReturnsEmptyStateWithValidToken()
	{
		await using var factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/config");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.True(doc.RootElement.TryGetProperty("global", out _));
		Assert.True(doc.RootElement.TryGetProperty("accounts", out _));
	}

	[Fact]
	public async Task SessionEvents_UpdateTrackerAndExposeSessionList()
	{
		await using TestFactory factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage post = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "StateChanged",
			state = "ConnectingWait2FA",
			message = "need 2fa"
		});

		Assert.Equal(HttpStatusCode.OK, post.StatusCode);

		using HttpResponseMessage sessions = await client.GetAsync("/v1/sessions");
		Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
		string body = await sessions.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("alice", doc.RootElement.GetProperty("sessions")[0].GetProperty("accountName").GetString());
	}

	[Fact]
	public async Task SessionEvents_CreateAuthChallengeAndAllowSubmission()
	{
		await using TestFactory factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage post = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "AuthCodeNeeded",
			state = "ConnectingWaitAuthCode",
			message = "enter code"
		});

		Assert.Equal(HttpStatusCode.OK, post.StatusCode);

		using HttpResponseMessage challenges = await client.GetAsync("/v1/auth/challenges");
		Assert.Equal(HttpStatusCode.OK, challenges.StatusCode);

		using HttpResponseMessage submit = await client.PostAsJsonAsync("/v1/auth/challenges/alice/code", new
		{
			code = "123456",
			type = "2fa"
		});

		Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
		Assert.Equal(2, factory.Events.AuthChallengeEvents.Count);
		Assert.Equal("auth_code_required", factory.Events.AuthChallengeEvents[0].ChallengeType);
		Assert.Equal("code_provided_2fa", factory.Events.AuthChallengeEvents[1].ChallengeType);
		Assert.Equal("123456", factory.Events.AuthChallengeEvents[1].Code);
	}

	[Fact]
	public async Task SessionEvents_QrRequired_TracksChallengeWithQrTypeAndUrl()
	{
		await using TestFactory factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage post = await client.PostAsJsonAsync("/v1/sessions/events", new
		{
			accountName = "alice",
			eventType = "qr_required",
			state = "ConnectingWaitQr",
			message = "https://s.team/q/1/ABCDEF"
		});

		Assert.Equal(HttpStatusCode.OK, post.StatusCode);
		Assert.Equal("qr_required", Assert.Single(factory.Events.AuthChallengeEvents).ChallengeType);
		Assert.Equal("https://s.team/q/1/ABCDEF", factory.Events.AuthChallengeEvents[0].Message);
		Assert.Null(factory.Events.AuthChallengeEvents[0].Code);

		using HttpResponseMessage challenges = await client.GetAsync("/v1/auth/challenges");
		Assert.Equal(HttpStatusCode.OK, challenges.StatusCode);
		string body = await challenges.Content.ReadAsStringAsync();
		using var doc = JsonDocument.Parse(body);
		Assert.Equal("qr_required", doc.RootElement.GetProperty("challenges")[0].GetProperty("challengeType").GetString());

		// The QR challenge shows up in the session tracker too, with the challenge URL intact.
		using HttpResponseMessage sessions = await client.GetAsync("/v1/sessions?account=alice");
		string sessionsBody = await sessions.Content.ReadAsStringAsync();
		using var sessionsDoc = JsonDocument.Parse(sessionsBody);
		Assert.Equal("qr_required", sessionsDoc.RootElement.GetProperty("sessions")[0].GetProperty("eventType").GetString());
		Assert.Equal("https://s.team/q/1/ABCDEF", sessionsDoc.RootElement.GetProperty("sessions")[0].GetProperty("message").GetString());
	}

	[Fact]
	public async Task AuthChallengeEventsStream_AllowsAdminAndRedactsCodeProvidedPayload()
	{
		await using TestFactory factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using var stream = await client.GetStreamAsync("/v1/auth/challenges/events?accountName=alice");
		using var reader = new StreamReader(stream, Encoding.UTF8);

		await factory.Events.WaitForAuthSubscriptionAsync();
		factory.Events.PublishAuthChallenge("alice", "code_provided_2fa", "Auth code provided for 2fa", "123456");

		Assert.Equal("event: ready", await reader.ReadLineAsync());
		Assert.Equal("data: {}", await reader.ReadLineAsync());
		Assert.True(string.IsNullOrEmpty(await reader.ReadLineAsync()));
		Assert.Equal("event: auth.code_provided_2fa", await reader.ReadLineAsync());
		string dataLine = await reader.ReadLineAsync() ?? string.Empty;
		Assert.Contains("\"challengeType\":\"code_provided_2fa\"", dataLine);
		Assert.DoesNotContain("\"code\"", dataLine);
	}

	[Fact]
	public async Task AuthChallengeEventsStream_AllowsAgentAndFiltersNonCodeProvidedEvents()
	{
		await using TestFactory factory = CreateFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");

		using var stream = await client.GetStreamAsync("/v1/auth/challenges/events?accountName=alice");
		using var reader = new StreamReader(stream, Encoding.UTF8);

		await factory.Events.WaitForAuthSubscriptionAsync();
		factory.Events.PublishAuthChallenge("alice", "auth_code_required", "enter code");
		factory.Events.PublishAuthChallenge("alice", "code_provided_2fa", "Auth code provided for 2fa", "123456");

		Assert.Equal("event: ready", await reader.ReadLineAsync());
		Assert.Equal("data: {}", await reader.ReadLineAsync());
		Assert.True(string.IsNullOrEmpty(await reader.ReadLineAsync()));
		Assert.Equal("event: auth.code_provided_2fa", await reader.ReadLineAsync());
		string dataLine = await reader.ReadLineAsync() ?? string.Empty;
		Assert.Contains("\"challengeType\":\"code_provided_2fa\"", dataLine);
		Assert.Contains("\"code\":\"123456\"", dataLine);
	}

	private static TestFactory CreateFactory()
	{
		return new TestFactory();
	}

	[Fact]
	public async Task CreateScheduledJob_ReturnsTemplateWithNextRunAndRejectsInvalidSchedule()
	{
		using var store = new SqliteJobStore(":memory:");
		await using TestFactory factory = CreateFactory();
		factory.JobStore = store;
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage post = await client.PostAsJsonAsync("/v1/jobs", new
		{
			action = "ping",
			region = "us-east",
			targets = new[] { "alice" },
			payload = new { minutes = 30 },
			schedule = new { intervalSeconds = 300 }
		});

		Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
		string body = await post.Content.ReadAsStringAsync();
		using (var doc = JsonDocument.Parse(body))
		{
			JsonElement job = doc.RootElement.GetProperty("job");
			Assert.Equal("scheduled", job.GetProperty("status").GetString());
			Assert.Equal(300, job.GetProperty("schedule").GetProperty("intervalSeconds").GetInt32());
			Assert.True(job.TryGetProperty("nextRunAt", out JsonElement next) && next.ValueKind == JsonValueKind.String);
		}

		// Template has no tasks — nothing to dispatch until the schedule fires.
		string jobId = (await store.ListJobs(10, null, CancellationToken.None))[0].Id;
		JobWithTasks fetched = await store.GetJob(jobId, CancellationToken.None);
		Assert.Empty(fetched.Tasks);

		using HttpResponseMessage badPost = await client.PostAsJsonAsync("/v1/jobs", new
		{
			action = "ping",
			targets = new[] { "alice" },
			schedule = new { intervalSeconds = 0 }
		});

		Assert.Equal(HttpStatusCode.BadRequest, badPost.StatusCode);
		string badBody = await badPost.Content.ReadAsStringAsync();
		Assert.Contains("intervalSeconds", badBody);
	}

	// Internal so the performance benchmarks can spin up the same API host.
	internal sealed class TestFactory : WebApplicationFactory<Program>
	{
		public RecordingEventBroker Events { get; } = new();

		/// <summary>Optional store override; a stub is used when unset.</summary>
		public IJobStore? JobStore { get; set; }

		protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
		{
			builder.UseEnvironment("Development");
			builder.ConfigureServices(services =>
			{
				services.RemoveAll<IJobStore>();
				services.RemoveAll<IEventBroker>();
				services.RemoveAll<SessionTracker>();
				services.RemoveAll<AuthChallengeTracker>();
				services.AddSingleton(new Config("admin-token", new HashSet<string>(StringComparer.Ordinal) { "agent-token" }, "Data Source=:memory:", 300, false, ":memory:"));
				services.AddSingleton<IJobStore>(JobStore ?? new FakeJobStore());
				services.AddSingleton<IEventBroker>(Events);
				services.AddSingleton<SessionTracker>();
				services.AddSingleton<AuthChallengeTracker>();
			});
		}
	}

	private sealed class FakeJobStore : IJobStore
	{
		/// <summary>Queued count served by <see cref="GetTaskStatusCounts"/> (for the /metrics test).</summary>
		public int QueuedTasks { get; set; }

		public Task<JobWithTasks> CreateJob(CreateJobRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<JobWithTasks> GetJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<Job>> ListJobs(int limit, string? account, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<IReadOnlyList<JobTask>> ListRecentTasksForTarget(string target, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobTask>>([]);
		public Task<IReadOnlyList<TaskCancel>> CancelJob(string jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyDictionary<Vapor.Protocol.JobTaskStatus, int>> GetTaskStatusCounts(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyDictionary<Vapor.Protocol.JobTaskStatus, int>>(
				new Dictionary<Vapor.Protocol.JobTaskStatus, int> { [Vapor.Protocol.JobTaskStatus.Queued] = QueuedTasks });
		public Task<IReadOnlyList<Job>> ListDueScheduledJobs(DateTimeOffset now, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);
		public Task<bool> HasActiveChildJob(string templateJobId, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<Job?> TriggerScheduledJob(string templateJobId, DateTimeOffset nextRunAt, IReadOnlyDictionary<string, string>? extraMeta, CancellationToken cancellationToken) => Task.FromResult<Job?>(null);
		public Task<bool> AdvanceSchedule(string templateJobId, DateTimeOffset nextRunAt, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<JobTask?> ClaimNextQueuedTask(string region, CancellationToken cancellationToken) => Task.FromResult<JobTask?>(null);
		public Task RequeueTask(string taskId, TimeSpan? retryDelay, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<int> RequeueStaleRunningTasks(TimeSpan taskLease, CancellationToken cancellationToken) => Task.FromResult(0);
		public Task<bool> HeartbeatTask(string taskId, int attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> SetTaskResult(TaskResult result, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<(JobTask Task, Job Job)> FailRunningTask(string taskId, string error, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	internal sealed class RecordingEventBroker : IEventBroker
	{
		private readonly object _gate = new();
		private readonly List<AuthSubscription> _authSubscriptions = [];
		private readonly List<JobSubscription> _jobSubscriptions = [];

		public List<Event> Events { get; } = [];
		public List<AuthChallengeEvent> AuthChallengeEvents { get; } = [];
		private readonly TaskCompletionSource _authSubscriptionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Active job-event subscriptions (mirrors <see cref="EventBroker"/> semantics; used by benchmarks).</summary>
		public int ActiveJobSubscriptions
		{
			get { lock (_gate) { return _jobSubscriptions.Count; } }
		}

		public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
		{
			var evt = new Event(Guid.NewGuid().ToString("N"), jobId, type, DateTimeOffset.UtcNow, payload);
			Events.Add(evt);

			// Mirror the real broker: exact-jobId subscribers plus "*" global ones.
			List<ChannelWriter<Event>> writers;
			lock (_gate)
			{
				writers = _jobSubscriptions
					.Where(sub => sub.JobId == "*" || string.Equals(sub.JobId, jobId, StringComparison.Ordinal))
					.Select(sub => sub.Channel.Writer)
					.ToList();
			}

			foreach (var writer in writers)
			{
				_ = writer.TryWrite(evt);
			}
		}

		public void PublishSession(string accountName, string eventType, string state, string? message = null)
		{
			Events.Add(new Event(Guid.NewGuid().ToString("N"), null, $"session.{eventType}", DateTimeOffset.UtcNow, new Dictionary<string, object?>
			{
				["accountName"] = accountName,
				["state"] = state,
				["message"] = message
			}));
		}

		public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null)
		{
			var evt = new AuthChallengeEvent(Guid.NewGuid().ToString("N"), accountName, challengeType, message, code, DateTimeOffset.UtcNow, null);
			AuthChallengeEvents.Add(evt);

			List<ChannelWriter<AuthChallengeEvent>> writers;
			lock (_gate)
			{
				writers = _authSubscriptions
					.Where(sub => sub.AccountName == null || string.Equals(sub.AccountName, accountName, StringComparison.OrdinalIgnoreCase))
					.Select(sub => sub.Channel.Writer)
					.ToList();
			}

			foreach (var writer in writers)
			{
				_ = writer.TryWrite(evt);
			}
		}

		public async IAsyncEnumerable<Event> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken, string jobId)
		{
			var channel = Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
			var subscription = new JobSubscription(channel, jobId);

			lock (_gate)
			{
				_jobSubscriptions.Add(subscription);
			}

			try
			{
				await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
				{
					yield return evt;
				}
			}
			finally
			{
				lock (_gate)
				{
					_jobSubscriptions.Remove(subscription);
				}
				channel.Writer.TryComplete();
			}
		}

		public IAsyncEnumerable<SessionEvent> SubscribeSessions(CancellationToken cancellationToken, string? accountName = null) => Empty<SessionEvent>();
		public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
		{
			var channel = Channel.CreateUnbounded<AuthChallengeEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
			var subscription = new AuthSubscription(channel, accountName);

			lock (_gate)
			{
				_authSubscriptions.Add(subscription);
				_authSubscriptionReady.TrySetResult();
			}

			try
			{
				await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
				{
					yield return evt;
				}
			}
			finally
			{
				lock (_gate)
				{
					_authSubscriptions.Remove(subscription);
				}
				channel.Writer.TryComplete();
			}
		}

		public Task WaitForAuthSubscriptionAsync() => _authSubscriptionReady.Task;

		private static async IAsyncEnumerable<T> Empty<T>()
		{
			await Task.CompletedTask;
			yield break;
		}

		private sealed record AuthSubscription(Channel<AuthChallengeEvent> Channel, string? AccountName);
		private sealed record JobSubscription(Channel<Event> Channel, string JobId);
	}
}
