using System.Text.Json;
using Xunit;

namespace Vapor.Protocol.Tests;

/// <summary>
/// Pins the shared JSON serializer contract (<see cref="JsonDefaults.Options"/>):
/// every REST and WS message in the platform is serialized through these options,
/// so camelCase naming, camelCase enum strings and null omission are wire
/// contracts, not implementation details.
/// </summary>
public sealed class JsonDefaultsContractTests
{
	private enum SampleStatus
	{
		Queued,
		Running,
		Finished
	}

	private sealed record Sample(string EventName, SampleStatus Status, string? Note = null, int Count = 0);

	[Fact]
	public void Serializes_CamelCasePropertyNames()
	{
		string json = JsonSerializer.Serialize(new Sample("job.finished", SampleStatus.Running), JsonDefaults.Options);

		Assert.Contains("\"eventName\"", json);
		Assert.Contains("\"status\"", json);
	}

	[Fact]
	public void Serializes_EnumValuesAsCamelCaseStrings()
	{
		string json = JsonSerializer.Serialize(new Sample("x", SampleStatus.Finished), JsonDefaults.Options);

		Assert.Contains("\"finished\"", json);
	}

	[Fact]
	public void Omits_NullProperties()
	{
		string json = JsonSerializer.Serialize(new Sample("x", SampleStatus.Queued, Note: null), JsonDefaults.Options);

		Assert.DoesNotContain("note", json);
	}

	[Fact]
	public void Keeps_NonNullDefaults()
	{
		string json = JsonSerializer.Serialize(new Sample("x", SampleStatus.Queued, Count: 3), JsonDefaults.Options);

		Assert.Contains("\"count\":3", json);
	}

	[Fact]
	public void Deserializes_CamelCaseJson()
	{
		Sample sample = JsonSerializer.Deserialize<Sample>(
			"""{"eventName":"job.started","status":"running","note":"hi","count":2}""", JsonDefaults.Options)!;

		Assert.Equal("job.started", sample.EventName);
		Assert.Equal(SampleStatus.Running, sample.Status);
		Assert.Equal("hi", sample.Note);
		Assert.Equal(2, sample.Count);
	}

	[Fact]
	public void Deserializes_EnumStringsCaseInsensitively()
	{
		Sample sample = JsonSerializer.Deserialize<Sample>("""{"eventName":"x","status":"RUNNING"}""", JsonDefaults.Options)!;

		Assert.Equal(SampleStatus.Running, sample.Status);
	}

	[Fact]
	public void RoundTrip_PreservesValues()
	{
		var original = new Sample("task.result", SampleStatus.Queued, "ok", 7);

		string json = JsonSerializer.Serialize(original, JsonDefaults.Options);
		Sample parsed = JsonSerializer.Deserialize<Sample>(json, JsonDefaults.Options)!;

		Assert.Equal(original, parsed);
	}
}
