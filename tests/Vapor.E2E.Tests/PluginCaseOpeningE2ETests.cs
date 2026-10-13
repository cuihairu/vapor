using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// 41-4 local half over real processes: the Csgo plugin's own build output is packaged
/// and installed through the direct-mode REST endpoint, then its three case_* actions
/// travel scheduler → WebSocket tunnel → agent task executor → plugin action pipeline.
/// Only the credential-free arms are exercised here — the dry-run simulator, the
/// sessionless inventory read, the closed configuration gates, and the consume ledger
/// behind an opened gate. The real Steam side of 41-4 stays with the operator-provided
/// credentials.
/// </summary>
[Collection("E2E")]
public sealed class PluginCaseOpeningE2ETests
{
	private const string Account = "e2e-case-account";
	private const string CaseId = "vapor.cases.starter";

	private readonly E2EStack _stack;

	public PluginCaseOpeningE2ETests(E2EStack stack)
	{
		_stack = stack;
	}

	[Fact]
	public async Task InstalledCsgoPlugin_CaseActions_RunEndToEndWithoutSteam()
	{
		StagedPackage staged = StageCsgoPackage();
		try
		{
			// Direct mode stages no catalog expectations, so the agent reconciles the
			// manifest against nothing but the digest and hot-loads.
			JsonElement dispatch = await PostAdminAsync("/v1/plugins/install", new
			{
				agentIds = new[] { E2EStack.AgentId },
				url = staged.Url,
				sha256 = staged.Sha256,
			});

			var jobs = dispatch.GetProperty("jobs");
			Assert.Equal(1, jobs.GetArrayLength());
			Assert.Equal(E2EStack.AgentId, jobs[0].GetProperty("agentId").GetString());
			string installJobId = jobs[0].GetProperty("jobId").GetString()
				?? throw new InvalidOperationException("install job id missing from dispatch response");

			var (installStatus, _) = await _stack.WaitForJobCompletionAsync(installJobId, ["Finished"]);
			Assert.Equal("finished", installStatus, ignoreCase: true);

			JsonElement logs = await _stack.GetAuditLogsAsync("plugin_install_dispatched");
			bool audited = logs.GetProperty("logs").EnumerateArray().Any(entry =>
				entry.TryGetProperty("details", out var details) &&
				details.TryGetProperty("url", out var auditedUrl) &&
				auditedUrl.GetString() == staged.Url);
			Assert.True(audited,
				$"plugin_install_dispatched audit entry for {staged.Url} was not recorded:{Environment.NewLine}{logs.GetRawText()}");

			// Capabilities ride the hello, which is one shot per WebSocket connection, so
			// the actions registered by that hot load only reach the scheduler now.
			await _stack.RestartAgentAsync("case_open");

			// Dry-run open: the credential-free happy path.
			var (openStatus, openTask) = await DispatchAndWaitAsync("case_open", ["Finished"], new Dictionary<string, object?>
			{
				["caseId"] = CaseId,
				["count"] = 2,
			});
			Assert.Equal("finished", openStatus, ignoreCase: true);

			var output = openTask.GetProperty("output").GetProperty("payload");
			Assert.Equal("dry-run", output.GetProperty("mode").GetString());
			Assert.Equal(CaseId, output.GetProperty("caseId").GetString());
			Assert.Equal(2, output.GetProperty("opened").GetInt32());
			Assert.True(output.GetProperty("results").GetArrayLength() > 0);

			// The real-open path stays closed behind open.enabled.
			await AssertTaskFailsAsync("case_open", new Dictionary<string, object?>
			{
				["caseId"] = CaseId,
				["crateAssetId"] = 123456789UL,
			}, "real case opening is disabled");

			// case_inventory is login-requiring, so the session-level gate rejects it
			// before the action body runs: it must not read a backpack without a live
			// session, however the request is shaped.
			await AssertTaskFailsAsync("case_inventory", new Dictionary<string, object?>(), "not logged in");

			// Consumption bookkeeping stays closed behind consume.enabled.
			await AssertTaskFailsAsync("case_consume", new Dictionary<string, object?>
			{
				["caseId"] = CaseId,
			}, "case consume is disabled");
		}
		finally
		{
			staged.Delete();
		}
	}

