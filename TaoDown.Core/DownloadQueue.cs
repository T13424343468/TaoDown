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
                    // 提交失败：释放名额并放回等待队列，等下次 Pump 重试
                    lock (_gate)
                    {
                        _activeIds.Remove(next.Task.Id);
                        _waiting.Add(next);
                    }

                    throw;
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
