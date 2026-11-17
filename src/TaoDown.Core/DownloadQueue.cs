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
    }

    /// <summary>入队；priority 越大越先开始，startAt 晚于当前时间则定时开始。</summary>
    public async Task EnqueueAsync(DownloadTask task, int priority = 0, DateTime? startAt = null, CancellationToken ct = default)
    {
        // 等待任务先落盘：此前等待队列纯内存，进程被杀任务全灭（2026-11-17 云批实锤 0/40）
        _service.RegisterWaiting(task);
        lock (_gate)
        {
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
                catch
                {
                    // 提交失败：释放名额并放回等待队列，退避重泵直至引擎接受
                    //（2026-11-17 云批实锤：只重试一次撞上引擎重启窗口即永久静默——
                    // 18:54 重启后 26 分钟零派发，40 任务 30 分钟仅 1/40 到终态）
                    lock (_gate)
                    {
                        _activeIds.Remove(next.Task.Id);
                        _waiting.Add(next);
                    }

                    ScheduleRetryPump(ct);

                    // 引擎暂不可用：本轮停泵，等待重泵；不外抛，避免连环失败刷屏
                    return;
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

    /// <summary>提交失败后连续重泵的次数上限；到顶后保留等待队列，等下一次入队/事件/引擎重启重触发。</summary>
    public int MaxRetryAttempts { get; set; } = 36;

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
            while (true)
            {
                try
                {
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_gate)
                {
                    var due = _waiting.Any(e => e.StartAt is null || e.StartAt <= _clock());
                    if (!due)
                    {
                        return; // 等待队列已清空（或只剩未到时的定时任务）：无需再泵
                    }
                }

                await PumpAsync(ct).ConfigureAwait(false);

                lock (_gate)
                {
                    var stillDue = _waiting.Any(e => e.StartAt is null || e.StartAt <= _clock());
                    if (!stillDue)
                    {
                        return; // 全部派发成功，收链
                    }

                    _retryAttempts++;
                    if (_retryAttempts >= Math.Max(1, MaxRetryAttempts))
                    {
                        return; // 到顶：不无限循环；下一次入队/事件/引擎自愈重提会重新拉起
                    }
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
            if (task.Status is DownloadStatus.Complete or DownloadStatus.Error or DownloadStatus.Paused)
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

    public void Dispose()
    {
        _service.Updated -= OnServiceUpdated;
        _service.Removed -= OnServiceRemoved;
    }
}
