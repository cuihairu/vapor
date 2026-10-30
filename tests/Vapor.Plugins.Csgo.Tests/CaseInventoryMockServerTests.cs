using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Vapor.Steam.Core.Models;
using Vapor.Steam.Core.Web;
using Xunit;

namespace Vapor.Plugins.Csgo.Tests;

/// <summary>
/// 41-4 mock-first (2026-10-18 用户令「先跑 mock，真实账号后补」): a real local
/// HTTP listener stands in for Steam Community's 730 backpack endpoint, and the
/// production SteamTradeClient — not a fake handler — issues a real GET against
/// it. That proves the credential-free chain end to end: the configurable base
/// URL (ctor arg / VAPOR_STEAM_INVENTORY_BASE_URL), the live HTTP round trip,
/// Steam's JSON parse, and the case/key classifier. Assertions pin the classified
/// case count and key count to the mock's fixed inventory, so a regression in any
/// link fails the test (non-zero exit) and stays repeatable. The real-credential
/// arm of 41-4 stays with the operator; this is the mock half only.
/// </summary>
public sealed class CaseInventoryMockServerTests : IDisposable
{
	private const ulong SteamId = 76561198000000001UL;

	// The fixed mock inventory: 2 Kilowatt Case + 3 Kilowatt Case Key + 1 other.
	// The classifier tallies by market_hash_name with stack count = amount, so a
	// match here pins the classified totals the assertions below rely on.
	private const int MockCaseCount = 2;
	private const int MockKeyCount = 3;
	private const int MockOtherCount = 1;
	private const int MockTotalAssets = 3;

	private readonly MockSteamInventoryServer _server = new();

	public void Dispose() => _server.Dispose();

	/// <summary>
	/// case_inventory through the REAL SteamTradeClient against the mock: pulls the
	/// backpack over live HTTP, classifies it, and returns the action's output dict.
	/// </summary>
	[Fact]
	public async Task CaseInventory_AgainstMockServer_ClassifiesCaseAndKeyCounts()
	{
		// The real trade client points its inventory base URL at the loopback mock.
		using var webHandler = CreateWebHandler();
		using var realClient = new SteamTradeClient(
			webHandler, NullLogger<SteamTradeClient>.Instance, _server.BaseUrl);

		// The action's factory ignores the session handler and hands back the
		// mock-pointed real client, so ExecuteAsync drives the genuine HTTP path.
		var plugin = new CsgoPlugin();
		var action = new CaseInventoryAction(
			plugin, NullLogger<CaseInventoryAction>.Instance, _ => realClient);

		using var session = CreateSession(CreateWebHandler());
		ActionResult result = await action.ExecuteAsync(
			session,
			Payload(("steam_id", SteamId.ToString())),
			CancellationToken.None);
		// Surface the action's own error rather than a bare NullRef, so a failed
		// HTTP/parse link reports its real message.
		Assert.True(result.Success, $"case_inventory failed: {result.Error}");
		IReadOnlyDictionary<string, object?> output = result.Output!;

		// Classified against the fixed mock inventory: one case kind, the key and
		// other tallies, and the asset count all line up with what the mock served.
		var cases = Assert.IsType<List<Dictionary<string, object?>>>(output["cases"]);
		Assert.Single(cases);
		Assert.Equal("Kilowatt Case", cases[0]["name"]);
		Assert.Equal(MockCaseCount, cases[0]["count"]);
		Assert.Equal(MockKeyCount, output["key_count"]);
		Assert.Equal(MockOtherCount, output["other_count"]);
		Assert.Equal(MockTotalAssets, output["total_count"]);
		Assert.Equal(730u, output["app_id"]);

		// The mock actually served a request (live HTTP, not an injected fake).
		Assert.True(_server.RequestCount >= 1,
			"the mock inventory endpoint was never hit — the base URL did not reach the real client");
	}

	/// <summary>
	/// The agent-side path: the plugin's own TradeClientFactory builds a
	/// two-arg SteamTradeClient, whose base URL resolves from the
	/// VAPOR_STEAM_INVENTORY_BASE_URL environment variable. Pointing that env var
	/// at the mock (no code change) proves the real agent wiring can be steered
	/// at a local endpoint purely by configuration — the 41-4 requirement.
	/// </summary>
	[Fact]
	public async Task CaseInventory_EnvVarSteersAgentFactory_ConnectsToMock()
	{
		Environment.SetEnvironmentVariable(
			SteamTradeClient.InventoryBaseUrlEnvironmentVariable, _server.BaseUrl);
		try
		{
			// The plugin's production factory, not an injected fake.
			var plugin = new CsgoPlugin();
			var action = new CaseInventoryAction(
				plugin, NullLogger<CaseInventoryAction>.Instance);

			ActionResult result = await action.ExecuteAsync(
				CreateSession(CreateWebHandler()),
				Payload(("steam_id", SteamId.ToString())),
				CancellationToken.None);
			Assert.True(result.Success, $"case_inventory failed: {result.Error}");

			// The env-var-steered client reached the mock and classified its counts.
			var cases = Assert.IsType<List<Dictionary<string, object?>>>(result.Output!["cases"]);
			Assert.Single(cases);
			Assert.Equal(MockCaseCount, cases[0]["count"]);
			Assert.Equal(MockKeyCount, result.Output!["key_count"]);
			Assert.True(_server.RequestCount >= 1,
				"the env-var-steered agent client never reached the mock");
		}
		finally
		{
			Environment.SetEnvironmentVariable(
				SteamTradeClient.InventoryBaseUrlEnvironmentVariable, null);
		}
	}

