using System.Net;
using System.Net.Sockets;
using Xunit;
using Vapor.Steam.Core.Steam;

namespace Vapor.Steam.Core.Tests.Unit;

public class SteamTimeSynchronizerTests
{
	[Fact]
	public void QueryTimeEndpoint_PointsAtSteamTwoFactorService()
	{
		Assert.Equal(
			"https://api.steampowered.com/ITwoFactorService/QueryTime/v1/",
			SteamTimeSynchronizer.QueryTimeEndpoint.ToString());
	}

	[Fact]
	public void GetCurrentSteamTime_UsesLocalTimeBeforeSync()
	{
		var timeProvider = new FakeTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromResult(0L), timeProvider);

		Assert.False(synchronizer.HasSynced);
		Assert.Equal(0L, synchronizer.OffsetSeconds);
		Assert.Equal(timeProvider.GetUtcNow().ToUnixTimeSeconds(), synchronizer.GetCurrentSteamTime());
	}

	[Fact]
	public async Task SyncAsync_ComputesOffset()
	{
		var localNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(localNow);
		var serverTime = localNow.ToUnixTimeSeconds() + 42;

		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromResult(serverTime), timeProvider);
		await synchronizer.SyncAsync();

		Assert.True(synchronizer.HasSynced);
		Assert.Equal(42L, synchronizer.OffsetSeconds);
		Assert.Equal(serverTime, synchronizer.GetCurrentSteamTime());
		Assert.NotNull(synchronizer.LastSyncedAt);
	}

	[Fact]
	public async Task SyncAsync_NegativeOffset()
	{
		var localNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(localNow);
		var serverTime = localNow.ToUnixTimeSeconds() - 7;

		var synchronizer = new SteamTimeSynchronizer(_ => Task.FromResult(serverTime), timeProvider);
		await synchronizer.SyncAsync();

		Assert.Equal(-7L, synchronizer.OffsetSeconds);
	}

	[Fact]
	public async Task SyncAsync_Failure_KeepsPreviousOffset()
	{
		var localNow = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(localNow);
		var serverTime = localNow.ToUnixTimeSeconds() + 10;
		var fail = false;

		var synchronizer = new SteamTimeSynchronizer(_ => fail
			? Task.FromException<long>(new HttpRequestException("network down"))
			: Task.FromResult(serverTime), timeProvider);

		await synchronizer.SyncAsync();
		Assert.Equal(10L, synchronizer.OffsetSeconds);

		fail = true;
		await Assert.ThrowsAsync<HttpRequestException>(() => synchronizer.SyncAsync());

		// Previous offset is preserved.
		Assert.Equal(10L, synchronizer.OffsetSeconds);
	}

	[Fact]
	public async Task SyncAsync_UsesMidpointOfRoundTrip()
	{
		var start = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
		var timeProvider = new FakeTimeProvider(start);
		var serverTime = start.ToUnixTimeSeconds() + 100;

		var synchronizer = new SteamTimeSynchronizer(_ =>
		{
			// Simulate a 4-second round trip.
			timeProvider.Advance(TimeSpan.FromSeconds(4));
			return Task.FromResult(serverTime);
		}, timeProvider);

		await synchronizer.SyncAsync();

		// Midpoint = start + 2s => offset = 100 - 2.
		Assert.Equal(98L, synchronizer.OffsetSeconds);
	}

	[Fact]
	public async Task QuerySteamServerTimeAsync_ParsesServerTimeFromResponse()
	{
		using StubServer server = StubServer.Start(("time/", async ctx =>
		{
			ctx.Response.ContentType = "application/json";
			await ctx.Response.OutputStream.WriteAsync("""{"response":{"server_time":"1700000123"}}"""u8.ToArray());
			ctx.Response.Close();
		}
		));

		long serverTime = await SteamTimeSynchronizer.QuerySteamServerTimeAsync(
			new Uri(server.Prefix + "time/"), CancellationToken.None);

		Assert.Equal(1700000123L, serverTime);
	}

	[Fact]
	public async Task QuerySteamServerTimeAsync_ServerError_ThrowsHttpRequestException()
	{
		using StubServer server = StubServer.Start(("time/", ctx =>
		{
			ctx.Response.StatusCode = 500;
			ctx.Response.Close();
			return Task.CompletedTask;
		}
		));

		await Assert.ThrowsAsync<HttpRequestException>(() =>
			SteamTimeSynchronizer.QuerySteamServerTimeAsync(
				new Uri(server.Prefix + "time/"), CancellationToken.None));
	}

	[Fact]
	public async Task QuerySteamServerTimeAsync_MissingServerTime_ThrowsInvalidOperation()
	{
		using StubServer server = StubServer.Start(("time/", async ctx =>
		{
			ctx.Response.ContentType = "application/json";
			await ctx.Response.OutputStream.WriteAsync("""{"response":{"unexpected":true}}"""u8.ToArray());
			ctx.Response.Close();
		}
		));

		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			SteamTimeSynchronizer.QuerySteamServerTimeAsync(
				new Uri(server.Prefix + "time/"), CancellationToken.None));

		Assert.Contains("server_time", ex.Message);
	}

	private sealed class FakeTimeProvider : TimeProvider
	{
		private DateTimeOffset _now;

		public FakeTimeProvider(DateTimeOffset now) => _now = now;

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan delta) => _now = _now.Add(delta);
	}

	/// <summary>Local HTTP stub for the endpoint-injectable QueryTime query.</summary>
	private sealed class StubServer : IDisposable
	{
		private readonly HttpListener _listener;
		private readonly Task _acceptLoop;

		private StubServer(HttpListener listener, Task acceptLoop)
		{
			_listener = listener;
			_acceptLoop = acceptLoop;
		}

		public string Prefix => _listener.Prefixes.Single();

		public static StubServer Start(params (string path, Func<HttpListenerContext, Task> handler)[] routes)
		{
			// HttpListener cannot bind port 0; grab a free port from a transient socket first.
			var portProbe = new TcpListener(IPAddress.Loopback, 0);
			portProbe.Start();
			int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
			portProbe.Stop();

			var listener = new HttpListener();
			listener.Prefixes.Add($"http://127.0.0.1:{port}/");
			listener.Start();
			return new StubServer(listener, AcceptLoopAsync(listener, routes));
		}

		private static async Task AcceptLoopAsync(
			HttpListener listener, (string path, Func<HttpListenerContext, Task> handler)[] routes)
		{
			while (listener.IsListening)
			{
				HttpListenerContext? context = null;
				try
				{
					context = await listener.GetContextAsync();
				}
				catch (Exception) when (!listener.IsListening)
				{
					return; // Stopped while waiting for a request.
				}
				catch (ObjectDisposedException)
				{
					return;
				}
				catch (HttpListenerException)
				{
					return;
				}

				if (context is null)
				{
					return;
				}

				string rawUrl = context.Request.Url?.AbsolutePath ?? "/";
				(string _, Func<HttpListenerContext, Task> handler)? match =
					routes.FirstOrDefault(r => rawUrl.EndsWith(r.path, StringComparison.Ordinal));
				if (match is null)
				{
					context.Response.StatusCode = 404;
					context.Response.Close();
					continue;
				}

				try
				{
					await match.Value.handler(context);
				}
				catch
				{
					try
					{
						context.Response.StatusCode = 500;
						context.Response.Close();
					}
					catch
					{
						// Client already gone; nothing to clean up.
					}
				}
			}
		}

		public void Dispose()
		{
			try
			{
				_listener.Stop();
				_listener.Close();
			}
			catch
			{
				// Best-effort teardown.
			}

			_acceptLoop.Wait(TimeSpan.FromSeconds(5));
		}
	}
}
