using System.Diagnostics.CodeAnalysis;

namespace Vapor.ControlPlane;

/// <summary>
/// Periodically sweeps expired auth-code deliveries out of the short-lived
/// <see cref="AuthChallengeOutbox"/> (ARCH-09). A cancelled periodic timer throws
/// from <c>WaitForNextTickAsync</c>, so the loop cannot exit through the body.
/// </summary>
public sealed class AuthChallengeOutboxCleaner : BackgroundService
{
	private readonly AuthChallengeOutbox _outbox;
	private readonly TimeSpan _interval;

	public AuthChallengeOutboxCleaner(AuthChallengeOutbox outbox, TimeSpan? interval = null)
	{
		_outbox = outbox;
		_interval = interval ?? TimeSpan.FromSeconds(30);
	}

	[ExcludeFromCodeCoverage]
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using PeriodicTimer timer = new(_interval);
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
		{
			_outbox.Expire();
		}
	}
}