	/// <summary>
	/// CaseOpening full flow (case_open dry-run) still runs against the same plugin
	/// wiring, so the newly-added action chain is exercised alongside the mock read.
	/// </summary>
	[Fact]
	public async Task CaseOpen_DryRunFullFlow_OpensCases()
	{
		var plugin = new CsgoPlugin();
		var action = new CaseOpenAction(plugin);

		IReadOnlyDictionary<string, object?> output = (await action.ExecuteAsync(
			CreateSession(CreateWebHandler()),
			Payload(("caseId", "vapor.cases.starter"), ("count", 2)),
			CancellationToken.None)).Output!;

		// The action wraps ToBatchPayload's anonymous object; serialize it to pin
		// the dry-run mode, the case id, and that `count` cases were opened.
		using var doc = System.Text.Json.JsonSerializer.SerializeToDocument(output["payload"]);
		var payload = doc.RootElement;
		Assert.Equal("dry-run", payload.GetProperty("mode").GetString());
		Assert.Equal("vapor.cases.starter", payload.GetProperty("caseId").GetString());
		Assert.Equal(2, payload.GetProperty("opened").GetInt32());
		Assert.True(payload.GetProperty("results").GetArrayLength() > 0);
	}

	private static SteamWebHandler CreateWebHandler() => new(
		new SteamWebHandlerConfig { RateLimitIntervalMs = 0, MaxRetries = 1, EnableCircuitBreaker = false },
		NullLogger<SteamWebHandler>.Instance);

	private static BotSession CreateSession(SteamWebHandler webHandler) => new(
		"mock_account",
		new AccountCredentials("mock_account", "password"),
		null!,
		NullLogger<BotSession>.Instance,
		null,
		webHandler,
		null);

	private static Dictionary<string, object?> Payload(params (string Key, object? Value)[] values) =>
		values.ToDictionary(entry => entry.Key, entry => entry.Value);

	/// <summary>
	/// A real loopback HTTP listener that answers Steam Community's inventory path
	/// ({base}/inventory/{steamId}/{appId}/{contextId}) with one fixed JSON page.
	/// more_items = 0 keeps the paginator to a single request.
	/// </summary>
	private sealed class MockSteamInventoryServer : IDisposable
	{
		private readonly HttpListener _listener;
		private readonly CancellationTokenSource _cts = new();
		private int _requestCount;

		public string BaseUrl { get; }
		public int RequestCount => Volatile.Read(ref _requestCount);

		public MockSteamInventoryServer()
		{
			int port = FreePort();
			BaseUrl = $"http://127.0.0.1:{port}";
			_listener = new HttpListener();
			_listener.Prefixes.Add($"{BaseUrl}/");
			_listener.Start();
			_ = Task.Run(ServeAsync);
		}

		private static int FreePort()
		{
			var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			int port = ((IPEndPoint)probe.LocalEndpoint).Port;
			probe.Stop();
			return port;
		}

		private async Task ServeAsync()
		{
			byte[] body = Encoding.UTF8.GetBytes(InventoryJson);
			while (!_cts.IsCancellationRequested)
			{
				HttpListenerContext context;
				try
				{
					context = await _listener.GetContextAsync().ConfigureAwait(false);
				}
				catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
				{
					return; // listener torn down on dispose
				}

				Interlocked.Increment(ref _requestCount);
				context.Response.StatusCode = 200;
				context.Response.ContentType = "application/json";
				await context.Response.OutputStream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
				context.Response.Close();
			}
		}

		/// <summary>
		/// Steam's inventory payload shape: assets carry classid/instanceid/assetid/amount,
		/// descriptions carry market_hash_name keyed by (classid, instanceid). Classification
		/// is amount-stacked: "…Case" → case, "…Key" → key (keys win), else other.
		/// </summary>
		private const string InventoryJson = """
			{
			  "success": true,
			  "more_items": 0,
			  "last_assetid": "3",
			  "total_inventory_count": 3,
			  "assets": [
			    { "classid": "100", "instanceid": "0", "assetid": "1", "amount": "2" },
			    { "classid": "101", "instanceid": "0", "assetid": "2", "amount": "3" },
			    { "classid": "102", "instanceid": "0", "assetid": "3", "amount": "1" }
			  ],
			  "descriptions": [
			    { "classid": "100", "instanceid": "0", "name": "Kilowatt Case", "market_hash_name": "Kilowatt Case", "type": "Container", "tradable": 1, "marketable": 1 },
			    { "classid": "101", "instanceid": "0", "name": "Kilowatt Case Key", "market_hash_name": "Kilowatt Case Key", "type": "Key", "tradable": 0, "marketable": 0 },
			    { "classid": "102", "instanceid": "0", "name": "AK-47 | Redline", "market_hash_name": "AK-47 | Redline", "type": "Rifle", "tradable": 1, "marketable": 1 }
			  ]
			}
			""";

		public void Dispose()
		{
			_cts.Cancel();
			try
			{
				_listener.Stop();
				_listener.Close();
			}
			catch (Exception)
			{
				// ignored: already stopped
			}
			_cts.Dispose();
		}
	}
}
