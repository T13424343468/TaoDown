namespace TaoDown.Core;

/// <summary>下载服务抽象，界面层只依赖此接口。</summary>
public interface IDownloadService
{
    /// <summary>新增下载任务，返回任务 Id。</summary>
    Task<string> AddAsync(DownloadTask task, CancellationToken ct = default);

    /// <summary>
    /// 等待队列登记：任务尚未提交引擎时就进列表并落盘（状态 Waiting）。
    /// 2026-11-17 云批实锤：等待队列此前纯内存，进程被杀 40 个排队任务全灭，
    /// 快照里没有它们，恢复恒为 0，重启后 0/40 永不下载。
    /// </summary>
    void RegisterWaiting(DownloadTask task);

    Task PauseAsync(string taskId, CancellationToken ct = default);

    Task ResumeAsync(string taskId, CancellationToken ct = default);

    Task RemoveAsync(string taskId, CancellationToken ct = default);

    /// <summary>暂停所有进行中的任务，返回处理数。</summary>
    Task<int> PauseAllAsync(CancellationToken ct = default);

    /// <summary>继续所有已暂停的任务，返回处理数。</summary>
    Task<int> ResumeAllAsync(CancellationToken ct = default);

    /// <summary>从列表清理已完成（含出错）的任务，返回清理数。</summary>
    Task<int> ClearFinishedAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DownloadTask>> ListAsync(CancellationToken ct = default);

    /// <summary>任务进度或状态变化时触发。</summary>
    event EventHandler<DownloadTask>? Updated;

    /// <summary>任务被移除时触发（参数为任务 Id），用于释放队列并发名额。</summary>
    event EventHandler<string>? Removed;
}
