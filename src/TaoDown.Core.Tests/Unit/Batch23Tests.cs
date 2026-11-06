using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TaoDown.Core;
using TaoDown.Engine;
using Xunit;

namespace TaoDown.Tests.Unit;

public class AutoClassifierTests
{
    [Theory]
    [InlineData("电影.mp4", "视频")]
    [InlineData("song.FLAC", "音频")]
    [InlineData("backup.7z", "压缩包")]
    [InlineData("report.pdf", "文档")]
    [InlineData("setup.msi", "安装包")]
    [InlineData("photo.png", "图片")]
    [InlineData("unknown.xyz", "其他")]
    [InlineData("", "其他")]
    [InlineData("noext", "其他")]
    public void Classify_MapsByExtension(string fileName, string expected) =>
        Assert.Equal(expected, AutoClassifier.Classify(fileName));

    [Fact]
    public void ResolveSaveDir_Enabled_PutsCategoryFolder()
    {
        var dir = AutoClassifier.ResolveSaveDir("a.mp4", @"D:\Downloads", true);
        Assert.EndsWith(Path.Combine("Downloads", "视频"), dir);
    }

    [Fact]
    public void ResolveSaveDir_Disabled_UsesRoot()
    {
        Assert.Equal(@"D:\Downloads", AutoClassifier.ResolveSaveDir("a.mp4", @"D:\Downloads", false));
    }

    [Fact]
    public void ResolveSaveDir_EmptyRoot_Throws()
    {
        Assert.Throws<ArgumentException>(() => AutoClassifier.ResolveSaveDir("a.mp4", " ", true));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Save_Load_Roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taodown-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var settings = new DownloadSettings { MaxConcurrent = 5, SpeedLimitKB = 2048, AutoClassify = false, SeedMinutes = 30 };
            store.Save(settings);

            var loaded = new SettingsStore(path).Load();
            Assert.Equal(5, loaded.MaxConcurrent);
            Assert.Equal(2048, loaded.SpeedLimitKB);
            Assert.False(loaded.AutoClassify);
            Assert.Equal(30, loaded.SeedMinutes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var loaded = new SettingsStore(Path.Combine(Path.GetTempPath(), $"taodown-none-{Guid.NewGuid():N}.json")).Load();
        Assert.Equal(3, loaded.MaxConcurrent);
        Assert.Equal(0, loaded.SpeedLimitKB);
        Assert.True(loaded.AutoClassify);
        Assert.True(loaded.ClipboardMonitor);
        Assert.Equal(0, loaded.SeedMinutes);
    }
}

public class DownloadQueueTests
{
    private sealed class FakeDownloadService : IDownloadService
    {
        public List<DownloadTask> Added { get; } = [];

        public event EventHandler<DownloadTask>? Updated;

        public event EventHandler<string>? Removed;

        public Task<string> AddAsync(DownloadTask task, CancellationToken ct = default)
        {
            Added.Add(task);
            task.Status = DownloadStatus.Active;
            return Task.FromResult(task.Id);
        }

        public Task PauseAsync(string taskId, CancellationToken ct = default) => Task.CompletedTask;

        public Task ResumeAsync(string taskId, CancellationToken ct = default) => Task.CompletedTask;

        public List<string> RemovedIds { get; } = [];

        public Task RemoveAsync(string taskId, CancellationToken ct = default)
        {
            RemovedIds.Add(taskId);
            Removed?.Invoke(this, taskId);
            return Task.CompletedTask;
        }

        public Task<int> PauseAllAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> ResumeAllAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> ClearFinishedAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<DownloadTask>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>([.. Added]);

        public void Complete(DownloadTask task)
        {
            task.Status = DownloadStatus.Complete;
            Updated?.Invoke(this, task);
        }
    }

    private static DownloadTask NewTask() => new() { Url = "http://a.com/f.zip", Kind = LinkKind.Http };

