using System.IO;
using TaoDown.Core;

namespace TaoDown.App;

/// <summary>启动与运行日志：落盘 %LOCALAPPDATA%\TaoDown\logs，用于无窗口静默失败的定位。</summary>
internal static class StartupLog
{
    private static readonly object Gate = new();

    public static string LogDir { get; } = DataRoot.Sub("logs");

    public static string LogFile => Path.Combine(LogDir, "startup.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            lock (Gate)
            {
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    public static void Write(string message, Exception ex)
        => Write($"{message}: {ex.GetType().Name} {ex.Message}\r\n{ex.StackTrace}");
}
