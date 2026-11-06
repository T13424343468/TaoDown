namespace TaoDown.Core;

/// <summary>容量/速度/剩余时间格式化，界面与测试共用。</summary>
public static class Format
{
    /// <summary>字节数 → 可读大小（B/KB/MB/GB），0 显示为 0 B。</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        return bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
            >= 1024L * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }

    /// <summary>字节/秒 → 可读速度（B/s/KB/s/MB/s），0 显示为空。</summary>
    public static string Speed(long bytesPerSec)
    {
        if (bytesPerSec <= 0)
        {
            return string.Empty;
        }

        return bytesPerSec switch
        {
            >= 1024 * 1024 => $"{bytesPerSec / 1024.0 / 1024:F1} MB/s",
            >= 1024 => $"{bytesPerSec / 1024.0:F0} KB/s",
            _ => $"{bytesPerSec} B/s",
        };
    }

    /// <summary>剩余字节与速度 → 「剩余 21 秒」；无法估算返回空。</summary>
    public static string Eta(long remainingBytes, long bytesPerSec)
    {
        if (bytesPerSec <= 0 || remainingBytes <= 0)
        {
            return string.Empty;
        }

        var seconds = remainingBytes / bytesPerSec;
        if (seconds >= 3600)
        {
            var h = seconds / 3600;
            var m = seconds % 3600 / 60;
            return m > 0 ? $"剩余 {h} 小时 {m} 分" : $"剩余 {h} 小时";
        }

        if (seconds >= 60)
        {
            var m = seconds / 60;
            var s = seconds % 60;
            return s > 0 ? $"剩余 {m} 分 {s} 秒" : $"剩余 {m} 分";
        }

        return $"剩余 {seconds} 秒";
    }

    /// <summary>任务汇总行：下载中/已暂停/已完成与可选的总速度。</summary>
    public static string Summary(int active, int paused, int done, long totalSpeed)
    {
        var text = $"下载中 {active} · 已暂停 {paused} · 已完成 {done}";
        if (totalSpeed > 0)
        {
            text += $" · 总速度 {Speed(totalSpeed)}";
        }

        return text;
    }

    /// <summary>托盘悬停提示：随任务状态实时变化。</summary>
    public static string TrayTip(int active, int paused) => (active, paused) switch
    {
        (> 0, _) => $"TaoDown — 下载中 {active}",
        (0, > 0) => $"TaoDown — 已暂停 {paused}，可继续",
        _ => "TaoDown — 没有进行中的下载",
    };
}
