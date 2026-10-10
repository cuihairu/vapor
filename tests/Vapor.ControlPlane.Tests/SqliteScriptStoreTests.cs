using Microsoft.Data.Sqlite;
using Vapor.ControlPlane;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class SqliteScriptStoreTests : IDisposable
{
	private readonly SqliteScriptStore _store = new(":memory:");

	public void Dispose() => _store.Dispose();

	private static ScriptRecord SampleScript(
		string id = "script-1",
		string name = "cleanup",
		string? description = null,
		string language = "shell",
		string? content = null) => new(
		Id: id,
		Name: name,
		Description: description ?? "",
		Language: language,
		Content: content ?? "#!/bin/sh\necho hi\n",
		CreatedAtMs: 1000,
		UpdatedAtMs: 2000);

	[Fact]
	public void Constructor_WithEmptyDbPath_Throws()
	{
		Assert.Throws<ArgumentException>(() => new SqliteScriptStore(""));
		Assert.Throws<ArgumentException>(() => new SqliteScriptStore("   "));
	}

	[Fact]
	public async Task Constructor_WithRelativePath_CreatesDirectory()
	{
		string tempDir = Path.Combine(Path.GetTempPath(), "vapor-script-store-tests");
		string dbPath = Path.Combine(tempDir, "scripts.db");
		try
		{
			using SqliteScriptStore store = new(dbPath);
			Assert.True(File.Exists(dbPath));
		}
		finally
		{
			// Windows: ADO.NET keeps a pooled file handle even after Dispose();
			// clearing the pool is what actually releases the file for deletion.
			SqliteConnection.ClearAllPools();
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, recursive: true);
			}
		}
	}

	[Fact]
	public async Task Upsert_InsertsThenUpdatesById()
	{
		ScriptRecord script = SampleScript(id: "s1", name: "first");
		await _store.UpsertAsync(script);
		Assert.Single(await _store.ListAsync());

		ScriptRecord updated = script with { Name = "second", Content = "#!/bin/sh\necho updated\n", UpdatedAtMs = 3000 };
		await _store.UpsertAsync(updated);

		List<ScriptRecord> scripts = await _store.ListAsync();
		Assert.Single(scripts);
		Assert.Equal("s1", scripts[0].Id);
		Assert.Equal("second", scripts[0].Name);
		Assert.Equal(3000, scripts[0].UpdatedAtMs);
	}

	[Fact]
	public async Task Get_WithUnknownId_ReturnsNull()
	{
		Assert.Null(await _store.GetAsync("missing"));
		Assert.Null(await _store.GetAsync(""));
	}

	[Fact]
	public async Task List_ReturnsScriptsOrderedByNameThenId()
	{
		await _store.UpsertAsync(SampleScript(id: "b-id", name: "beta"));
		await _store.UpsertAsync(SampleScript(id: "a-id", name: "beta"));
		await _store.UpsertAsync(SampleScript(id: "c-id", name: "alpha"));

		List<ScriptRecord> scripts = await _store.ListAsync();

		var names = scripts.Select(s => s.Name).ToList();
		Assert.Equal(new[] { "alpha", "beta", "beta" }, names);
		Assert.Equal("a-id", scripts[1].Id);
		Assert.Equal("b-id", scripts[2].Id);
	}

	[Fact]
	public async Task Delete_RemovesExistingAndReportsFalseForUnknown()
	{
		await _store.UpsertAsync(SampleScript(id: "s1"));

		Assert.True(await _store.DeleteAsync("s1"));
		Assert.Empty(await _store.ListAsync());

		Assert.False(await _store.DeleteAsync("s1"));
		Assert.False(await _store.DeleteAsync("missing"));
	}
}
