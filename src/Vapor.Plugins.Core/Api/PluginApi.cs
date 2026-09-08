namespace Vapor.Plugins.Core;

/// <summary>
/// Defines the plugin API contract version exposed by the host and the SemVer
/// compatibility rules used to decide whether a plugin can be loaded.
/// </summary>
/// <remarks>
/// Compatibility rule:
/// <list type="bullet">
/// <item>Major versions must match exactly.</item>
/// <item>The plugin's minor version must be less than or equal to the host's minor version.</item>
/// <item>Patch/build and prerelease labels are ignored for compatibility decisions.</item>
/// </list>
/// </remarks>
public static class PluginApi
{
	/// <summary>The plugin API version implemented by this host.</summary>
	public static readonly Version Current = new(1, 0, 0);

	/// <summary>
	/// Parses a SemVer-ish version string (e.g. "1.2", "1.2.3", "1.2.3-beta.1") into a
	/// <see cref="Version"/>, stripping any prerelease or build metadata suffix.
	/// </summary>
	public static bool TryParseVersion(string? text, out Version version)
	{
		version = new Version(0, 0);

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var core = text.Trim();
		var plusIndex = core.IndexOf('+', StringComparison.Ordinal);
		if (plusIndex >= 0)
		{
			core = core[..plusIndex];
		}

		var dashIndex = core.IndexOf('-', StringComparison.Ordinal);
		if (dashIndex >= 0)
		{
			core = core[..dashIndex];
		}

		return Version.TryParse(core, out version!) && version.Major > 0;
	}

	/// <summary>
	/// Determines whether a plugin targeting <paramref name="requestedApiVersion"/> can be
	/// loaded by this host.
	/// </summary>
	public static bool IsCompatible(Version requestedApiVersion, out string? reason)
	{
		ArgumentNullException.ThrowIfNull(requestedApiVersion);

		if (requestedApiVersion.Major != Current.Major)
		{
			reason = $"plugin targets API major version {requestedApiVersion.Major} but host implements {Current.Major}";
			return false;
		}

		var requestedMinor = Math.Max(requestedApiVersion.Minor, 0);
		if (requestedMinor > Current.Minor)
		{
			reason = $"plugin targets API version {requestedApiVersion} but host implements {Current}";
			return false;
		}

		reason = null;
		return true;
	}

	/// <summary>
	/// Parses and validates a manifest API version string against this host.
	/// </summary>
	public static bool IsCompatible(string? apiVersionText, out string? reason)
	{
		if (!TryParseVersion(apiVersionText, out var requested))
		{
			reason = $"invalid plugin API version '{apiVersionText}'";
			return false;
		}

		return IsCompatible(requested, out reason);
	}
}
