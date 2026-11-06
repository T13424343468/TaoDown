using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests;

/// <summary>暴力测试：下载队列随机操作压测——并发不越界、不卡死、状态机不崩。</summary>
public class QueueStressTests
{
    /// <summary>最小假服务：记录动作，随机故障注入。</summary>
    private sealed class StressFakeService : IDownloadService
    {
        public List<string> Added { get; } = [];
        public List<string> RemovedIds { get; } = [];
        public int PauseCalls;
        public int FailEveryNth = 0; // >0 时每 N 次调用抛一次故障

        private int _calls;

        public event EventHandler<DownloadTask>? Updated;
        public event EventHandler<string>? Removed;

        private void MaybeFail()
        {
            if (FailEveryNth <= 0)
            {
                return;
            }

            if (Interlocked.Increment(ref _calls) % FailEveryNth == 0)
            {
                throw new InvalidOperationException("压测注入故障");
            }
        }

        public Task<string> AddAsync(DownloadTask task, CancellationToken ct = default)
        {
            MaybeFail();
            lock (Added)
            {
                Added.Add(task.Id);
                task.Status = DownloadStatus.Active;
            }

            return Task.FromResult(task.Id);
        }

        public Task PauseAsync(string taskId, CancellationToken ct = default)
        {
            MaybeFail();
            Interlocked.Increment(ref PauseCalls);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(string taskId, CancellationToken ct = default)
        {
            MaybeFail();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string taskId, CancellationToken ct = default)
        {
            MaybeFail();
            lock (RemovedIds)
            {
                RemovedIds.Add(taskId);
            }

            Removed?.Invoke(this, taskId);
            return Task.CompletedTask;
        }

        public Task<int> PauseAllAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> ResumeAllAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> ClearFinishedAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<DownloadTask>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>([]);
    }

    private static void TryIgnore(Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            // 压测注入的故障：调用方按契约吞掉
        }
    }

    [Fact]
    public async Task Hammer_RandomOps_ConcurrencyNeverExceedsCap()
    {
        var service = new StressFakeService { FailEveryNth = 7 }; // 每 7 次调用注入一次故障
        using var queue = new DownloadQueue(service) { MaxConcurrent = 3 };

        const int total = 200;
        var tasks = Enumerable.Range(0, total)
            .Select(i => new DownloadTask { Url = $"http://a.com/{i}.zip", Kind = LinkKind.Http })
            .ToList();

        foreach (var task in tasks)
        {
            await queue.EnqueueAsync(task);
            Assert.True(service.Added.Count - service.RemovedIds.Count <= 3, "并发名额越界");
        }

        Assert.Equal(3, service.Added.Count); // 只有 3 个启动，其余排队

        // 维度：压测中随机改并发上限（1..5），额度只升不降作为断言基准（降额不回收在途任务）

        // 随机暴力操作：暂停/继续/删除交错，含故障注入
        var r = new Random(2026);
        var maxCapSoFar = 3;
        for (var round = 0; round < 30; round++)
        {
            queue.MaxConcurrent = r.Next(1, 6);
            maxCapSoFar = Math.Max(maxCapSoFar, queue.MaxConcurrent);
            foreach (var task in tasks)
            {
                switch (r.Next(4))
                {
                    case 0:
                        TryIgnore(() => service.PauseAsync(task.Id));
                        break;
                    case 1:
                        TryIgnore(() => service.ResumeAsync(task.Id));
                        break;
                    case 2:
                        // 故障注入：服务层抛错由界面层接住；这里验证队列不因此崩溃或越界
                        TryIgnore(() => service.RemoveAsync(task.Id));
                        break;
                    default:
                        break;
                }

                Assert.True(service.Added.Count - service.RemovedIds.Count <= maxCapSoFar, $"第 {round} 轮并发越界");
            }
        }

        // 收尾：压测后队列仍能正常吞新任务（不卡死、名额不泄漏）
        var extra = new DownloadTask { Url = "http://a.com/extra.zip", Kind = LinkKind.Http };
        await queue.EnqueueAsync(extra);
        Assert.Contains(extra.Id, service.Added);
    }
}
