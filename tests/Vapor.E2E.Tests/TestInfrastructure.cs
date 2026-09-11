using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Vapor.E2E.Tests;

/// <summary>Shared helpers for locating build outputs and running child processes.</summary>
internal static class TestInfrastructure
{
	/// <summary>Walks up from the test binary until the repository root (contains Vapor.sln).</summary>
	public static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vapor.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName
			?? throw new InvalidOperationException($"Could not locate Vapor.sln starting from {AppContext.BaseDirectory}");
	}

	public static string FindAppDll(string projectName, string dllName)
	{
#if DEBUG
		var configurations = new[] { "Debug", "Release" };
#else
		var configurations = new[] { "Release", "Debug" };
#endif

		string repoRoot = FindRepoRoot();
		foreach (var configuration in configurations)
		{
			string candidate = Path.Combine(repoRoot, "src", projectName, "bin", configuration, "net10.0", dllName);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new InvalidOperationException(
			$"{dllName} was not found under {repoRoot}/src/{projectName}/bin. Run 'dotnet build Vapor.sln' before running the E2E tests.");
	}

	/// <summary>Reserves an ephemeral port from the OS and releases it for the child process to bind.</summary>
	public static int FindFreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}

	public static string CreateTempWorkDir()
	{
		string path = Path.Combine(Path.GetTempPath(), "vapor-e2e", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		Directory.CreateDirectory(Path.Combine(path, "home"));
		Directory.CreateDirectory(Path.Combine(path, "plugins-empty"));
		return path;
	}
}

/// <summary>A child process whose console output is captured to a file for failure diagnostics.</summary>
internal sealed class VaporProcess : IDisposable
{
	private readonly Process _process;
	private readonly string _logPath;

	private VaporProcess(Process process, string logPath)
	{
		_process = process;
		_logPath = logPath;
	}

	public bool HasExited => _process.HasExited;
	public int ExitCode => _process.HasExited ? _process.ExitCode : throw new InvalidOperationException("process has not exited");

	public static VaporProcess Start(string dllPath, IReadOnlyDictionary<string, string> environment, string logPath)
	{
		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		psi.ArgumentList.Add("exec");
		psi.ArgumentList.Add(dllPath);

		foreach (var (key, value) in environment)
		{
			psi.Environment[key] = value;
		}

		var process = Process.Start(psi)
			?? throw new InvalidOperationException($"Failed to start process for {dllPath}");

		// Pump both streams into a single diagnostics log; reads run on background tasks
		// so the child never stalls on a full pipe buffer.
		_ = PumpAsync(process.StandardOutput, "[out]", logPath);
		_ = PumpAsync(process.StandardError, "[err]", logPath);

		return new VaporProcess(process, logPath);
	}

	private static async Task PumpAsync(StreamReader reader, string prefix, string logPath)
	{
		try
		{
			while (await reader.ReadLineAsync() is { } line)
			{
				await File.AppendAllTextAsync(logPath, $"{prefix} {line}{Environment.NewLine}");
			}
		}
		catch
		{
			// Process teardown can kill the pipes mid-read; that is fine.
		}
	}

	/// <summary>Returns the tail of the process log, used to diagnose test failures.</summary>
	public string ReadLogTail(int maxChars = 8000)
	{
		try
		{
			using var reader = new StreamReader(File.Open(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
			string content = reader.ReadToEnd();
			return content.Length <= maxChars ? content : content[^maxChars..];
		}
		catch (IOException)
		{
			return "<log unavailable>";
		}
	}

	public void Dispose()
	{
		try
		{
			if (!_process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit(10_000);
			}
		}
		catch
		{
			// Best-effort teardown.
		}

		_process.Dispose();
	}
}
