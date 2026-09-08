using Microsoft.Extensions.Logging;

namespace Vapor.Plugins.Core;

/// <summary>
/// Default <see cref="IPluginHostServices"/> implementation backed by a logger factory and
/// an arbitrary service provider.
/// </summary>
public sealed class DefaultPluginHostServices : IPluginHostServices
{
	public DefaultPluginHostServices(ILoggerFactory loggerFactory, IServiceProvider services)
	{
		LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
		Services = services ?? throw new ArgumentNullException(nameof(services));
	}

	public ILoggerFactory LoggerFactory { get; }

	public IServiceProvider Services { get; }
}
