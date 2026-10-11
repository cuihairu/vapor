using Xunit;

using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

public class Cs2GcWaitRegistryTests
{
	private sealed record ProbeBody(uint Request);

	[Fact]
	public async Task Register_CompletesOnMatchingMessageType()
	{
		var registry = new Cs2GcWaitRegistry();
		(Task<object?> task, IDisposable registration) = registry.Register(1090);
		using (registration)
		{
			var body = new ProbeBody(1007);
			Assert.True(registry.Complete(1090, body));
			Assert.Same(body, await task);
		}
	}

	[Fact]
	public void Complete_UnknownMessageType_ReturnsFalse()
	{
		var registry = new Cs2GcWaitRegistry();
		(Task<object?> task, IDisposable registration) = registry.Register(1090);
		using (registration)
		{
			Assert.False(registry.Complete(4004, new ProbeBody(1)));
			Assert.False(task.IsCompleted);
		}
	}

	[Fact]
	public async Task Complete_VerifyRejection_LeavesWaitPending()
	{
		var registry = new Cs2GcWaitRegistry();
		(Task<object?> task, IDisposable registration) = registry.Register(1090, body => ((ProbeBody)body).Request == 1007);
		using (registration)
		{
			Assert.False(registry.Complete(1090, new ProbeBody(9999)));
			Assert.False(task.IsCompleted);

			Assert.True(registry.Complete(1090, new ProbeBody(1007)));
			Assert.Equal((uint)1007, ((ProbeBody)(await task)!).Request);
		}
	}

	[Fact]
	public async Task Complete_IsOneShotPerWait()
	{
		var registry = new Cs2GcWaitRegistry();
		(Task<object?> task, IDisposable registration) = registry.Register(1090);
		using (registration)
		{
			Assert.True(registry.Complete(1090, new ProbeBody(1007)));
			Assert.False(registry.Complete(1090, new ProbeBody(1008)));
			Assert.Equal((uint)1007, ((ProbeBody)(await task)!).Request);
		}
	}

	[Fact]
	public async Task Dispose_BeforeReply_SettlesTheTaskWithNull_AndRemovesTheWait()
	{
		var registry = new Cs2GcWaitRegistry();
		(Task<object?> task, IDisposable registration) = registry.Register(1090);
		registration.Dispose();

		Assert.False(registry.Complete(1090, new ProbeBody(1007)));
		Assert.Null(await task!);
	}

	[Fact]
	public async Task Dispose_AfterComplete_IsHarmless()
	{
		var registry = new Cs2GcWaitRegistry();
		(Task<object?> task, IDisposable registration) = registry.Register(1090);
		Assert.True(registry.Complete(1090, new ProbeBody(1007)));
		registration.Dispose();

		Assert.NotNull(await task);
	}

	[Fact]
	public async Task Complete_MultipleWaitsOnSameType_FeedsThemInRegistrationOrder()
	{
		var registry = new Cs2GcWaitRegistry();
		(Task<object?> first, IDisposable firstRegistration) = registry.Register(1090);
		(Task<object?> second, IDisposable secondRegistration) = registry.Register(1090);
		using (firstRegistration)
		using (secondRegistration)
		{
			var body = new ProbeBody(1007);
			Assert.True(registry.Complete(1090, body));
			Assert.Same(body, await first);
			Assert.False(second.IsCompleted);
		}
	}

	[Fact]
	public void Complete_NullBody_Throws()
	{
		var registry = new Cs2GcWaitRegistry();
		Assert.Throws<ArgumentNullException>(() => registry.Complete(1090, null!));
	}

	[Fact]
	public void Protocol_Constants_MatchTheGeneratedGcBindings()
	{
		// The constants encode SteamKit2's generated CS2 GC values; a drift here
		// would send well-formed messages to the wrong message types.
		Assert.Equal((uint)730, Cs2GcProtocol.AppId);
		Assert.Equal(2ul, Cs2GcProtocol.InventoryContextId);
		Assert.Equal((uint)4006, Cs2GcProtocol.ClientHelloMsg);
		Assert.Equal((uint)4004, Cs2GcProtocol.ClientWelcomeMsg);
		Assert.Equal((uint)2534, Cs2GcProtocol.OpenCrateMsg);
		Assert.Equal((uint)1090, Cs2GcProtocol.ItemCustomizationNotificationMsg);
		Assert.Equal((uint)1007, Cs2GcProtocol.UnlockCrateRequest);
		Assert.Equal((uint)2000568, Cs2GcProtocol.ClientHelloVersion);
	}
}
