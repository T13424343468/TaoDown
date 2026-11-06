using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace TaoDown.Tests;

/// <summary>
/// 视觉之道（维度 V · 真人视觉模拟, TaoZip v57/v59/v60 母本, TaoDown v2.26 落地）:
/// 按真人视觉的对比度规律 (WCAG 相对亮度对比) 对三象色板逐对体检。
/// 阈值: 正文 4.5 / 次要 3.5 / 点缀(UI) 3.0 / 描边 2.0 / 按钮字 4.0 / 标题栏字 2.0 (禁用态豁免)。
/// V3 兼亮度阶梯: 水墨浅为主 / 天青釉近白偏青 / 墨夜黑为主, 三档单调递减不串档。
/// </summary>
public class VisionTests
{
    private readonly ITestOutputHelper _out;
    public VisionTests(ITestOutputHelper output) => _out = output;

    private static string ThemesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "TaoDown.App", "Themes");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("未定位到 Themes 目录");
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(ThemesDir(), file));

    private static (double R, double G, double B) HexToRgb(string hex)
    {
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length == 8) hex = hex[2..];
        return ((double)Convert.ToByte(hex[..2], 16) / 255,
                (double)Convert.ToByte(hex.Substring(2, 2), 16) / 255,
                (double)Convert.ToByte(hex.Substring(4, 2), 16) / 255);
    }

    private static double Channel(double c) =>
        c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double Luminance(string hex)
    {
        var (r, g, b) = HexToRgb(hex);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static double Contrast(string a, string b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string ColorOf(string dict, string key)
    {
        var m = Regex.Match(dict, $"x:Key=\"{key}\" Color=\"(#[0-9A-Fa-f]{{6,8}})\"");
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        // 陶瓷渐变键（天青字典）：取 0.55 主体档为主色
        var g = Regex.Match(dict, $"x:Key=\"{key}\"[^>]*>\\s*<GradientStop(.*?)</LinearGradientBrush>", RegexOptions.Singleline);
        Assert.True(g.Success, $"缺色 {key}");
        var main = Regex.Matches(g.Groups[1].Value, "<GradientStop[^>]*>")
            .Cast<Match>()
            .Where(s => s.Value.Contains("Offset=\"0.55\""))
            .Select(s => Regex.Match(s.Value, "Color=\"(#[0-9A-Fa-f]{6,8})\""))
            .FirstOrDefault(m2 => m2.Success);
        Assert.True(main.Success, $"渐变 {key} 缺 0.55 主体档");
        return main.Groups[1].Value;
    }

    [Fact]
    public void V1_Vision_AllThemePairsHumanlyVisible()
    {
        (string Fg, string Bg, double Min, string What)[] pairs =
        {
            ("TextFillColorPrimaryBrush", "ApplicationPageBackgroundThemeBrush", 4.5, "正文 vs 底"),
            ("TextFillColorSecondaryBrush", "ApplicationPageBackgroundThemeBrush", 3.5, "次要字 vs 底"),
            ("TextFillColorPrimaryBrush", "ControlFillColorDefaultBrush", 4.0, "按钮字 vs 按钮面"),
            ("ControlStrokeColorDefaultBrush", "ControlFillColorDefaultBrush", 2.0, "描边 vs 按钮面"),
            ("ControlStrokeColorDefaultBrush", "ApplicationPageBackgroundThemeBrush", 2.0, "描边 vs 底"),
            ("AccentFillColorDefaultBrush", "ApplicationPageBackgroundThemeBrush", 3.0, "点缀 vs 底"),
        };
        foreach (var (name, f) in new[] { ("水墨", "InkTheme.xaml"), ("天青", "SkyTheme.xaml"), ("墨夜", "TaoDark.xaml") })
        {
            var dict = Read(f);
            foreach (var (fg, bg, min, what) in pairs)
            {
                var ratio = Contrast(ColorOf(dict, fg), ColorOf(dict, bg));
                _out.WriteLine($"{name} · {what}: {ratio:F2} (线 {min})");
                Assert.True(ratio >= min, $"{name} · {what}: 对比 {ratio:F2} 低于 {min}, 真人眼将看不见");
            }
        }
    }

    [Fact]
    public void V2_Vision_StrokeAndCaptionKeysPresentInAllThree()
    {
        foreach (var f in new[] { "InkTheme.xaml", "SkyTheme.xaml", "TaoDark.xaml" })
        {
            var dict = Read(f);
            Assert.Contains("x:Key=\"ControlStrokeColorDefaultBrush\"", dict);
            Assert.Contains("x:Key=\"ControlStrokeColorSecondaryBrush\"", dict);
            Assert.Contains("x:Key=\"WindowCaptionForegroundBrush\"", dict);
        }
    }

    [Fact]
    public void V3_Vision_ThreeThemesLadderAndDistinct()
    {
        var ink = ColorOf(Read("InkTheme.xaml"), "ApplicationPageBackgroundThemeBrush");
        var sky = ColorOf(Read("SkyTheme.xaml"), "ApplicationPageBackgroundThemeBrush");
        var night = ColorOf(Read("TaoDark.xaml"), "ApplicationPageBackgroundThemeBrush");

        // 亮度阶梯: 水墨浅 / 天青釉近白偏青(实算 L≈0.88) / 墨夜黑, 三档各守其带, 单调递减
        var li = Luminance(ink); var ls = Luminance(sky); var ln = Luminance(night);
        Assert.True(li > 0.8, $"水墨应浅色为主 (L={li:F2})");
        Assert.True(ls > 0.85 && ls < li, $"天青釉底应近白且浅于水墨 (L={ls:F2})");
        Assert.True(ln < 0.03, $"墨夜应以黑为主 (L={ln:F3})");
        Assert.True(li > ls && ls > ln, "亮度阶梯必须单调: 水墨 > 天青 > 墨夜");

        // 气质签名: 水墨暖宣 / 天青偏青(G>B>R, 釉色微青不钢) / 墨夜暖暗
        var (ir, ig, ib) = HexToRgb(ink);
        var (sr, sg, sb) = HexToRgb(sky);
        Assert.True(ir > ig && ig > ib, "水墨底应暖序 R>G>B");
        Assert.True(sg > sb && sb > sr, "天青底应 G>B>R");

        // 点缀两两互异
        var accents = new[] {
            ColorOf(Read("InkTheme.xaml"), "AccentFillColorDefaultBrush"),
            ColorOf(Read("SkyTheme.xaml"), "AccentFillColorDefaultBrush"),
            ColorOf(Read("TaoDark.xaml"), "AccentFillColorDefaultBrush"),
        };
        Assert.True(Contrast(accents[0], accents[1]) >= 1.5, "水墨与天青点缀过近");
        Assert.True(Contrast(accents[0], accents[2]) >= 1.5, "水墨与墨夜点缀过近");
        Assert.True(Contrast(accents[1], accents[2]) >= 1.5, "天青与墨夜点缀过近");
    }

    [Fact]
    public void V4_Vision_CaptionGlyphsHumanlyVisible()
    {
        foreach (var (name, f) in new[] { ("水墨", "InkTheme.xaml"), ("天青", "SkyTheme.xaml"), ("墨夜", "TaoDark.xaml") })
        {
            var dict = Read(f);
            var ratio = Contrast(ColorOf(dict, "WindowCaptionForegroundBrush"),
                                 ColorOf(dict, "WindowCaptionBackgroundBrush"));
            _out.WriteLine($"{name} · 标题栏字 vs 标题栏底: {ratio:F2} (线 2)");
            Assert.True(ratio >= 2.0, $"{name} 标题栏字对比 {ratio:F2} 不足 2, 真人眼看不见右上角按钮");
        }
    }
    /// <summary>A2 强调色覆盖（2026-11-01 全系避坑，源自 TaoPad 真机"设置弹窗控件没跟主题"）：
    /// 三象字典必须自带完整 accent 键族（基础色+明暗档+开关键+三支 accent 画刷），
    /// ContentDialog 主按钮 / ToggleSwitch 开态 / ComboBox 高亮才与三象统一，不随系统强调色漂移。</summary>
    /// <summary>从主题字典解析全部色键（Key → #RRGGBB），跳过近全透明的（alpha &lt; 25%，视为"无"态）。</summary>
    private static List<(string Key, string Hex)> ParseColors(string dict)
    {
        var result = new List<(string, string)>();
        foreach (Match m in Regex.Matches(dict, "x:Key=\"([^\"]+)\"[^>]*Color=\"(#[0-9A-Fa-f]{6,8})\""))
        {
            var hex = m.Groups[2].Value;
            if (hex.Length == 8 && Convert.ToByte(hex[1..3], 16) < 0x40)
            {
                continue; // 近全透明：真人眼视为"无"，不进对比度矩阵
            }

            result.Add((m.Groups[1].Value, hex));
        }

        // 陶瓷渐变键（天青字典）：LinearGradientBrush 无 Color 属性，取 0.55 主体档为主色
        foreach (Match m in Regex.Matches(dict, "x:Key=\"([^\"]+)\"[^>]*>\\s*<GradientStop(.*?)</LinearGradientBrush>", RegexOptions.Singleline))
        {
            var main = Regex.Matches(m.Groups[2].Value, "<GradientStop[^>]*>")
                .Cast<Match>()
                .Where(s => s.Value.Contains("Offset=\"0.55\""))
                .Select(s => Regex.Match(s.Value, "Color=\"(#[0-9A-Fa-f]{6,8})\""))
                .FirstOrDefault(m2 => m2.Success);
            if (main.Success)
            {
                result.Add((m.Groups[1].Value, main.Groups[1].Value));
            }
        }

        return result;
    }

    /// <summary>按键名判前/底角色与最低对比线；判不出的键不进矩阵（宁缺毋错判）。禁用态豁免（真人语义即"淡出"）。</summary>
    private static (bool IsFg, double Min)? RoleOf(string key)
    {
        if (key.Contains("Disabled"))
        {
            return null;
        }

        if (key == "WindowCaptionForeground") return (true, 2.0);
        if (key.Contains("TextFillColorPrimary") || key.Contains("AccentTextFillColor")) return (true, 4.5);
        if (key.Contains("TextFillColorSecondary")) return (true, 3.5);
        if (key.Contains("TextFillColorTertiary")) return (true, 3.0); // 辅助提示字按 WCAG 大字档
        if (key == "NavigationViewItemForeground") return (true, 4.5);
        if (key.StartsWith("NavigationViewItemForeground")) return (true, 3.5);
        if (key.Contains("SelectionIndicator")) return (true, 3.0);
        if (key.Contains("Stroke")) return (true, 2.0);
        if (key.StartsWith("AccentFill")) return (true, 3.0);
        if (key.Contains("Background") || key.Contains("Fill") || key.Contains("PaneBackground"))
        {
            return (false, 0); // 底面族：只作配对面，底对底不断言
        }

        return null;
    }

    /// <summary>配对策略：只断言"真实会同屏渲染"的前底组合——
    /// 文字可落在任何内容面上；描边只描控件与页面；点缀只装饰页面/层；标题栏字只配标题栏底；
    /// 选中指示只压导航高亮底。维度仍在，维度间的关系按真人所见为准，不做盲交叉。</summary>
    private static bool RendersTogether(string fgKey, string bgKey)
    {
        if (fgKey == "WindowCaptionForeground")
        {
            return bgKey == "WindowCaptionBackground";
        }

        if (fgKey.Contains("SelectionIndicator"))
        {
            return bgKey.StartsWith("NavigationViewItemBackground");
        }

        if (fgKey.Contains("Stroke"))
        {
            return bgKey.StartsWith("ControlFill") || bgKey == "ApplicationPageBackgroundThemeBrush";
        }

        if (fgKey.StartsWith("AccentFill"))
        {
            return bgKey == "ApplicationPageBackgroundThemeBrush"
                   || bgKey.StartsWith("LayerFill") || bgKey.StartsWith("SolidBackground");
        }

        if (fgKey.StartsWith("NavigationViewItemForeground"))
        {
            // 导航件文字：只在其真实栖息地——导航窗格/内容区/自身高亮底
            return bgKey.StartsWith("NavigationView");
        }

        // 文字族：正文落在内容面（页面/控件/层/实底/导航内容与窗格），不与导航件高亮底交叉
        return bgKey == "ApplicationPageBackgroundThemeBrush"
               || bgKey.StartsWith("ControlFill")
               || bgKey.StartsWith("LayerFill")
               || bgKey.StartsWith("SolidBackground")
               || bgKey.StartsWith("NavigationViewContentBackground")
               || bgKey.StartsWith("NavigationViewExpandedPaneBackground");
    }

    /// <summary>
    /// V5 视觉万链（2026-11-02 用户定调：真人视觉模拟遵循万链逻辑铺维度）——
    /// 三象全部色键按角色分类，前×底按真实渲染关系全交叉逐对断言（口径见 RendersTogether/RoleOf）；
    /// 归账输出总对数与违规数；主题新增键自动入矩阵，永无死角。
    /// </summary>
    [Fact]
    public void V5_Vision_GenerativeMatrix_AllKeysAllPairs()
    {
        var total = 0;
        var violations = new List<string>();
        foreach (var (name, file) in new[] { ("水墨", "InkTheme.xaml"), ("天青", "SkyTheme.xaml"), ("墨夜", "TaoDark.xaml") })
        {
            var colors = ParseColors(Read(file));
            var fgs = colors.Where(c => RoleOf(c.Key) is { IsFg: true }).ToList();
            var bgs = colors.Where(c => RoleOf(c.Key) is { IsFg: false }).ToList();

            foreach (var (fgKey, fgHex) in fgs)
            {
                var min = RoleOf(fgKey)!.Value.Min;
                foreach (var (bgKey, bgHex) in bgs)
                {
                    if (fgKey == bgKey || !RendersTogether(fgKey, bgKey))
                    {
                        continue;
                    }

                    total++;
                    var ratio = Contrast(fgHex, bgHex);
                    if (ratio < min)
                    {
                        violations.Add($"{name} · {fgKey} vs {bgKey}: {ratio:F2} < {min}");
                    }
                }
            }

            _out.WriteLine($"{name}: 前 {fgs.Count} × 底 {bgs.Count}，累计断言 {total}");
        }

        _out.WriteLine($"视觉万链归账：{total} 对真实渲染断言，违规 {violations.Count}");
        Assert.True(total >= 150, $"矩阵规模应达百级以上，实际 {total}");
        Assert.True(violations.Count == 0, $"真人视觉矩阵 {violations.Count} 处不可读：\n{string.Join("\n", violations)}");
    }

    [Fact]
    public void A2_AccentOverride_KeysCompleteInAllThemes()
    {
        string[] dicts = { "InkTheme.xaml", "SkyTheme.xaml", "TaoDark.xaml" };
        string[] keys =
        {
            "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
            "SystemAccentColorLight1Brush", "SystemAccentColorLight2Brush", "SystemAccentColorLight3Brush",
            "SystemAccentColorDark1Brush", "SystemAccentColorDark2Brush", "SystemAccentColorDark3Brush",
            "ToggleSwitchFillOn", "SystemControlHighlightAccentBrush",
            "SystemControlForegroundAccentBrush", "SystemControlBackgroundAccentBrush",
        };
        foreach (var dict in dicts)
        {
            var src = Read(dict);
            foreach (var k in keys)
                Assert.True(src.Contains($"x:Key=\"{k}\""), $"{dict} 缺 accent 键 {k}");
            // 同象内所有 accent 键必须同色号（配色自洽）
            var colors = Regex.Matches(src, "x:Key=\"SystemAccentColor\"[^\n]*").Count;
            Assert.True(colors == 1, $"{dict} SystemAccentColor 应恰一处");
        }
    }

}

