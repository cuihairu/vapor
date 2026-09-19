using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Vapor.Protocol;
using Xunit;

namespace Vapor.Protocol.Tests;

/// <summary>
/// Property-based JSON round-trip invariants (FsCheck) over the wire models:
/// serialize → deserialize must restore the record exactly, for arbitrary field
/// values. Models with <c>object?</c> payload dictionaries are excluded on
/// purpose — System.Text.Json reads those back as JsonElement, which is a known
/// (and accepted) loss, not a regression this gate tracks.
/// </summary>
public sealed class ProtocolJsonRoundTripPropertyTests
{
	private static readonly JsonSerializerOptions s_options = JsonDefaults.Options;

	private static Property RoundTrips<T>(T original)
	{
		string json = JsonSerializer.Serialize(original, s_options);
		T restored = JsonSerializer.Deserialize<T>(json, s_options)!;
		return (Equals(restored, original)).ToProperty();
	}

	// Arbitrary longs must be folded into the representable DateTimeOffset range
	// (year 1..9999); 253402300799999 is the last representable unix millisecond.
	private static DateTimeOffset FromUnixMs(long ms, int offsetMinutes)
	{
		const long maxMs = 253402300799999;
		long safe = ((ms % maxMs) + maxMs) % maxMs;
		return DateTimeOffset.FromUnixTimeMilliseconds(safe).ToOffset(TimeSpan.FromMinutes(offsetMinutes % 1680 - 840));
	}

	[Property]
	public Property TaskHeartbeat_RoundTrips(NonNull<string> taskId, PositiveInt attempt, long ms, PositiveInt offsetMinutes) =>
		RoundTrips(new TaskHeartbeat(
			TaskId: taskId.Get,
			Attempt: attempt.Get,
			Ts: FromUnixMs(ms, offsetMinutes.Get)));

	[Property]
	public Property TaskCancel_RoundTrips(NonNull<string> taskId, PositiveInt attempt, long ms, PositiveInt offsetMinutes, bool withReason, NonNull<string> reason)
	{
		string? maybeReason = withReason ? reason.Get : null;
		return RoundTrips(new TaskCancel(
			TaskId: taskId.Get,
			Attempt: attempt.Get,
			Ts: FromUnixMs(ms, offsetMinutes.Get),
			Reason: maybeReason));
	}

	[Property]
	public Property ErrorResponse_RoundTrips(NonNull<string> error) =>
		RoundTrips(new ErrorResponse(error.Get));

	[Property]
	public Property AgentHello_RoundTrips(NonNull<string> agentId, NonNull<string> region, bool withMeta, string[] keys, bool[] values, string[] metaKeys, string[] metaValues, bool includeCapabilities)
	{
		if (keys.Length != values.Length || metaKeys.Length != metaValues.Length)
		{
			return true.ToProperty();
		}

		// FsCheck's default string generator yields nulls and duplicates; fold
		// both away — wire maps are unique-keyed and non-null keyed.
		Dictionary<string, bool>? capabilities = includeCapabilities
			? keys.Zip(values, (k, v) => (Key: k ?? "", v))
				.GroupBy(p => p.Key).Select(g => (g.Key, g.First().v)).ToDictionary(p => p.Item1, p => p.Item2)
			: null;
		Dictionary<string, string>? meta = withMeta
			? metaKeys.Zip(metaValues, (k, v) => (Key: k ?? "", v))
				.GroupBy(p => p.Key).Select(g => (g.Key, g.First().v)).ToDictionary(p => p.Item1, p => p.Item2)
			: null;

		var original = new AgentHello(
			AgentId: agentId.Get,
			Region: region.Get,
			Capabilities: capabilities,
			Meta: meta);

		string json = JsonSerializer.Serialize(original, s_options);
		AgentHello restored = JsonSerializer.Deserialize<AgentHello>(json, s_options)!;

		// Dictionary fields compare by reference under record equality, so the
		// round-trip is asserted field-by-field instead.
		bool equal =
			restored.AgentId == original.AgentId &&
			restored.Region == original.Region &&
			restored.Capabilities is null == (original.Capabilities is null) &&
			restored.Meta is null == (original.Meta is null) &&
			(restored.Capabilities is null || restored.Capabilities.SequenceEqual(original.Capabilities!)) &&
			(restored.Meta is null || restored.Meta.SequenceEqual(original.Meta!));
		return equal.ToProperty();
	}
}
