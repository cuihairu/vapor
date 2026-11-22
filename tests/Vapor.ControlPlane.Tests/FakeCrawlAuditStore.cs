using Vapor.ControlPlane;

namespace Vapor.ControlPlane.Tests;

internal sealed class FakeCrawlAuditStore : IAuditStore
{
	public List<AuditEntry> Entries { get; } = [];
	public bool ThrowOnRecord { get; set; }
	/// <summary>RecordAsync throws cancellation (the stopping-token rethrow path).</summary>
	public bool ThrowCanceledOnRecord { get; set; }

	public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
	{
		if (ThrowCanceledOnRecord)
		{
			throw new OperationCanceledException();
		}

		if (ThrowOnRecord)
		{
			throw new IOException("audit store unavailable");
		}

		Entries.Add(entry);
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<AuditEntry>>(Entries);

	public Task<int> CountAsync(AuditQuery query, CancellationToken cancellationToken)
		=> Task.FromResult(Entries.Count);
}
