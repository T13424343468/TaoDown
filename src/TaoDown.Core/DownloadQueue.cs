namespace TaoDown.Core;

/// <summary>
/// 下载队列：并发上限、优先级、定时开始。
/// 提交前先占用并发名额（占坑），避免并发提交时超限；
/// 任务进入 Complete/Error/Paused 释放名额并自动放行下一个等待任务。
/// </summary>
public sealed class DownloadQueue : IDisposable
{
    private sealed class Entry
    {
        public DownloadTask Task { get; init; } = null!;

        public int Priority { get; init; }

        public DateTime? StartAt { get; init; }
    }

    private readonly IDownloadService _service;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly List<Entry> _waiting = [];
    private readonly HashSet<string> _activeIds = [];

    private bool _pumping;

    /// <summary>同时下载任务数上限。</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>任务被提交到引擎（开始下载）时触发。</summary>
    public event EventHandler<DownloadTask>? Submitted;

    public DownloadQueue(IDownloadService service, Func<DateTime>? clock = null)
    {
        _service = service;
        _clock = clock ?? (() => DateTime.Now);
        _service.Updated += OnServiceUpdated;
        _service.Removed += OnServiceRemoved;
        _ = ReconcileLoopAsync();
    }

    /// <summary>入队；priority 越大越先开始，startAt 晚于当前时间则定时开始。</summary>
    public async Task EnqueueAsync(DownloadTask task, int priority = 0, DateTime? startAt = null, CancellationToken ct = default)
    {
        // 等待任务先落盘：此前等待队列纯内存，进程被杀任务全灭（2026-11-17 云批实锤 0/40）
        _service.RegisterWaiting(task);
        lock (_gate)
        {
            // 去重：引擎自愈重启会整队重提，同一任务双份入列会双份下载（占坑表和等待队列都查）
            if (_waiting.Any(e => e.Task.Id == task.Id) || _activeIds.Contains(task.Id))
            {
                return;
            }

            _waiting.Add(new Entry { Task = task, Priority = priority, StartAt = startAt });
        }

        await PumpAsync(ct);
    }

    /// <summary>从等待队列移除（尚未提交引擎的任务）。</summary>
    public bool Dequeue(string taskId)
    {
        lock (_gate)
        {
            return _waiting.RemoveAll(e => e.Task.Id == taskId) > 0;
        }
    }

    /// <summary>等待中的任务（按优先级排序的快照）。</summary>
    public IReadOnlyList<DownloadTask> Waiting()
    {
        lock (_gate)
        {
            return [.. _waiting
                .OrderByDescending(e => e.Priority)
                .ThenBy(e => e.Task.CreatedAt)
                .Select(e => e.Task)];
        }
    }

