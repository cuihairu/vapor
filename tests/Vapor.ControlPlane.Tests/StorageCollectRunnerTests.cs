using System.Text.Json;
using Vapor.ControlPlane;
using Vapor.Protocol;
using Xunit;

namespace Vapor.ControlPlane.Tests;

public class StorageCollectRunnerTests
{
    private static IJobStore CreateStore() => new SqliteJobStore(":memory:");

    [Fact]
    public void SelectFarmSources_FiltersByRoleAndState()
    {
        var accounts = new List<AccountSpec>
        {
            new("farm-online", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000001"),
            new("farm-idle", true, AccountDesiredState.Idle, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000002"),
            new("farm-farm", true, AccountDesiredState.Farm, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000003"),
            new("farm-boost", true, AccountDesiredState.Boost, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000004"),
            new("farm-offline", true, AccountDesiredState.Offline, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000005"),
            new("farm-disabled", false, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000006"),
            new("storage-acct", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Storage, "76561198000000007"),
        };

        var farms = StorageCollectRunner.SelectFarmSources(accounts);

        Assert.Equal(4, farms.Count);
        Assert.Contains(farms, f => f.AccountName == "farm-online");
        Assert.Contains(farms, f => f.AccountName == "farm-idle");
        Assert.Contains(farms, f => f.AccountName == "farm-farm");
        Assert.Contains(farms, f => f.AccountName == "farm-boost");
        Assert.DoesNotContain(farms, f => f.AccountName == "farm-offline");
        Assert.DoesNotContain(farms, f => f.AccountName == "farm-disabled");
        Assert.DoesNotContain(farms, f => f.AccountName == "storage-acct");
    }

    [Fact]
    public async Task DispatchCollectAsync_HappyPath_CompletesWithResults()
    {
        var store = CreateStore();
        var storage = new AccountSpec("storage", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Storage, "76561198000000099");
        var farms = new List<AccountSpec>
        {
            new("farm1", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000001"),
            new("farm2", true, AccountDesiredState.Idle, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000002"),
        };

        var originalWindow = StorageCollectRunner.WaitWindow;
        var originalPoll = StorageCollectRunner.PollInterval;
        StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(2);
        StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            var run = await StorageCollectRunner.DispatchCollectAsync(store, storage, farms, "collect message", new[] { 730, 440 }, CancellationToken.None);

            Assert.True(run.Completed);
            Assert.Equal(2, run.Tasks.Count);

            foreach (var task in run.Tasks)
            {
                Assert.Equal(JobTaskStatus.Finished, task.Status);
                Assert.Null(task.Error);
                Assert.Equal(5, task.ItemCount);
                Assert.NotNull(task.TradeOfferId);
                Assert.True(task.Confirmed);
                Assert.NotNull(task.ConfirmJobId);
                Assert.Null(task.ConfirmError);
            }

            // Verify job meta
            var job = await store.GetJob(run.JobId, CancellationToken.None);
            Assert.Equal("storage-collect", job.Meta?["origin"]);
            Assert.Equal("storage", job.Meta?["storage"]);

            // Verify confirm jobs have parent_job
            foreach (var task in run.Tasks)
            {
                var confirmJob = await store.GetJob(task.ConfirmJobId!, CancellationToken.None);
                Assert.Equal("storage-collect", confirmJob.Meta?["origin"]);
                Assert.Equal("storage", confirmJob.Meta?["storage"]);
                Assert.Equal(run.JobId, confirmJob.Meta?["parent_job"]);
            }
        }
        finally
        {
            StorageCollectRunner.WaitWindow = originalWindow;
            StorageCollectRunner.PollInterval = originalPoll;
        }
    }

    [Fact]
    public async Task DispatchCollectAsync_TimeWindowCloses_ReturnsPending()
    {
        var store = CreateStore();
        var storage = new AccountSpec("storage", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Storage, "76561198000000099");
        var farms = new List<AccountSpec>
        {
            new("farm1", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000001"),
        };

        var originalWindow = StorageCollectRunner.WaitWindow;
        var originalPoll = StorageCollectRunner.PollInterval;
        StorageCollectRunner.WaitWindow = TimeSpan.FromMilliseconds(10);
        StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(5);
        try
        {
            var run = await StorageCollectRunner.DispatchCollectAsync(store, storage, farms, null, null, CancellationToken.None);

            Assert.False(run.Completed);
            Assert.Equal(0, run.Tasks.Count);
            Assert.NotNull(run.JobId);
        }
        finally
        {
            StorageCollectRunner.WaitWindow = originalWindow;
            StorageCollectRunner.PollInterval = originalPoll;
        }
    }

    [Fact]
    public async Task DispatchCollectAsync_ChainsConfirmations_WhenMobileConfirmationRequired()
    {
        var store = CreateStore();
        var storage = new AccountSpec("storage", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Storage, "76561198000000099");
        var farms = new List<AccountSpec>
        {
            new("farm1", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000001"),
        };

        var originalWindow = StorageCollectRunner.WaitWindow;
        var originalPoll = StorageCollectRunner.PollInterval;
        StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(2);
        StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            var run = await StorageCollectRunner.DispatchCollectAsync(store, storage, farms, null, null, CancellationToken.None);

            Assert.True(run.Completed);
            var task = run.Tasks[0];
            Assert.Equal(JobTaskStatus.Finished, task.Status);
            Assert.NotNull(task.TradeOfferId);
            Assert.True(task.Confirmed);
            Assert.NotNull(task.ConfirmJobId);
            Assert.Null(task.ConfirmError);

            // Verify the confirm job output
            var confirmJob = await store.GetJob(task.ConfirmJobId!, CancellationToken.None);
            var confirmTask = confirmJob.Tasks.Single(t => t.Target == "farm1");
            Assert.Equal(JobTaskStatus.Finished, confirmTask.Status);
            var confirmedFlag = OutputFlag(confirmTask.Output, "confirmed");
            Assert.True(confirmedFlag);
        }
        finally
        {
            StorageCollectRunner.WaitWindow = originalWindow;
            StorageCollectRunner.PollInterval = originalPoll;
        }
    }

    [Fact]
    public async Task BuildSnapshotAsync_ReturnsPerAccountLastCollect()
    {
        var store = CreateStore();
        var storage = new AccountSpec("storage", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Storage, "76561198000000099");
        var farms = new List<AccountSpec>
        {
            new("farm1", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000001"),
            new("farm2", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000002"),
        };

        var originalWindow = StorageCollectRunner.WaitWindow;
        var originalPoll = StorageCollectRunner.PollInterval;
        StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(2);
        StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            // First collect
            var run1 = await StorageCollectRunner.DispatchCollectAsync(store, storage, farms, "first", null, CancellationToken.None);
            Assert.True(run1.Completed);

            // Small delay to ensure different timestamps
            await Task.Delay(50);

            // Second collect (only farm1)
            var run2 = await StorageCollectRunner.DispatchCollectAsync(store, storage, [farms[0]], "second", null, CancellationToken.None);
            Assert.True(run2.Completed);

            var snapshot = await StorageCollectRunner.BuildSnapshotAsync(store, CancellationToken.None);

            Assert.Equal(2, snapshot.Count);

            var farm1Entry = snapshot.First(e => e.Account == "farm1");
            var farm2Entry = snapshot.First(e => e.Account == "farm2");

            // farm1 should show the second (newer) collect
            Assert.Equal(run2.JobId, farm1Entry.JobId);
            Assert.Equal("second", farm1Entry.Error); // Error field carries message in our test

            // farm2 should show the first collect
            Assert.Equal(run1.JobId, farm2Entry.JobId);

            // Both should have storage name
            Assert.Equal("storage", farm1Entry.Storage);
            Assert.Equal("storage", farm2Entry.Storage);
        }
        finally
        {
            StorageCollectRunner.WaitWindow = originalWindow;
            StorageCollectRunner.PollInterval = originalPoll;
        }
    }

    [Fact]
    public async Task BuildSnapshotAsync_MatchesConfirmByOfferId()
    {
        var store = CreateStore();
        var storage = new AccountSpec("storage", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Storage, "76561198000000099");
        var farms = new List<AccountSpec>
        {
            new("farm1", true, AccountDesiredState.Online, null, null, null, null, null, null, null, null, AccountRole.Farm, "76561198000000001"),
        };

        var originalWindow = StorageCollectRunner.WaitWindow;
        var originalPoll = StorageCollectRunner.PollInterval;
        StorageCollectRunner.WaitWindow = TimeSpan.FromSeconds(2);
        StorageCollectRunner.PollInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            var run = await StorageCollectRunner.DispatchCollectAsync(store, storage, farms, null, null, CancellationToken.None);
            Assert.True(run.Completed);

            var snapshot = await StorageCollectRunner.BuildSnapshotAsync(store, CancellationToken.None);

            Assert.Single(snapshot);
            var entry = snapshot[0];
            Assert.Equal("farm1", entry.Account);
            Assert.NotNull(entry.TradeOfferId);
            Assert.Equal(run.Tasks[0].TradeOfferId, entry.TradeOfferId);
            Assert.True(entry.Confirmed);
            Assert.Equal(run.Tasks[0].ConfirmJobId, entry.ConfirmJobId);
            Assert.Null(entry.ConfirmError);
        }
        finally
        {
            StorageCollectRunner.WaitWindow = originalWindow;
            StorageCollectRunner.PollInterval = originalPoll;
        }
    }

    private static bool OutputFlag(IReadOnlyDictionary<string, object?>? output, string key)
    {
        if (output is null || !output.TryGetValue(key, out object? raw) || raw is null)
        {
            return false;
        }

        return raw switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } s => bool.TryParse(s.GetString(), out bool parsed) && parsed,
            _ => false
        };
    }
}