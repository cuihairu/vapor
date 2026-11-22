using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class PluginCatalogServiceTests
{
	[Fact]
	public void ParseIndex_RejectsDocumentsWithoutPluginsArray()
	{
		Assert.ThrowsAny<FormatException>(() => PluginCatalogService.ParseIndex("""{"items":[]}""", "https://x/index.json"));
		Assert.ThrowsAny<Exception>(() => PluginCatalogService.ParseIndex("not json", "https://x/index.json"));
	}

	[Fact]
	public void ParseIndex_RejectsEntriesMissingRequiredStrings()
	{
		Assert.ThrowsAny<Exception>(() => PluginCatalogService.ParseIndex(
			"""{"plugins":[{"id":"vapor.x","name":"X","version":"1.0.0","apiVersion":"1.0"}]}""",
			"https://x/index.json"));
	}

	[Fact]
	public void ParseIndex_LowercasesChecksumAndKeepsOptionalFields()
	{
		var declared = """{"id":"vapor.x","name":"X","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/x.zip","sha256":"AABB","permissions":["actions","web"],"trust":"community","publisher":"vapor"}""";
		var nonString = """{"id":"vapor.y","name":"Y","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/y.zip","sha256":"CCDD","publisher":5}""";
		var omitted = """{"id":"vapor.z","name":"Z","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/z.zip","sha256":"EEFF"}""";

		// The signing block keeps every optional-field rule: a complete object is
		// parsed, and each half-missing or wrong-typed shape is undeclared, not an error.
		var complete = """{"id":"vapor.a","name":"A","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/a.zip","sha256":"AA01","signing":{"publicKey":"a2V5","signature":"c2ln"}}""";
		var nonObject = """{"id":"vapor.b","name":"B","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/b.zip","sha256":"BB02","signing":5}""";
		var noKey = """{"id":"vapor.c","name":"C","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/c.zip","sha256":"CC03","signing":{"signature":"c2ln"}}""";
		var keyNonString = """{"id":"vapor.d","name":"D","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/d.zip","sha256":"DD04","signing":{"publicKey":5,"signature":"c2ln"}}""";
		var noSignature = """{"id":"vapor.e","name":"E","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/e.zip","sha256":"EE05","signing":{"publicKey":"a2V5"}}""";
		var signatureNonString = """{"id":"vapor.f","name":"F","version":"1.0.0","apiVersion":"1.0","url":"https://pkg/f.zip","sha256":"FF06","signing":{"publicKey":"a2V5","signature":5}}""";

		PluginCatalog catalog = PluginCatalogService.ParseIndex(
			"""{"plugins":[""" + complete + "," + nonObject + "," + noKey + "," + keyNonString + "," + noSignature + "," + signatureNonString + "," + declared + "," + nonString + "," + omitted + "]}",
			"https://x/index.json");

		Assert.Equal(9, catalog.Entries.Count);
		PluginIndexEntry entry = catalog.Entries[6];
		Assert.Equal("aabb", entry.Sha256);
		Assert.Equal("community", entry.Trust);
		Assert.Equal(new[] { "actions", "web" }, entry.Permissions);
		Assert.Equal("vapor", entry.Publisher);
		// A non-string or absent publisher is simply undeclared, not an error.
		Assert.Null(catalog.Entries[7].Publisher);
		Assert.Null(catalog.Entries[8].Publisher);

		// The complete block parses into both halves, keyed by the entry's own digest.
		PluginIndexEntry signed = catalog.Entries[0];
		Assert.Equal("aa01", signed.Sha256);
		Assert.NotNull(signed.Signing);
		Assert.Equal("a2V5", signed.Signing!.PublicKey);
		Assert.Equal("c2ln", signed.Signing.Signature);

		// Every half-declared shape is treated as undeclared, never staged partial.
		Assert.Null(catalog.Entries[1].Signing);
		Assert.Null(catalog.Entries[2].Signing);
		Assert.Null(catalog.Entries[3].Signing);
		Assert.Null(catalog.Entries[4].Signing);
		Assert.Null(catalog.Entries[5].Signing);
		Assert.Null(entry.Signing);
	}

	[Fact]
	public async Task GetCatalogAsync_NotConfigured_ReturnsUnconfiguredSnapshot()
	{
		var service = new PluginCatalogService(new HttpClient(new FakeIndexHandler("{}")), NullLogger<PluginCatalogService>.Instance, () => null);

		PluginCatalog catalog = await service.GetCatalogAsync(CancellationToken.None);

		Assert.False(catalog.Configured);
		Assert.Empty(catalog.Entries);
	}

	[Fact]
	public async Task GetCatalogAsync_CachesUntilInvalidated()
	{
		var handler = new FakeIndexHandler("""{"plugins":[]}""");
		var service = new PluginCatalogService(new HttpClient(handler), NullLogger<PluginCatalogService>.Instance, () => "https://x/index.json");

		await service.GetCatalogAsync(CancellationToken.None);
		await service.GetCatalogAsync(CancellationToken.None);
		Assert.Equal(1, handler.Calls);

		service.Invalidate();
		await service.GetCatalogAsync(CancellationToken.None);
		Assert.Equal(2, handler.Calls);
	}

	[Fact]
	public async Task GetCatalogAsync_UrlChangeBypassesStaleCache()
	{
		var handler = new FakeIndexHandler("""{"plugins":[]}""");
		string url = "https://x/index-1.json";
		var service = new PluginCatalogService(new HttpClient(handler), NullLogger<PluginCatalogService>.Instance, () => url);

		await service.GetCatalogAsync(CancellationToken.None);
		url = "https://x/index-2.json";
		await service.GetCatalogAsync(CancellationToken.None);

		Assert.Equal(2, handler.Calls);
	}

	private sealed class FakeIndexHandler(string body) : HttpMessageHandler
	{
		public int Calls { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			});
		}
	}
}
