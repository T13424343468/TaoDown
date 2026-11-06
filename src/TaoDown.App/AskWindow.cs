namespace TaoDown.App;

using System.Collections.Concurrent;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

/// <summary>
/// 顶级询问窗（v2.59）：独立窗口、置顶、屏幕居中，保证主窗被最小化或遮挡时用户也看得见。
/// 浏览器接管与剪贴板确认共用。选「立即下载」返回 true，关闭或「暂不」返回 false。
/// </summary>
public sealed class AskWindow : Window, IThemePaintable
{
    private readonly TaskCompletionSource<bool> _done;
    private readonly StackPanel _root;
    private bool _answered;

    // 并发询问窗级联计数：多个下载确认同时弹出时依次错开，不叠在正中同一点
    private static int _active;

    private AskWindow(string title, string detail, TaskCompletionSource<bool> done)
    {
        _done = done;
        Title = title;

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var detailText = new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 18),
        };
        var okButton = new Button { Content = "立即下载", MinWidth = 120, Height = 34 };
        var cancelButton = new Button { Content = "暂不", MinWidth = 88, Height = 34 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        Content = _root = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Children = { titleText, detailText, buttons },
        };

        void Answered(bool result)
        {
            if (_answered)
            {
                return;
            }

            _answered = true;
            _done.TrySetResult(result);
            Close();
        }

        okButton.Click += (_, _) => Answered(true);
        cancelButton.Click += (_, _) => Answered(false);
        Closed += (_, _) => Answered(false);

        // 置顶 + 居中 + 激活：Win32 系统调用，全包版本可用。
        // 第 N 个并发窗向右下错开 N×36px（最多 6 档回绕），每扇窗都看得见、点得到。
        var step = (Interlocked.Increment(ref _active) - 1) % 6 * 36;
        WindowPlacement.MakeTopmostCentered(this, 520, 260, step, step);
        Closed += (_, _) => Interlocked.Decrement(ref _active);
        // v2.62：子窗随三象——弹出即落定当前主题基调与底色，墨夜不再是白色调
        ThemeService.ApplyToPopup(this, App.Settings?.ThemeId ?? 0);
        Activate();
    }

    /// <summary>换肤回调：窗口底按当前主题字典直涂。</summary>
    public void PaintTheme()
    {
        try
        {
            if (ThemeService.Brush("ApplicationPageBackgroundThemeBrush") is { } bg)
            {
                _root.Background = bg;
            }
        }
        catch
        {
            // 外观增强，失败不阻断
        }
    }


    // 同链接未决窗登记（2026-11-03 用户定案）：同一链接已有未决询问窗时，永远只弹那一个——
    // 重复点击合并掉；现有窗做出选择或关闭后再次点击才弹新窗；不同链接每次都弹新窗。
    private static readonly ConcurrentDictionary<string, AskWindow> Pending = new();

    /// <summary>弹出询问窗。url 非空时做同链接合并：已有未决窗则把现有窗带到前台，本次请求合并（返回 false）。</summary>
    public static async Task<bool> ShowAsync(string title, string detail, string? url = null)
    {
        var key = PendingKey(url);
        if (key is not null && Pending.TryGetValue(key, out var existing))
        {
            try
            {
                existing.Activate(); // 用户又点了一次：把未决的询问窗带到前台，不弹新窗
            }
            catch
            {
                // 现有窗恰好刚关，忽略
            }

            return false;
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var win = new AskWindow(title, detail, done);
        if (key is not null && !Pending.TryAdd(key, win))
        {
            // 竞态：同刻另一扇同链接窗抢先登记——关掉本窗，交给先到的那扇
            done.TrySetResult(false);
            return false;
        }

        try
        {
            return await done.Task;
        }
        finally
        {
            if (key is not null)
            {
                Pending.TryRemove(new KeyValuePair<string, AskWindow>(key, win));
            }
        }
    }

    /// <summary>同链接判定：去空白与尾斜杠、去掉锚点、忽略大小写。</summary>
    private static string? PendingKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var u = url.Trim();
        var frag = u.IndexOf('#');
        if (frag >= 0)
        {
            u = u[..frag];
        }

        return u.TrimEnd('/').ToLowerInvariant();
    }
}
