using FsCheck.Xunit;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

/// <summary>
/// Property-based invariants (FsCheck) over FarmPolicy normalization — the
/// per-game budget acceptance domain and the declaration-order-preserving
/// priority list. TradePolicy normalization already has its own property
/// class (TradePolicyPropertyTests); this one pins the documented fork on
/// the farm side: list position IS the queue priority, so normalization may
/// drop zeros and collapse duplicates but must never reorder. The example
/// tests in AccountStoreTests pin each rejection arm; these sweep arbitrary
/// budgets (NaN/±∞ via boundary tables the default double generator never
/// produces) and arbitrary app lists with zeros and duplicates forced in.
/// </summary>
public sealed class FarmPolicyPropertyTests
{
	// Boundary budgets the default FsCheck double generator never produces;
	// picked via a Ring-normalized index so int.MinValue cannot overflow.
	private static readonly double?[] SpecialBudgets =
		{ null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.0, -1.0, 2.5 };

	private static double? BudgetFrom(int raw, int pick)
	{
		switch (((pick % 4) + 4) % 4)
		{
			case 0: return raw / 1000.0;       // arbitrary decimals, both signs
			case 1: return raw * 1000000.0;    // large magnitudes, both signs
			case 2: return raw * 1e-300;       // tiny magnitudes (underflow to ±0 included)
			default: return SpecialBudgets[(int)(((uint)raw) % (uint)SpecialBudgets.Length)];
		}
	}

	// Zeros and a duplicate are forced into every list so the drop/dedupe
	// arms fire on every run instead of relying on random collisions.
	private static uint[] WithZeroAndDuplicate(uint[] rawApps)
	{
		uint head = rawApps.Length > 0 ? rawApps[0] : 7u;
		return rawApps.Append(0u).Append(head).ToArray();
	}

	private static FarmPriorityOrder OrderFrom(int idx) => (FarmPriorityOrder)(((idx % 3) + 3) % 3);

	private static FarmPolicy BuildFarmPolicy(int budgetRaw, int budgetPick, int orderIdx, uint[] rawApps, bool nullApps)
		=> new FarmPolicy(BudgetFrom(budgetRaw, budgetPick), OrderFrom(orderIdx),
			nullApps ? null : WithZeroAndDuplicate(rawApps));

	private static bool ListsEqual<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
	{
		if (a is null || b is null)
		{
			return a is null && b is null;
		}
		return a.Count == b.Count && a.SequenceEqual(b);
	}

	// Independent reimplementation of "drop zeros, keep first occurrences" —
	// the documented declaration-order-preserving semantics farm priorities
	// must have.
	private static List<uint> StableDedupDropZero(IEnumerable<uint> source)
	{
		var seen = new HashSet<uint>();
		var result = new List<uint>();
		foreach (uint id in source)
		{
			if (id != 0u && seen.Add(id))
			{
				result.Add(id);
			}
		}
		return result;
	}

	// Null-aware element-wise equality: "nothing active" legitimately
	// normalizes to null, and normalization rebuilds the array — record
	// reference equality on IReadOnlyList would lie either way.
	private static bool FarmPolicyEquals(FarmPolicy? a, FarmPolicy? b)
		=> (a is null, b is null) switch
		{
			(true, true) => true,
			(false, false) => a!.PerGameHourBudget.Equals(b!.PerGameHourBudget)
				&& a.PriorityOrder == b.PriorityOrder
				&& ListsEqual(a.PriorityApps, b.PriorityApps),
			_ => false,
		};

	[Property]
	public void FarmPolicy_Normalize_IsIdempotent(int budgetRaw, int budgetPick, int orderIdx, uint[] rawApps, int appsPick)
	{
		FarmPolicy input = BuildFarmPolicy(budgetRaw, budgetPick, orderIdx, rawApps, appsPick % 2 == 1);
		FarmPolicy? once;
		try
		{
			once = AccountStore.NormalizeFarmPolicy(input);
		}
		catch (ArgumentException)
		{
			return; // throw-domain sample: idempotence only constrains accepted inputs
		}
		// The output is null ("nothing active") or carries a finite positive
		// budget, so the second pass cannot throw either.
		FarmPolicy? twice = AccountStore.NormalizeFarmPolicy(once);
		Assert.True(FarmPolicyEquals(once, twice), $"not idempotent for {input}");
	}

	[Property]
	public void FarmPolicy_ThrowsExactlyOnNonFiniteOrNonPositiveBudget(int raw, int pick, int orderIdx, uint[] rawApps)
	{
		var policy = new FarmPolicy(BudgetFrom(raw, pick), OrderFrom(orderIdx), rawApps);
		bool shouldThrow = policy.PerGameHourBudget is { } budget && (!double.IsFinite(budget) || budget <= 0);
		if (shouldThrow)
		{
			Assert.Throws<ArgumentException>(() => AccountStore.NormalizeFarmPolicy(policy));
		}
		else
		{
			AccountStore.NormalizeFarmPolicy(policy); // must not throw
		}
	}

	[Property]
	public void FarmPolicy_NormalizedOutput_SatisfiesDomainAndNullExactness(int budgetRaw, int budgetPick, int orderIdx, uint[] rawApps, int appsPick)
	{
		FarmPolicy input = BuildFarmPolicy(budgetRaw, budgetPick, orderIdx, rawApps, appsPick % 2 == 1);
		FarmPolicy? output;
		try
		{
			output = AccountStore.NormalizeFarmPolicy(input);
		}
		catch (ArgumentException)
		{
			return;
		}
		if (output is null)
		{
			// Null is exactly "nothing active": no budget, default order, no usable priorities.
			Assert.True(input.PerGameHourBudget is null
				&& input.PriorityOrder == FarmPriorityOrder.CardsDescending
				&& StableDedupDropZero(input.PriorityApps ?? []).Count == 0);
			return;
		}
		if (output.PerGameHourBudget is { } budget)
		{
			Assert.True(double.IsFinite(budget) && budget > 0, $"budget {budget} left the domain");
		}
		if (output.PriorityApps is { } list)
		{
			Assert.NotEmpty(list);
			Assert.All(list, id => Assert.NotEqual(0u, id));
			Assert.Equal(list.Count, list.Distinct().Count());
		}
	}

	[Property]
	public void FarmPolicy_PriorityApps_KeepDeclarationOrder(uint[] rawApps)
	{
		List<uint> expected = StableDedupDropZero(rawApps);
		// A budget keeps the policy alive even when every priority entry is unusable.
		FarmPolicy? normalized = AccountStore.NormalizeFarmPolicy(new FarmPolicy(PerGameHourBudget: 1.0, PriorityApps: rawApps));
		if (expected.Count == 0)
		{
			Assert.Null(normalized!.PriorityApps);
		}
		else
		{
			Assert.Equal(expected, normalized!.PriorityApps);
		}
	}
}
