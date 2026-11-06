﻿namespace TaoDown.App;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

/// <summary>引导窗结果：知道了（含直接关闭）/ 图文说明。</summary>
public enum GuideWindowResult
{
    Known,
    GuidePage,
}

/// <summary>
/// 首启引导窗（v2.60）：独立顶级窗口、置顶、屏幕居中——不再挂在主窗的对话框宿主里，
/// 主窗被最小化、遮挡或启动期界面抖动都不影响可见性。
/// 返回用户的选择；直接关闭视为「知道了」。
/// </summary>
public sealed class GuideWindow : Window, IThemePaintable
{
    private readonly TaskCompletionSource<GuideWindowResult> _done;
    private readonly StackPanel _root;
    private bool _answered;

    private GuideWindow(string title, string guideText, TaskCompletionSource<GuideWindowResult> done)
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
        var body = new TextBlock
        {
            Text = guideText,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
        };
        var knownButton = new Button { Content = "知道了", MinWidth = 120, Height = 34 };
        var guideButton = new Button { Content = "图文说明", MinWidth = 110, Height = 34 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        buttons.Children.Add(guideButton);
        buttons.Children.Add(knownButton);
        Content = _root = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 20),
            Children = { titleText, body, buttons },
        };

        void Answered(GuideWindowResult result)
        {
            if (_answered)
            {
                return;
            }

            _answered = true;
            _done.TrySetResult(result);
            Close();
        }

        knownButton.Click += (_, _) => Answered(GuideWindowResult.Known);
        guideButton.Click += (_, _) => Answered(GuideWindowResult.GuidePage);
        Closed += (_, _) => Answered(GuideWindowResult.Known);

        // 置顶 + 居中 + 激活：Win32 系统调用，全包版本可用
            WindowPlacement.MakeTopmostCentered(this, 560, 420);
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


    public static Task<GuideWindowResult> ShowAsync(string title, string guideText)
    {
        var done = new TaskCompletionSource<GuideWindowResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = new GuideWindow(title, guideText, done);
        return done.Task;
    }
}
