using Microsoft.Data.Sqlite;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class SqliteWebhookDeliveryStoreTests : IDisposable
{
	private readonly SqliteWebhookDeliveryStore _store = new(":memory:");
	private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));

	public void Dispose()
	{
		_store.Dispose();
		_cts.Dispose();
	}

	private static WebhookDeliveryRecord NewRecord(
		string notificationId = "n-1",
		int attempt = 1,
		string outcome = "delivered",
		int? statusCode = 200,
		string? error = null,
		string? jobId = null,
		string? accountName = null,
		long? attemptedAtMs = null)
	{
		return new WebhookDeliveryRecord(
			NotificationId: notificationId,
			Category: "job",
			Type: "job.created",
			JobId: jobId,
			AccountName: accountName,
			Attempt: attempt,
			Outcome: outcome,
			StatusCode: statusCode,
			Error: error,
			AttemptedAtMs: attemptedAtMs ?? 1_700_000_000_000 + attempt);
	}

	[Fact]
	public void Constructor_BlankDbPath_Throws()
	{
		Assert.Throws<ArgumentException>(() => new SqliteWebhookDeliveryStore("   "));
	}

	[Fact]
	public void Constructor_RelativePath_CreatesDirectory()
	{
		string dir = Path.Combine(Path.GetTempPath(), $"webhook-store-{Guid.NewGuid():N}", "sub");
		try
		{
			using var store = new SqliteWebhookDeliveryStore(Path.Combine(dir, "webhook.db"));
			Assert.True(Directory.Exists(dir));
		}
		finally
		{
			// Windows: ADO.NET keeps a pooled file handle even after Dispose();
			// clearing the pool is what actually releases the file for deletion.
			SqliteConnection.ClearAllPools();
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public async Task RecordAndQuery_RoundTripsAllFields()
	{
		await _store.RecordAsync(NewRecord(jobId: "job-7", accountName: "alice", error: null), _cts.Token);
		await _store.RecordAsync(NewRecord(attempt: 2, outcome: "failed", statusCode: null, error: "network down"), _cts.Token);

		IReadOnlyList<WebhookDeliveryRecord> rows = await _store.QueryAsync(cancellationToken: _cts.Token);

		Assert.Equal(2, rows.Count);
		WebhookDeliveryRecord first = rows[0]; // attempted_at_ms DESC: attempt 2 first
		Assert.Equal(2, first.Attempt);
		Assert.Equal("failed", first.Outcome);
		Assert.Null(first.StatusCode);
		Assert.Equal("network down", first.Error);
		WebhookDeliveryRecord second = rows[1];
		Assert.Equal("n-1", second.NotificationId);
		Assert.Equal("job", second.Category);
		Assert.Equal("job.created", second.Type);
		Assert.Equal("job-7", second.JobId);
		Assert.Equal("alice", second.AccountName);
		Assert.Equal("delivered", second.Outcome);
		Assert.Equal(200, second.StatusCode);
		Assert.Equal(1_700_000_000_001, second.AttemptedAtMs);
	}

	[Fact]
	public async Task Query_FiltersByNotificationAndOutcome()
	{
		await _store.RecordAsync(NewRecord(notificationId: "n-1", outcome: "failed", statusCode: 500, error: "boom"), _cts.Token);
		await _store.RecordAsync(NewRecord(notificationId: "n-1", attempt: 2, outcome: "delivered"), _cts.Token);
		await _store.RecordAsync(NewRecord(notificationId: "n-2", outcome: "delivered"), _cts.Token);

		IReadOnlyList<WebhookDeliveryRecord> byNotification = await _store.QueryAsync(notificationId: "n-1", cancellationToken: _cts.Token);
		IReadOnlyList<WebhookDeliveryRecord> delivered = await _store.QueryAsync(outcome: "delivered", cancellationToken: _cts.Token);
		IReadOnlyList<WebhookDeliveryRecord> both = await _store.QueryAsync(notificationId: "n-1", outcome: "failed", cancellationToken: _cts.Token);
		int failedTotal = await _store.CountAsync(outcome: "failed", cancellationToken: _cts.Token);

		Assert.Equal(2, byNotification.Count);
		Assert.All(byNotification, row => Assert.Equal("n-1", row.NotificationId));
		Assert.Equal(2, delivered.Count);
		Assert.All(delivered, row => Assert.Equal("delivered", row.Outcome));
		WebhookDeliveryRecord failed = Assert.Single(both);
		Assert.Equal(1, failed.Attempt);
		Assert.Equal(1, failedTotal);
	}

	[Fact]
	public async Task Query_AppliesLimitAndOffsetInDescendingOrder()
	{
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			await _store.RecordAsync(NewRecord(attempt: attempt, outcome: "failed", statusCode: 500), _cts.Token);
		}

		IReadOnlyList<WebhookDeliveryRecord> firstPage = await _store.QueryAsync(limit: 2, offset: 0, cancellationToken: _cts.Token);
		IReadOnlyList<WebhookDeliveryRecord> secondPage = await _store.QueryAsync(limit: 2, offset: 2, cancellationToken: _cts.Token);
		int total = await _store.CountAsync(cancellationToken: _cts.Token);

		Assert.Equal(2, firstPage.Count);
		Assert.Equal(2, secondPage.Count);
		Assert.Equal(5, total);
		Assert.Equal(new[] { 5, 4 }, firstPage.Select(row => row.Attempt).ToArray());
		Assert.Equal(new[] { 3, 2 }, secondPage.Select(row => row.Attempt).ToArray());
	}

	[Fact]
	public async Task Query_ZeroOrNegativeLimit_ClampsToDefaultPageSize()
	{
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			await _store.RecordAsync(NewRecord(attempt: attempt, outcome: "failed", statusCode: 500), _cts.Token);
		}

		// limit <= 0 must not return zero rows; it clamps to the default page size.
		IReadOnlyList<WebhookDeliveryRecord> rows = await _store.QueryAsync(limit: 0, cancellationToken: _cts.Token);
		Assert.Equal(3, rows.Count);
		Assert.Equal(new[] { 3, 2, 1 }, rows.Select(row => row.Attempt).ToArray());
	}

	[Fact]
	public async Task Query_EmptyStore_ReturnsEmptyListAndZeroCount()
	{
		Assert.Empty(await _store.QueryAsync(cancellationToken: _cts.Token));
		Assert.Equal(0, await _store.CountAsync(cancellationToken: _cts.Token));
	}

	[Fact]
	public async Task RecordAsync_BlankNotificationId_Throws()
	{
		await Assert.ThrowsAsync<ArgumentException>(
			() => _store.RecordAsync(NewRecord(notificationId: " "), _cts.Token));
	}
}
