using System.IO;
using System.Text;

namespace TaoDown.Engine;

/// <summary>
/// 刷新链路黑匣子（v2.54 真机排查产物）：真机出现「文件已下完、列表行却一直停在
/// 正在连接服务器」，静态排查无法定位是界面定时器没跑、RPC 查询失败还是循环代码
/// 抛异常——三者都被静默吞掉。此日志把刷新链路的三种可能如实落盘，供真机取证：
/// 心跳（定时器在跑 + 活动任务数）、轮询失败（异常原文，节流）、循环结构异常。
/// </summary>
internal static class RefreshDiag
{
    private static readonly object Gate = new();
    private static long _lastHeartbeatTicks;

    /// <summary>写入一行诊断日志；日志自身绝不抛异常，失败静默。</summary>
    public static void Write(string message)
    {
        try
        {
            var dir = TaoDown.Core.DataRoot.Sub("logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "refresh-diag.log");
            lock (Gate)
            {
                // 单文件上限 1MB，超了直接重开，绝不无限膨胀
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                {
                    File.Delete(path);
                }

                File.AppendAllText(
                    path,
                    $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // 诊断日志失败不影响下载功能
        }
    }

    /// <summary>每轮刷新入口调用；至多每 60 秒落一条心跳，证明定时器与轮询活着。</summary>
    public static void Heartbeat(int activeCount)
    {
        var now = DateTime.UtcNow.Ticks;
        lock (Gate)
        {
            if (now - _lastHeartbeatTicks < TimeSpan.FromSeconds(60).Ticks)
            {
                return;
            }

            _lastHeartbeatTicks = now;
        }

        Write($"心跳：待刷新任务 {activeCount} 个");
    }
}
