using Moq;
using Xunit;
using Vapor.Steam.Core.Security;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>Proxy assignment tests: malformed/blank/credential-bearing endpoints, live-session restart and deferred-restart reporting.</summary>
public partial class SessionManagerTests
{
	[Fact]
	public async Task SetProxyAsync_WithMalformedProxy_ThrowsStaticMessageWithoutEchoingCredentials()
	{
		// ProxyOptions.Invalid echoes the raw endpoint string, credentials
		// included. The assignment path must never surface that text: the
		// rethrow carries a static message only.
		var ex = await Assert.ThrowsAsync<ArgumentException>(
			() => _manager.SetProxyAsync("test_account", "socks5://user:s3cret@host:notaport", CancellationToken.None));

		// ArgumentException appends " (Parameter 'proxy')" to the message itself.
		Assert.StartsWith("invalid proxy endpoint: expected scheme://host:port (http, https or socks5)", ex.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("s3cret", ex.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("notaport", ex.Message, StringComparison.Ordinal);
		Assert.Equal("proxy", ex.ParamName);
	}

	[Fact]
	public async Task SetProxyAsync_WithoutCredentialStore_ThrowsInvalidOperationException()
	{
		// The default manager in this fixture has no credential store wired in.
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(
			() => _manager.SetProxyAsync("test_account", "socks5://gw.example.com:1080", CancellationToken.None));

		Assert.Contains("credential store", ex.Message);
	}

	[Fact]
	public async Task SetProxyAsync_WithNoLiveSession_PersistsTrimmedProxyAndDefersRestart()
	{
		var stored = CreateProxyAssignmentCredentialStore("socks5://gw.example.com:1080");
		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			stored.Object);

		// Surrounding whitespace must not survive into the persisted endpoint.
		var result = await manager.SetProxyAsync("test_account", "  socks5://gw.example.com:1080  ", CancellationToken.None);

		Assert.False(result.Cleared);
		Assert.False(result.SessionRestarted);
		Assert.Equal("socks5://gw.example.com:1080", result.Proxy);
		stored.Verify(
			s => s.SaveProxyAsync("test_account", "socks5://gw.example.com:1080", It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task SetProxyAsync_WithCredentialBearingProxy_ReportsMaskedEndpointOnly()
	{
		var stored = CreateProxyAssignmentCredentialStore("socks5://alice:secret123@10.0.0.9:1080");
		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			stored.Object);

		var result = await manager.SetProxyAsync("test_account", "socks5://alice:secret123@10.0.0.9:1080", CancellationToken.None);

		// The raw endpoint (secret included) goes to the encrypted store only;
		// the reported form is the masked display shape.
		Assert.Equal("socks5://alice:<redacted>@10.0.0.9:1080", result.Proxy);
		stored.Verify(
			s => s.SaveProxyAsync("test_account", "socks5://alice:secret123@10.0.0.9:1080", It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task SetProxyAsync_WithBlankProxy_ClearsPersistedProxy()
	{
		var stored = CreateProxyAssignmentCredentialStore(null);
		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			stored.Object);

		var result = await manager.SetProxyAsync("test_account", "   ", CancellationToken.None);

		Assert.True(result.Cleared);
		Assert.False(result.SessionRestarted);
		Assert.Null(result.Proxy);
		stored.Verify(
			s => s.SaveProxyAsync("test_account", null, It.IsAny<CancellationToken>()),
			Times.Once);
	}

	[Fact]
	public async Task SetProxyAsync_WithLiveSession_RestartsSessionThroughNewExit()
	{
		// The store starts empty, a session is restored direct (no proxy), and
		// only then is the new exit written: the rebuild must read the freshly
		// persisted proxy, not a stale value, so GetProxyAsync resolves lazily.
		string? storedProxy = null;
		var stored = CreateSuccessfulRestoreCredentialStore();
		stored
			.Setup(s => s.GetProxyAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => storedProxy);
		stored
			.Setup(s => s.SaveProxyAsync("test_account", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask)
			.Callback<string, string?, CancellationToken>((_, proxy, _) => storedProxy = proxy);
		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			stored.Object);
		var original = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(original);

		var result = await manager.SetProxyAsync("test_account", "socks5://gw.example.com:1080", CancellationToken.None);

		Assert.False(result.Cleared);
		Assert.True(result.SessionRestarted);
		Assert.Equal("socks5://gw.example.com:1080", result.Proxy);
		var rebuilt = await manager.GetSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(rebuilt);
		Assert.NotSame(original, rebuilt);
	}

	[Fact]
	public async Task SetProxyAsync_WithLiveSession_ClearDropsBackToDirect()
	{
		// Clearing while a session is live exercises the parsed-null arms on
		// the rebuild path (no masked endpoint, the log falls back to
		// "(direct)"): the session still rebuilds, now without an exit.
		string? storedProxy = "socks5://gw.example.com:1080";
		var stored = CreateSuccessfulRestoreCredentialStore();
		stored
			.Setup(s => s.GetProxyAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => storedProxy);
		stored
			.Setup(s => s.SaveProxyAsync("test_account", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask)
			.Callback<string, string?, CancellationToken>((_, proxy, _) => storedProxy = proxy);
		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			stored.Object);
		var original = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(original);

		var result = await manager.SetProxyAsync("test_account", "   ", CancellationToken.None);

		Assert.True(result.Cleared);
		Assert.Null(result.Proxy);
		Assert.True(result.SessionRestarted);
		var rebuilt = await manager.GetSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(rebuilt);
		Assert.NotSame(original, rebuilt);
	}

	[Fact]
	public async Task SetProxyAsync_WithLiveSession_RestoreFailure_ReportsDeferredRestartAndDropsSession()
	{
		// First sign-in (the direct restore) succeeds; every later one — the
		// post-assignment rebuild — fails. A failed rebuild leaves no half-open
		// session behind: the account comes back at its next scheduled login.
		var loginAttempts = 0;
		var stored = CreateSuccessfulRestoreCredentialStore();
		string? storedProxy = null;
		stored
			.Setup(s => s.GetProxyAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => storedProxy);
		stored
			.Setup(s => s.SaveProxyAsync("test_account", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask)
			.Callback<string, string?, CancellationToken>((_, proxy, _) => storedProxy = proxy);
		_steamClientManagerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.UpdateLogOnDetailsAsync("test_account", "access-token", "refresh-token"))
			.Returns(Task.CompletedTask);
		_steamClientManagerMock
			.Setup(m => m.LoginAsync("test_account", string.Empty, It.IsAny<CancellationToken>()))
			.Returns(() => Interlocked.Increment(ref loginAttempts) == 1
				? Task.CompletedTask
				: Task.FromException(new InvalidOperationException("login exploded")));

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			stored.Object);
		var original = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(original);

		var result = await manager.SetProxyAsync("test_account", "socks5://gw.example.com:1080", CancellationToken.None);

		// The assignment persisted either way; only the restart was deferred.
		Assert.False(result.Cleared);
		Assert.False(result.SessionRestarted);
		var afterFailure = await manager.GetSessionAsync("test_account", CancellationToken.None);
		Assert.Null(afterFailure);
	}

	[Fact]
	public async Task SetProxyAsync_WithLiveSessionAndBlankProxy_ClearsAndRestartsDirect()
	{
		// Clearing with a live session rebuilds it proxy-free: the assignment
		// report carries no endpoint at all (parsed never runs) while the
		// restart still happened — the direct connection is verified state.
		var stored = CreateSuccessfulRestoreCredentialStore();
		string? storedProxy = "socks5://gw.example.com:1080";
		stored
			.Setup(s => s.GetProxyAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(() => storedProxy);
		stored
			.Setup(s => s.SaveProxyAsync("test_account", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask)
			.Callback<string, string?, CancellationToken>((_, proxy, _) => storedProxy = proxy);
		SetupSuccessfulTokenLogin();

		using var manager = new SessionManager(
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_steamClientManagerMock.Object,
			stored.Object);
		var original = await manager.TryRestoreSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(original);

		var result = await manager.SetProxyAsync("test_account", "   ", CancellationToken.None);

		Assert.True(result.Cleared);
		Assert.Null(result.Proxy);
		Assert.True(result.SessionRestarted);
		var rebuilt = await manager.GetSessionAsync("test_account", CancellationToken.None);
		Assert.NotNull(rebuilt);
		Assert.NotSame(original, rebuilt);
	}

	/// <summary>
	/// Strict store pre-settled for a SetProxyAsync call with the given proxy
	/// outcome: assignment persistence plus the read-backs of a plain (no
	/// session) call path. Tests that restore sessions extend it further.
	/// </summary>
	private static Mock<ICredentialStore> CreateProxyAssignmentCredentialStore(string? proxyOnRead)
	{
		var credentialStoreMock = new Mock<ICredentialStore>(MockBehavior.Strict);
		credentialStoreMock
			.Setup(s => s.SaveProxyAsync("test_account", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		credentialStoreMock
			.Setup(s => s.GetProxyAsync("test_account", It.IsAny<CancellationToken>()))
			.ReturnsAsync(proxyOnRead);
		return credentialStoreMock;
	}

}
