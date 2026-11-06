using System.Text.RegularExpressions;
using Xunit;

namespace TaoDown.Tests;

/// <summary>主题三象字典一致性（2026-11-01 真机反馈"浅色象与水墨雷同、墨夜脱系"重构后的回归闸；
/// 2026-11-06 浅色象定稿为天青并引入陶瓷釉面渐变，解析器同步支持渐变主体档）：
/// ①三字典禁 ThemeDictionaries 分段（扁平键值）；②键集完全一致（同键同义不同值）；
/// ③亮度阶梯锚点：水墨浅 #FCFBF7 &gt; 天青釉 #EDF2EF &gt; 墨夜黑 #131211；
/// ④层色底定律：LayerFillColorDefaultBrush 三象都在且随象变化。</summary>
public class ThemeDictionaryTests
{
    private static string AppDir()
    {
        var appDir = FindAppDir();
        return appDir;
    }

    private static Dictionary<string, string> KeysOf(string file)
    {
        var src = File.ReadAllText(Path.Combine(AppDir(), "Themes", file));
        Assert.DoesNotContain(".ThemeDictionaries", src);
        var map = new Dictionary<string, string>();
        // 纯色键：SolidColorBrush 直读 Color 属性
        foreach (Match m in Regex.Matches(src, @"x:Key=""([^""]+)""[^>]*Color=""([^""]+)"""))
            map[m.Groups[1].Value] = m.Groups[2].Value;
        // 陶瓷渐变键（天青字典）：LinearGradientBrush 无 Color 属性，取 0.55 主体档为主色
        foreach (Match m in Regex.Matches(src, @"x:Key=""([^""]+)""[^>]*>\s*<GradientStop(.*?)</LinearGradientBrush>", RegexOptions.Singleline))
        {
            var main = Regex.Matches(m.Groups[2].Value, "<GradientStop[^>]*>")
                .Cast<Match>()
                .Where(s => s.Value.Contains("Offset=\"0.55\""))
                .Select(s => Regex.Match(s.Value, "Color=\"(#[0-9A-Fa-f]{6,8})\""))
                .FirstOrDefault(m2 => m2.Success);
            if (main.Success)
                map[m.Groups[1].Value] = main.Groups[1].Value;
        }
        return map;
    }

    /// <summary>
    /// App 工程目录定位（云端适配）：从当前目录与程序目录逐级向上找 src\TaoDown.App，
    /// 兼容本地沙箱、GitHub 云端两种布局；沙箱原路径保留为最后兜底。
    /// </summary>
    private static string FindAppDir()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var d = new DirectoryInfo(start);
            for (var i = 0; i < 10 && d != null; i++, d = d.Parent)
            {
                var cand = Path.Combine(d.FullName, "src", "TaoDown.App");
                if (Directory.Exists(cand)) return cand;
                cand = Path.Combine(d.FullName, "TaoDown.App");
                if (Directory.Exists(cand)) return cand;
            }
        }
        return "/root/workspace/projects/201259/src/TaoDown.App";
    }
    [Fact]
    public void ThreeDictionaries_SameKeySet()
    {
        var ink = KeysOf("InkTheme.xaml");
        var sky = KeysOf("SkyTheme.xaml");
        var dark = KeysOf("TaoDark.xaml");
        Assert.True(ink.Count >= 30, $"水墨键数不足: {ink.Count}");
        Assert.Equal(ink.Keys.OrderBy(k => k), sky.Keys.OrderBy(k => k));
        Assert.Equal(ink.Keys.OrderBy(k => k), dark.Keys.OrderBy(k => k));
    }

    [Fact]
    public void LadderAnchors_EachThemeDistinct()
    {
        Assert.Equal("#FCFBF7", KeysOf("InkTheme.xaml")["ApplicationPageBackgroundThemeBrush"]); // v2.49 对齐 Tao 系列母本近白宣底
        Assert.Equal("#EDF2EF", KeysOf("SkyTheme.xaml")["ApplicationPageBackgroundThemeBrush"]); // 天青釉底（2026-11-06 用户定稿）
        Assert.Equal("#131211", KeysOf("TaoDark.xaml")["ApplicationPageBackgroundThemeBrush"]);
    }

    [Fact]
    public void LayerFill_PresentAndVaries()
    {
        var ink = KeysOf("InkTheme.xaml")["LayerFillColorDefaultBrush"];
        var sky = KeysOf("SkyTheme.xaml")["LayerFillColorDefaultBrush"];
        var dark = KeysOf("TaoDark.xaml")["LayerFillColorDefaultBrush"];
        Assert.NotEqual(ink, sky);
        Assert.NotEqual(ink, dark);
        Assert.NotEqual(sky, dark);
    }

    [Fact]
    public void Pages_ConsumeThemeBrushes()
    {
        var files = new[] { "MainWindow.xaml", "Pages/DownloadsPage.xaml", "Pages/SettingsPage.xaml" };
        foreach (var f in files)
        {
            var src = File.ReadAllText(Path.Combine(AppDir(), f));
            Assert.True(
                src.Contains("ThemeResource ApplicationPageBackgroundThemeBrush")
                || src.Contains("ThemeResource LayerFillColorDefaultBrush"),
                $"{f} 未消费主题笔刷");
        }
    }

    [Fact]
    public void ThemeService_MapsThreeThemesAndMotherTwoBeat()
    {
        // 沙箱不编译 App 工程: 源码级断言 (对齐 AppReferenceCoherenceTests 兜底思路)
        // v2.49: 舞步对齐 TaoPad v76 母本「先归默认再落目标」。v2.46 曾禁归零，依据是
        // 当时重绘依赖 ActualThemeChanged 事件（归零后两次赋值合并成无变化→事件不触发→混合态）；
        // v2.47 起重绘改为 Apply 显式调用 PaintTheme（两分支都直涂），不再赌事件，
        // 归零舞步的失败前提已消除。归零再落 + 显式直涂 + 缓存控件强制重估 = 母本三件套。
        var src = File.ReadAllText(Path.Combine(AppDir(), "ThemeService.cs"));
        Assert.Contains("\"SkyTheme\"", src);
        Assert.Contains("\"TaoDark\"", src);
        Assert.Contains("\"InkTheme\"", src);
        Assert.Contains("root.RequestedTheme = ElementTheme.Default", src); // 归零拍（母本 v76）
        Assert.Contains("paintable.PaintTheme()", src);                     // 显式直涂，不赌事件
        Assert.DoesNotContain(".ThemeDictionaries", src);
    }
}
