using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Logging;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit.Logging;

public sealed class RedactingLoggerProviderTests
{
	private sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>>? StructuredState);

	private sealed class CapturingProvider : ILoggerProvider
	{
		private readonly object _gate = new();

		public List<CapturedLog> Captured { get; } = [];

		public List<object?> CapturedScopes { get; } = [];

		public bool Disposed { get; private set; }

		public ILogger CreateLogger(string categoryName)
		{
			return new CapturingLogger(this);
		}

		public void Dispose()
		{
			Disposed = true;
		}

		private sealed class CapturingLogger(CapturingProvider owner) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull
			{
				lock (owner._gate)
				{
					owner.CapturedScopes.Add(state);
				}

				return NullScope.Instance;
			}

			public bool IsEnabled(LogLevel logLevel) => true;

			void ILogger.Log<TState>(
				LogLevel logLevel,
				EventId eventId,
				TState state,
				Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				IReadOnlyList<KeyValuePair<string, object?>>? structured =
					state as IReadOnlyList<KeyValuePair<string, object?>>;

				lock (owner._gate)
				{
					owner.Captured.Add(new CapturedLog(
						logLevel,
						formatter(state, exception),
						exception,
						structured));
				}
			}

			private sealed class NullScope : IDisposable
			{
				public static readonly NullScope Instance = new();

				public void Dispose()
				{
				}
			}
		}
	}

	/// <summary>
	/// A sink that invokes the formatter with a <c>null</c> state: the redacting
	/// formatter escapes to any inner provider typed as
	/// <c>Func&lt;object?, Exception?, string&gt;</c>, and the provider contract
	/// does not forbid a sink from passing null — the defensive <c>??</c> arm in
	/// <c>RedactingFormatter</c> exists precisely for such callers.
	/// </summary>
	private sealed class NullStateSinkProvider : ILoggerProvider
	{
		public string? FormatterResultForNullState { get; private set; }

		public ILogger CreateLogger(string categoryName)
		{
			return new NullStateSinkLogger(this);
		}

		public void Dispose()
		{
		}

		private sealed class NullStateSinkLogger(NullStateSinkProvider owner) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			void ILogger.Log<TState>(
				LogLevel logLevel,
				EventId eventId,
				TState state,
				Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				owner.FormatterResultForNullState = formatter(default!, exception);
			}
		}
	}

	private static ILogger CreateLogger(CapturingProvider capturing)
	{
		var provider = new RedactingLoggerProvider(capturing);
		return provider.CreateLogger("Test");
	}

	[Fact]
	public void Log_WithPlainTextSecrets_RedactsValues()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		logger.LogInformation("login password=hunter2 token=abc123def user=bob");

		var entry = Assert.Single(capturing.Captured);
		Assert.DoesNotContain("hunter2", entry.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("abc123def", entry.Message, StringComparison.Ordinal);
		Assert.Contains("<redacted>", entry.Message, StringComparison.Ordinal);
		Assert.Contains("user=bob", entry.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Log_WithJsonPayloadSecrets_RedactsValues()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		string json = "{\"password\":\"hunter2\",\"refreshToken\":\"rt-xyz\",\"action\":\"login\"}";
		logger.Log(LogLevel.Information, default, json, null, static (state, _) => state);

		var entry = Assert.Single(capturing.Captured);
		Assert.DoesNotContain("hunter2", entry.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("rt-xyz", entry.Message, StringComparison.Ordinal);
		Assert.Contains("login", entry.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Log_WhenSinkInvokesFormatterWithNullState_FallsBackToEmptyString()
	{
		var sink = new NullStateSinkProvider();
		var provider = new RedactingLoggerProvider(sink);
		var logger = provider.CreateLogger("Test");

		// The plain-string overload routes through the non-structured inner.Log
		// call, handing the redacting formatter to the sink; the sink then invokes
		// it with a null state, which must degrade to string.Empty — never NRE.
		logger.Log(LogLevel.Information, default, "stateless", null, static (state, _) => state);

		Assert.Equal(string.Empty, sink.FormatterResultForNullState);
	}

	[Fact]
	public void Log_WithStructuredState_RedactsSensitiveValues()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		var state = new List<KeyValuePair<string, object?>>
		{
			new("accountName", "alice"),
			new("authCode", "987654"),
			new("count", 42)
		};

		logger.Log(
			LogLevel.Information,
			new EventId(1),
			state,
			null,
			(s, _) => $"account={s.First(p => p.Key == "accountName").Value} code={s.First(p => p.Key == "authCode").Value}");

		var entry = Assert.Single(capturing.Captured);
		Assert.NotNull(entry.StructuredState);
		Assert.DoesNotContain("987654", entry.Message, StringComparison.Ordinal);

		var authCodePair = Assert.Single(entry.StructuredState!, p => p.Key == "authCode");
		Assert.Equal("<redacted>", authCodePair.Value);

		var accountPair = Assert.Single(entry.StructuredState!, p => p.Key == "accountName");
		Assert.Equal("alice", accountPair.Value);
	}

	[Fact]
	public void Log_StructuredState_EnumeratesViaNonGenericInterface()
	{
		// The redacted state wraps the pairs in a custom IReadOnlyList implementation;
		// consumers that only see System.Collections.IEnumerable (older log sinks,
		// debuggers) must enumerate the same redacted pairs through it.
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		logger.Log(
			LogLevel.Information,
			new EventId(1),
			new List<KeyValuePair<string, object?>> { new("accountName", "bob"), new("authCode", "123456") },
			null,
			static (_, _) => "structured");

		var entry = Assert.Single(capturing.Captured);
		Assert.NotNull(entry.StructuredState);
		var plain = new List<KeyValuePair<string, object?>>();
		foreach (var pair in (System.Collections.IEnumerable)entry.StructuredState!)
		{
			plain.Add((KeyValuePair<string, object?>)pair!);
		}

		Assert.Contains(plain, p => p.Key == "authCode" && "<redacted>".Equals(p.Value));
		Assert.Contains(plain, p => p.Key == "accountName" && "bob".Equals(p.Value));
		Assert.Contains(plain, p => p.Key == "{OriginalFormat}" && "structured".Equals(p.Value));
	}

	[Fact]
	public void Log_WithException_ReddactsExceptionContent()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		var exception = new InvalidOperationException("request failed with key=ABCD-EFGH");
		logger.LogError(exception, "operation failed");

		var entry = Assert.Single(capturing.Captured);
		Assert.NotNull(entry.Exception);
		Assert.DoesNotContain("ABCD-EFGH", entry.Exception.ToString(), StringComparison.Ordinal);
		Assert.Contains("InvalidOperationException", entry.Exception.Data["OriginalExceptionType"]?.ToString() ?? "", StringComparison.Ordinal);
	}

	[Fact]
	public void Log_WithInnerException_RedactsNestedExceptionContent()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		var exception = new InvalidOperationException(
			"outer failed",
			new HttpRequestException("request failed with token=inner-secret-xyz"));
		logger.LogError(exception, "operation failed");

		var entry = Assert.Single(capturing.Captured);
		object? inner = entry.Exception?.Data["InnerException"];
		Assert.NotNull(inner);
		Assert.DoesNotContain("inner-secret-xyz", inner.ToString()!, StringComparison.Ordinal);
	}

	[Fact]
	public void RedactedException_ConstructorChain_CarriesMessageAndInner()
	{
		// Full ctor surface of the private redaction exception: reflection keeps
		// the type private while pinning the contract its throw sites rely on.
		Type exType = typeof(RedactingLoggerProvider)
			.GetNestedType("RedactingLogger", BindingFlags.NonPublic)!
			.GetNestedType("RedactedException", BindingFlags.NonPublic)!;
		var parameterless = (Exception)Activator.CreateInstance(exType, nonPublic: true)!;
		Assert.StartsWith("Exception of type", parameterless.Message, StringComparison.Ordinal);

		var inner = new TimeoutException("socket died");
		var full = (Exception)exType.GetConstructor(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
				[typeof(string), typeof(Exception)], null)!
			.Invoke(["redacted", inner]);
		Assert.Equal("redacted", full.Message);
		Assert.Same(inner, full.InnerException);
	}

	[Fact]
	public void BeginScope_RedactsStringsAndStructuredPairsAndPassesThroughOtherState()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		using (logger.BeginScope("login password=hunter2"))
		using (logger.BeginScope(new List<KeyValuePair<string, object?>>
			   {
				   new("accountName", "alice"),
				   new("authCode", "987654")
			   }))
		using (logger.BeginScope(42))
		{
			logger.LogInformation("inside scopes");
		}

		Assert.Equal(3, capturing.CapturedScopes.Count);

		string redactedString = Assert.IsType<string>(capturing.CapturedScopes[0]);
		Assert.DoesNotContain("hunter2", redactedString, StringComparison.Ordinal);

		var redactedPairs = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(capturing.CapturedScopes[1]);
		Assert.Equal("<redacted>", Assert.Single(redactedPairs, p => p.Key == "authCode").Value);
		Assert.Equal("alice", Assert.Single(redactedPairs, p => p.Key == "accountName").Value);

		// Unrecognized scope state passes through untouched.
		Assert.Equal(42, capturing.CapturedScopes[2]);
	}

	[Fact]
	public void BeginScope_StructuredState_EnumeratesViaNonGenericIEnumerable()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		using (logger.BeginScope(new List<KeyValuePair<string, object?>>
			   {
				   new("accountName", "alice"),
				   new("authCode", "987654")
			   }))
		{
			logger.LogInformation("inside scope");
		}

		// The non-generic enumerator is part of the redacted state's surface (used by
		// structural walkers that only know IEnumerable); it must yield the same pairs.
		var redactedScope = Assert.IsAssignableFrom<System.Collections.IEnumerable>(Assert.Single(capturing.CapturedScopes));
		var pairs = new List<KeyValuePair<string, object?>>();
		foreach (var pair in redactedScope)
		{
			pairs.Add((KeyValuePair<string, object?>)pair!);
		}

		Assert.Equal(2, pairs.Count);
		Assert.Contains(pairs, p => p.Key == "accountName" && (string)p.Value! == "alice");
		Assert.Equal("<redacted>", Assert.Single(pairs, p => p.Key == "authCode").Value);
	}

	[Fact]
	public void Log_WithStructuredState_ExposesCountAndIndexerOverRedactedPairs()
	{
		var capturing = new CapturingProvider();
		var logger = CreateLogger(capturing);

		var state = new List<KeyValuePair<string, object?>>
		{
			new("accountName", "alice"),
			new("authCode", "987654")
		};

		logger.Log(LogLevel.Information, new EventId(1), state, null, static (s, _) => "formatted");

		var entry = Assert.Single(capturing.Captured);
		var structured = entry.StructuredState!;

		// The redacted state appends the original format placeholder as the final pair.
		Assert.Equal(3, structured.Count);
		Assert.Equal("accountName", structured[0].Key);
		Assert.Equal("<redacted>", structured[1].Value);
		Assert.Equal("{OriginalFormat}", structured[2].Key);
		Assert.Equal("formatted", structured[2].Value);
	}

	[Fact]
	public void Log_WhenInnerDisabled_DoesNotForward()
	{
		var capturing = new DisabledCapturingProvider();
		var provider = new RedactingLoggerProvider(capturing);
		var logger = provider.CreateLogger("Test");

		logger.LogInformation("password=hunter2");

		Assert.Empty(capturing.Captured);
	}

	[Fact]
	public void Dispose_DisposesInnerProvider()
	{
		var inner = new CapturingProvider();
		var provider = new RedactingLoggerProvider(inner);

		provider.Dispose();

		Assert.True(inner.Disposed);
	}

	[Fact]
	public void AddRedactingConsole_RegistersWrappedConsoleProvider()
	{
		var services = new ServiceCollection();
		services.AddLogging(builder => builder.AddRedactingConsole());

		using var provider = services.BuildServiceProvider();
		var loggerProviders = provider.GetServices<ILoggerProvider>().ToList();

		Assert.Contains(loggerProviders, p => p is RedactingLoggerProvider);
		Assert.DoesNotContain(loggerProviders, p => p is Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider);

		var factory = provider.GetRequiredService<ILoggerFactory>();
		ILogger logger = factory.CreateLogger("Test");

		logger.LogInformation("token=super-secret-value");

		// The provider composition itself is validated; console output is exercised implicitly.
		Assert.True(logger.IsEnabled(LogLevel.Information));
	}

	private sealed class DisabledCapturingProvider : ILoggerProvider
	{
		public List<CapturedLog> Captured { get; } = [];

		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;

		public ILogger CreateLogger(string categoryName)
		{
			return new DisabledLogger(this);
		}

		private sealed class DisabledLogger(DisabledCapturingProvider owner) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => false;

			void ILogger.Log<TState>(
				LogLevel logLevel,
				EventId eventId,
				TState state,
				Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				owner.Captured.Add(new CapturedLog(logLevel, formatter(state, exception), exception, null));
			}
		}
	}
}
