using System.Globalization;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace Vapor.Steam.Core.Tests.Unit;

/// <summary>
/// Property-based tests over PayloadReader's defensive payload reads. The
/// properties pin the lookup semantics (an exact key always wins over case
/// variants; an OrdinalIgnoreCase variant is found when the exact key is
/// absent), the range arithmetic (a boxed long converts iff it fits int;
/// a boxed double converts iff it is a whole number in range — NaN and the
/// infinities therefore yield null), the round-trips (any int survives the
/// boxed/invariant-string/JSON-string forms; a bool survives its own
/// ToString, so "True"/"False" must parse), and totality: no combination of
/// shapes and keys ever throws.
/// </summary>
public sealed class PayloadReaderPropertyTests
{
	// One representative of every branch the readers can hit, including the
	// degenerate numeric forms (NaN, infinities, extremes).
	private static readonly object?[] Shapes =
		[null, 1, 1L, 2.5, double.NaN, double.PositiveInfinity, "text", true, long.MaxValue, int.MinValue];

	private static Dictionary<string, object?> FromJson(string json) =>
		JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

	// --- TryGetValue lookup semantics ---

	[Property]
	public void TryGetValue_ExactKey_WinsOverCaseVariant(NonNull<string> key, string exactValue, string decoyValue)
	{
		var payload = new Dictionary<string, object?> { [key.Get] = exactValue };

		// When the upper-cased twin is a distinct key it holds a decoy that
		// must NOT be returned; when it self-collides with the exact key there
		// is no variant to place, so the decoy is skipped.
		var twin = key.Get.ToUpperInvariant();
		if (twin != key.Get)
		{
			payload[twin] = decoyValue;
		}

		Assert.True(PayloadReader.TryGetValue(payload, key.Get, out var value));
		Assert.Equal(exactValue, value);
	}

	[Property]
	public void TryGetValue_CaseVariant_IsFoundWhenExactKeyAbsent(NonNull<string> key, string value)
	{
		var payload = new Dictionary<string, object?> { [key.Get.ToUpperInvariant()] = value };

		Assert.True(PayloadReader.TryGetValue(payload, key.Get, out var found));
		Assert.Equal(value, found);
	}

	// --- GetInt32 range arithmetic ---

	[Property]
	public void GetInt32_BoxedLong_ConvertsExactlyWithinIntRange(long value)
	{
		var payload = new Dictionary<string, object?> { ["k"] = value };
		var result = PayloadReader.GetInt32(payload, "k");

		if (value is >= int.MinValue and <= int.MaxValue)
		{
			Assert.Equal((int)value, result);
		}
		else
		{
			Assert.Null(result);
		}
	}

	[Property]
	public void GetInt32_BoxedDouble_NullUnlessWholeNumberWithinRange(double value)
	{
		var payload = new Dictionary<string, object?> { ["k"] = value };
		var result = PayloadReader.GetInt32(payload, "k");

		var expected = value >= int.MinValue && value <= int.MaxValue && Math.Floor(value) == value
			? (int)value
			: (int?)null;

		Assert.Equal(expected, result);
	}

	[Property]
	public void GetInt32_AnyInt_SurvivesBoxedInvariantStringAndJsonStringForms(int value)
	{
		const string key = "k";

		Assert.Equal(value, PayloadReader.GetInt32(new Dictionary<string, object?> { [key] = value }, key));
		Assert.Equal(
			value,
			PayloadReader.GetInt32(
				new Dictionary<string, object?> { [key] = value.ToString(CultureInfo.InvariantCulture) }, key));
		Assert.Equal(
			value,
			PayloadReader.GetInt32(FromJson($$"""{"{{key}}":"{{value}}"}"""), key));
	}

	// --- GetBool round-trip ---

	[Property]
	public void GetBool_AnyBool_SurvivesBoxedToStringAndJsonForms(bool value)
	{
		const string key = "k";

		Assert.Equal(value, PayloadReader.GetBool(new Dictionary<string, object?> { [key] = value }, key));
		// bool.ToString() is "True"/"False": bool.TryParse must accept its own
		// output casing.
		Assert.Equal(value, PayloadReader.GetBool(new Dictionary<string, object?> { [key] = value.ToString() }, key));
		Assert.Equal(value, PayloadReader.GetBool(FromJson($$"""{"{{key}}":{{value.ToString().ToLowerInvariant()}}}"""), key));
	}

	// --- totality ---

	[Property]
	public void Readers_NeverThrow_OnAnyShapeAndKeyCombination(NonNull<string> key, int exactShape, int twinShape)
	{
		var payload = new Dictionary<string, object?>
		{
			[key.Get] = Shapes[(uint)exactShape % Shapes.Length],
			[key.Get.ToUpperInvariant()] = Shapes[(uint)twinShape % Shapes.Length],
		};

		var text = PayloadReader.GetString(payload, key.Get);
		var number = PayloadReader.GetInt32(payload, key.Get);
		var flag = PayloadReader.GetBool(payload, key.Get);

		// A null value reads as null through every reader, regardless of what
		// the case twin holds.
		if (payload[key.Get] is null)
		{
			Assert.Null(text);
			Assert.Null(number);
			Assert.Null(flag);
		}
	}
}
