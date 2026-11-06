using System.Text.Json;

namespace TaoDown.Core;

/// <summary>
/// 外部唤起载荷：点 magnet/ed2k/迅雷链接或双击 .torrent 文件时，
/// 系统会把目标作为命令行参数交给 TaoDown。本类负责从命令行里识别
/// 可下载的链接与种子文件，供主实例与二次启动转发共用。
/// </summary>
public sealed class LaunchPayload
{
    public List<string> Links { get; } = [];

    public List<string> TorrentFiles { get; } = [];

    public bool HasAny => Links.Count > 0 || TorrentFiles.Count > 0;
}

public static class LaunchArgs
{
    private static readonly string[] TorrentExts = [".torrent"];

    /// <summary>
    /// 从命令行参数中解析可下载内容。argv[0] 是程序自身路径，跳过；
    /// .torrent 文件（存在性由 fileExists 校验）进种子清单，其余按链接分类识别。
    /// </summary>
    public static LaunchPayload Parse(string[] argv, Func<string, bool>? fileExists = null)
    {
        var payload = new LaunchPayload();
        for (var i = 1; i < argv.Length; i++)
        {
            var arg = (argv[i] ?? string.Empty).Trim().Trim('"');
            if (arg.Length == 0 || arg.Length > 2048)
            {
                continue;
            }

            if (TorrentExts.Any(e => arg.EndsWith(e, StringComparison.OrdinalIgnoreCase)) &&
                (fileExists is null || fileExists(arg)))
            {
                payload.TorrentFiles.Add(arg);
                continue;
            }

            var link = LinkTextScanner.FindFirst(arg) ?? LooseLinkParser.TryNormalize(arg);
            if (link is not null && LinkClassifier.Classify(link) != LinkKind.Unknown &&
                !payload.Links.Contains(link))
            {
                payload.Links.Add(link);
            }
        }

        return payload;
    }
}

/// <summary>
/// 二次启动转发通道：新进程把要下载的内容写入收件文件再通知主实例，
/// 主实例读取后入队并清理。写文件+信号两步，避免跨进程内存同步。
/// </summary>
public static class LaunchForward
{
    /// <summary>收件文件路径（可传目录便于测试；默认走应用数据根目录）。</summary>
    public static string FilePath(string? dir = null)
    {
        return Path.Combine(dir ?? DataRoot.Root(), "launch-forward.txt");
    }

    public static void Write(LaunchPayload payload, string? dir = null)
    {
        var lines = new List<string>();
        lines.AddRange(payload.Links.Select(l => "L\t" + l));
        lines.AddRange(payload.TorrentFiles.Select(f => "T\t" + f));
        if (lines.Count == 0)
        {
            return;
        }

        var path = FilePath(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>读取并清空收件文件；无内容或解析失败返回空载荷。</summary>
    public static LaunchPayload TryRead(string? dir = null)
    {
        var payload = new LaunchPayload();
        try
        {
            var path = FilePath(dir);
            if (!File.Exists(path))
            {
                return payload;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                if (parts[0] == "L" && parts[1].Length > 0)
                {
                    payload.Links.Add(parts[1]);
                }
                else if (parts[0] == "T" && parts[1].Length > 0)
                {
                    payload.TorrentFiles.Add(parts[1]);
                }
            }
        }
        catch (Exception)
        {
            // 收件文件损坏：当作空处理，不阻断主实例
        }

        Clear(dir);
        return payload;
    }

    public static void Clear(string? dir = null)
    {
        try
        {
            File.Delete(FilePath(dir));
        }
        catch (Exception)
        {
            // 删除失败留给下次覆盖写，不影响功能
        }
    }
}

/// <summary>
/// 链接协议接管：注册 magnet/ed2k/thunder/flashget/qqdl 协议与 .torrent
/// 文件关联到 HKCU（当前用户生效，无需管理员权限），装完点链接即唤起 TaoDown。
/// 全部写出/删除计划均为纯数据，便于测试核验；Apply 仅在 Windows 执行。
/// </summary>
public static class ProtocolRegistrar
{
    /// <summary>接管的 URL 协议（点这些链接时由 TaoDown 打开）。</summary>
    public static readonly string[] Protocols = ["magnet", "ed2k", "thunder", "flashget", "qqdl"];

    private const string TorrentProgId = "TaoDown.Torrent";

    /// <summary>协议接管计划（键, 值名, 值值）。名称为空表示写键默认值。</summary>
    public static IReadOnlyList<(string Key, string Name, string Value)> PlannedWrites(string exePath)
    {
        var command = "\"" + exePath + "\" \"%1\"";
        var icon = "\"" + exePath + "\",0";
        var writes = new List<(string, string, string)>();
        foreach (var p in Protocols)
        {
            var root = @"Software\Classes\" + p;
            writes.Add((root, "", "URL:TaoDown 下载"));
            writes.Add((root, "URL Protocol", ""));
            writes.Add((root + @"\shell\open\command", "", command));
            writes.Add((root + @"\DefaultIcon", "", icon));
        }

        writes.Add((@"Software\Classes\.torrent", "", TorrentProgId));
        writes.Add((@"Software\Classes\" + TorrentProgId, "", "BT 种子文件"));
        writes.Add((@"Software\Classes\" + TorrentProgId + @"\shell\open\command", "", command));
        writes.Add((@"Software\Classes\" + TorrentProgId + @"\DefaultIcon", "", icon));
        return writes;
    }

    /// <summary>取消接管时需要删除的键（整键删除，恢复系统默认行为）。</summary>
    public static IReadOnlyList<string> PlannedRemovals()
    {
        var keys = Protocols.Select(p => @"Software\Classes\" + p).ToList();
        keys.Add(@"Software\Classes\.torrent");
        keys.Add(@"Software\Classes\" + TorrentProgId);
        return keys;
    }

    /// <summary>开机自启计划（Run 键值名与值）。</summary>
    public static (string Key, string Name, string Value) PlannedAutoStart(string exePath)
    {
        return (@"Software\Microsoft\Windows\CurrentVersion\Run", "TaoDown", "\"" + exePath + "\"");
    }

    /// <summary>写出协议关联（Windows 专属；其他平台静默跳过）。</summary>
    public static void Apply(string exePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var (key, name, value) in PlannedWrites(exePath))
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key, writable: true);
            if (name.Length == 0)
            {
                k.SetValue(null, value, Microsoft.Win32.RegistryValueKind.String);
            }
            else
            {
                k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
            }
        }
    }

    /// <summary>取消协议关联（尽力而为；失败不影响主功能）。</summary>
    public static void Remove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var key in PlannedRemovals())
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
                // 键不存在或被占用：跳过即可
            }
        }
    }

    /// <summary>设置/取消开机自启（Windows 专属；其他平台静默跳过）。</summary>
    public static void ApplyAutoStart(bool enabled, string exePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (key, name, value) = PlannedAutoStart(exePath);
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key, writable: true);
        if (enabled)
        {
            k.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
        }
        else
        {
            try
            {
                k.DeleteValue(name, throwOnMissingValue: false);
            }
            catch (Exception)
            {
                // 删除失败不影响
            }
        }
    }
}
