using System.Diagnostics;
using Vapor.Protocol;
using Vapor.Steam.Core;

namespace Vapor.Agent;

/// <summary>
/// Host action "script_exec": runs an operator script from the control-plane
/// script repository on this agent machine. The repository lives control-plane
/// side, so the payload carries the full script body; the agent resolves an
/// interpreter from the declared language, captures stdout/stderr and the exit
/// code, and reports all three back on the task result. Classified
/// NonIdempotent — an arbitrary script can double external side effects when
/// re-run, so dispatch is capped like every other non-idempotent action.
/// </summary>
public sealed class ScriptExecAction : IHostAction
{
	internal const int DefaultTimeoutSeconds = 120;
	internal const int MaxTimeoutSeconds = 300;
	private const int MaxCapturedChars = 65536;

	public string Name => "script_exec";

	public ActionMetadata Metadata => new ActionMetadata(
		Name,
		"Runs an operator script on this agent host and reports the exit code with captured output",
		RequiresLogin: false,
		TimeoutSeconds: MaxTimeoutSeconds
	)
	{ Safety = ActionSafety.NonIdempotent };

	public async Task<ActionResult> ExecuteAsync(
		IReadOnlyDictionary<string, object?> payload,
		CancellationToken cancellationToken)
	{
		string? content = PayloadReader.GetString(payload, "content");
		if (string.IsNullOrWhiteSpace(content))
		{
			return new ActionResult(false, "payload field 'content' (script body) is required", null);
		}

		string language = (PayloadReader.GetString(payload, "language") ?? "shell").Trim().ToLowerInvariant();
		(string FileName, string[] Args)? interpreter = ResolveInterpreter(language);
		if (interpreter is null)
		{
			return new ActionResult(false, $"unsupported language '{language}' (supported: shell, python, powershell)", null);
		}

		int? requestedTimeout = PayloadReader.GetInt32(payload, "timeoutSeconds");
		if (requestedTimeout is < 1 or > MaxTimeoutSeconds)
		{
			return new ActionResult(false, $"timeoutSeconds must be between 1 and {MaxTimeoutSeconds}", null);
		}
		int timeoutSeconds = requestedTimeout ?? DefaultTimeoutSeconds;

		// CA2000 suppressed: the process (and with it the redirected streams) is
		// disposed by the using declaration before every return path below.
#pragma warning disable CA2000
		var startInfo = new ProcessStartInfo
		{
			FileName = interpreter.Value.FileName,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		foreach (string arg in interpreter.Value.Args)
		{
			startInfo.ArgumentList.Add(arg);
		}
		startInfo.ArgumentList.Add(content);
		using Process process = new() { StartInfo = startInfo };
#pragma warning restore CA2000

		process.Start();

		// Drain both pipes from the start so a full buffer can never block the
		// child; the readers complete once the (possibly killed) process exits
		// and closes its end of the pipes. The reader tasks deliberately do not
		// observe the caller token: a cancel kills the process (closing the
		// pipes) rather than aborting the read, so the partial output still
		// surfaces for capture.
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

		// CA2000 suppressed: timeoutCts is disposed in this method's finally.
#pragma warning disable CA2000
		CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
#pragma warning restore CA2000
		bool timedOut = false;
		try
		{
			timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
			await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			timedOut = true;
			TryKill(process);
			await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Caller cancel (agent shutdown / task_cancel): kill the child so
			// nothing outlives the dispatch loop, then let the cancel propagate
			// the way every other host action does.
			TryKill(process);
			throw;
		}
		finally
		{
			timeoutCts.Dispose();
		}

		string stdout = await stdoutTask.ConfigureAwait(false);
		string stderr = await stderrTask.ConfigureAwait(false);
		int exitCode = process.ExitCode;
		(bool success, string? error) = timedOut
			? (false, $"script timed out after {timeoutSeconds}s")
			: exitCode == 0 ? (true, null) : (false, $"script exited with code {exitCode}");

		return new ActionResult(success, error, new Dictionary<string, object?>
		{
			["exitCode"] = timedOut ? null : exitCode,
			["timedOut"] = timedOut,
			["stdout"] = Truncate(stdout, out bool stdoutTruncated),
			["stderr"] = Truncate(stderr, out bool stderrTruncated),
			["stdoutTruncated"] = stdoutTruncated,
			["stderrTruncated"] = stderrTruncated
		});
	}

	/// <summary>
	/// Language → interpreter mapping. Every interpreter receives the script on
	/// its command line (via -c / -Command), so nothing is written to disk and
	/// there is no temp-file lifecycle to leak. Resolved per execution — an
	/// agent that lacks pwsh simply fails that execution with the process start
	/// error surfaced by the host executor.
	/// </summary>
	internal static (string FileName, string[] Args)? ResolveInterpreter(string language) => language switch
	{
		"shell" => ("/bin/sh", ["-c"]),
		"python" => ("python3", ["-c"]),
		"powershell" => ("pwsh", ["-NoProfile", "-NonInteractive", "-Command"]),
		_ => null
	};

	/// <summary>
	/// Output caps: a chatty script must not push megabytes through the tunnel
	/// result frame. Truncation is reported, not hidden.
	/// </summary>
	internal static string Truncate(string value, out bool truncated)
	{
		if (value.Length <= MaxCapturedChars)
		{
			truncated = false;
			return value;
		}

		truncated = true;
		return value[..MaxCapturedChars];
	}

	internal static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
			// The child exited between the HasExited check and the kill; the
			// capture tasks observe the exit either way, so the race is benign.
		}
	}
}
