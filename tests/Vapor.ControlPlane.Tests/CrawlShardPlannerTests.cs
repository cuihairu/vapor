using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public sealed class CrawlShardPlannerTests
{
	private static AccountSpec Spec(string name, string? region = null) =>
		new(name, Enabled: true, AccountDesiredState.Offline, Region: region);

	[Fact]
	public void Build_RoundRobinsAcrossPoolInPoolOrder()
	{
		var pool = new[] { Spec("alice"), Spec("bob"), Spec("carol") };

		var plan = CrawlShardPlanner.Build(
			new uint[] { 1, 2, 3, 4, 5, 6, 7 },
			pool,
			overrides: null,
			shardSize: 200);

		Assert.Empty(plan.Warnings);
		// Pool order buckets: alice takes indexes 0/3/6, bob 1/4, carol 2/5.
		Assert.Equal(3, plan.Assignments.Count); // one shard per account (shard covers all)
		var byAccount = plan.Assignments.ToDictionary(a => a.Account, a => a.AppIds);
		Assert.Equal(new uint[] { 1, 4, 7 }, byAccount["alice"]);
		Assert.Equal(new uint[] { 2, 5 }, byAccount["bob"]);
		Assert.Equal(new uint[] { 3, 6 }, byAccount["carol"]);
	}

	[Fact]
	public void Build_OverridesWinOverRotation()
	{
		var pool = new[] { Spec("alice"), Spec("bob") };

		var plan = CrawlShardPlanner.Build(
			new uint[] { 1, 2, 3, 4 },
			pool,
			new Dictionary<uint, string> { [3] = "bob" },
			shardSize: 200);

		var byAccount = plan.Assignments.ToDictionary(a => a.Account, a => a.AppIds);
		// 3 would have landed on alice by rotation (index 2 % 2); the override
		// moves it to bob, whose bucket keeps plan order: [2, 3, 4].
		Assert.Equal(new uint[] { 1 }, byAccount["alice"]);
		Assert.Equal(new uint[] { 2, 3, 4 }, byAccount["bob"]);
	}

	[Fact]
	public void Build_OverrideNamingAccountOutsidePool_FallsBackToRotationWithWarning()
	{
		var pool = new[] { Spec("alice"), Spec("bob") };

		var plan = CrawlShardPlanner.Build(
			new uint[] { 1, 2 },
			pool,
			new Dictionary<uint, string> { [1] = "mallory" },
			shardSize: 200);

		// The app is not lost — it falls back to rotation; only the override is voided.
		var allApps = plan.Assignments.SelectMany(a => a.AppIds).OrderBy(id => id).ToArray();
		Assert.Equal(new uint[] { 1, 2 }, allApps);
		var warning = Assert.Single(plan.Warnings);
		Assert.Contains("mallory", warning);
		Assert.Contains("1", warning);
	}

	[Fact]
	public void Build_WithEmptyPool_ReturnsNoAssignments()
	{
		var plan = CrawlShardPlanner.Build(new uint[] { 1, 2 }, [], null, shardSize: 50);

		Assert.Empty(plan.Assignments);
	}

	[Fact]
	public void Build_WithSingleAccount_GivesEverythingToIt()
	{
		var plan = CrawlShardPlanner.Build(new uint[] { 1, 2, 3 }, new[] { Spec("solo") }, null, shardSize: 200);

		var assignment = Assert.Single(plan.Assignments);
		Assert.Equal(new uint[] { 1, 2, 3 }, assignment.AppIds);
	}

	[Fact]
	public void Build_ChunksLargeBucketsPerAccount()
	{
		var plan = CrawlShardPlanner.Build(
			new uint[] { 1, 2, 3, 4, 5 },
			new[] { Spec("solo") },
			null,
			shardSize: 2);

		Assert.Equal(3, plan.Assignments.Count); // 2 + 2 + 1
		Assert.Equal(new uint[] { 1, 2 }, plan.Assignments[0].AppIds);
		Assert.Equal(new uint[] { 3, 4 }, plan.Assignments[1].AppIds);
		Assert.Equal(new uint[] { 5 }, plan.Assignments[2].AppIds);
	}

	[Fact]
	public void Build_PassesAccountRegionIntoAssignments()
	{
		var plan = CrawlShardPlanner.Build(
			new uint[] { 1, 2 },
			new[] { Spec("alice", region: "eu"), Spec("bob") },
			null,
			shardSize: 200);

		var byAccount = plan.Assignments.ToDictionary(a => a.Account, a => a.Region);
		Assert.Equal("eu", byAccount["alice"]);
		Assert.Null(byAccount["bob"]);
	}

	[Fact]
	public void Chunk_ClampsShardSizeToActionBatchLimit()
	{
		var bucket = Enumerable.Range(1, 201).Select(i => (uint)i).ToList();

		// Oversized shard is clamped to the action's 200-app batch limit.
		var clamped = CrawlShardPlanner.Chunk(bucket, shardSize: 999);
		Assert.Equal(2, clamped.Count);
		Assert.Equal(200, clamped[0].Count);
		Assert.Equal(201U, Assert.Single(clamped[1]));

		// Zero collapses to one app per shard rather than looping forever.
		var unit = CrawlShardPlanner.Chunk(new uint[] { 7, 8 }, shardSize: 0);
		Assert.Equal(2, unit.Count);
		Assert.Equal(new uint[] { 7 }, unit[0]);
	}

	[Fact]
	public void Build_IsDeterministicForIdenticalInputs()
	{
		var pool = new[] { Spec("alice"), Spec("bob"), Spec("carol") };
		var overrides = new Dictionary<uint, string> { [4] = "carol" };

		var first = CrawlShardPlanner.Build(new uint[] { 1, 2, 3, 4, 5 }, pool, overrides, shardSize: 2);
		var second = CrawlShardPlanner.Build(new uint[] { 1, 2, 3, 4, 5 }, pool, overrides, shardSize: 2);

		var firstShape = first.Assignments.Select(a => $"{a.Account}:{string.Join(',', a.AppIds)}");
		var secondShape = second.Assignments.Select(a => $"{a.Account}:{string.Join(',', a.AppIds)}");
		Assert.Equal(firstShape, secondShape);
	}
}
