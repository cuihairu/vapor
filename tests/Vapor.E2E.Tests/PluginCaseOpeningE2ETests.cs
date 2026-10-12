using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Vapor.E2E.Tests;

/// <summary>
/// 41-4 local half over real processes: the Csgo plugin's own build output is packaged
/// and installed through the direct-mode REST endpoint, then its three case_* actions
/// travel scheduler → WebSocket tunnel → agent task executor → plugin action pipeline.
/// Only the credential-free arms are exercised here — the dry-run simulator plus the two
/// closed configuration gates and the sessionless inventory read. The real Steam side of
/// 41-4 stays with the operator-provided credentials.
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

	private static StagedPackage StageCsgoPackage()
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

		ZipFile.CreateFromDirectory(staging, staging + ".zip");
		string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(staging + ".zip"))).ToLowerInvariant();
		return new StagedPackage(staging, new Uri(staging + ".zip").AbsoluteUri, sha256);
	}
}
