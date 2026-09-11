using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Vapor.Protocol;
using Vapor.Steam.Core;
using Xunit;

namespace Vapor.Agent.Tests;

public class AgentTaskExecutorTests
{
	private const string AccountName = "test_account";

	private static BotSession CreateStubSession()
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		registry.Register(new Vapor.Steam.Core.Actions.EchoAction(NullLogger<Vapor.Steam.Core.Actions.EchoAction>.Instance));

		var credentials = new AccountCredentials(AccountName, "test_password");
		var session = new BotSession(
			AccountName,
			credentials,
			registry,
			NullLogger<BotSession>.Instance,
			steamClientManager: null,
			steamWebHandler: null,
			eventCallback: null);

		// The command pump must be running, otherwise ExecuteActionAsync would wait forever.
		session.Start();
		return session;
	}

	private static JobTask CreateTask(
		string action = "echo",
		string? target = null,
		IReadOnlyDictionary<string, object?>? payload = null)
	{
		var now = DateTimeOffset.UtcNow;
		return new JobTask(
			Id: "task-1",
			JobId: "job-1",
			Target: target ?? AccountName,
			Action: action,
			Region: null,
			Payload: payload,
			Status: JobTaskStatus.Running,
			Attempt: 1,
			CreatedAt: now,
			UpdatedAt: now);
	}

	private static (Mock<ISessionManager> SessionManager, List<AccountCredentials> CapturedCredentials) CreateMockedManager(BotSession session)
	{
		var mock = new Mock<ISessionManager>();
		var captured = new List<AccountCredentials>();

		mock.Setup(m => m.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<AccountCredentials>(), It.IsAny<CancellationToken>()))
			.Callback((string _, AccountCredentials credentials, CancellationToken _) => captured.Add(credentials))
			.ReturnsAsync(session);

		return (mock, captured);
	}

	[Fact]
	public async Task PasswordPayload_CreatesSessionWithPassword_AndExecutesAction()
	{
		var session = CreateStubSession();
		var (manager, captured) = CreateMockedManager(session);
		var payload = new Dictionary<string, object?> { ["password"] = "s3cret" };

		var (success, error, output) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: payload), manager.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		Assert.Null(error);
		Assert.Equal(AccountName, Assert.IsType<string>(output!["account"]));

		var credentials = Assert.Single(captured);
		Assert.Equal("s3cret", credentials.Password);
	}

	[Fact]
	public async Task PasswordAlias_Pass_IsRecognized()
	{
		var session = CreateStubSession();
		var (manager, captured) = CreateMockedManager(session);
		var payload = new Dictionary<string, object?> { ["pass"] = "alias-pw" };

		var (success, _, _) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: payload), manager.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		Assert.Equal("alias-pw", Assert.Single(captured).Password);
	}

	[Fact]
	public async Task RefreshTokenOnly_BuildsTokenOnlyCredentials()
	{
		var session = CreateStubSession();
		var (manager, captured) = CreateMockedManager(session);
		var payload = new Dictionary<string, object?> { ["refreshToken"] = "rt-value" };

		var (success, _, _) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: payload), manager.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		var credentials = Assert.Single(captured);
		Assert.Equal("rt-value", credentials.RefreshToken);
		Assert.Equal(string.Empty, credentials.Password);
	}

	[Fact]
	public async Task RefreshTokenSnakeCaseAlias_IsRecognized()
	{
		var session = CreateStubSession();
		var (manager, captured) = CreateMockedManager(session);
		var payload = new Dictionary<string, object?> { ["refresh_token"] = "snake-rt" };

		var (success, _, _) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: payload), manager.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		Assert.Equal("snake-rt", Assert.Single(captured).RefreshToken);
	}

	[Fact]
	public async Task AccessTokenAlongsideRefreshToken_IsForwarded()
	{
		var session = CreateStubSession();
		var (manager, captured) = CreateMockedManager(session);
		var payload = new Dictionary<string, object?>
		{
			["refreshToken"] = "rt-value",
			["accessToken"] = "at-value"
		};

		var (success, _, _) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: payload), manager.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		var credentials = Assert.Single(captured);
		Assert.Equal("at-value", credentials.AccessToken);
		Assert.Equal("rt-value", credentials.RefreshToken);
	}

	[Fact]
	public async Task PasswordWithAuthCodes_AreForwarded()
	{
		var session = CreateStubSession();
		var (manager, captured) = CreateMockedManager(session);
		var payload = new Dictionary<string, object?>
		{
			["password"] = "s3cret",
			["authCode"] = "12345",
			["two_factor_code"] = "654321"
		};

		var (success, _, _) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: payload), manager.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		var credentials = Assert.Single(captured);
		Assert.Equal("12345", credentials.AuthCode);
		Assert.Equal("654321", credentials.TwoFactorCode);
	}

	[Fact]
	public async Task NoCredentials_NoStoredSession_ReturnsNotFoundError()
	{
		var mock = new Mock<ISessionManager>();
		mock.Setup(m => m.TryRestoreSessionAsync(AccountName, It.IsAny<CancellationToken>()))
			.ReturnsAsync((BotSession?)null);

		var (success, error, output) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: new Dictionary<string, object?>()), mock.Object, NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.Equal("No credentials provided and no stored session found", error);
		Assert.Null(output);
	}

	[Fact]
	public async Task NoCredentials_StoredSessionRestored_ExecutesAction()
	{
		var session = CreateStubSession();
		var mock = new Mock<ISessionManager>();
		mock.Setup(m => m.TryRestoreSessionAsync(AccountName, It.IsAny<CancellationToken>()))
			.ReturnsAsync(session);

		var (success, error, output) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: new Dictionary<string, object?>()), mock.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
		Assert.Null(error);
		Assert.Equal(AccountName, Assert.IsType<string>(output!["account"]));
		mock.Verify(m => m.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<AccountCredentials>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task NullPayload_FallsBackToStoredSession()
	{
		var session = CreateStubSession();
		var mock = new Mock<ISessionManager>();
		mock.Setup(m => m.TryRestoreSessionAsync(AccountName, It.IsAny<CancellationToken>()))
			.ReturnsAsync(session);

		var (success, _, _) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: null), mock.Object, NullLogger.Instance, CancellationToken.None);

		Assert.True(success);
	}

	[Fact]
	public async Task CanceledWhileCreatingSession_ReturnsCanceledError()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		var mock = new Mock<ISessionManager>();
		mock.Setup(m => m.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<AccountCredentials>(), It.IsAny<CancellationToken>()))
			.Returns(Task.FromCanceled<BotSession>(cts.Token));

		var (success, error, output) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: new Dictionary<string, object?> { ["password"] = "s3cret" }),
			mock.Object,
			NullLogger.Instance,
			cts.Token);

		Assert.False(success);
		Assert.Equal("canceled", error);
		Assert.Null(output);
	}

	[Fact]
	public async Task UnexpectedException_IsMappedToErrorMessage()
	{
		var mock = new Mock<ISessionManager>();
		mock.Setup(m => m.GetOrCreateSessionAsync(It.IsAny<string>(), It.IsAny<AccountCredentials>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new InvalidOperationException("steam is down"));

		var (success, error, output) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(payload: new Dictionary<string, object?> { ["password"] = "s3cret" }),
			mock.Object,
			NullLogger.Instance,
			CancellationToken.None);

		Assert.False(success);
		Assert.Equal("steam is down", error);
		Assert.Null(output);
	}

	[Fact]
	public async Task UnknownAction_ReportsFailureFromSession()
	{
		var session = CreateStubSession();
		var (manager, _) = CreateMockedManager(session);
		var payload = new Dictionary<string, object?> { ["password"] = "s3cret" };

		var (success, error, _) = await AgentTaskExecutor.ExecuteAsync(
			CreateTask(action: "does_not_exist", payload: payload), manager.Object, NullLogger.Instance, CancellationToken.None);

		Assert.False(success);
		Assert.NotNull(error);
	}
}
