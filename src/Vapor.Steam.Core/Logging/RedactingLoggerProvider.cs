using System.Text;
using Microsoft.Extensions.Logging;
using Vapor.Steam.Core.Utilities;

namespace Vapor.Steam.Core.Logging;

/// <summary>
/// Wraps any <see cref="ILoggerProvider"/> and redacts sensitive values
/// (passwords, tokens, auth codes, keys, ...) from every log message,
/// structured state value, scope and exception message before they reach
/// the underlying sink. This is the last-chance, provider-level safety net
/// for the "full-chain log redaction" requirement.
/// </summary>
public sealed class RedactingLoggerProvider : ILoggerProvider
{
	private readonly ILoggerProvider _inner;

	public RedactingLoggerProvider(ILoggerProvider inner)
	{
		_inner = inner ?? throw new ArgumentNullException(nameof(inner));
	}

	public ILogger CreateLogger(string categoryName)
	{
		return new RedactingLogger(_inner.CreateLogger(categoryName));
	}

	public void Dispose()
	{
		_inner.Dispose();
	}

	private sealed class RedactingLogger(ILogger inner) : ILogger
	{
		private static readonly Func<object?, Exception?, string> RedactingFormatter = static (state, _) =>
			state?.ToString() ?? string.Empty;

		public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

		IDisposable? ILogger.BeginScope<TState>(TState state)
		{
			object? redacted = state switch
			{
				string s => SensitiveDataRedactor.Redact(s),
				IReadOnlyList<KeyValuePair<string, object?>> pairs => RedactStructuredState(pairs),
				_ => state
			};

			return inner.BeginScope(redacted);
		}

		void ILogger.Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (!inner.IsEnabled(logLevel))
			{
				return;
			}

			string originalMessage = formatter(state, exception);
			string redactedMessage = SensitiveDataRedactor.Redact(originalMessage);

			Exception? redactedException = exception == null ? null : RedactException(exception);

			if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
			{
				var redactedState = new RedactedStructuredState(
					RedactStructuredState(structured),
					redactedMessage);

				inner.Log(logLevel, eventId, redactedState, redactedException, RedactingFormatter);
				return;
			}

			inner.Log(logLevel, eventId, redactedMessage, redactedException, RedactingFormatter);
		}

		private static IReadOnlyList<KeyValuePair<string, object?>> RedactStructuredState(
			IReadOnlyList<KeyValuePair<string, object?>> state)
		{
			List<KeyValuePair<string, object?>> redacted = new(state.Count);
			foreach (var pair in state)
			{
				redacted.Add(new KeyValuePair<string, object?>(
					pair.Key,
					SensitiveDataRedactor.RedactValue(pair.Key, pair.Value as string) ?? pair.Value));
			}

			return redacted;
		}

		private static Exception RedactException(Exception exception)
		{
			// ToString() carries type, message and stack trace; redact all of it
			// so query strings or embedded secrets in any part are neutralized.
			string redacted = SensitiveDataRedactor.Redact(exception.ToString());
			var wrapper = new RedactedException(redacted);
			wrapper.Data["OriginalExceptionType"] = exception.GetType().FullName;

			if (exception.InnerException != null)
			{
				wrapper.Data["InnerException"] = RedactException(exception.InnerException);
			}

			return wrapper;
		}

		/// <summary>
		/// Structured state carrying already-redacted values plus the redacted
		/// formatted message, so console/json formatters render safe output.
		/// </summary>
		private sealed class RedactedStructuredState(
			IReadOnlyList<KeyValuePair<string, object?>> pairs,
			string message)
			: IReadOnlyList<KeyValuePair<string, object?>>
		{
			private readonly IReadOnlyList<KeyValuePair<string, object?>> _pairs = [.. pairs, new KeyValuePair<string, object?>("{OriginalFormat}", message)];

			public int Count => _pairs.Count;

			public KeyValuePair<string, object?> this[int index] => _pairs[index];

			public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _pairs.GetEnumerator();

			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

			public override string ToString()
			{
				var sb = new StringBuilder();
				foreach (var pair in _pairs)
				{
					if (sb.Length > 0)
					{
						sb.Append(", ");
					}

					sb.Append(pair.Key).Append(':').Append(pair.Value);
				}

				return sb.ToString();
			}
		}

		private sealed class RedactedException(string message) : Exception(message);
	}
}
