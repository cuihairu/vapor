using System.Text.Json;
using Vapor.Agent;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

/// <summary>
/// ScriptExecAction over real child processes: metadata/classification, the
/// language → interpreter mapping, payload validation (content, language,
/// timeout bounds), output capture (stdout/stderr/exit code), the timeout kill
/// path, and the capture truncation. Shell is the only interpreter CI can rely
/// on, so execution tests run through it; the other languages are covered at
/// the mapping level.
/// </summary>
public sealed class ScriptExecActionTests
{
	[Fact]
	public void Metadata_IsHostScopedNonIdempotent()
	{
		var action = new ScriptExecAction();

		Assert.Equal("script_exec", action.Name);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(ScriptExecAction.MaxTimeoutSeconds, action.Metadata.TimeoutSeconds);
		Assert.Equal(ActionSafety.NonIdempotent, action.Metadata.Safety);
	}

	[Theory]
	[InlineData("shell", "/bin/sh", "-c")]
	[InlineData("python", "python3", "-c")]
	[InlineData("powershell", "pwsh", "-NoProfile")]
	public void ResolveInterpreter_MapsKnownLanguages(string language, string fileName, string firstArg)
	{
		(string FileName, string[] Args)? resolved = ScriptExecAction.ResolveInterpreter(language);

		Assert.NotNull(resolved);
		Assert.Equal(fileName, resolved!.Value.FileName);
		Assert.Equal(firstArg, resolved.Value.Args[0]);
	}

	[Theory]
	[InlineData("lua")]
	[InlineData("")]
	public void ResolveInterpreter_UnknownLanguage_ReturnsNull(string language)
	{
		Assert.Null(ScriptExecAction.ResolveInterpreter(language));
	}

	[Fact]
	public async Task ExecuteAsync_NormalizesLanguageTagBeforeResolving()
	{
		var action = new ScriptExecAction();

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["content"] = "echo ok", ["language"] = "  SHELL " },
			CancellationToken.None);

		Assert.True(result.Success);
	}

	[Fact]
	public async Task ExecuteAsync_RunsShellScriptAndCapturesStreams()
	{
		var action = new ScriptExecAction();

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["content"] = "echo hello; echo oops >&2" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.NotNull(result.Output);
		Assert.Equal(0, Assert.IsType<int>(result.Output!["exitCode"]));
		Assert.False(Assert.IsType<bool>(result.Output!["timedOut"]));
		Assert.Contains("hello", Assert.IsType<string>(result.Output!["stdout"]));
		Assert.Contains("oops", Assert.IsType<string>(result.Output!["stderr"]));
		Assert.False(Assert.IsType<bool>(result.Output!["stdoutTruncated"]));
	}

	[Fact]
	public async Task ExecuteAsync_NonZeroExit_FailsWithExitCode()
	{
		var action = new ScriptExecAction();

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["content"] = "exit 3" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("script exited with code 3", result.Error);
		Assert.Equal(3, Assert.IsType<int>(result.Output!["exitCode"]));
	}

	[Fact]
	public async Task ExecuteAsync_MissingContent_FailsWithRequiredMessage()
	{
		var action = new ScriptExecAction();

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["language"] = "shell" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("'content'", result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_UnknownLanguage_FailsWithSupportedList()
	{
		var action = new ScriptExecAction();

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["content"] = "echo hi", ["language"] = "lua" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("unsupported language 'lua'", result.Error);
		Assert.Null(result.Output);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-5)]
	[InlineData(301)]
	public async Task ExecuteAsync_TimeoutOutsideBounds_Fails(int timeoutSeconds)
	{
		var action = new ScriptExecAction();

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["content"] = "echo hi", ["timeoutSeconds"] = timeoutSeconds },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("timeoutSeconds must be between 1 and 300", result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_TimeoutKillsProcessAndReportsStructuredResult()
	{
		var action = new ScriptExecAction();

		ActionResult result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["content"] = "sleep 30", ["timeoutSeconds"] = 1 },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("script timed out after 1s", result.Error);
		Assert.Null(result.Output!["exitCode"]);
		Assert.True(Assert.IsType<bool>(result.Output!["timedOut"]));
	}

	[Fact]
	public async Task ExecuteAsync_CallerCancel_KillsChildAndPropagates()
	{
		var action = new ScriptExecAction();
		using CancellationTokenSource cts = new();
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => action.ExecuteAsync(
				new Dictionary<string, object?> { ["content"] = "sleep 30" },
				cts.Token));
	}

	[Fact]
	public async Task ExecuteAsync_JsonElementPayloadChannels_ParseLikeInProcessValues()
	{
		// The dispatch path delivers payload values as JsonElement; the action
		// must read them exactly like the in-process string/int shapes.
		JsonElement json = JsonSerializer.SerializeToElement(new
		{
			content = "echo wired",
			language = "shell",
			timeoutSeconds = 10
		});
		var payload = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);

		var action = new ScriptExecAction();
		ActionResult result = await action.ExecuteAsync(payload, CancellationToken.None);

		Assert.True(result.Success);
		Assert.Contains("wired", Assert.IsType<string>(result.Output!["stdout"]));
	}

	[Fact]
	public void Truncate_ShortValue_PassesThrough()
	{
		string captured = ScriptExecAction.Truncate("short", out bool truncated);

		Assert.False(truncated);
		Assert.Equal("short", captured);
	}

	[Fact]
	public void Truncate_OversizedValue_ClipsAndFlags()
	{
		string captured = ScriptExecAction.Truncate(new string('x', 70000), out bool truncated);

		Assert.True(truncated);
		Assert.Equal(65536, captured.Length);
	}

	[Fact]
	public void TryKill_NeverStartedProcess_SwallowsInvalidOperationException()
	{
		// An unstarted Process throws from HasExited; the kill helper must treat
		// that race as benign so a timeout never turns into a crash.
		using var process = new System.Diagnostics.Process();

		ScriptExecAction.TryKill(process);
	}
}
