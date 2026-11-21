using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>Audit-persistence branch tests: swallowed failures and default-field fallback.</summary>
public sealed partial class ProgramBranchCoverageTests
{
	[Fact]
	public async Task AuditPersistenceFailure_IsSwallowedByEndpoints()
	{
		await using BranchFactory factory = new() { AuditStore = new ThrowingAuditStore() };
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		// Generic store failure: the endpoint still succeeds, audit loss is only logged.
		ThrowingAuditStore store = (ThrowingAuditStore)factory.AuditStore!;
		store.Throw = new InvalidOperationException("disk full");
		using HttpResponseMessage ok = await client.PutAsJsonAsync("/v1/accounts/alice", new { desiredState = "offline" });
		Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

		// Cancellation is not swallowed: it propagates as a failed request.
		store.Throw = new OperationCanceledException();
		using HttpResponseMessage canceled = await client.PutAsJsonAsync("/v1/accounts/bob", new { desiredState = "offline" });
		Assert.Equal(HttpStatusCode.InternalServerError, canceled.StatusCode);
	}

	[Fact]
	public async Task WriteAuditLog_NullDetailsAndMissingRemoteIp_FallBackToDefaults()
	{
		// A DefaultHttpContext has no remote IP: the "unknown" sentinel and the
		// null remoteIp field must hold everywhere, and a null details payload
		// serializes as an empty object rather than throwing.
		// The helpers used to be top-level local functions of the single-file
		// Program (compiler-generated names, matchable only by reflection);
		// since the endpoint split they are internal static members of
		// EndpointHelpers and bind directly.
		Microsoft.AspNetCore.Http.DefaultHttpContext noIp = new();
		RecordingAuditStore recorded = new();
		await EndpointHelpers.WriteAuditLog(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, recorded, noIp, "test.action", null, null, null).ConfigureAwait(true);
		Assert.Equal("unknown", EndpointHelpers.GetAuditActor(noIp));
		Assert.Null(Assert.Single(recorded.Entries).RemoteIp);

		// An empty X-Forwarded-For header fails IsNullOrEmpty and falls through
		// to the connection address; a whitespace-only header passes the check
		// and is taken verbatim (the filter is IsNullOrEmpty, not trim-based).
		Microsoft.AspNetCore.Http.DefaultHttpContext withIp = new();
		withIp.Connection.RemoteIpAddress = IPAddress.Loopback;
		withIp.Request.Headers["X-Forwarded-For"] = "";
		Assert.Equal(IPAddress.Loopback.ToString(), EndpointHelpers.GetAuditActor(withIp));

		Microsoft.AspNetCore.Http.DefaultHttpContext whitespaceIp = new();
		whitespaceIp.Connection.RemoteIpAddress = IPAddress.Loopback;
		whitespaceIp.Request.Headers["X-Forwarded-For"] = "   ";
		Assert.Equal("   ", EndpointHelpers.GetAuditActor(whitespaceIp));

		// A usable X-Forwarded-For wins over the connection address.
		Microsoft.AspNetCore.Http.DefaultHttpContext forwarded = new();
		forwarded.Connection.RemoteIpAddress = IPAddress.Loopback;
		forwarded.Request.Headers["X-Forwarded-For"] = "10.0.0.1";
		Assert.Equal("10.0.0.1", EndpointHelpers.GetAuditActor(forwarded));

		// A connection that does carry an address flows verbatim into both the
		// structured log line and the persisted entry (the "unknown" sentinel
		// and the null remoteIp stay on the null-address side above), and a
		// non-null details payload round-trips into the entry.
		Microsoft.AspNetCore.Http.DefaultHttpContext withAddress = new();
		withAddress.Connection.RemoteIpAddress = IPAddress.Loopback;
		RecordingAuditStore recordedAddressed = new();
		Dictionary<string, object?> details = new() { ["outcome"] = "recorded" };
		await EndpointHelpers.WriteAuditLog(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, recordedAddressed, withAddress, "test.action", "alice", "job-7", details).ConfigureAwait(true);
		AuditEntry entry = Assert.Single(recordedAddressed.Entries);
		Assert.Equal(IPAddress.Loopback.ToString(), entry.RemoteIp);
		Assert.Equal("alice", entry.AccountName);
		Assert.Equal("job-7", entry.JobId);
		Assert.Equal("recorded", entry.Details!["outcome"]);
	}

	/// <summary>Captures every recorded entry (no failures, no persistence).</summary>
	private sealed class RecordingAuditStore : IAuditStore
	{
		public List<AuditEntry> Entries { get; } = [];

		public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
		{
			Entries.Add(entry);
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AuditEntry>>([]);
		public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
	}

	private sealed class ThrowingAuditStore : IAuditStore
	{
		/// <summary>Next exception to throw; null persists nothing and succeeds.</summary>
		public Exception? Throw { get; set; }

		public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
		{
			return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
		}

		public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AuditEntry>>([]);
		public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken) => Task.FromResult(0);
	}

}
