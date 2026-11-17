using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TaoDown.Core;

using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>
/// 队列泵韧性（2026-11-17 云批 run 43924703268 实锤）：
/// 引擎自愈重启窗口内提交失败，队列不得永久哑火——
/// 此前只重试一次，批量入队撞上失败窗口后 26 分钟零派发，40 任务仅 1/40 到终态。
/// 现要求：退避重泵直至引擎接受（有界），批量入队恢复后整队继续派发。
/// </summary>
public class QueueRetryTests
{
    /// <summary>可控假服务：前 FailTimes 次提交抛错（引擎未就绪），之后成功。</summary>
    private sealed class FlakyService : IDownloadService
    {
        private int _calls;

        public int FailTimes { get; set; }

        public List<string> Added { get; } = [];

        public event EventHandler<DownloadTask>? Updated;

        public event EventHandler<string>? Removed;

        public void RegisterWaiting(DownloadTask task)
        {
            // 测试替身：等待登记无副作用
        }

        public Task<string> AddAsync(DownloadTask task, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n <= FailTimes)
            {
                throw new InvalidOperationException($"aria2 RPC 错误: 引擎未就绪（第 {n} 次提交）");
            }

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

        public void MarkWaiting(string taskId, DownloadTask task)
        {
            // 模拟引擎自愈重启：活动任务被服务层标回等待并广播
            task.Status = DownloadStatus.Waiting;
            Updated?.Invoke(this, task);
        }
    }

    private static DownloadTask MakeTask(int i) => new()
    {
        Url = $"http://a.com/{i}.zip",
        Kind = LinkKind.Http,
    };

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "等待超时：条件未在时限内满足");
    }

    [Fact]
    public async Task R1_SubmitFailureDuringRestartWindow_RecoversAndPumpsAll()
    {
        // 场景：引擎自愈重启窗口，前 3 次提交全部失败；之后引擎就绪，重泵把整队派发下去
        var service = new FlakyService { FailTimes = 3 };
        using var queue = new DownloadQueue(service)
        {
            MaxConcurrent = 3,
            RetryDelay = TimeSpan.FromMilliseconds(30),
        };

        var tasks = Enumerable.Range(1, 3).Select(MakeTask).ToList();
        foreach (var task in tasks)
        {
            await queue.EnqueueAsync(task);
        }

        // 退避重泵接住：3 个全部到引擎，不哑火
        await WaitFor(() => service.Added.Count == 3);
        Assert.Equal(3, service.Added.Count);
        Assert.Empty(queue.Waiting());
    }

    [Fact]
    public async Task R2_MassEnqueueDuringFailureWindow_NoOrphanTasks()
    {
        // 场景：40 任务批量入队（对应云批 B 段），入队期提交连失败；
        // 重泵恢复后逐个派发，名额腾一个进一个，无任务滞留
        var service = new FlakyService { FailTimes = 10 };
        using var queue = new DownloadQueue(service)
        {
            MaxConcurrent = 3,
            RetryDelay = TimeSpan.FromMilliseconds(30),
        };

        var tasks = Enumerable.Range(1, 40).Select(MakeTask).ToList();
        var byId = tasks.ToDictionary(t => t.Id);
        foreach (var task in tasks)
        {
            await queue.EnqueueAsync(task);
        }

        await WaitFor(() => service.Added.Count >= 3, timeoutMs: 10_000); // 恢复派发

        // 名额腾挪驱动整队走完：完成一个放行一个
        var completed = new HashSet<string>();
        while (service.Added.Count < 40)
        {
            string? next;
            lock (service.Added)
            {
                next = service.Added.FirstOrDefault(id => !completed.Contains(id));
            }

            if (next is null)
            {
                await Task.Delay(20);
                continue;
            }

            completed.Add(next);
            service.Complete(next, byId[next]);
        }

        Assert.Equal(40, service.Added.Count);
        Assert.Empty(queue.Waiting());
    }

    [Fact]
    public async Task R3_RetriesBounded_QueueStaysAvailableForNextEvent()
    {
        // 场景：引擎长时间不可用，快速重试到顶后不弃队——转慢速对账，
        // 引擎在慢速窗口内恢复时，等待任务被兜底拉起（云批 43990094154 实锤：
        // 第三次引擎重启的重提事件未接住，25 个任务标回等待后再无人管）
        var service = new FlakyService { FailTimes = int.MaxValue };
        using var queue = new DownloadQueue(service)
        {
            MaxConcurrent = 3,
            RetryDelay = TimeSpan.FromMilliseconds(20),
            MaxRetryAttempts = 2,
            SlowRetryDelay = TimeSpan.FromMilliseconds(60),
        };

        var task = MakeTask(1);
        await queue.EnqueueAsync(task);
        await Task.Delay(300); // 让快速重试跑满 2 次并转入慢速对账

        Assert.Empty(service.Added); // 引擎一直没就绪：如实未派发
        Assert.Single(queue.Waiting()); // 任务仍在等待队列，不丢

        service.FailTimes = 0; // 引擎在慢速窗口内恢复

        await WaitFor(() => service.Added.Count == 1, timeoutMs: 5000); // 慢速对账兜底拉起
        Assert.Empty(queue.Waiting());
    }

    [Fact]
    public async Task R4_EngineRestartMarksWaiting_SlotsFreedImmediately()
    {
        // 云批 43990094154 病根：引擎重启把活动任务标回等待，但并发名额账本仍记着旧账，
        // 队列认定满员，重提的整队永久派不出去（29 分钟零派发）
        var service = new FlakyService();
        using var queue = new DownloadQueue(service)
        {
            MaxConcurrent = 1,
            RetryDelay = TimeSpan.FromMilliseconds(30),
        };

        var a = MakeTask(1);
        var b = MakeTask(2);
        var c = MakeTask(3);
        await queue.EnqueueAsync(a);
        await queue.EnqueueAsync(b); // b 排队（名额 1 被 a 占）
        await queue.EnqueueAsync(c); // c 排队
        Assert.Equal([a.Id], service.Added);

        // 引擎自愈重启：a 被标回等待 → 名额当场腾出 → b 立刻顶上，不是等没人管
        service.MarkWaiting(a.Id, a);
        await WaitFor(() => service.Added.Count >= 2);
        Assert.Equal([a.Id, b.Id], service.Added);

        service.MarkWaiting(b.Id, b);
        await WaitFor(() => service.Added.Count >= 3);
        Assert.Equal([a.Id, b.Id, c.Id], service.Added);
    }

    [Fact]
    public async Task R5_RestormResubmit_DoesNotDoubleSubmit()
    {
        // 引擎重启整队重提时，同一任务若已提交不得再入列：双份提交=双份下载
        var service = new FlakyService();
        using var queue = new DownloadQueue(service) { MaxConcurrent = 3 };

        var a = MakeTask(1);
        await queue.EnqueueAsync(a);
        await queue.EnqueueAsync(a); // 重提同任务
        await Task.Delay(100);

        Assert.Single(service.Added); // 只提交一次
    }

