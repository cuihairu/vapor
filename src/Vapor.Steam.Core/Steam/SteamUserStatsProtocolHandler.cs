using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SteamKit2;
using SteamKit2.Internal;

namespace Vapor.Steam.Core.Steam;

/// <summary>
/// One answered stats-protocol exchange: which response arrived, its result
/// code, and the parsed body of exactly one of the two message kinds.
/// </summary>
internal sealed record UserStatsProtocolResponse(
	EResult Result,
	CMsgClientGetUserStatsResponse? GetResponse,
	CMsgClientStoreUserStatsResponse? StoreResponse);

/// <summary>
/// Routes the two legacy stats responses the stock <see cref="SteamUserStats"/>
/// handler ignores (SteamKit2 3.x only services leaderboards): a load answer to
/// <c>EMsg.ClientGetUserStats</c> and a store answer to
/// <c>EMsg.ClientStoreUserStats2</c> (Steam reuses EMsg.ClientStoreUserStatsResponse
/// for the Store2 reply). Callers pair requests to responses by job id through
/// <see cref="RegisterWait"/> before sending, so an answer can never race its
/// registration.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SteamUserStatsProtocolHandler : ClientMsgHandler
{
	private readonly ConcurrentDictionary<ulong, TaskCompletionSource<UserStatsProtocolResponse?>> _pending = new();

	/// <summary>
	/// Registers the wait for <paramref name="jobId"/> and returns the completion
	/// task, resolving to null on timeout (Steam never answered within the window).
	/// </summary>
	internal (Task<UserStatsProtocolResponse?> Task, IDisposable Registration) RegisterWait(ulong jobId)
	{
		var tcs = new TaskCompletionSource<UserStatsProtocolResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[jobId] = tcs;
		return (tcs.Task, new Registration(this, jobId));
	}

	public override void HandleMsg(IPacketMsg packetMsg)
	{
		switch (packetMsg.MsgType)
		{
			case EMsg.ClientGetUserStatsResponse:
				{
					var msg = new ClientMsgProtobuf<CMsgClientGetUserStatsResponse>(packetMsg);
					Complete(msg.TargetJobID, new UserStatsProtocolResponse(
						(EResult)msg.Body.eresult, msg.Body, null));
					break;
				}
			case EMsg.ClientStoreUserStatsResponse:
				{
					var msg = new ClientMsgProtobuf<CMsgClientStoreUserStatsResponse>(packetMsg);
					Complete(msg.TargetJobID, new UserStatsProtocolResponse(
						(EResult)msg.Body.eresult, null, msg.Body));
					break;
				}
		}
	}

	private void Complete(ulong jobId, UserStatsProtocolResponse response)
	{
		if (_pending.TryRemove(jobId, out var tcs))
		{
			tcs.TrySetResult(response);
		}
	}

	private sealed class Registration : IDisposable
	{
		private readonly SteamUserStatsProtocolHandler _handler;
		private readonly ulong _jobId;

		public Registration(SteamUserStatsProtocolHandler handler, ulong jobId)
		{
			_handler = handler;
			_jobId = jobId;
		}

		public void Dispose() => _handler._pending.TryRemove(_jobId, out _);
	}
}