    [Fact]
    public async Task RemovedActiveTask_FreesSlot_NextStarts()
    {
        var service = new FakeDownloadService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var first = NewTask();
        var second = NewTask();
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);
        Assert.Equal(1, service.Added.Count); // 并发=1，第二个排队

        await service.RemoveAsync(first.Id); // 删除活动任务 → 触发 Removed

        Assert.Contains(first.Id, service.RemovedIds);
        Assert.Equal(2, service.Added.Count); // 名额释放，第二个自动开始
    }

    [Fact]
    public async Task RemovedUnknownTask_DoesNothing()
    {
        var service = new FakeDownloadService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var only = NewTask();
        await queue.EnqueueAsync(only);
        await service.RemoveAsync("不存在的Id");
        Assert.Equal(1, service.Added.Count);
        Assert.Equal(0, queue.Waiting().Count);
    }

    [Fact]
    public async Task Enqueue_RespectsConcurrencyLimit()
    {
        var service = new FakeDownloadService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 2 };
        for (var i = 0; i < 5; i++)
        {
            await queue.EnqueueAsync(NewTask());
        }

        Assert.Equal(2, service.Added.Count);
        Assert.Equal(3, queue.Waiting().Count);
    }

    [Fact]
    public async Task CompletedTask_FreesSlot_NextStarts()
    {
        var service = new FakeDownloadService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var first = NewTask();
        var second = NewTask();
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);
        Assert.Single(service.Added);

        service.Complete(first);
        await queue.PumpAsync(); // 显式放行，避免依赖事件后台任务的时序

        Assert.Equal(2, service.Added.Count);
        Assert.Contains(second, service.Added);
    }

    [Fact]
    public async Task HigherPriority_WaitsInOrder_TakesSlotFirst()
    {
        var service = new FakeDownloadService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var low = NewTask();
        var mid = NewTask();
        var high = NewTask();
        await queue.EnqueueAsync(low, priority: 0);   // 立即占坑开跑
        await queue.EnqueueAsync(mid, priority: 5);
        await queue.EnqueueAsync(high, priority: 10);

        Assert.Single(service.Added);
        Assert.Contains(low, service.Added);
        Assert.Equal([high, mid], queue.Waiting());   // 等待顺序按优先级

        service.Complete(low);
        await queue.PumpAsync();

        Assert.Equal(2, service.Added.Count);
        Assert.Contains(high, service.Added);         // 高优先级先拿到空位
    }

    [Fact]
    public async Task ScheduledStart_FutureTaskWaits()
    {
        var service = new FakeDownloadService();
        var now = new DateTime(2026, 10, 31, 18, 0, 0);
        var current = now;
        using var queue = new DownloadQueue(service, clock: () => current) { MaxConcurrent = 3 };
        var future = NewTask();
        var ready = NewTask();
        await queue.EnqueueAsync(future, startAt: now.AddHours(1));
        await queue.EnqueueAsync(ready);

        Assert.Single(service.Added);
        Assert.Contains(ready, service.Added);

        // 时间到达后放行
        current = now.AddHours(2);
        await queue.PumpAsync();
        Assert.Equal(2, service.Added.Count);
        Assert.Contains(future, service.Added);
    }

    [Fact]
    public async Task Dequeue_RemovesWaitingTask()
    {
        var service = new FakeDownloadService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var first = NewTask();
        var second = NewTask();
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);

        Assert.True(queue.Dequeue(second.Id));
        service.Complete(first);
        await queue.PumpAsync();

        Assert.Single(service.Added);
        Assert.DoesNotContain(second, service.Added);
    }
}

public class EnginePortTests
{
    [Fact]
    public void IsPortFree_FreePort_True_Occupied_False()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.False(Aria2EngineHost.IsPortFree(port));
        }
        finally
        {
            listener.Stop();
        }

        Assert.True(Aria2EngineHost.IsPortFree(port));
    }
}
