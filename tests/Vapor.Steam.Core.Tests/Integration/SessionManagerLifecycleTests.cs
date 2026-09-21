using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Vapor.Steam.Core.Actions;
using Vapor.Steam.Core.Security;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Integration;

/// <summary>
/// SessionManager 后台生命周期臂：事件转发泵的取消退出、令牌刷新循环的取消退出、
/// 以及同账号刷新已在进行时的跳过臂。私有泵任务表与刷新通道通过反射驱动，
/// 让这些防御臂在无时序竞争的前提下确定性地到达（见 tests/TESTING.md）。
/// </summary>
public sealed class SessionManagerLifecycleTests
{
	[Fact]
	public async Task DisposedManager_PumpExitsThroughTheCancellationArm()
	{
		var manager = CreateManager();

		var session = await manager.GetOrCreateSessionAsync(
			"pump-alice", new AccountCredentials("pump-alice", "pass"), CancellationToken.None);
		Assert.NotNull(session);

		// Driven directly (not via the manager's Task.Run) so the pump is
		// guaranteed to have started: the manager tracks Task.Run's unwrap
		// wrapper, which reports WaitingForActivation from the instant it is
		// created — no status poll can tell "queued" from "running", so a
		// Dispose landing before the queued pump starts would cancel it
		// wholesale instead of proving the OCE arm. A direct call synchronously
		// runs the pump body up to its first await on the manager's token.
		var pump = (Task)typeof(SessionManager)
			.GetMethod("PumpSessionEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(manager, [session, "pump-alice"])!;

		// The pump outlives its spawn point and only ever exits when the manager's
		// wide cancellation token fires; completing the pump proves the OCE arm ran
		// (the session channel has no producer-side completion, and disposal's
		// channel teardown completes it just as cleanly).
		manager.Dispose();

		await pump.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(TaskStatus.RanToCompletion, pump.Status);
	}

	[Fact]
	public async Task PumpSessionEvents_ExitsThroughChannelCompletion()
	{
		var manager = CreateManager();

		var session = await manager.GetOrCreateSessionAsync(
			"pump-close-bob", new AccountCredentials("pump-close-bob", "pass"), CancellationToken.None);
		Assert.NotNull(session);

		// The pump's await-foreach only ends normally when the session's event
		// channel completes — the manager itself never completes it (disposal
		// fires the token instead, which surfaces as the OCE arm), so this
		// drain-and-exit arm is otherwise unreachable. Completing the writer
		// here proves the foreach ends just as cleanly when a producer does
		// shut down.
		var channel = (Channel<SessionEvent>)typeof(BotSession)
			.GetField("_eventChannel", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(session)!;
		var pump = (Task)typeof(SessionManager)
			.GetMethod("PumpSessionEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(manager, [session, "pump-close-bob"])!;

		channel.Writer.Complete();

		// 30s budget: under CI load (2 cores + instrumentation) the pump's wakeup
		// after Complete still has to fight the scheduler; the healthy path does
		// not wait at all (the channel completes while the foreach is parked).
		await pump.WaitAsync(TimeSpan.FromSeconds(30));
		Assert.Equal(TaskStatus.RanToCompletion, pump.Status);

		manager.Dispose();
	}

	[Fact]
	public async Task GetOrCreateSession_DuplicateAccount_DisposesTheSecondAndReturnsTheFirst()
	{
		var manager = CreateManager();

		var first = await manager.GetOrCreateSessionAsync(
			"duplicate-alice", new AccountCredentials("duplicate-alice", "pass"), CancellationToken.None);
		var second = await manager.GetOrCreateSessionAsync(
			"duplicate-alice", new AccountCredentials("duplicate-alice", "pass"), CancellationToken.None);

		Assert.Same(first, second);

		manager.Dispose();
	}

	[Fact]
	public async Task RefreshPass_AlreadyInFlight_SkipsTheDuplicateRefresh()
	{
		var store = new Mock<ICredentialStore>(MockBehavior.Strict);
		store.Setup(m => m.GetAccessTokenAsync("busy-alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StoredAccessToken("tok", DateTimeOffset.UtcNow.AddHours(-1)));
		store.Setup(m => m.HasCredentialsAsync("busy-alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(true);
		var steam = new Mock<ISteamClientManager>(MockBehavior.Strict);
		steam.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		steam.Setup(m => m.LoginAsync("busy-alice", "pass", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		using var manager = CreateManager(credentialStore: store, steamClientManager: steam);

		var session = await manager.GetOrCreateSessionAsync(
			"busy-alice", new AccountCredentials("busy-alice", "pass"), CancellationToken.None);
		await session.LoginDirectAsync(CancellationToken.None);
		Assert.Equal(SessionState.Connected, session.State);

		// Simulate a refresh already running for this account: the pass must skip
		// the duplicate dispatch (and leave the in-flight marker alone).
		ConcurrentDictionary<string, byte> inFlight = GetInFlightMap(manager);
		Assert.True(inFlight.TryAdd("busy-alice", 0));

		await InvokeRefreshPassAsync(manager);

		steam.Verify(m => m.RefreshAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task TokenRefreshLoop_ExitsQuietlyThroughCancellation()
	{
		var store = new Mock<ICredentialStore>(MockBehavior.Strict);
		int ticks = 0;
		store.Setup(m => m.GetAccessTokenAsync("loop-alice", It.IsAny<CancellationToken>()))
			.ReturnsAsync(new StoredAccessToken("tok", DateTimeOffset.UtcNow.AddHours(2)))
			.Callback(() => Interlocked.Increment(ref ticks));
		var steam = new Mock<ISteamClientManager>(MockBehavior.Strict);
		steam.Setup(m => m.ConnectAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
		steam.Setup(m => m.LoginAsync("loop-alice", "pass", It.IsAny<CancellationToken>()))
			.Returns(Task.CompletedTask);
		var manager = CreateManager(
			credentialStore: store,
			steamClientManager: steam,
			checkInterval: TimeSpan.FromMilliseconds(25));

		var session = await manager.GetOrCreateSessionAsync(
			"loop-alice", new AccountCredentials("loop-alice", "pass"), CancellationToken.None);
		await session.LoginDirectAsync(CancellationToken.None);

		Task loop = GetRefreshLoop(manager);
		Assert.NotNull(loop);

		// Wait for the first observed tick so disposal is guaranteed to land on an
		// in-flight WaitForNextTickAsync (a cancel before the task even started
		// would skip the loop body entirely and never reach the OCE arm).
		DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (Volatile.Read(ref ticks) == 0 && DateTimeOffset.UtcNow < deadline)
		{
			await Task.Delay(10);
		}
		Assert.True(Volatile.Read(ref ticks) >= 1, "refresh loop never ticked");

		manager.Dispose();
		await loop.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(TaskStatus.RanToCompletion, loop.Status);
	}

	private static SessionManager CreateManager(
		Mock<ICredentialStore>? credentialStore = null,
		Mock<ISteamClientManager>? steamClientManager = null,
		TimeSpan? checkInterval = null)
	{
		var registry = new ActionRegistry(NullLogger<ActionRegistry>.Instance);
		registry.Register(new LoginAction(NullLogger<LoginAction>.Instance));
		return new SessionManager(
			registry,
			NullLogger<SessionManager>.Instance,
			steamClientManager?.Object,
			credentialStore?.Object,
			loggerFactory: null,
			tokenRefreshCheckInterval: checkInterval);
	}

	private static Task GetRefreshLoop(SessionManager manager) =>
		(Task)typeof(SessionManager)
			.GetField("_tokenRefreshTask", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(manager)!;

	private static ConcurrentDictionary<string, byte> GetInFlightMap(SessionManager manager) =>
		(ConcurrentDictionary<string, byte>)typeof(SessionManager)
			.GetField("_tokenRefreshInFlight", BindingFlags.NonPublic | BindingFlags.Instance)!
			.GetValue(manager)!;

	private static async Task InvokeRefreshPassAsync(SessionManager manager)
	{
		MethodInfo? refresh = typeof(SessionManager).GetMethod(
			"RefreshExpiringSessionsAsync", BindingFlags.NonPublic | BindingFlags.Instance);
		Assert.NotNull(refresh);
		try
		{
			await ((Task)refresh.Invoke(manager, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(10));
		}
		catch (TargetInvocationException ex) when (ex.InnerException is not null)
		{
			System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
		}
	}
}
