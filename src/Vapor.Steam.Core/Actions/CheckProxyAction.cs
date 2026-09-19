using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Utilities;
using Vapor.Steam.Core.Web;

namespace Vapor.Steam.Core.Actions;

/// <summary>
/// Connectivity self-check for the account's egress proxy: resolves the proxy
/// from the payload (or the session's configured value), opens the same kind of
/// proxied HttpClient stack the session itself uses, and reports the observed
/// exit IP, Steam reachability and round-trip latency. The proxy endpoint in the
/// output is always the masked form — credentials never leave the agent.
/// </summary>
public sealed class CheckProxyAction : IAction
{
	private readonly ILogger<CheckProxyAction> _logger;

	public CheckProxyAction(ILogger<CheckProxyAction> logger)
	{
		_logger = logger;
	}

	public string Name => "check_proxy";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Checks the account's proxy: verifies reachability and reports the exit IP, Steam reachability and latency",
		RequiresLogin: false,
		TimeoutSeconds: 30
	);

	// Test seam: stands in for the live network probes.
	internal Func<ProxyOptions, CancellationToken, Task<ProxyProbeResult>>? ProbeOverride { get; set; }

	public async Task<ActionResult> ExecuteAsync(
		BotSession session,
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		var rawProxy = PayloadReader.GetString(payload, "proxy") ?? session.ConfiguredProxy;

		if (string.IsNullOrWhiteSpace(rawProxy))
		{
			return new ActionResult(true, null, new Dictionary<string, object?>
			{
				["proxyEnabled"] = false,
				["account"] = session.AccountName
			});
		}

		ProxyOptions options;
		try
		{
			options = ProxyOptions.Parse(rawProxy, "proxy");
		}
		catch (ArgumentException ex)
		{
			return new ActionResult(false, ex.Message, null);
		}

		var probe = ProbeOverride ?? ProbeAsync;
		ProxyProbeResult result;
		try
		{
			result = await probe(options, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Proxy check for {AccountName} through {Proxy} failed", session.AccountName, options.ToString());
			result = new ProxyProbeResult(null, false, null, ex.Message);
		}

		var output = new Dictionary<string, object?>
		{
			["proxyEnabled"] = true,
			["proxy"] = options.ToString(),
			["scheme"] = options.Scheme.ToString(),
			["account"] = session.AccountName,
			["exitIp"] = result.ExitIp,
			["steamReachable"] = result.SteamReachable,
			["error"] = result.Error
		};

		if (result.LatencyMs is { } latency)
		{
			output["latencyMs"] = latency;
		}

		var success = result.ExitIp != null && result.SteamReachable;
		return new ActionResult(success, success ? null : (result.Error ?? "proxy probe did not complete"), output);
	}

	/// <summary>Live probes: exit IP via the standard echo endpoint, then Steam reachability.</summary>
	[ExcludeFromCodeCoverage]
	internal static async Task<ProxyProbeResult> ProbeAsync(ProxyOptions options, CancellationToken cancellationToken)
	{
		// CA2000 suppressed: ownership of both handler and client transfers to the
		// using-scoped HttpClient below.
#pragma warning disable CA2000
		var handler = new System.Net.Http.SocketsHttpHandler
		{
			ConnectTimeout = TimeSpan.FromSeconds(8),
			Proxy = options.ToWebProxy(),
			UseProxy = true
		};
		using var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
#pragma warning restore CA2000

		var stopwatch = Stopwatch.StartNew();
		string? exitIp;
		try
		{
			using var response = await client.GetAsync(
				new Uri("https://api.ipify.org/"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			response.EnsureSuccessStatusCode();
			exitIp = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
		}
		catch (Exception ex)
		{
			return new ProxyProbeResult(null, false, null, $"exit-ip probe failed: {ex.Message}");
		}

		stopwatch.Stop();
		var latency = stopwatch.ElapsedMilliseconds;

		bool steamReachable;
		try
		{
			using var steam = await client.GetAsync(
				new Uri("https://steamcommunity.com"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			steamReachable = (int)steam.StatusCode < 500;
		}
		catch (Exception ex)
		{
			return new ProxyProbeResult(exitIp, false, latency, $"steam probe failed: {ex.Message}");
		}

		return new ProxyProbeResult(exitIp, steamReachable, latency, null);
	}
}

/// <summary>What a proxy probe observed: exit IP, Steam reachability, latency, and a failure reason.</summary>
public sealed record ProxyProbeResult(
	string? ExitIp,
	bool SteamReachable,
	long? LatencyMs,
	string? Error
);
