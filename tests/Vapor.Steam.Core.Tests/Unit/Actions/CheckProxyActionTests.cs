using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Actions;

/// <summary>
/// Example coverage for CheckProxyAction over an injected probe seam: the
/// no-proxy short circuit, payload proxy winning over the configured one,
/// malformed proxy failing with the parse error, success output carrying the
/// masked endpoint (never the raw credentials), and probe failures mapping to
/// (success=false, error, still-informative output). The live two-stage probe
/// itself is exercised through the HandlerOverride transport seam — a fake
/// HttpMessageHandler answering canned responses, so no network is touched.
/// </summary>
public sealed class CheckProxyActionTests : IDisposable
{
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public async Task ExecuteAsync_WithoutAnyProxy_ReportsProxyDisabled()
	{
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance);
		var session = CreateSession(proxy: null);

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal(false, result.Output["proxyEnabled"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithConfiguredProxy_ProbesIt_WithoutPayloadOverride()
	{
		var probed = new List<ProxyOptions>();
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance)
		{
			ProbeOverride = (options, _) =>
			{
				probed.Add(options);
				return Task.FromResult(new ProxyProbeResult("203.0.113.7", true, 42, null));
			}
		};
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		var options = Assert.Single(probed);
		Assert.Equal(1080, options.Port);
		Assert.Equal("203.0.113.7", result.Output!["exitIp"]);
		Assert.Equal(true, result.Output["steamReachable"]);
		Assert.Equal(42L, result.Output["latencyMs"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithPayloadOverride_PayloadWins()
	{
		var probed = new List<ProxyOptions>();
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance)
		{
			ProbeOverride = (options, _) =>
			{
				probed.Add(options);
				return Task.FromResult(new ProxyProbeResult("198.51.100.2", true, 5, null));
			}
		};
		var session = CreateSession(proxy: "socks5://configured.example.com:1080");

		var result = await action.ExecuteAsync(
			session, new Dictionary<string, object?> { ["proxy"] = "http://override.example.com:8080" }, CancellationToken.None);

		Assert.True(result.Success);
		var options = Assert.Single(probed);
		Assert.Equal(ProxyScheme.Http, options.Scheme);
		Assert.Equal("override.example.com", options.Host);
	}

	[Fact]
	public async Task ExecuteAsync_WithMalformedProxy_FailsWithParseError()
	{
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance);
		var session = CreateSession(proxy: null);

		var result = await action.ExecuteAsync(
			session, new Dictionary<string, object?> { ["proxy"] = "not-a-proxy" }, CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("scheme://host:port", result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_OnSuccess_OutputCarriesMaskedProxyOnly()
	{
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance)
		{
			ProbeOverride = (_, _) => Task.FromResult(new ProxyProbeResult("203.0.113.7", true, 42, null))
		};
		var session = CreateSession(proxy: "socks5://john:s3cret@10.0.0.9:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		var masked = (string?)result.Output!["proxy"];
		Assert.Equal("socks5://john:<redacted>@10.0.0.9:1080", masked);
	}

	[Fact]
	public async Task ExecuteAsync_WhenProbeFails_MapsToFailureWithErrorOutput()
	{
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance)
		{
			ProbeOverride = (_, _) => Task.FromResult(new ProxyProbeResult(null, false, null, "exit-ip probe failed: no route"))
		};
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("no route", result.Error);
		Assert.NotNull(result.Output);
		Assert.Equal("exit-ip probe failed: no route", result.Output!["error"]);
	}

	[Fact]
	public async Task ExecuteAsync_FailedProbeWithoutError_FallsBackToGenericMessage()
	{
		// Probe completes but reports neither an exit IP nor an error: the
		// result.Error ?? fallback arm fires.
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance)
		{
			ProbeOverride = (_, _) => Task.FromResult(new ProxyProbeResult(null, false, null, null))
		};
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("proxy probe did not complete", result.Error);
	}

	[Fact]
	public async Task ExecuteAsync_WhenProbeThrows_MapsToFailureWithoutThrowing()
	{
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance)
		{
			ProbeOverride = (_, _) => throw new InvalidOperationException("socket exploded")
		};
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.NotNull(result.Output);
		Assert.Contains("socket exploded", (string?)result.Output["error"]);
	}

	[Fact]
	public void Metadata_DescribesProxyCheck()
	{
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance);

		Assert.Equal("check_proxy", action.Name);
		Assert.Equal("check_proxy", action.Metadata.Name);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(30, action.Metadata.TimeoutSeconds);
	}

	[Fact]
	public async Task ExecuteAsync_WhenCancellationIsExternal_RethrowsOperationCanceled()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance)
		{
			ProbeOverride = (_, ct) => Task.FromCanceled<ProxyProbeResult>(ct)
		};
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		// Task.FromCanceled surfaces as TaskCanceledException (an OCE subclass).
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => action.ExecuteAsync(session, new Dictionary<string, object?>(), cts.Token));
	}

	[Fact]
	public async Task ExecuteAsync_WithoutProbeOverride_LiveProbeReportsCancellationAsProbeFailure()
	{
		// No ProbeOverride: the ?? right arm binds the live ProbeAsync method group.
		// The pre-cancelled token makes the probe's first GetAsync throw before any
		// network I/O is attempted; ProbeAsync's internal catch folds that into a
		// failed probe result, so the action completes with success=false. With no
		// HandlerOverride either, this also covers the live SocketsHttpHandler arm
		// of the transport seam (constructed, never connected).
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance);
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), cts.Token);

		Assert.False(result.Success);
		Assert.NotNull(result.Output);
		Assert.Equal(false, result.Output["steamReachable"]);
		Assert.Contains("exit-ip probe failed", (string?)result.Output["error"]);
	}

	[Fact]
	public async Task ExecuteAsync_LiveProbe_FullSuccess_ReportsExitIpLatencyAndBothEndpoints()
	{
		var handler = new FakeHandler(call => call == 0
			? TextResponse("203.0.113.7")
			: new HttpResponseMessage(HttpStatusCode.OK));
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance) { HandlerOverride = () => handler };
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Equal("203.0.113.7", result.Output!["exitIp"]);
		Assert.Equal(true, result.Output["steamReachable"]);
		Assert.IsType<long>(result.Output["latencyMs"]);
		Assert.Equal(new[] { "https://api.ipify.org/", "https://steamcommunity.com/" }, handler.RequestedUris);
	}

	[Fact]
	public async Task ExecuteAsync_LiveProbe_SteamServerSideError_ReportsUnreachableButKeepsExitIp()
	{
		// A 5xx from Steam is reachable-but-degraded: the probe completes with
		// steamReachable=false and no error text, so the action falls back to its
		// generic message while the observed exit IP survives into the output.
		var handler = new FakeHandler(call => call == 0
			? TextResponse("198.51.100.2")
			: new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance) { HandlerOverride = () => handler };
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("proxy probe did not complete", result.Error);
		Assert.Equal("198.51.100.2", result.Output!["exitIp"]);
		Assert.Equal(false, result.Output["steamReachable"]);
	}

	[Fact]
	public async Task ExecuteAsync_LiveProbe_SteamProbeThrows_ReportsSteamFailureWithExitIpAndLatency()
	{
		var handler = new FakeHandler(call => call == 0
			? TextResponse("203.0.113.7")
			: throw new HttpRequestException("connection reset"));
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance) { HandlerOverride = () => handler };
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("steam probe failed: connection reset", result.Error);
		Assert.Equal("203.0.113.7", result.Output!["exitIp"]);
		Assert.Equal(false, result.Output["steamReachable"]);
		Assert.IsType<long>(result.Output["latencyMs"]);
	}

	[Fact]
	public async Task ExecuteAsync_LiveProbe_ExitIpFailure_StopsBeforeSteamProbe()
	{
		var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
		var action = new CheckProxyAction(NullLogger<CheckProxyAction>.Instance) { HandlerOverride = () => handler };
		var session = CreateSession(proxy: "socks5://gw.example.com:1080");

		var result = await action.ExecuteAsync(session, new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("exit-ip probe failed", result.Error);
		Assert.Null(result.Output!["exitIp"]);
		Assert.Equal(false, result.Output["steamReachable"]);
		Assert.Equal("https://api.ipify.org/", Assert.Single(handler.RequestedUris));
	}

	/// <summary>
	/// In-memory transport for the live-probe tests: records the requested URIs
	/// in order and answers each call from a script, so the probe's two-stage
	/// flow (exit IP, then Steam reachability) runs without any network.
	/// </summary>
	private sealed class FakeHandler : HttpMessageHandler
	{
		private readonly Func<int, HttpResponseMessage> _respond;

		public FakeHandler(Func<int, HttpResponseMessage> respond) => _respond = respond;

		public List<string> RequestedUris { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestedUris.Add(request.RequestUri!.ToString());
			return Task.FromResult(_respond(RequestedUris.Count - 1));
		}
	}

	private static HttpResponseMessage TextResponse(string text) =>
		new(HttpStatusCode.OK) { Content = new StringContent(text) };

	private BotSession CreateSession(string? proxy)
	{
		var session = new BotSession(
			"test_account",
			new AccountCredentials("test_account", "password", Proxy: proxy),
			new Mock<IActionRegistry>(MockBehavior.Loose).Object,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: null,
			eventCallback: null);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
