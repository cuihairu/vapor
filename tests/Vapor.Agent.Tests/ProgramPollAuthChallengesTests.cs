using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

public sealed class ProgramPollAuthChallengesTests
{
	[Fact]
	public async Task PollAuthChallenges_NoSessions_WaitsBeforePollingAgain()
	{
		Type program = typeof(AgentWebSocketUri).Assembly.GetType("Program", throwOnError: true)!;
		MethodInfo? poll = program
			.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
			.SingleOrDefault(method => method.Name.Contains("PollAuthChallengesAsync", StringComparison.Ordinal));
		Assert.NotNull(poll);

		using var cancellation = new CancellationTokenSource();
		var sessionManager = new Mock<ISessionManager>();
		int pollCount = 0;
		sessionManager
			.Setup(manager => manager.ListSessions())
			.Callback(() =>
			{
				if (Interlocked.Increment(ref pollCount) == 1)
				{
					cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
				}
			})
			.Returns(Array.Empty<BotSession>());

		Task polling = (Task)poll.Invoke(
			null,
			[
				"agent-1",
				"local",
				"ws://localhost:5000/agent",
				"agent-key",
				sessionManager.Object,
				NullLogger.Instance,
				cancellation.Token
			])!;

		await polling.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
		sessionManager.Verify(manager => manager.ListSessions(), Times.Once);
	}
}
