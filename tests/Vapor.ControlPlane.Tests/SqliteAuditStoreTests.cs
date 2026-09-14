using System.Text.Json;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class SqliteAuditStoreTests : IDisposable
{
	private readonly SqliteAuditStore _store = new(":memory:");
	private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(5));

	public void Dispose()
	{
		_store.Dispose();
		_cts.Dispose();
	}

	private AuditEntry NewEntry(
		string action,
		string? account = null,
		string? jobId = null,
		Dictionary<string, object?>? details = null,
		DateTimeOffset? timestamp = null)
	{
		return new AuditEntry(
			Id: Id.New(),
			Timestamp: timestamp ?? DateTimeOffset.UtcNow,
			Action: action,
			Actor: "tester",
			RemoteIp: "127.0.0.1",
			AccountName: account,
			JobId: jobId,
			Details: details
		);
	}

	[Fact]
	public void Constructor_BlankDbPath_Throws()
	{
		Assert.Throws<ArgumentException>(() => new SqliteAuditStore("   "));
	}

	[Fact]
	public async Task RecordAndQuery_RoundTripsEntries()
	{
		await _store.RecordAsync(NewEntry("job.created", jobId: "job-1", details: new Dictionary<string, object?> { ["targetCount"] = 3 }), _cts.Token);

		IReadOnlyList<AuditEntry> entries = await _store.QueryAsync(new AuditQuery(), _cts.Token);

		var entry = Assert.Single(entries);
		Assert.Equal("job.created", entry.Action);
		Assert.Equal("job-1", entry.JobId);
		Assert.Equal("tester", entry.Actor);
		Assert.NotNull(entry.Details);
		Assert.Equal(3, ((JsonElement)entry.Details!["targetCount"]!).GetInt32());
	}

	[Fact]
	public async Task Record_RedactsSensitiveDetailsBeforePersistence()
	{
		await _store.RecordAsync(NewEntry(
			"auth.code.submitted",
			account: "alice",
			details: new Dictionary<string, object?>
			{
				["type"] = "2fa",
				["code"] = "123456",
				["password"] = "hunter2",
				["refreshToken"] = "rt-secret"
			}), _cts.Token);

		IReadOnlyList<AuditEntry> entries = await _store.QueryAsync(new AuditQuery(), _cts.Token);

		var entry = Assert.Single(entries);
		Assert.Equal("<redacted>", entry.Details!["code"]!.ToString());
		Assert.Equal("<redacted>", entry.Details!["password"]!.ToString());
		Assert.Equal("<redacted>", entry.Details!["refreshToken"]!.ToString());
		Assert.Equal("2fa", entry.Details!["type"]!.ToString());
	}

	[Fact]
	public async Task Record_WithEmptyAction_Throws()
	{
		await Assert.ThrowsAsync<ArgumentException>(
			() => _store.RecordAsync(NewEntry(" "), _cts.Token));
	}

	[Fact]
	public async Task Query_FiltersByActionAccountAndJob()
	{
		await _store.RecordAsync(NewEntry("job.created", account: "alice", jobId: "job-1"), _cts.Token);
		await _store.RecordAsync(NewEntry("job.canceled", account: "bob", jobId: "job-2"), _cts.Token);
		await _store.RecordAsync(NewEntry("session.login", account: "alice", jobId: null), _cts.Token);

		IReadOnlyList<AuditEntry> byAction = await _store.QueryAsync(new AuditQuery(Action: "session.login"), _cts.Token);
		IReadOnlyList<AuditEntry> byAccount = await _store.QueryAsync(new AuditQuery(AccountName: "alice"), _cts.Token);
		IReadOnlyList<AuditEntry> byJob = await _store.QueryAsync(new AuditQuery(JobId: "job-2"), _cts.Token);
		IReadOnlyList<AuditEntry> all = await _store.QueryAsync(new AuditQuery(), _cts.Token);

		Assert.Equal("session.login", Assert.Single(byAction).Action);
		Assert.Equal(2, byAccount.Count);
		Assert.All(byAccount, entry => Assert.Equal("alice", entry.AccountName));
		Assert.Equal("job.canceled", Assert.Single(byJob).Action);
		Assert.Equal(3, all.Count);
	}

	[Fact]
	public async Task Query_FiltersByTimeRange()
	{
		DateTimeOffset start = DateTimeOffset.UtcNow.AddMinutes(-5);
		DateTimeOffset middle = DateTimeOffset.UtcNow.AddMinutes(-1);
		DateTimeOffset end = DateTimeOffset.UtcNow.AddMinutes(5);

		await _store.RecordAsync(NewEntry("event.before", timestamp: start.AddMinutes(-1)), _cts.Token);
		await _store.RecordAsync(NewEntry("event.in-range", timestamp: middle), _cts.Token);
		await _store.RecordAsync(NewEntry("event.after", timestamp: end.AddMinutes(1)), _cts.Token);

		IReadOnlyList<AuditEntry> inRange = await _store.QueryAsync(new AuditQuery(From: start, To: end), _cts.Token);

		Assert.Equal("event.in-range", Assert.Single(inRange).Action);
	}

	[Fact]
	public async Task Query_AppliesLimitAndOffsetInDescendingOrder()
	{
		DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
		for (int i = 0; i < 5; i++)
		{
			await _store.RecordAsync(NewEntry($"event.{i}", timestamp: baseTime.AddMinutes(i)), _cts.Token);
		}

		IReadOnlyList<AuditEntry> firstPage = await _store.QueryAsync(new AuditQuery(Limit: 2, Offset: 0), _cts.Token);
		IReadOnlyList<AuditEntry> secondPage = await _store.QueryAsync(new AuditQuery(Limit: 2, Offset: 2), _cts.Token);
		int total = await _store.CountAsync(new AuditQuery(), _cts.Token);

		Assert.Equal(2, firstPage.Count);
		Assert.Equal(2, secondPage.Count);
		Assert.Equal(5, total);
		Assert.Equal("event.4", firstPage[0].Action);
		Assert.Equal("event.3", firstPage[1].Action);
		Assert.Equal("event.2", secondPage[0].Action);
		Assert.Equal("event.1", secondPage[1].Action);
	}

	[Fact]
	public async Task Query_EmptyStore_ReturnsEmptyList()
	{
		IReadOnlyList<AuditEntry> entries = await _store.QueryAsync(new AuditQuery(), _cts.Token);
		int count = await _store.CountAsync(new AuditQuery(Action: "nope"), _cts.Token);

		Assert.Empty(entries);
		Assert.Equal(0, count);
	}
}
