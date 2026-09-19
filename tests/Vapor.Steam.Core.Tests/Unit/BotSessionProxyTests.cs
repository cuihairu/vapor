using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Per-account proxy hand-off on the password log-on path: a configured proxy
/// is staged on the transport before the CM log-on, and the same flow without
/// one must not touch the transport's proxy setter.
/// </summary>
public sealed class BotSessionProxyTests : IDisposable
{
	private const string Account = "proxy_account";
	private const string Proxy = "socks5://gw.example.com:1080";

	private readonly Mock<ILogger<BotSession>> _loggerMock = new(MockBehavior.Loose);
	private readonly Mock<IActionRegistry> _actionRegistryMock = new(MockBehavior.Loose);
	private readonly Mock<ISteamClientManager> _managerMock = new(MockBehavior.Loose);
	private readonly List<BotSession> _sessions = [];

	[Fact]
	public async Task LoginAsync_WithConfiguredProxy_StagesItOnTheTransport()
	{
		_managerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_managerMock
			.Setup(m => m.LoginAsync(Account, "password", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		var session = CreateSession(proxy: Proxy);
		session.Start();

		var result = await session.LoginAsync(CancellationToken.None);

		Assert.True(result.Success);
		_managerMock.Verify(
			m => m.SetAccountProxyAsync(Account, Proxy, It.IsAny<CancellationToken>()),
			Times.Once);
		_managerMock.Verify(m => m.LoginAsync(Account, "password", It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task LoginAsync_WithoutProxy_NeverTouchesTransportProxySetter()
	{
		_managerMock
			.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		_managerMock
			.Setup(m => m.LoginAsync(Account, "password", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		var session = CreateSession(proxy: null);
		session.Start();

		var result = await session.LoginAsync(CancellationToken.None);

		Assert.True(result.Success);
		_managerMock.Verify(
			m => m.SetAccountProxyAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
			Times.Never);
	}

	private BotSession CreateSession(string? proxy)
	{
		var session = new BotSession(
			Account,
			new AccountCredentials(Account, "password", Proxy: proxy),
			_actionRegistryMock.Object,
			_loggerMock.Object,
			_managerMock.Object);
		_sessions.Add(session);
		return session;
	}

	public void Dispose()
	{
		foreach (var session in _sessions)
		{
			session.Dispose();
		}
	}
}
