namespace TaoDown.Core;

/// <summary>
/// 链接查重（修红 run 44061254563 M16 实锤：重复粘贴同一链接会建出多个真实下载任务，
/// 每个都真实走引擎下载，完成态弹多次）。同一 URL 且任务未失败（非 Error）即视为已存在，
/// 调用方复用既有任务、不再重复添加；失败的旧任务允许重试，不挡新任务。
/// </summary>
public static class DuplicateLinkGuard
{
    /// <summary>在任务快照里找同 URL 的既有任务；找到返回该任务，找不到返回 null。</summary>
    public static DownloadTask? FindDuplicate(IReadOnlyList<DownloadTask> tasks, string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        DownloadTask? best = null;
        foreach (var t in tasks)
        {
            if (t.Status == DownloadStatus.Error)
            {
                continue; // 失败任务不算占用，允许重试
            }

            if (!string.Equals(t.Url?.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 等待/活动中的任务优先于终态（用户大概率在等它，贴上去直接看到同一个任务）
            if (t.Status is DownloadStatus.Waiting or DownloadStatus.Active or DownloadStatus.Paused)
            {
                return t;
            }

            best ??= t;
        }

        return best;
    }
}
