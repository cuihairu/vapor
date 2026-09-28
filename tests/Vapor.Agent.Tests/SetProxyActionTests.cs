using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Agent;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

/// <summary>
/// SetProxyAction over a mocked session manager: payload validation, the
/// assign/clear pass-through of the session manager's semantics, output
/// mapping (the masked endpoint only — the action never echoes raw
/// credentials), and failure mapping when the assignment rejects an endpoint.
/// </summary>
public sealed class SetProxyActionTests
{
	[Fact]
	public async Task ExecuteAsync_WithoutAccountField_FailsWithRequiredMessage()
	{
		var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
		var action = new SetProxyAction(sessionManager.Object, NullLogger.Instance);

		var result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["proxy"] = "socks5://gw.example.com:1080" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.Contains("'account'", result.Error);
		Assert.Null(result.Output);
	}

	[Fact]
	public async Task ExecuteAsync_WithAccountOnly_TreatsMissingProxyAsClear()
	{
		var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
		var action = new SetProxyAction(sessionManager.Object, NullLogger.Instance);
		sessionManager
			.Setup(m => m.SetProxyAsync("test_account", null, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ProxyAssignmentResult(null, Cleared: true, SessionRestarted: false));

		var result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["account"] = "test_account" },
			CancellationToken.None);

		Assert.True(result.Success);
		Assert.Null(result.Error);
		Assert.Equal(true, result.Output!["cleared"]);
		Assert.Equal(false, result.Output["sessionRestarted"]);
	}

	[Fact]
	public async Task ExecuteAsync_WithProxy_PassesRawEndpointThrough()
	{
		// The action forwards the raw endpoint (the session manager is the
		// validator) and reports only the masked form the assignment returns.
		var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
		var action = new SetProxyAction(sessionManager.Object, NullLogger.Instance);
		const string raw = "  socks5://alice:secret123@10.0.0.9:1080  ";
		sessionManager
			.Setup(m => m.SetProxyAsync("test_account", raw, It.IsAny<CancellationToken>()))
			.ReturnsAsync(new ProxyAssignmentResult("socks5://alice:<redacted>@10.0.0.9:1080", Cleared: false, SessionRestarted: true));

		var result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["account"] = "test_account", ["proxy"] = raw },
			CancellationToken.None);

		Assert.True(result.Success);
		sessionManager.Verify(
			m => m.SetProxyAsync("test_account", raw, It.IsAny<CancellationToken>()),
			Times.Once);
		Assert.Equal("test_account", result.Output!["account"]);
		Assert.Equal("socks5://alice:<redacted>@10.0.0.9:1080", result.Output["proxy"]);
		Assert.Equal(false, result.Output["cleared"]);
		Assert.Equal(true, result.Output["sessionRestarted"]);
	}

	[Fact]
	public async Task ExecuteAsync_WhenAssignmentRejectsEndpoint_FailsWithStaticMessage()
	{
		var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
		var action = new SetProxyAction(sessionManager.Object, NullLogger.Instance);
		sessionManager
			.Setup(m => m.SetProxyAsync("test_account", "nope", It.IsAny<CancellationToken>()))
			.ThrowsAsync(new ArgumentException(
				"invalid proxy endpoint: expected scheme://host:port (http, https or socks5)", "proxy"));

		var result = await action.ExecuteAsync(
			new Dictionary<string, object?> { ["account"] = "test_account", ["proxy"] = "nope" },
			CancellationToken.None);

		Assert.False(result.Success);
		Assert.StartsWith("invalid proxy endpoint:", result.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("nope", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public void Metadata_DescribesSetProxy()
	{
		var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
		var action = new SetProxyAction(sessionManager.Object, NullLogger.Instance);

		Assert.Equal("set_proxy", action.Name);
		Assert.Equal("set_proxy", action.Metadata.Name);
		Assert.False(action.Metadata.RequiresLogin);
		Assert.Equal(120, action.Metadata.TimeoutSeconds);
	}
}
