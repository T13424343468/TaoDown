using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using TaoDown.Core;

namespace TaoDown.App;

/// <summary>主窗口：左侧导航（下载/设置）与剪贴板监视。
/// 换肤大面直涂（Pad 母本手法）：窗口底/导航/内容框架由 ThemeService 回调重涂，不赌 ThemeResource 重估时机。</summary>
public sealed partial class MainWindow : Window, IThemePaintable
{
    private bool _checkingClipboard;
    private TrayIcon? _tray;

    /// <summary>换肤回调：窗口底、导航、内容框架三面按当前主题字典直涂。</summary>
    public void PaintTheme()
    {
        try
        {
            if (ThemeService.Brush("ApplicationPageBackgroundThemeBrush") is { } bg)
            {
                RootGrid.Background = bg;
                Nav.Background = bg;
            }

            // v2.46: 导航窗格/内容区底色走 NavigationView 内部 ThemeResource，
            // Nav.Background 对窗格无效——把导航相关键从应用级主题字典抄进 Nav.Resources，
            // 元素级资源优先于应用级，窗格与条目配色直接钉死，不赌重估时机（真机混合态根治）。
            PinNavBrushes();
            // v2.49: 钉完新色后强制导航整体重估基调（Pad 母本手法：先归默认再落目标）。
            // NavigationView 内部会缓存窗格/条目配色，字典交换后不强制归零，
            // 回切水墨时窗格残留深色（真机截图坐实：内容区已浅、导航窗格仍黑）。
            RefreshNavTheme();
            // v2.51: 模板内 RootSplitView 窗格底色直涂（残留根因与修法见 PinSplitViewPane 注释）
            PinSplitViewPane();
            ThemeService.PaintControl(PageFrame);

            // 当前页跟着重涂：下载页/设置页根面板底色立即跟上（IThemeRepaintable 契约）
            if (PageFrame.Content is IThemeRepaintable page)
            {
                page.RepaintTheme();
            }
        }
        catch
        {
            // 外观增强, 失败不阻断
        }
    }

