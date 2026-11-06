using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TaoDown.Core;

namespace TaoDown.App;

/// <summary>可被主题服务整体涂色的窗口契约：换肤后由 ThemeService 回调重涂大面。</summary>
public interface IThemePaintable
{
    /// <summary>按当前主题字典把关键大面（窗口底/导航/内容框架）直接涂色。</summary>
    void PaintTheme();
}

/// <summary>
/// 页面级重涂契约：换肤后由 MainWindow.PaintTheme 回调，
/// 把页面根面板底色按当前主题字典直涂，不赌 ThemeResource 重估时机。
/// </summary>
public interface IThemeRepaintable
{
    /// <summary>按当前主题字典把页面根面板底色直涂。</summary>
    void RepaintTheme();
}

/// <summary>
/// 主题服务（Tao 系列三象，挂载方式对齐 TaoZip 真机验证过的母本）：
/// 0=水墨（近白宣底 #FCFBF7，黛青点缀；v2.49 对齐 Tao 系列母本去黄提白），1=天青（天青釉底 #EDF2EF，青瓷点缀；2026-11-06 用户定稿对齐 TaoAgent 天青色板，卡片/控件面带陶瓷釉面渐变），2=墨夜（暖炭近黑 #131211，宣纸白字）。
/// 字典扁平键值（禁 ThemeDictionaries 分段），三字典同键同义不同值，互斥只挂当象一个。
/// 换肤 = 摘净三套字典 + 把当象字典追加到 XamlControlsResources 之后 + 基调两拍（Pad v76 母本：先归默认再落目标）+ 大面代码直涂 + 缓存控件强制重估。
/// v2.47 定案（真机截图坐实根因）：WinUI 合并字典取值「后挂者覆盖先挂者」，
/// 主题字典必须排在 XamlControlsResources 之后才生效；旧版 Insert(0) 把字典插在最前被整体覆盖，
/// 界面退回框架默认色，墨夜即白底白字混合态。直涂取值同源：Brush() 优先从当象字典取，
/// 保证代码直涂与 XAML ThemeResource 解析一致，不再各取各的。
/// 换肤入口唯一 ThemeService.Apply。
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;

    /// <summary>当前已挂字典名（幂等守卫用）：同字典 + 基调已落定时跳过舞步，只重涂大面。</summary>
    private static string? _currentDictName;

    /// <summary>
    /// 快切并档定时器（2026-11-02 真机：6 秒连切 8 次，部分控件吃第一拍漏第二拍出混合态）。
    /// 150ms 内的连击只落定最后一次，中间态一次都不执行；字段持有防 GC 回收
    /// （v2.46 刷新时钟被回收的同型教训，时钟必须挂在字段上）。
    /// </summary>
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _coalesceTimer;

    /// <summary>并档期间的待落定目标（窗口 + 主题），由最后一次 Apply 覆盖。</summary>
    private static Window? _pendingWindow;
    private static int _pendingThemeId;

    /// <summary>基调映射（唯一真相）：水墨/天青浅色，墨夜深色。</summary>
    internal static ElementTheme BaseFor(int themeId) =>
        themeId == 2 ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>字典映射（唯一真相）：主题 → 字典文件名（与 Themes/*.xaml 同名同源）。</summary>
    internal static string DictFor(int themeId) =>
        themeId == 1 ? "SkyTheme" : themeId == 2 ? "TaoDark" : "InkTheme";

    /// <summary>
    /// 按键取当前主题笔刷（大面直涂用）：优先从当象字典取，与 XAML ThemeResource 同一来源；
    /// 再回退应用级。返回基类 Brush——天青象的卡片/控件面是陶瓷釉面渐变（LinearGradientBrush），
    /// 收窄成 SolidColorBrush 会把渐变键漏成 null。缺键返回 null 不炸。
    /// </summary>
    public static Brush? Brush(string key)
    {
        if (_current is not null && _current[key] is Brush direct)
        {
            return direct;
        }

        return Application.Current.Resources[key] as Brush;
    }

    /// <summary>给单个控件涂主题色（Page/Frame 等控件态用）；null 安全。</summary>
    public static void PaintControl(Microsoft.UI.Xaml.Controls.Control? control, string key = "LayerFillColorDefaultBrush")
    {
        if (control is null)
        {
            return;
        }

        try
        {
            if (Brush(key) is { } brush)
            {
                control.Background = brush;
            }
        }
        catch (Exception)
        {
            // 外观增强，失败不阻断
        }
    }

    /// <summary>对窗口应用主题：摘净三套字典 + 挂当象 + 母本两拍基调 + 大面直涂（三象归一，万流不漏）。</summary>
    public static void Apply(Window? window, int themeId)
    {
        if (window is null)
        {
            return;
        }

        // 快切并档：150ms 内的连击只落定最后一次，杜绝中间混合态（无论多快都不能串）。
        var queue = window.DispatcherQueue;
        if (queue is null)
        {
            ApplyNow(window, themeId);
            return;
        }

        if (_coalesceTimer is null)
        {
            _coalesceTimer = queue.CreateTimer();
            _coalesceTimer.Interval = TimeSpan.FromMilliseconds(150);
            _coalesceTimer.IsRepeating = false;
            _coalesceTimer.Tick += (_, _) =>
            {
                _coalesceTimer.Stop();
                var w = _pendingWindow;
                var id = _pendingThemeId;
                _pendingWindow = null;
                ApplyNow(w, id);
            };
        }

        _pendingWindow = window;
        _pendingThemeId = themeId;
        _coalesceTimer.Stop();
        _coalesceTimer.Start();
    }

    /// <summary>
    /// 给临时子窗立即落定主题（v2.62）：不走并档拍子——弹窗即开即用，不能等 150ms 的白闪。
    /// 基调两拍 + 大面直涂 + 标题栏钉色与主窗同一套舞步，保证子窗与三象一致（真机反馈：
    /// 墨夜下子窗仍白色调）。
    /// </summary>
    public static void ApplyToPopup(Window window, int themeId) => ApplyNow(window, themeId);

    /// <summary>真正的落定舞步：摘净字典 + 挂当象 + 基调两拍 + 大面直涂。由并档定时器唯一调用。</summary>
    private static void ApplyNow(Window? window, int themeId)
    {
        try
        {
            if (window is null)
            {
                return; // 窗口已关或未挂：无事可做（空引用警告归零，行为与原捕获路径一致）
            }

            var dictName = DictFor(themeId);
            // 幂等守卫: 同字典已挂且基调已落定时不再跑舞步，只把大面重新直涂
            // （重复进设置页/重复 Apply 不再制造过渡基调，杜绝混合态复发路径）。
            var alreadySettled = _currentDictName == dictName &&
                window.Content is FrameworkElement settledRoot && settledRoot.ActualTheme == BaseFor(themeId);
            Swap(dictName);
            if (alreadySettled)
            {
                if (window is IThemePaintable settled)
                {
                    settled.PaintTheme();
                }

                return;
            }

            var requested = BaseFor(themeId);
            if (window.Content is FrameworkElement root)
            {
                var before = root.ActualTheme;
                // 两拍基调（Pad 母本 v76 定案）：先归默认再落目标。归零到 Default 让已生成
                // 控件的内部模板按系统基调全量重取，再落目标值；两次赋值在同一调用栈内完成，
                // 中间值不经历渲染帧。v2.49 根因修复：旧“先反再落”在切回浅色象时会先落回
                // 深色基调，NavigationView 这类内部缓存配色的控件会趁机把深色值缓存住，
                // 切回水墨后窗格残留暗色（真机截图坐实：内容区已浅、导航窗格仍黑）。
                try
                {
                    if (root.ActualTheme == requested || root.RequestedTheme == requested)
                    {
                        root.RequestedTheme = ElementTheme.Default;
                    }
                }
                catch (Exception)
                {
                    // 归零失败不影响后续设置
                }

                root.RequestedTheme = requested;

                LogApply(themeId, before, root.ActualTheme);
                PinTitleBar(window, root);
            }

            // 大面直涂: 不依赖 ThemeResource 重估时机, 切换必上屏 (Pad 母本手法)
            if (window is IThemePaintable paintable)
            {
                paintable.PaintTheme();
            }
        }
        catch (Exception)
        {
            // 主题属于外观增强，任何异常都不影响主功能
        }
    }

    /// <summary>
    /// 摘净三套字典再「追加」当象字典（v2.47 对齐 TaoZip 母本 RemoveTheme + Add）：
    /// WinUI 合并字典后挂者覆盖先挂者，主题字典必须排在 XamlControlsResources 之后才生效；
    /// 旧版 Insert(0) 插在最前被整体覆盖，界面退回框架默认色（墨夜白底白字混合态根因）。
    /// 摘净不靠记忆——凡主题字典一律摘；加载失败回退水墨。
    /// </summary>
    private static void Swap(string dictName)
    {
        var res = Application.Current.Resources;
        for (var i = res.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var src = res.MergedDictionaries[i].Source?.OriginalString ?? "";
            if (src.Contains("InkTheme") || src.Contains("SkyTheme") || src.Contains("TaoDark"))
            {
                res.MergedDictionaries.RemoveAt(i);
            }
        }

        ResourceDictionary? next = null;
        try
        {
            next = new ResourceDictionary { Source = new Uri($"ms-appx:///Themes/{dictName}.xaml") };
        }
        catch (Exception)
        {
            // 加载失败回退水墨
        }

        if (next is null && dictName != "InkTheme")
        {
            try
            {
                next = new ResourceDictionary { Source = new Uri("ms-appx:///Themes/InkTheme.xaml") };
            }
            catch (Exception)
            {
                // 回退也失败则放弃换肤
            }
        }

        if (next is null)
        {
            _current = null;
            _currentDictName = null;
            return;
        }

        res.MergedDictionaries.Add(next); // 追加到末尾：后挂者覆盖先挂者，主题值才压得住框架字典
        _current = next;
        var loaded = System.IO.Path.GetFileNameWithoutExtension(next.Source?.OriginalString ?? "");
        _currentDictName = loaded is "InkTheme" or "SkyTheme" or "TaoDark" ? loaded : dictName; // 只在成功挂载后按实际加载记名
    }

    /// <summary>
    /// 标题栏随象而染（TaoZip v60 母本）: 标题栏与右上角三颗小按钮
    /// （最小化/最大化/关闭）颜色跟系统走——墨夜近黑底上深色门钮即隐身。
    /// 按当前主题字典里的 WindowCaption 笔刷给系统标题栏钉全套色。
    /// </summary>
    private static void PinTitleBar(Window window, FrameworkElement root)
    {
        try
        {
            var appWindow = window.AppWindow;
            if (appWindow?.TitleBar is not { } titleBar) return;
            // TaoZip v79 定案: 主题字典挂在 Application.Current.Resources,
            // root.Resources 直接索引不做应用级回退 → 永远取不到 → 标题栏从未钉色,
            // 深色主题下窗控按钮隐身。改从应用级资源取。
            var appRes = Application.Current.Resources;
            if (appRes["WindowCaptionForegroundBrush"] is not SolidColorBrush glyph) return;
            if (appRes["WindowCaptionBackgroundBrush"] is not SolidColorBrush capBg) return;
            titleBar.BackgroundColor = capBg.Color;
            titleBar.ForegroundColor = glyph.Color;
            titleBar.ButtonBackgroundColor = capBg.Color;
            titleBar.ButtonForegroundColor = glyph.Color;
            // TaoZip v80 定案: 悬停/按下底色原样钉 capBg, 墨夜下与常态毫无差别, 肉眼看不出悬停反馈。
            // 改为向前景色调和: 悬停 12% 轻提亮, 按下 20% 更明显。
            titleBar.ButtonHoverBackgroundColor = Blend(capBg.Color, glyph.Color, 0.12f);
            titleBar.ButtonHoverForegroundColor = glyph.Color;
            titleBar.ButtonPressedBackgroundColor = Blend(capBg.Color, glyph.Color, 0.20f);
            titleBar.ButtonPressedForegroundColor = glyph.Color;
        }
        catch (Exception)
        {
            // 标题栏着色属于外观增强, 失败静默
        }
    }

    /// <summary>颜色按比例向目标调和 (t=0 取 a, t=1 取 b), 供窗控悬停/按下态生成可辨底色。</summary>
    private static Windows.UI.Color Blend(Windows.UI.Color a, Windows.UI.Color b, float t) =>
        Windows.UI.Color.FromArgb(a.A,
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// 换肤自证日志: 每次 Apply 追加一行 (目标主题/切换前后实际基调/当前合并字典)。
    /// 真机若再现"切换没效果", 日志可直接定位是重估没触发还是字典没挂上。外观项, 写失败静默。
    /// </summary>
    private static void LogApply(int themeId, ElementTheme before, ElementTheme after)
    {
        try
        {
            var dicts = string.Join(",", Application.Current.Resources.MergedDictionaries
                .Select(d => System.IO.Path.GetFileNameWithoutExtension(d.Source?.OriginalString ?? ""))
                .Where(n => !string.IsNullOrEmpty(n)));
            var dir = DataRoot.Sub("logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "theme.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} theme={themeId} base {before}→{after} dicts=[{dicts}]\r\n");
        }
        catch (Exception)
        {
            // 日志失败不影响功能
        }
    }
}
