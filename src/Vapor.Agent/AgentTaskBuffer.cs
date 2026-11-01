using System.Threading.Channels;
using Vapor.Protocol;

namespace Vapor.Agent;

/// <summary>
/// Bounded buffer for tasks an agent has received but not yet started.
/// ARCH-05: the receiver loop writes into this channel, the serial execution
/// loop reads from it. A <see cref="BoundedChannelFullMode.Wait"/> channel
/// applies back-pressure to the websocket instead of silently dropping a task
/// when the buffer is full, so the control plane's send queue eventually fills
/// and it stops piling work onto this agent.
/// </summary>
public static class AgentTaskBuffer
{
	/// <summary>Fallback when the capacity is not configured.</summary>
	public const int DefaultCapacity = 32;

	/// <summary>
	/// Resolves the buffer capacity from an environment string. Invalid,
	/// non-positive or unset values fall back to <paramref name="fallback"/>
	/// so a misconfigured agent never silently degrades to unbounded.
	/// </summary>
	public static int ResolveCapacity(string? value, int fallback = DefaultCapacity)
	{
		if (value is null)
		{
			return fallback;
		}

		if (int.TryParse(value, out int capacity) && capacity > 0)
		{
			return capacity;
		}

		return fallback;
	}

	/// <summary>Creates the bounded task channel used by <c>RunOnce</c>.</summary>
	public static Channel<WSMessage> CreateChannel(int capacity)
	{
		if (capacity <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity), "task buffer capacity must be positive");
		}

		return Channel.CreateBounded<WSMessage>(new BoundedChannelOptions(capacity)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = true
		});
	}
}
