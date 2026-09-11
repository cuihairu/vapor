using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Vapor.Steam.Core;

namespace Vapor.Steam.Core.Tests.Unit;

public class ActionRegistryExecutionObserverTests
{
	private sealed class RecordingObserver(List<(string Action, bool Success, double DurationMs)> log) : IActionExecutionObserver
	{
		public void OnActionExecuted(string actionName, bool success, double durationMs) =>
			log.Add((actionName, success, durationMs));
	}

	private sealed class ThrowingObserver : IActionExecutionObserver
	{
		public void OnActionExecuted(string actionName, bool success, double durationMs) =>
			throw new InvalidOperationException("observer exploded");
	}

	private sealed class StubAction(string name, bool success = true) : IAction
	{
		public string Name => name;

		public ActionMetadata Metadata { get; } = new(name, "Stub action.");

		public Task<ActionResult> ExecuteAsync(
			BotSession session,
			IReadOnlyDictionary<string, object?> payload,
			CancellationToken cancellationToken) => Task.FromResult(new ActionResult(success));
	}

	[Fact]
	public void AddThenRaise_NotifiesAllObservers()
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		var first = new List<(string, bool, double)>();
		var second = new List<(string, bool, double)>();

		registry.AddExecutionObserver(new RecordingObserver(first));
		registry.AddExecutionObserver(new RecordingObserver(second));
		registry.RaiseActionExecuted("echo", success: true, durationMs: 12.5);

		var entry = Assert.Single(first);
		Assert.Equal(("echo", true, 12.5), entry);
		Assert.Single(second);
	}

	[Fact]
	public void RemoveObserver_StopsNotifications()
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		var log = new List<(string, bool, double)>();
		var observer = new RecordingObserver(log);

		registry.AddExecutionObserver(observer);
		Assert.True(registry.RemoveExecutionObserver(observer));
		Assert.False(registry.RemoveExecutionObserver(observer));
		registry.RaiseActionExecuted("echo", success: true, durationMs: 1);

		Assert.Empty(log);
	}

	[Fact]
	public void Raise_WithThrowingObserver_DoesNotBreakOthers()
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		var log = new List<(string, bool, double)>();

		registry.AddExecutionObserver(new ThrowingObserver());
		registry.AddExecutionObserver(new RecordingObserver(log));
		registry.RaiseActionExecuted("echo", success: false, durationMs: 3);

		var entry = Assert.Single(log);
		Assert.Equal(("echo", false, 3), entry);
	}

	[Fact]
	public async Task BotSession_Execution_NotifiesObserver()
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		registry.Register(new StubAction("stub_ok"));
		registry.Register(new StubAction("stub_fail", success: false));

		var log = new List<(string, bool, double)>();
		registry.AddExecutionObserver(new RecordingObserver(log));

		var session = new BotSession(
			"test_account",
			new AccountCredentials("test_account", "password"),
			registry,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: null,
			eventCallback: null);
		session.Start();
		var ok = await session.ExecuteActionAsync("stub_ok", new Dictionary<string, object?>(), CancellationToken.None);
		var fail = await session.ExecuteActionAsync("stub_fail", new Dictionary<string, object?>(), CancellationToken.None);
		await session.DisconnectAsync(CancellationToken.None);

		Assert.True(ok.Success);
		Assert.False(fail.Success);
		Assert.Equal(2, log.Count);
		Assert.Equal(("stub_ok", true, log[0].Item3), log[0]);
		Assert.Equal(("stub_fail", false, log[1].Item3), log[1]);
		Assert.All(log, entry => Assert.True(entry.Item3 >= 0));
	}
}