    /// <summary>放行已到时间的等待任务，直至占满并发额度。</summary>
    public async Task PumpAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_pumping)
            {
                return;
            }

            _pumping = true;
        }

        try
        {
            while (true)
            {
                Entry? next;
                lock (_gate)
                {
                    if (_activeIds.Count >= Math.Max(1, MaxConcurrent))
                    {
                        return;
                    }

                    next = _waiting
                        .Where(e => e.StartAt is null || e.StartAt <= _clock())
                        .OrderByDescending(e => e.Priority)
                        .ThenBy(e => e.Task.CreatedAt)
                        .FirstOrDefault();
                    if (next is null)
                    {
                        return;
                    }

                    _waiting.Remove(next);
                    _activeIds.Add(next.Task.Id);
                }

                try
                {
                    await _service.AddAsync(next.Task, ct);
                }
                catch (OperationCanceledException)
                {
                    // 提交被打断（停机）：任务放回队尾等待，退出本轮派发
                    lock (_gate)
                    {
                        _activeIds.Remove(next.Task.Id);
                        _waiting.Add(next);
                    }
                    return;
                }
                catch
                {
                    // 提交失败：名额当场释放，任务带冷却期挪到队尾，本轮继续派发后续任务。
                    // 此前是放回队头 + return 停泵：队头任务一直失败会把整条队列堵死
                    //（2026-11-17 云批 44022149029 实锤：FTP 任务卡住队头，24.8 分钟零 addUri，
                    // 排在其后的全部 HTTP 任务永不被派发）。
                    lock (_gate)
                    {
                        _activeIds.Remove(next.Task.Id);
                        _waiting.Add(new Entry
                        {
                            Task = next.Task,
                            Priority = next.Priority,
                            StartAt = _clock() + RetryDelay,
                        });
                    }

                    ScheduleRetryPump(ct);

                    // 冷却期由重泵/对账链兜底重试；不外抛，避免连环失败刷屏
                    continue;
                }

                Submitted?.Invoke(this, next.Task);
            }
        }
        finally
        {
            lock (_gate)
            {
                _pumping = false;
            }
        }
    }

    /// <summary>提交失败后的重泵间隔：给引擎自愈/源恢复留窗口。</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>提交失败后连续重泵的次数上限；到顶后转入慢速对账（云批 43990094154 实锤：
    /// 引擎第三次自愈重启时重提事件未接住，25 个任务被标回等待后再无人管）。</summary>
    public int MaxRetryAttempts { get; set; } = 36;

    /// <summary>快速重试到顶后的慢速对账间隔：只要等待队列还有任务，慢速兜底重泵直到排空。</summary>
    public TimeSpan SlowRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _lifetimeCts = new();

    private bool _retryScheduled;

    private int _retryAttempts;

    /// <summary>单飞重泵：失败链只有一条，期间的新失败不叠加新链。</summary>
    private void ScheduleRetryPump(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_retryScheduled)
            {
                return;
            }

            _retryScheduled = true;
            _retryAttempts = 0;
        }

        _ = RetryPumpLoopAsync(ct);
    }

    private async Task RetryPumpLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                var delay = _retryAttempts < Math.Max(1, MaxRetryAttempts) ? RetryDelay : SlowRetryDelay;
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_waiting.Count == 0)
                    {
                        return; // 等待队列已清空：无需再泵
                    }
                }

                await PumpAsync(ct).ConfigureAwait(false);

                lock (_gate)
                {
                    if (_waiting.Count == 0)
                    {
                        return; // 全部派发成功，收链
                    }

                    _retryAttempts++;
                }

                // 快速重试有界；到顶不弃队，转慢速对账兜底直到排空或对象释放
                if (_retryAttempts >= Math.Max(1, MaxRetryAttempts) && SlowRetryDelay <= TimeSpan.Zero)
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // 重泵链内异常不外抛：等待队列仍在，后续事件会再触发
        }
        finally
        {
            lock (_gate)
            {
                _retryScheduled = false;
                _retryAttempts = 0;
            }
        }
    }

    private void OnServiceUpdated(object? sender, DownloadTask task)
    {
        var slotFreed = false;
        lock (_gate)
        {
            // 引擎自愈重启会把活动任务标回等待：名额必须当场腾出，
            // 否则队列按旧账认定满员，重提的整队永久派不出去（云批 43990094154 第三次重启后 29 分钟零派发实锤）
            if (task.Status is DownloadStatus.Complete or DownloadStatus.Error or DownloadStatus.Paused
                or DownloadStatus.Waiting)
            {
                slotFreed = _activeIds.Remove(task.Id);
            }
        }

        if (slotFreed)
        {
            _ = PumpAsync();
        }
    }

    private void OnServiceRemoved(object? sender, string taskId)
    {
        lock (_gate)
        {
            _activeIds.Remove(taskId);
            // 排队中（未提交引擎）的任务被移除时也要出队，防止僵尸任务占位
            _waiting.RemoveAll(e => e.Task.Id == taskId);
        }

        _ = PumpAsync();
    }

    /// <summary>对账扫描间隔：等待队列有活且名额有空时的兜底重泵周期（事件漏接的最终保险）。</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(60);

    private async Task ReconcileLoopAsync()
    {
        while (!_lifetimeCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReconcileInterval, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool due;
            lock (_gate)
            {
                due = _activeIds.Count < Math.Max(1, MaxConcurrent)
                    && _waiting.Any(e => e.StartAt is null || e.StartAt <= _clock());
            }

            if (due)
            {
                _ = PumpAsync();
            }
        }
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        _service.Updated -= OnServiceUpdated;
        _service.Removed -= OnServiceRemoved;
    }
}
