using System.Runtime.CompilerServices;
using Vapor.ControlPlane;
using Vapor.Protocol;

namespace Vapor.ControlPlane.Tests;

internal sealed class FakeCrawlEventBroker : IEventBroker
{
	private readonly object _lock = new();
	private readonly List<(string? JobId, string Type, IReadOnlyDictionary<string, object?>? Payload)> _published = [];

	/// <summary>
	/// Snapshot on read: Publish runs on the worker's background loop while
	/// assertions and WaitUntilAsync predicates enumerate concurrently.
	/// </summary>
	public IReadOnlyList<(string? JobId, string Type, IReadOnlyDictionary<string, object?>? Payload)> Published
	{
		get { lock (_lock) return _published.ToArray(); }
	}

	/// <summary>Publish throws InvalidOperationException this many times before behaving again.</summary>
	public int ThrowOnPublishRuns { get; set; }

	public void Publish(string? jobId, string type, IReadOnlyDictionary<string, object?>? payload)
	{
		if (ThrowOnPublishRuns > 0)
		{
			ThrowOnPublishRuns--;
			throw new InvalidOperationException("event broker unavailable");
		}

		lock (_lock)
		{
			_published.Add((jobId, type, payload));
		}
	}

	public void PublishSession(string accountName, string eventType, string state, string? message = null)
	{
	}

	public void PublishAuthChallenge(string accountName, string challengeType, string? message = null, string? code = null, int attempt = 1)
	{
	}

	public async IAsyncEnumerable<Event> Subscribe([EnumeratorCancellation] CancellationToken cancellationToken, string jobId)
	{
		await Task.CompletedTask;
		yield break;
	}

	public async IAsyncEnumerable<SessionEvent> SubscribeSessions([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
	{
		await Task.CompletedTask;
		yield break;
	}

	public async IAsyncEnumerable<AuthChallengeEvent> SubscribeAuthChallenges([EnumeratorCancellation] CancellationToken cancellationToken, string? accountName = null)
	{
		await Task.CompletedTask;
		yield break;
	}
}