	/// <summary>
	/// 41-4 local half, consume arm: a re-staged package flips consume.enabled and pins
	/// ledger.path in its own manifest; the direct-mode install hot-swaps the loaded
	/// plugin (unload → load re-runs InitializeAsync), so the running agent records
	/// ledger entries without any Steam contact. Asserts the action output totals, the
	/// unknown-case arm behind the open gate, and the durable JSONL archive.
	/// </summary>
	[Fact]
	public async Task ConsumeEnabledPlugin_RecordsLedgerEntriesWithoutSteam()
	{
		string ledgerPath = Path.Combine(Path.GetTempPath(), "vapor-e2e-ledger-" + Guid.NewGuid().ToString("N") + ".jsonl");
		StagedPackage staged = StageCsgoPackage(new Dictionary<string, string>
		{
			["consume.enabled"] = "true",
			["ledger.path"] = ledgerPath,
		});
		try
		{
			JsonElement dispatch = await PostAdminAsync("/v1/plugins/install", new
			{
				agentIds = new[] { E2EStack.AgentId },
				url = staged.Url,
				sha256 = staged.Sha256,
			});

			var jobs = dispatch.GetProperty("jobs");
			Assert.Equal(1, jobs.GetArrayLength());
			string installJobId = jobs[0].GetProperty("jobId").GetString()
				?? throw new InvalidOperationException("install job id missing from dispatch response");

			var (installStatus, _) = await _stack.WaitForJobCompletionAsync(installJobId, ["Finished"]);
			Assert.Equal("finished", installStatus, ignoreCase: true);

			// The restart only carries new capabilities when this test runs before the
			// default-package one on a fresh stack; afterwards the hello already
			// declares the case actions and the reconnect changes no routing.
			await _stack.RestartAgentAsync("case_consume");

			var (status, task) = await DispatchAndWaitAsync("case_consume", ["Finished"], new Dictionary<string, object?>
			{
				["caseId"] = CaseId,
				["count"] = 3,
				["keys"] = 5,
				["awards"] = new[] { "operation breakout" },
			});
			Assert.Equal("finished", status, ignoreCase: true);

			// Unlike CaseOpenAction (which nests its batch under a "payload" key),
			// CaseConsumeAction returns its totals flat in the output dictionary.
			var output = task.GetProperty("output");
			Assert.Equal("ledger", output.GetProperty("mode").GetString());
			Assert.Equal(CaseId, output.GetProperty("caseId").GetString());
			Assert.Equal(3, output.GetProperty("cases").GetInt32());
			Assert.Equal(5, output.GetProperty("keys").GetInt32());
			Assert.Equal(1, output.GetProperty("ledger").GetProperty("entries").GetInt32());
			Assert.Equal(3, output.GetProperty("ledger").GetProperty("cases").GetInt32());
			Assert.Equal(5, output.GetProperty("ledger").GetProperty("keys").GetInt32());

			// The gate is open but the catalog stays authoritative: an unknown case id
			// must fail with the catalog error instead of silently recording an entry.
			await AssertTaskFailsAsync("case_consume", new Dictionary<string, object?>
			{
				["caseId"] = "vapor.cases.missing",
			}, "unknown case");

			// The archive holds one durable JSONL line for the recorded entry, which
			// is what lets ledger totals survive agent restarts (the constructor
			// replays well-formed lines).
			string[] lines = File.ReadAllLines(ledgerPath);
			Assert.Single(lines);
			using var doc = JsonDocument.Parse(lines[0]);
			Assert.Equal(CaseId, doc.RootElement.GetProperty("caseId").GetString());
			Assert.Equal(3, doc.RootElement.GetProperty("cases").GetInt32());
			Assert.Equal(5, doc.RootElement.GetProperty("keys").GetInt32());
			Assert.Equal("operation breakout", doc.RootElement.GetProperty("awards")[0].GetString());
		}
		finally
		{
			staged.Delete();
			try
			{
				File.Delete(ledgerPath);
			}
			catch (IOException)
			{
				// Best-effort cleanup; the OS reclaims the temp file eventually.
			}
		}
	}

