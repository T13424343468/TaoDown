using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>下载队列调度维度：优先级、定时开始、名额腾挪、排队出队。</summary>
public class QueueDepthTests
{
    /// <summary>可控假服务：记录提交顺序，手工触发完成/移除。</summary>
    private sealed class FakeService : IDownloadService
    {
        public List<string> Added { get; } = [];
        public event EventHandler<DownloadTask>? Updated;
        public event EventHandler<string>? Removed;

        public Task<string> AddAsync(DownloadTask task, CancellationToken ct = default)
        {
            lock (Added)
            {
                Added.Add(task.Id);
            }

            return Task.FromResult(task.Id);
        }

        public Task PauseAsync(string taskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(string taskId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string taskId, CancellationToken ct = default)
        {
            Removed?.Invoke(this, taskId);
            return Task.CompletedTask;
        }

        public Task<int> PauseAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ResumeAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearFinishedAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<DownloadTask>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>([]);

        public void Complete(string taskId, DownloadTask task)
        {
            task.Status = DownloadStatus.Complete;
            Updated?.Invoke(this, task);
        }
    }

    private static DownloadTask MakeTask(int i) => new()
    {
        Url = $"http://a.com/{i}.zip",
        Kind = LinkKind.Http,
    };

    [Fact]
    public async Task Priority_HighestFirst_ThenNextOnSlotFree()
    {
        var service = new FakeService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var a = MakeTask(1);
        var b = MakeTask(2);
        var c = MakeTask(3);
        await queue.EnqueueAsync(a, priority: 0);
        await queue.EnqueueAsync(b, priority: 5);
        await queue.EnqueueAsync(c, priority: 9);

        Assert.Equal([a.Id], service.Added); // 名额只有 1：先到的直接占坑，后到的高优先级排队

        await service.RemoveAsync(a.Id); // 腾出名额，排队中优先级最高的 c 顶上，再看 b
        await service.RemoveAsync(c.Id);
        Assert.Equal([a.Id, c.Id, b.Id], service.Added);
    }

    [Fact]
    public async Task SamePriority_FifoByCreatedTime()
    {
        var service = new FakeService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var first = MakeTask(1);
        var second = MakeTask(2);
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);
        await service.RemoveAsync(first.Id);
        Assert.Equal([first.Id, second.Id], service.Added);
    }

    [Fact]
    public async Task ScheduledFuture_StaysWaiting_UntilClockPasses()
    {
        var service = new FakeService();
        var now = new DateTime(2026, 11, 1, 12, 0, 0);
        var clock = now;
        using var queue = new DownloadQueue(service, () => clock) { MaxConcurrent = 2 };
        var task = MakeTask(1);
        await queue.EnqueueAsync(task, startAt: now.AddMinutes(30));

        Assert.Empty(service.Added); // 未到时间不启动
        Assert.Equal([task], queue.Waiting());

        clock = now.AddMinutes(31);
        await queue.PumpAsync();
        Assert.Equal([task.Id], service.Added); // 到点放行
    }

    [Fact]
    public async Task DequeueWaiting_TaskNeverStarts()
    {
        var service = new FakeService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var a = MakeTask(1);
        var b = MakeTask(2);
        await queue.EnqueueAsync(a);
        await queue.EnqueueAsync(b);
        Assert.True(queue.Dequeue(b.Id)); // 排队中的 b 被移除
        await service.RemoveAsync(a.Id); // 腾名额

        Assert.Equal([a.Id], service.Added); // b 不再被提交
        Assert.Empty(queue.Waiting());
    }

    [Fact]
    public async Task CompleteFreesSlot_NextWaitingStarts()
    {
        var service = new FakeService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var a = MakeTask(1);
        var b = MakeTask(2);
        await queue.EnqueueAsync(a);
        await queue.EnqueueAsync(b);

        a.Status = DownloadStatus.Complete;
        service.Complete(a.Id, a);

        // Updated 事件同步回调里 Pump 是 fire-and-forget，等一拍
        await Task.Delay(50);
        Assert.Equal([a.Id, b.Id], service.Added);
    }

    [Fact]
    public async Task Waiting_SortedByPriorityThenTime()
    {
        var service = new FakeService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };
        var a = MakeTask(1);
        var b = MakeTask(2);
        var c = MakeTask(3);
        await queue.EnqueueAsync(a, priority: 1); // a 直接占坑
        await queue.EnqueueAsync(b, priority: 9);
        await queue.EnqueueAsync(c, priority: 1);

        Assert.Equal([b, c], queue.Waiting()); // 排队中的：高优先级在前，同级按入队时间
    }
}
