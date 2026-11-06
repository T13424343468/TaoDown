using System.Text.RegularExpressions;
using Xunit;

namespace TaoDown.Tests;

/// <summary>A1 App 工程引用自洽（2026-11-01 TaoPad 真机 CS0103 避坑，全系同型）：
/// 沙箱不编译 WinUI App 工程，界面侧编译错误只有真机能看见——源码层兜底：
/// App 工程所有 .cs 对 ThemeService 的静态成员引用必须在 ThemeService.cs 有定义。</summary>
public class AppReferenceCoherenceTests
{
    [Fact]
    public void ThemeService_MemberReferences_AllHaveDefinitions()
    {
        var appDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "TaoDown.App");
        if (!Directory.Exists(appDir))
            appDir = "/root/workspace/projects/201259/src/TaoDown.App";
        Assert.True(Directory.Exists(appDir), $"App 工程目录不存在：{appDir}");

        var defs = File.ReadAllText(Path.Combine(appDir, "ThemeService.cs"));
        var orphans = new List<string>();
        foreach (var f in Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.EndsWith("ThemeService.cs")))
        {
            var src = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(src, @"ThemeService\.(\w+)"))
                if (!Regex.IsMatch(defs, @"\b" + m.Groups[1].Value + @"\b"))
                    orphans.Add($"{Path.GetFileName(f)}:{m.Groups[1].Value}");
        }
        Assert.True(orphans.Count == 0, $"孤儿引用 {string.Join(",", orphans)}");
    }

    [Fact]
    public void BareDataRootUsage_MustHaveCoreUsing()
    {
        // 2026-11-02 真机 CS0103 教训: 重写文件漏 using TaoDown.Core, 裸用 DataRoot 编译失败。
        // 沙箱兜底: App 工程 .cs 凡出现裸 DataRoot., 必须显式 using TaoDown.Core。
        var appDir = Path.Combine(Directory.GetCurrentDirectory(), "..", "TaoDown.App");
        if (!Directory.Exists(appDir))
            appDir = "/root/workspace/projects/201259/src/TaoDown.App";
        Assert.True(Directory.Exists(appDir), $"App 工程目录不存在：{appDir}");
        var bad = new List<string>();
        foreach (var f in Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var src = File.ReadAllText(f);
            var bare = Regex.IsMatch(src, @"(?<![\w.])DataRoot\.");
            var hasUsing = src.Contains("using TaoDown.Core;");
            if (bare && !hasUsing) bad.Add(Path.GetFileName(f));
        }
        Assert.True(bad.Count == 0, $"裸用 DataRoot 缺 using TaoDown.Core: {string.Join(",", bad)}");
    }
}