	/// <summary>Creates a job for one action and waits for a terminal task state.</summary>
	private async Task<(string Status, JsonElement Task)> DispatchAndWaitAsync(
		string action,
		string[] terminalStatuses,
		Dictionary<string, object?> payload)
	{
		// The task executor resolves a session before any action runs, so a dummy
		// refresh token keeps every dispatch off the no-credentials arm.
		payload["refreshToken"] = "dummy-e2e-token";

		JsonElement created = await _stack.CreateJobAsync(new
		{
			action,
			region = E2EStack.AgentRegion,
			targets = new[] { Account },
			payload,
		});

		string jobId = created.GetProperty("job").GetProperty("id").GetString()
			?? throw new InvalidOperationException("job id missing from create response");

		return await _stack.WaitForJobCompletionAsync(jobId, terminalStatuses);
	}

	private async Task AssertTaskFailsAsync(string action, Dictionary<string, object?> payload, string expectedErrorPart)
	{
		var (status, task) = await DispatchAndWaitAsync(action, ["Failed"], payload);
		Assert.Equal("failed", status, ignoreCase: true);

		string error = task.GetProperty("error").GetString() ?? string.Empty;
		Assert.Contains(expectedErrorPart, error, StringComparison.OrdinalIgnoreCase);
	}

	private async Task<JsonElement> PostAdminAsync(string path, object request)
	{
		using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(request) };
		message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", E2EStack.AdminApiKey);
		using var response = await _stack.Http.SendAsync(message);

		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException(
				$"POST {path} failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
		}

		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		return doc.RootElement.Clone();
	}

	/// <summary>
	/// Packages the Csgo plugin's own build output: manifest, entry assembly and the
	/// shared assemblies its deps.json resolves, plus the digest the installer checks.
	/// </summary>
	private sealed record StagedPackage(string StagingDir, string Url, string Sha256)
	{
		/// <summary>Removes the zip and its staging directory; best effort.</summary>
		public void Delete()
		{
			try
			{
				File.Delete(StagingDir + ".zip");
				Directory.Delete(StagingDir, recursive: true);
			}
			catch (IOException)
			{
				// Best-effort cleanup; the OS reclaims the temp files eventually.
			}
		}
	}

	private static StagedPackage StageCsgoPackage(IReadOnlyDictionary<string, string>? configurationOverrides = null)
	{
		string entryDll = TestInfrastructure.FindAppDll("Vapor.Plugins.Csgo", "Vapor.Plugins.Csgo.dll");
		string binDir = Path.GetDirectoryName(entryDll)
			?? throw new InvalidOperationException($"No directory for {entryDll}");

		string staging = Path.Combine(Path.GetTempPath(), "vapor-e2e-csgo-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(staging);
		foreach (string file in Directory.EnumerateFiles(binDir))
		{
			string name = Path.GetFileName(file);
			if (name.Equals("plugin.json", StringComparison.OrdinalIgnoreCase) ||
				name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
				name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
			{
				File.Copy(file, Path.Combine(staging, name));
			}
		}

		ApplyManifestOverrides(Path.Combine(staging, "plugin.json"), configurationOverrides);

		ZipFile.CreateFromDirectory(staging, staging + ".zip");
		string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staging + ".zip"))).ToLowerInvariant();
		return new StagedPackage(staging, new Uri(staging + ".zip").AbsoluteUri, sha256);
	}

	/// <summary>
	/// Rewrites the staged manifest's configuration values before zipping. The plugin
	/// readers resolve environment over configuration over fallback, so per-test knobs
	/// ride the package itself instead of agent-wide environment variables that would
	/// leak into the other tests in the collection.
	/// </summary>
	private static void ApplyManifestOverrides(string manifestPath, IReadOnlyDictionary<string, string>? overrides)
	{
		if (overrides is null || overrides.Count == 0)
		{
			return;
		}

		JsonNode root = JsonNode.Parse(File.ReadAllText(manifestPath))
			?? throw new InvalidOperationException($"Manifest at {manifestPath} parsed to null");
		if (root["configuration"] is not JsonObject configuration)
		{
			throw new InvalidOperationException($"Manifest at {manifestPath} has no configuration object");
		}

		foreach ((string key, string value) in overrides)
		{
			configuration[key] = value;
		}

		File.WriteAllText(manifestPath, root.ToJsonString());
	}
}
