using System.Diagnostics;

namespace TaoDown.Core;

/// <summary>
/// 全部应用数据统一存放根目录（长期要求：Tao 系列存储默认 D 盘，不占 C 盘系统空间）。
/// 首选 D:\TaoDownData\TaoDown；D 盘不可用（无 D 盘/只读）时回退
/// %LOCALAPPDATA%\TaoDown，绝不抛异常。老版本数据在 C 盘时自动整目录搬过来。
/// </summary>
public static class DataRoot
{
    /// <summary>首选数据根（D 盘）。</summary>
    public const string PreferredRoot = @"D:\TaoDownData\TaoDown";

    private static string? _cached;

    /// <summary>取当前生效的数据根目录；永不到达"无目录"状态（需要时创建）。</summary>
    public static string Root()
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        var fallback = LegacyRoot;
        string chosen;
        try
        {
            Directory.CreateDirectory(PreferredRoot);
            chosen = PreferredRoot;
            MigrateLegacy(fallback, chosen);
        }
        catch (Exception)
        {
            // D 盘不可用：回退老位置，功能不受影响
            try
            {
                Directory.CreateDirectory(fallback);
            }
            catch (Exception)
            {
                // 极端环境兜底：退回当前目录，保证程序不死
                chosen = Path.Combine(AppContext.BaseDirectory, "data");
                _cached = chosen;
                return chosen;
            }

            chosen = fallback;
        }

        _cached = chosen;
        return chosen;
    }

    /// <summary>数据根下的子目录（自动创建；失败不抛，返回原路径由调用方再兜底）。</summary>
    public static string Sub(params string[] segments)
    {
        var path = Path.Combine(new[] { Root() }.Concat(segments).ToArray());
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception)
        {
            // 调用方按需再兜底
        }

        return path;
    }

    /// <summary>老版本数据位置（C 盘用户目录），迁移源。</summary>
    public static string LegacyRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaoDown");

    /// <summary>首次切到 D 盘时把老数据整体搬过来（目标为空才搬；单文件失败不阻断）。</summary>
    public static void MigrateLegacy(string legacyRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(legacyRoot))
            {
                return;
            }

            // 目标已有真实内容（非刚创建）就不重复搬，避免覆盖新数据；目标不存在视为空
            var hasContent = Directory.Exists(newRoot) && Directory.EnumerateFileSystemEntries(newRoot).Any();
            if (hasContent)
            {
                return;
            }

            CopyTree(legacyRoot, newRoot);
        }
        catch (Exception)
        {
            // 搬家失败不阻断启动，继续用新目录
        }
    }

    /// <summary>递归复制目录内容。</summary>
    public static void CopyTree(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("theme.log", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 日志不搬，避免撑爆判断
            }

            File.Copy(file, Path.Combine(targetDir, name), overwrite: false);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            CopyTree(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }
}