/// <summary>按链接类型选择性失败的假服务：FTP 提交一直失败，HTTP 正常。</summary>
    private sealed class SelectivelyFailingService : IDownloadService
    {
        public List<string> Added { get; } = [];

        public event EventHandler<DownloadTask>? Updated;

        public event EventHandler<string>? Removed;

        public void RegisterWaiting(DownloadTask task)
        {
        }

        public Task<string> AddAsync(DownloadTask task, CancellationToken ct = default)
        {
            if (task.Kind == LinkKind.Ftp)
            {
                throw new InvalidOperationException("FTP 服务不可用（425 无法打开数据连接）");
            }

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

        public void MarkWaiting(string taskId, DownloadTask task)
        {
            task.Status = DownloadStatus.Waiting;
            Updated?.Invoke(this, task);
        }
    }

    [Fact]
    public async Task R6_HeadOfQueueKeepsFailing_HttpTasksBehindStillDispatched()
    {
        // 2026-11-17 云批 44022149029 实锤：FTP 任务提交失败卡在队头，
        // PumpAsync 的 catch 直接 return 停泵，排在后面的全部 HTTP 任务 24.8 分钟零派发。
        // 现要求：队头任务失败进冷却期挪到队尾，后续 HTTP 任务照常派发。
        var service = new SelectivelyFailingService();
        using var queue = new DownloadQueue(service)
        {
            MaxConcurrent = 3,
            RetryDelay = TimeSpan.FromMilliseconds(30),
        };

        var ftp = new DownloadTask { Url = "ftp://a.com/file.zip", Kind = LinkKind.Ftp };
        var http1 = MakeTask(1);
        var http2 = MakeTask(2);
        var http3 = MakeTask(3);
        await queue.EnqueueAsync(ftp); // 队头 FTP 一直失败
        await queue.EnqueueAsync(http1);
        await queue.EnqueueAsync(http2);
        await queue.EnqueueAsync(http3);

        // 3 个 HTTP 全部到引擎，不被队头堵死；FTP 仍在等待队列轮转冷却
        await WaitFor(() => service.Added.Count == 3, timeoutMs: 5000);
        Assert.Equal([http1.Id, http2.Id, http3.Id], service.Added.Take(3));
        Assert.Contains(ftp.Id, queue.Waiting().Select(t => t.Id)); // FTP 未丢，仍受兜底照管
    }
}