    /// <summary>把导航相关主题笔刷从应用级字典抄进 Nav.Resources（元素级优先，直接生效）。</summary>
    private void PinNavBrushes()
    {
        try
        {
            // v2.47: 统一走 ThemeService.Brush（优先当象字典），直涂与 XAML 解析同源不串色
            // v2.49: 取不到键时删掉旧钉值回落应用级字典——元素级钉值只反映当前象，
            // 绝不把上一象的颜色钉死在导航上（回切残留暗色的兜底路径）。
            foreach (var key in NavBrushKeys)
            {
                if (ThemeService.Brush(key) is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                {
                    Nav.Resources[key] = brush;
                }
                else
                {
                    Nav.Resources.Remove(key);
                }
            }
        }
        catch
        {
            // 外观增强, 失败不阻断
        }
    }

    /// <summary>导航控件基调强制重估：先归默认再落目标，内部模板全量重取配色（母本 v76 手法）。</summary>
    private void RefreshNavTheme()
    {
        try
        {
            var target = ThemeService.BaseFor(App.Settings.ThemeId);
            Nav.RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Default;
            Nav.RequestedTheme = target;
        }
        catch
        {
            // 外观增强, 失败不阻断
        }
    }

    /// <summary>v2.51: NavigationView 模板内 RootSplitView.PaneBackground 由模板 VisualState 以
    /// ThemeResource 赋值（generic.xaml 实据），元素级钉键 + 基调两拍在真机来回切换后仍被模板
    /// 缓存值压过（切天青后窗格残留水墨米白）。直接遍历视觉树找 RootSplitView，把窗格底色
    /// 按当象字典直涂——控件本地值优先于模板与状态设置，钉一次即生效。PinNavBrushes 与
    /// RefreshNavTheme 兜底保留。SplitView 尚未挂树时取不到，由 Nav.Loaded 再补一次。</summary>
    private Microsoft.UI.Xaml.Controls.SplitView? FindRootSplitView(Microsoft.UI.Xaml.DependencyObject root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.SplitView hit)
            {
                return hit;
            }

            if (FindRootSplitView(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>把模板内 RootSplitView 的窗格底色按当象字典钉死（v2.51 窗格残留根治）。</summary>
    private void PinSplitViewPane()
    {
        try
        {
            if (FindRootSplitView(Nav) is not { } split)
            {
                return;
            }

            if (ThemeService.Brush("NavigationViewExpandedPaneBackground")
                is Microsoft.UI.Xaml.Media.SolidColorBrush pane)
            {
                split.PaneBackground = pane;
            }
        }
        catch
        {
            // 外观增强, 失败不阻断
        }
    }

    /// <summary>导航控件内部模板消费的主题资源键（无 Brush 后缀变体为模板实际引用名）。</summary>
    private static readonly string[] NavBrushKeys =
    [
        "NavigationViewExpandedPaneBackground",
        "NavigationViewContentBackground",
        "NavigationViewItemForeground",
        "NavigationViewItemForegroundPointerOver",
        "NavigationViewItemForegroundPressed",
        "NavigationViewItemForegroundDisabled",
        "NavigationViewItemForegroundSelected",
        "NavigationViewItemBackgroundPointerOver",
        "NavigationViewItemBackgroundPressed",
        "NavigationViewItemBackgroundSelected",
        "NavigationViewSelectionIndicatorForeground",
    ];

    // v2.54 真机根修（20:33 用户日志三证合一定位）：刷新时钟原来是 ctor 局部变量，
    // WinRT 计时器无托管引用即被 GC 回收，轮询无声停摆（refresh-diag 心跳 20:26:07 后绝迹，
    // 而进程与 RPC 依旧活着）——「文件已下完、卡片永远连接中」的间歇性卡死即此。常驻字段钉住引用。
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer = null!;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"TaoDown {typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "dev"}";
        // WinUI3 的窗口尺寸用 AppWindow 设置（XAML 无 Width/Height 属性）
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1040, 700));
        try
        {
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "TaoDown.ico"));
        }
        catch
        {
            // 图标缺失不影响运行
        }
        var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            Math.Max(0, (workArea.Width - 1040) / 2 + workArea.X),
            Math.Max(0, (workArea.Height - 700) / 2 + workArea.Y)));
        Nav.SelectedItem = Nav.MenuItems[0];
        PageFrame.Navigate(typeof(DownloadsPage));
        PaintTheme();
        // v2.51: 首次 PaintTheme 时模板树可能尚未生成，挂载完成后补钉一次窗格底色
        Nav.Loaded += (_, _) => PinSplitViewPane();

        // 托盘与关闭拦截（关闭时可询问：最小化到托盘 / 直接退出，可记住选择）
        AppWindow.Closing += OnAppWindowClosing;
        try
        {
            _tray = new TrayIcon(
                System.IO.Path.Combine(AppContext.BaseDirectory, "TaoDown.ico"),
                "TaoDown — 下载在后台继续",
                WinRT.Interop.WindowNative.GetWindowHandle(this),
                RestoreFromTray,
                () => _ = SafeTrayAsync(() => App.Downloads!.PauseAllAsync()),
                () => _ = SafeTrayAsync(() => App.Downloads!.ResumeAllAsync()),
                () => _ = ExitAppAsync());
        }
        catch (Exception)
        {
            _tray = null; // 托盘不可用不影响主功能
        }

        App.Downloads!.Updated += OnTaskUpdated;

        _refreshTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        var timer = _refreshTimer;
        timer.Interval = TimeSpan.FromMilliseconds(800);
        var refreshing = false;
        timer.Tick += async (_, _) =>
        {
            if (refreshing)
            {
                return; // 上一轮未完成时不叠加执行
            }

            refreshing = true;
            try
            {
                _refreshRounds++;
                // 刷新心跳：约每分钟落一条，证明轮询循环活着（真机排查「页面不更新」）
                if (_refreshRounds % 60 == 1)
                {
                    StartupLog.Write($"刷新心跳正常（第 {_refreshRounds} 轮）");
                }

                await App.Downloads!.RefreshAsync();
                // v2.54：引擎进程崩溃后不再无声挂起——连续失败达到阈值就拉起新引擎并恢复队列
                if (App.Downloads.MainRpcFailStreak >= 5 || App.Downloads.FtpRpcFailStreak >= 5)
                {
                    await App.ReviveEngineAsync();
                }

                await UpdateTrayTipAsync();
            }
            catch (Exception)
            {
                // 单轮刷新失败不致命，下轮重试；防止异常穿到界面层
            }
            finally
            {
                refreshing = false;
            }
        };
        timer.Start();

        Clipboard.ContentChanged += OnClipboardChanged;
    }

    /// <summary>刷新循环轮数（心跳日志用）。</summary>
    private long _refreshRounds;

    /// <summary>浏览器接管等场景：跳转到下载页查看新任务（批次五）。</summary>
    public void ShowDownloads()
    {
        Nav.SelectedItem = Nav.MenuItems[0];
        if (PageFrame.Content?.GetType() != typeof(DownloadsPage))
        {
            PageFrame.Navigate(typeof(DownloadsPage));
        }
    }

    /// <summary>首次启动：检测已装浏览器并引导加载插件（一次性，独立顶级窗）。</summary>
    public async Task ShowBrowserGuideAsync()
    {
        if (!BrowserGuide.ShouldShow())
        {
            return;
        }

        try
        {
            var browsers = BrowserGuide.DetectInstalled();
            var guideText = BrowserGuide.BuildGuideText(browsers);
            StartupLog.Write("首启引导窗唤起（独立置顶窗）");
            var guideChoice = await GuideWindow.ShowAsync("浏览器下载接管已就绪", guideText);
            BrowserGuide.MarkShown();
            if (guideChoice == GuideWindowResult.GuidePage)
            {
                StartupLog.Write("首启引导：用户选择图文说明");
                var guide = System.IO.Path.Combine(AppContext.BaseDirectory, "BROWSER-EXTENSION-GUIDE.html");
                if (System.IO.File.Exists(guide))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = guide,
                        UseShellExecute = true,
                    });
                }
            }
            else
            {
                StartupLog.Write("首启引导：用户确认结束");
            }
        }
        catch (Exception ex)
        {
            // 引导失败不阻断主功能；失败不落标记，下次启动再试一次；留痕便于定位
            StartupLog.Write("首启引导窗唤起失败(不阻断)", ex);
        }
    }

    /// <summary>关闭拦截：按设置决定最小化到托盘、直接退出或每次询问。</summary>
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        var behavior = App.Settings?.CloseBehavior ?? 0;
        if (behavior == 2)
        {
            // 直接退出：取消关窗走统一清理，防止进程与托盘残留
            args.Cancel = true;
            _ = ExitAppAsync();
            return;
        }

        args.Cancel = true;
        if (behavior == 1)
        {
            MinimizeToTray();
            return;
        }

        _ = ShowCloseDialogAsync();
    }

    /// <summary>统一退出：清托盘与监听 → 停引擎子进程 → 强制结束进程（幂等）。
    /// 不用 Application.Exit：它对桌面应用不可靠，可能留下无窗口僵尸进程占着单实例锁。</summary>
    public async Task ExitAppAsync()
    {
        try
        {
            PrepareExit();
            await Task.Run(App.Shutdown);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    /// <summary>退出前清理界面资源：托盘图标必须删除，否则任务栏残留假图标。</summary>
    private void PrepareExit()
    {
        try
        {
            Clipboard.ContentChanged -= OnClipboardChanged;
        }
        catch (Exception)
        {
            // 剪贴板解绑失败不影响退出
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception)
        {
            // 托盘清理失败不影响退出
        }
    }

    private string? _lastTrayTip;

    /// <summary>托盘悬停提示跟随任务状态；只在文案变化时才更新系统图标。</summary>
    private async Task UpdateTrayTipAsync()
    {
        var tasks = await App.Downloads!.ListAsync();
        var active = tasks.Count(t => t.Status is DownloadStatus.Active or DownloadStatus.Waiting);
        var paused = tasks.Count(t => t.Status == DownloadStatus.Paused);
        var tip = Format.TrayTip(active, paused);
        if (tip != _lastTrayTip)
        {
            _lastTrayTip = tip;
            _tray?.UpdateTip(tip);
        }
    }

    /// <summary>托盘菜单触发的后台操作：吞掉异常，保证托盘交互永不崩。</summary>
    private static async Task SafeTrayAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // 托盘操作失败不影响主程序
        }
    }

    private void MinimizeToTray()
    {
        AppWindow.Hide();
    }

    public void RestoreFromTray()
    {
        AppWindow.Show();
        try
        {
            _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch
        {
            // 置前失败不影响
        }
    }

    /// <summary>关闭询问：最小化到托盘 / 直接退出，可勾选记住选择。</summary>
    private async Task ShowCloseDialogAsync()
    {
        try
        {
            var remember = new CheckBox { Content = "记住我的选择" };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock
            {
                Text = "点击关闭后，TaoDown 要怎么处理？\n最小化到托盘会保留下载继续运行。",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(remember);
            var dialog = new ContentDialog
            {
                Title = "关闭 TaoDown",
                Content = panel,
                PrimaryButtonText = "最小化到托盘",
                SecondaryButtonText = "直接退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (remember.IsChecked == true && App.Settings is not null && App.SettingsStore is not null)
                {
                    App.Settings.CloseBehavior = 1;
                    App.SettingsStore.Save(App.Settings);
                }

                MinimizeToTray();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                if (remember.IsChecked == true && App.Settings is not null && App.SettingsStore is not null)
                {
                    App.Settings.CloseBehavior = 2;
                    App.SettingsStore.Save(App.Settings);
                }

                _ = ExitAppAsync();
            }
        }
        catch (Exception)
        {
            // 弹窗失败时退回默认行为：直接退出
            _ = ExitAppAsync();
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        var current = PageFrame.Content?.GetType();
        Type target = tag switch
        {
            "downloads" => typeof(DownloadsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DownloadsPage),
        };
        if (current != target)
        {
            PageFrame.Navigate(target);
        }
    }

    // 每个任务的上一状态：只在「未完成 → 完成 / 出错」的跃迁时弹气泡，启动刷新不重弹
    private readonly Dictionary<string, DownloadStatus> _lastStatus = new();

    private void OnTaskUpdated(object? sender, DownloadTask task)
    {
        if (PageFrame.Content is DownloadsPage page)
        {
            page.UpsertRow(task);
        }

        var prev = _lastStatus.GetValueOrDefault(task.Id);
        _lastStatus[task.Id] = task.Status;
        if (prev != task.Status)
        {
            // 真机排查：状态每次流转都如实落 startup.log，与引擎侧 Download complete 互相对照
            StartupLog.Write($"任务状态：{task.FileName ?? task.Url} {prev}→{task.Status}");
        }
        if (prev == task.Status || task.Status is not (DownloadStatus.Complete or DownloadStatus.Error))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(task.FileName) ? "任务" : task.FileName!;
        if (task.Status == DownloadStatus.Complete)
        {
            _tray?.ShowBalloon("下载完成", $"{name} · {Format.Size(task.CompletedBytes)}\n点击托盘图标可打开下载页。");
        }
        else
        {
            // v2.54：引擎自愈重启会批量把任务标成出错——这类只进列表行内说明，不连环弹气泡
            var isSelfHealMark = task.Error is { } reason && reason.Contains("引擎已自动重启");
            if (!isSelfHealMark)
            {
                _tray?.ShowBalloon("下载失败", $"{name} · {task.Error ?? "原因未知，可在下载页重试。"}");
            }
        }
    }

    /// <summary>外部唤起（点链接/双击种子/二次启动转发）：恢复窗口并自动入队，用户零操作。</summary>
    public async Task HandleLaunchPayloadAsync(LaunchPayload payload)
    {
        if (!payload.HasAny)
        {
            return;
        }

        RestoreFromTray();
        ShowDownloads();
        if (PageFrame.Content is DownloadsPage page)
        {
            await page.AcceptLaunchAsync(payload);
        }
    }

    /// <summary>剪贴板监视：捕获下载链接并填入下载页（批次三）。</summary>
    private async void OnClipboardChanged(object? sender, object e)
    {
        if (_checkingClipboard || App.Settings is not { ClipboardMonitor: true })
        {
            return;
        }

        _checkingClipboard = true;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            var text = (await content.GetTextAsync()).Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 4096 ||
                text == App.PendingClipboardLink)
            {
                return;
            }

            // 碎片、标准链接、以及带前后缀文本里的链接都能自动补全识别
            var normalized = LinkTextScanner.FindFirst(text)
                ?? LooseLinkParser.TryNormalize(text);
            if (normalized is null || LinkClassifier.Classify(normalized) == LinkKind.Unknown)
            {
                return;
            }

            if (PageFrame.Content is DownloadsPage page)
            {
                // v2.60：只对「像直接下载的文件」弹询问窗——复制网页地址/HTML 直接
                // 忽略，不填输入框、不设待处理标记（真机反馈：复制网页地址也不该有任何动作）。
                if (!LinkClassifier.LooksLikeDirectDownload(normalized))
                {
                    return;
                }

                App.PendingClipboardLink = normalized;
                page.SetClipboardLink(normalized);

                // v2.59：改顶级置顶询问窗——独立窗口、屏幕居中、保证可见；确认后主窗回下载页置顶
                if (await AskWindow.ShowAsync("检测到剪贴板链接", $"{normalized}\n是否立即添加下载？", normalized))
                {
                    RestoreFromTray();
                    Nav.SelectedItem = Nav.MenuItems[0];
                    await page.AddFromClipboardAsync(normalized);
                }
            }
            else
            {
                Nav.SelectedItem = Nav.MenuItems[0];
            }
        }
        catch (Exception)
        {
            // 剪贴板被其他进程短暂占用时忽略本轮，等待下次变化
        }
        finally
        {
            _checkingClipboard = false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
