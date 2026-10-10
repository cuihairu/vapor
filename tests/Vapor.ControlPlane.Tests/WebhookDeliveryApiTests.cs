using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// GET /v1/notifications/deliveries — auth gate, newest-first ordering with total,
/// notificationId/outcome filters, and limit/offset paging. Rows are seeded straight
/// into the store the endpoint resolves (the factory's in-memory override).
/// </summary>
public sealed class WebhookDeliveryApiTests
{
	[Fact]
	public async Task Deliveries_RequiresAuthorization()
	{
		await using var factory = new ControlPlaneApiTests.TestFactory();
		using var client = factory.CreateClient();

		using HttpResponseMessage response = await client.GetAsync("/v1/notifications/deliveries");

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Deliveries_EmptyLog_ReturnsEmptyListAndZeroTotal()
	{
		await using ControlPlaneApiTests.TestFactory factory = new ControlPlaneApiTests.TestFactory();
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/notifications/deliveries");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal(0, doc.RootElement.GetProperty("deliveries").GetArrayLength());
		Assert.Equal(100, doc.RootElement.GetProperty("limit").GetInt32());
		Assert.Equal(0, doc.RootElement.GetProperty("offset").GetInt32());
	}

	[Fact]
	public async Task Deliveries_ReturnsSeededRowsNewestFirstWithTotal()
	{
		var store = new SqliteWebhookDeliveryStore(":memory:");
		await SeedAsync(store);
		await using ControlPlaneApiTests.TestFactory factory = new ControlPlaneApiTests.TestFactory();
		factory.WebhookDeliveryStore = store;
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage response = await client.GetAsync("/v1/notifications/deliveries");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(4, doc.RootElement.GetProperty("total").GetInt32());
		JsonElement rows = doc.RootElement.GetProperty("deliveries");
		Assert.Equal(4, rows.GetArrayLength());

		JsonElement newest = rows[0];
		Assert.Equal("n-2", newest.GetProperty("notificationId").GetString());
		Assert.Equal(1, newest.GetProperty("attempt").GetInt32());
		Assert.Equal("delivered", newest.GetProperty("outcome").GetString());
		Assert.Equal(200, newest.GetProperty("statusCode").GetInt32());
		// Null errors are omitted from the wire shape (JsonOptions default-ignore).
		Assert.False(newest.TryGetProperty("error", out _));
		Assert.False(newest.TryGetProperty("jobId", out _));
		Assert.Equal(4000, newest.GetProperty("attemptedAtMs").GetInt64());

		JsonElement oldest = rows[3];
		Assert.Equal("n-1", oldest.GetProperty("notificationId").GetString());
		Assert.Equal(1, oldest.GetProperty("attempt").GetInt32());
		Assert.Equal("failed", oldest.GetProperty("outcome").GetString());
		Assert.Equal(500, oldest.GetProperty("statusCode").GetInt32());
		Assert.Equal("webhook returned 500", oldest.GetProperty("error").GetString());
	}

	[Fact]
	public async Task Deliveries_FiltersByNotificationAndOutcome()
	{
		var store = new SqliteWebhookDeliveryStore(":memory:");
		await SeedAsync(store);
		await using ControlPlaneApiTests.TestFactory factory = new ControlPlaneApiTests.TestFactory();
		factory.WebhookDeliveryStore = store;
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage byNotification = await client.GetAsync("/v1/notifications/deliveries?notificationId=n-1");
		using var doc = JsonDocument.Parse(await byNotification.Content.ReadAsStringAsync());
		Assert.Equal(3, doc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal(3, doc.RootElement.GetProperty("deliveries").GetArrayLength());

		using HttpResponseMessage failedOnly = await client.GetAsync("/v1/notifications/deliveries?outcome=failed");
		using var failedDoc = JsonDocument.Parse(await failedOnly.Content.ReadAsStringAsync());
		Assert.Equal(2, failedDoc.RootElement.GetProperty("total").GetInt32());

		using HttpResponseMessage both = await client.GetAsync("/v1/notifications/deliveries?notificationId=n-1&outcome=delivered");
		using var bothDoc = JsonDocument.Parse(await both.Content.ReadAsStringAsync());
		Assert.Equal(1, bothDoc.RootElement.GetProperty("total").GetInt32());
		JsonElement row = bothDoc.RootElement.GetProperty("deliveries")[0];
		Assert.Equal(3, row.GetProperty("attempt").GetInt32());
	}

	[Fact]
	public async Task Deliveries_AppliesLimitAndOffset()
	{
		var store = new SqliteWebhookDeliveryStore(":memory:");
		await SeedAsync(store);
		await using ControlPlaneApiTests.TestFactory factory = new ControlPlaneApiTests.TestFactory();
		factory.WebhookDeliveryStore = store;
		using var client = factory.CreateClient();
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "admin-token");

		using HttpResponseMessage firstPage = await client.GetAsync("/v1/notifications/deliveries?limit=2");
		using var firstDoc = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
		Assert.Equal(4, firstDoc.RootElement.GetProperty("total").GetInt32());
		Assert.Equal(2, firstDoc.RootElement.GetProperty("limit").GetInt32());
		JsonElement firstRows = firstDoc.RootElement.GetProperty("deliveries");
		Assert.Equal(2, firstRows.GetArrayLength());
		Assert.Equal("n-2", firstRows[0].GetProperty("notificationId").GetString());
		Assert.Equal("n-1", firstRows[1].GetProperty("notificationId").GetString());
		Assert.Equal(3, firstRows[1].GetProperty("attempt").GetInt32());

		using HttpResponseMessage secondPage = await client.GetAsync("/v1/notifications/deliveries?limit=2&offset=2");
		using var secondDoc = JsonDocument.Parse(await secondPage.Content.ReadAsStringAsync());
		JsonElement secondRows = secondDoc.RootElement.GetProperty("deliveries");
		Assert.Equal(2, secondRows.GetArrayLength());
		Assert.Equal(2, secondRows[0].GetProperty("attempt").GetInt32());
		Assert.Equal(1, secondRows[1].GetProperty("attempt").GetInt32());
	}

	private static async Task SeedAsync(SqliteWebhookDeliveryStore store)
	{
		await store.RecordAsync(new WebhookDeliveryRecord(
			NotificationId: "n-1", Category: "job", Type: "job.created", JobId: "job-7",
			AccountName: "alice", Attempt: 1, Outcome: "failed", StatusCode: 500,
			Error: "webhook returned 500", AttemptedAtMs: 1000), CancellationToken.None);
		await store.RecordAsync(new WebhookDeliveryRecord(
			NotificationId: "n-1", Category: "job", Type: "job.created", JobId: "job-7",
			AccountName: "alice", Attempt: 2, Outcome: "failed", StatusCode: 503,
			Error: "webhook returned 503", AttemptedAtMs: 2000), CancellationToken.None);
		await store.RecordAsync(new WebhookDeliveryRecord(
			NotificationId: "n-1", Category: "job", Type: "job.created", JobId: "job-7",
			AccountName: "alice", Attempt: 3, Outcome: "delivered", StatusCode: 200,
			Error: null, AttemptedAtMs: 3000), CancellationToken.None);
		await store.RecordAsync(new WebhookDeliveryRecord(
			NotificationId: "n-2", Category: "session", Type: "state_changed", JobId: null,
			AccountName: "bob", Attempt: 1, Outcome: "delivered", StatusCode: 200,
			Error: null, AttemptedAtMs: 4000), CancellationToken.None);
	}
}
