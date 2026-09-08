namespace Vapor.Plugins.Core;

/// <summary>Raised when plugin discovery, loading or unloading fails.</summary>
public sealed class PluginException : Exception
{
	public PluginException(string message) : base(message)
	{
	}

	public PluginException(string message, Exception innerException) : base(message, innerException)
	{
	}
}
