using System.Collections.Concurrent;

namespace Vapor.Steam.Core.Steam;

/// <summary>
/// Outcome of the CS2 game-coordinator handshake: whether the session is
/// usable, plus an operator-facing status line for action output.
/// </summary>
public sealed record Cs2GcSessionResult(bool Success, string Message);

/// <summary>The prize asset ids the game coordinator reported for one crate.</summary>
public sealed record Cs2OpenCrateResult(IReadOnlyList<ulong> PrizeAssetIds);

/// <summary>
/// CS2 game-coordinator wire constants shared by the transport glue and the
/// case-opening plugin (values from SteamKit2's generated CS2 GC bindings).
/// </summary>
public static class Cs2GcProtocol
{
	/// <summary>CS2's app id, and the app id GC traffic is routed by.</summary>
	public const uint AppId = 730;

	/// <summary>The CS2 backpack inventory context.</summary>
	public const ulong InventoryContextId = 2;

	/// <summary>
	/// Client hello version sent with <c>k_EMsgGCClientHello</c>. The GC turns
	/// stale versions away, so this tracks the GameTracking-CS2 commit feed —
	/// the same discipline every third-party CS2 client follows.
	/// </summary>
	public const uint ClientHelloVersion = 2000568;

	/// <summary><c>k_EMsgGCClientHello</c> — the handshake request.</summary>
	public const uint ClientHelloMsg = 4006;

	/// <summary><c>k_EMsgGCClientWelcome</c> — the handshake answer.</summary>
	public const uint ClientWelcomeMsg = 4004;

	/// <summary><c>k_EMsgGCOpenCrate</c> — consume key + crate, roll the loot.</summary>
	public const uint OpenCrateMsg = 2534;

	/// <summary><c>k_EMsgGCItemCustomizationNotification</c> — the open answer.</summary>
	public const uint ItemCustomizationNotificationMsg = 1090;

	/// <summary>
	/// <c>k_EGCItemCustomizationNotification_UnlockCrate</c> — the request kind
	/// a notification must carry to count as an open answer.
	/// </summary>
	public const uint UnlockCrateRequest = 1007;
}

/// <summary>
/// One-shot reply matching for CS2 game-coordinator exchanges. GC replies
/// carry no job id to correlate on, so a wait registers the response EMsg it
/// expects plus an optional body check, and the callback pump hands every
/// decoded reply to the registry — the first wait whose check accepts the
/// body wins. This is the testable half of the exchange; the SteamKit2
/// wiring around it (in <see cref="SteamClientManager"/>) is excluded from
/// coverage like the rest of that class.
/// </summary>
public sealed class Cs2GcWaitRegistry
{
	private sealed record Wait(uint ResponseMsg, TaskCompletionSource<object?> Completion, Func<object, bool>? Verify);

	private readonly ConcurrentDictionary<Guid, Wait> _waits = new();

	/// <summary>
	/// Registers a wait for <paramref name="responseMsg"/>, optionally gated by
	/// a body check. The completion task resolves to the accepted body, or null
	/// when the registration is disposed before a matching reply arrives.
	/// </summary>
	public (Task<object?> Task, IDisposable Registration) Register(uint responseMsg, Func<object, bool>? verify = null)
	{
		Wait wait = new(responseMsg, new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously), verify);
		Guid id = Guid.NewGuid();
		_waits[id] = wait;
		Task<object?> task = wait.Completion.Task;
		IDisposable registration = new Registration(this, id, wait);
		return (task, registration);
	}

	/// <summary>
	/// Offers one decoded reply to every registered wait on that message type;
	/// the first wait whose check accepts the body wins and is removed. Returns
	/// true when some wait consumed the reply.
	/// </summary>
	public bool Complete(uint responseMsg, object body)
	{
		ArgumentNullException.ThrowIfNull(body);

		foreach (Guid id in _waits.Keys)
		{
			if (!_waits.TryGetValue(id, out Wait? wait) || wait.ResponseMsg != responseMsg || wait.Completion.Task.IsCompleted)
			{
				continue;
			}

			if (wait.Verify is not null && !wait.Verify(body))
			{
				continue;
			}

			if (_waits.TryRemove(new KeyValuePair<Guid, Wait>(id, wait)))
			{
				_ = wait.Completion.TrySetResult(body);
				return true;
			}
		}

		return false;
	}

	private sealed class Registration : IDisposable
	{
		private readonly Cs2GcWaitRegistry _registry;
		private readonly Guid _id;
		private readonly Wait _wait;

		public Registration(Cs2GcWaitRegistry registry, Guid id, Wait wait)
		{
			_registry = registry;
			_id = id;
			_wait = wait;
		}

		public void Dispose()
		{
			// Pairwise remove: a wait already completed (or superseded) is no
			// longer stored under this id, and disposing must never settle
			// someone else's wait. A wait removed while still pending settles
			// with null, so its awaiting consumer cannot hang past the dispose.
			if (_registry._waits.TryRemove(new KeyValuePair<Guid, Wait>(_id, _wait)))
			{
				_ = _wait.Completion.TrySetResult(null);
			}
		}
	}
}
