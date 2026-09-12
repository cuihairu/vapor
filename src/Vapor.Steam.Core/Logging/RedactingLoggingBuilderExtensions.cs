using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Vapor.Steam.Core.Logging;

public static class RedactingLoggingBuilderExtensions
{
	/// <summary>
	/// Registers the console logger wrapped by <see cref="RedactingLoggerProvider"/>,
	/// so all console output is redacted at the provider level before reaching the sink.
	/// </summary>
	public static ILoggingBuilder AddRedactingConsole(this ILoggingBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);

		builder.AddConsole();

		// Replace the direct ILoggerProvider -> ConsoleLoggerProvider registration
		// with a redacting wrapper; keep the console provider itself resolvable
		// so the wrapper can obtain it from DI.
		for (int i = builder.Services.Count - 1; i >= 0; i--)
		{
			ServiceDescriptor descriptor = builder.Services[i];
			if (descriptor.ServiceType == typeof(ILoggerProvider) &&
				descriptor.ImplementationType == typeof(ConsoleLoggerProvider))
			{
				builder.Services.RemoveAt(i);
			}
		}

		builder.Services.TryAddSingleton<ConsoleLoggerProvider>();
		builder.Services.Add(
			ServiceDescriptor.Singleton<ILoggerProvider>(
				sp => new RedactingLoggerProvider(sp.GetRequiredService<ConsoleLoggerProvider>())));

		return builder;
	}
}
