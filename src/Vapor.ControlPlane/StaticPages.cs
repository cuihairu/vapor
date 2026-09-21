using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.FileProviders;

namespace Vapor.ControlPlane;

/// <summary>
/// Static page wiring, extracted from the top-level statements so the
/// deployment-layout branch is isolated. Static pages must resolve next to the
/// deployed binary, not the process working directory —
/// <c>dotnet /path/to/Vapor.ControlPlane.dll</c> run from any cwd would
/// otherwise 404 every page (the default WebRoot is ContentRoot, which defaults
/// to the cwd for a bare dll launch). Development (<c>dotnet run</c>, wwwroot
/// not copied next to the bin) falls back to the default provider, which serves
/// the project's wwwroot through StaticWebAssets. The fallback arm is excluded
/// from coverage: within a test process the deployed wwwroot always exists
/// next to the binary, so no unit test can observe it (see tests/TESTING.md).
/// </summary>
[ExcludeFromCodeCoverage]
internal static class StaticPages
{
	internal static void Use(WebApplication app)
	{
		string deployedWwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
		if (Directory.Exists(deployedWwwroot))
		{
			app.UseStaticFiles(new StaticFileOptions
			{
				FileProvider = new PhysicalFileProvider(deployedWwwroot)
			});
		}
		else
		{
			app.UseStaticFiles();
		}
	}
}
