using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Property-based invariants (FsCheck) over the trade-policy normalizer and the
/// numeric payload reader — the same "any shape stays conservative" guarantees the
/// example-based tests in AccountStoreTests/DesiredStateReconcilerTests pin down,
/// now swept over arbitrary inputs.
/// </summary>
public sealed class TradePolicyPropertyTests
{
	// ---------- NormalizeTradePolicy ----------

	private static IReadOnlyList<ulong>? NormalizeWhitelist(bool autoAccept, IReadOnlyList<ulong> input) =>
		AccountStore.NormalizeTradePolicy(new TradePolicy(autoAccept, input))?.PartnerWhitelist;

	private static bool ThrowsArgException(bool autoAccept, IReadOnlyList<ulong> input)
	{
		try
		{
			NormalizeWhitelist(autoAccept, input);
			return false;
		}
		catch (ArgumentException)
		{
			return true;
		}
	}

	[Property]
	public Property Whitelist_IsSortedDistinctAndZeroFree(bool autoAccept, ulong[] input)
	{
		IReadOnlyList<ulong>? result;
		try
		{
			result = NormalizeWhitelist(autoAccept, input);
		}
		catch (ArgumentException)
		{
			// Rejection is only legal for autoAccept with an effectively-empty whitelist.
			return (autoAccept && input.All(id => id == 0ul)).ToProperty();
		}

		if (result is null)
		{
			// Null only when nothing active: flag off, or every entry dropped.
			return (!autoAccept || input.All(id => id == 0ul)).ToProperty();
		}

		return (result.All(id => id != 0ul)
			&& result.SequenceEqual(result.Distinct())
			&& result.SequenceEqual(result.OrderBy(id => id)))
			.ToProperty();
	}

	[Property]
	public Property Whitelist_IsInputOrderIndependent(bool autoAccept, ulong[] input)
	{
		bool forwardThrows = ThrowsArgException(autoAccept, input);
		bool backwardThrows = ThrowsArgException(autoAccept, input.Reverse().ToArray());

		if (forwardThrows || backwardThrows)
		{
			// Both orders must reject together.
			return (forwardThrows && backwardThrows).ToProperty();
		}

		// No rejection: results must agree.
		IReadOnlyList<ulong>? forward = NormalizeWhitelist(autoAccept, input);
		IReadOnlyList<ulong>? backward = NormalizeWhitelist(autoAccept, input.Reverse().ToArray());
		return (forward is null == backward is null
			&& (forward is null || forward.SequenceEqual(backward!)))
			.ToProperty();
	}

	[Property]
	public Property Normalization_IsIdempotent(ulong[] input)
	{
		// Idempotence in the usable-input regime (flag off): normalizing an
		// already-normalized whitelist must not move it.
		IReadOnlyList<ulong>? once = NormalizeWhitelist(autoAccept: false, input);
		if (once is null)
		{
			return true.ToProperty();
		}

		IReadOnlyList<ulong>? twice = NormalizeWhitelist(autoAccept: false, once);
		return (twice is not null && twice.SequenceEqual(once)).ToProperty();
	}

	[Property]
	public Property AutoAcceptWithAllZeroWhitelist_IsAlwaysRejected(PositiveInt length)
	{
		ulong[] ids = Enumerable.Repeat(0ul, length.Get).ToArray();

		return ThrowsArgException(autoAccept: true, ids).ToProperty();
	}

	// ---------- TryGetDouble (payload reader conservatism) ----------

	[Property]
	public Property TryGetDouble_NeverThrowsAndFalseLeavesValueUntouched(long? asLong, double? asDouble, int? asInt, string? asString, bool present)
	{
		var dict = new Dictionary<string, object?>();
		if (present)
		{
			// One arm per call, matching how the reconciler receives payload values.
			object? raw = (asLong, asDouble, asInt) switch
			{
				(not null, _, _) => asLong,
				(_, not null, _) => asDouble,
				(_, _, not null) => asInt,
				_ => asString
			};
			dict["hours"] = raw;
		}

		bool ok = DesiredStateReconciler.TryGetDouble(dict, "hours", out double value);
		// Conservatism contract: never throws; true implies a finite number, and
		// false implies the conventional zeroed out value (Try-pattern baseline).
		return (ok ? !double.IsNaN(value) && !double.IsInfinity(value) : value == 0).ToProperty();
	}

	[Property]
	public Property TryGetDouble_NumericArms_ConvertExactly(long l, int i)
	{
		var fromLong = new Dictionary<string, object?> { ["v"] = l };
		var fromInt = new Dictionary<string, object?> { ["v"] = i };

		bool longOk = DesiredStateReconciler.TryGetDouble(fromLong, "v", out double gotL);
		bool intOk = DesiredStateReconciler.TryGetDouble(fromInt, "v", out double gotI);

		return (longOk && gotL == (double)l).ToProperty()
			.And(intOk && gotI == (double)i);
	}

	[Property]
	public Property TryGetDouble_StringArm_AgreesWithInvariantTryParse(NonNull<string> raw)
	{
		var dict = new Dictionary<string, object?> { ["v"] = raw.Get };

		bool ok = DesiredStateReconciler.TryGetDouble(dict, "v", out double value);
		bool parses = double.TryParse(raw.Get, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double expected);

		// The string arm must agree exactly with the invariant TryParse it fronts.
		return (ok == parses && (!ok || value == expected)).ToProperty();
	}

	[Property]
	public Property TryGetDouble_JsonElementNumberArm_RoundTrips(double original)
	{
		if (double.IsNaN(original) || double.IsInfinity(original))
		{
			return true.ToProperty(); // JSON cannot carry non-finite numbers; out of scope.
		}

		string json = JsonSerializer.Serialize(original);
		using JsonDocument doc = JsonDocument.Parse(json);
		var dict = new Dictionary<string, object?> { ["v"] = doc.RootElement.Clone() };

		return (DesiredStateReconciler.TryGetDouble(dict, "v", out double value) && value == original).ToProperty();
	}
}
