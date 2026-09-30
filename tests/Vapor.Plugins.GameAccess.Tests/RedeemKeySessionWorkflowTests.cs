using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Plugins.Core;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Plugins.GameAccess.Tests;

/// <summary>
/// Moved with <c>RedeemKeyAction</c> from the host's integration suite: the
/// redeem workflow exercised through a real <see cref="SessionManager"/> +
/// <see cref="ActionRegistry"/> — the same composition the agent assembles
/// after plugin load — rather than through the action class directly.
/// </summary>
public sealed class RedeemKeySessionWorkflowTests : IDisposable
{
	private readonly Mock<ILogger<SessionManager>> _loggerMock = new(MockBehavior.Loose);
	private readonly ActionRegistry _actionRegistry;
	private readonly SessionManager _sessionManager;

	public RedeemKeySessionWorkflowTests()
	{
		_actionRegistry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		_sessionManager = new SessionManager(_actionRegistry, _loggerMock.Object, null);
		_actionRegistry.Register(new RedeemKeyAction(NullLogger<RedeemKeyAction>.Instance));
	}

	public void Dispose() => _sessionManager.Dispose();

	[Fact]
	public async Task Workflow_RedeemKeyWithValidKey_ReturnsStubResponse()
	{
		var session = await _sessionManager.GetOrCreateSessionAsync(
			"test_account", new AccountCredentials("test_account", "password"), CancellationToken.None);

		var payload = new Dictionary<string, object?> { ["key"] = "AAAAA-BBBBB-CCCCC" };
		var result = await session.ExecuteActionAsync("redeem_key", payload, CancellationToken.None);

		// In stub mode (no SteamClientManager), action returns error
		Assert.NotNull(result.Output);
		Assert.Equal("redeem_key", result.Output["action"]?.ToString());

		var maskedKey = result.Output["key"]?.ToString() ?? "";
		Assert.DoesNotContain("BBBBB", maskedKey);

		Assert.False(result.Success);
		Assert.Contains("Steam client not available", result.Error);
	}

	[Fact]
	public async Task Workflow_RedeemKeyWithMissingKey_ReturnsFailure()
	{
		var session = await _sessionManager.GetOrCreateSessionAsync(
			"test_account", new AccountCredentials("test_account", "password"), CancellationToken.None);

		var result = await session.ExecuteActionAsync("redeem_key", new Dictionary<string, object?>(), CancellationToken.None);

		Assert.False(result.Success);
		Assert.Equal("key is required", result.Error);
	}
}
