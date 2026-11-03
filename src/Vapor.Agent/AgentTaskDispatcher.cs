using System.Collections.Concurrent;
using System.Threading.Channels;
using Vapor.Protocol;

namespace Vapor.Agent;

/// <summary>
/// Routes task frames into the bounded execution buffer without ever blocking
/// the websocket receive loop, so a <c>task_cancel</c> frame arriving behind a
/// full buffer is still read and honored instead of being stranded behind a
/// pending <c>WriteAsync</c>. A task frame that cannot enter the full buffer is
/// parked in an in-order overflow queue; the serial execution loop drains a
/// slot and then flushes the overflow back in, preserving dispatch order.
/// </summary>
public sealed class AgentTaskDispatcher : IDisposable
{
	private readonly Channel<WSMessage> _buffer;
	private readonly ConcurrentQueue<WSMessage> _overflow = new();
	private readonly object _writeLock = new();

	public AgentTaskDispatcher(int capacity)
	{
		if (capacity <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity), "task buffer capacity must be positive");
		}

		_buffer = Channel.CreateBounded<WSMessage>(new BoundedChannelOptions(capacity)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = true,
			SingleWriter = false
		});
		Capacity = capacity;
	}

	/// <summary>Frames waiting in the overflow queue (back-pressure parking).</summary>
	public int OverflowCount => _overflow.Count;

	/// <summary>Buffer capacity, for diagnostics.</summary>
	public int Capacity { get; }

	/// <summary>The serial execution loop reads dispatched frames here.</summary>
	public ChannelReader<WSMessage> Reader => _buffer.Reader;

	/// <summary>
	/// Enqueues a task frame without blocking. If the bounded buffer is full the
	/// frame is parked in the overflow queue, preserving arrival order; the
	/// receive loop therefore never stalls and keeps reading control frames.
	/// </summary>
	public void Enqueue(WSMessage frame)
	{
		lock (_writeLock)
		{
			if (_overflow.IsEmpty && _buffer.Writer.TryWrite(frame))
			{
				return;
			}

			_overflow.Enqueue(frame);
		}
	}

	/// <summary>
	/// Moves parked frames back into the buffer while a slot is free. Called by
	/// the serial execution loop after it consumes a frame.
	/// </summary>
	public void FlushOverflow()
	{
		lock (_writeLock)
		{
			while (_overflow.TryPeek(out WSMessage? next) && _buffer.Writer.TryWrite(next))
			{
				_overflow.TryDequeue(out _);
			}
		}
	}

	/// <summary>Completes the writer so the reader loop ends on disconnect.</summary>
	public void Complete() => _buffer.Writer.TryComplete();

	/// <summary>Disposes by completing the writer.</summary>
	public void Dispose() => Complete();
}
