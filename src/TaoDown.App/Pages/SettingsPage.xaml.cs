using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaoDown.Core;

namespace TaoDown.App;

/// <summary>设置页：下载目录、并发、速度上限、自动分类、剪贴板监视、做种。</summary>
public sealed partial class SettingsPage : Page, IThemeRepaintable
{
    private bool _suppressThemeChange;

    public SettingsPage()
    {
        InitializeComponent();
        RepaintTheme();
    }

    /// <summary>根面板底色直涂（IThemeRepaintable）：换肤后由 MainWindow.PaintTheme 回调，不赌重估时机。</summary>
    public void RepaintTheme()
    {
        try
        {
            if (ThemeService.Brush("LayerFillColorDefaultBrush") is { } brush)
            {
                RootScroll.Background = brush;
            }
        }
        catch
        {
            // 外观增强, 失败不阻断
        }
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var settings = App.Settings;
        DownloadDirBox.Text = settings.DownloadDir;
        MaxConcurrentBox.Value = settings.MaxConcurrent;
        SpeedLimitBox.Value = settings.SpeedLimitKB;
        AutoClassifySwitch.IsOn = settings.AutoClassify;
        ClipboardMonitorSwitch.IsOn = settings.ClipboardMonitor;
        CloseBehaviorBox.SelectedIndex = settings.CloseBehavior is >= 0 and <= 2 ? settings.CloseBehavior : 0;
        ProtocolTakeoverSwitch.IsOn = settings.ProtocolTakeover;
        StartWithWindowsSwitch.IsOn = settings.StartWithWindows;
        BrowserTakeoverSwitch.IsOn = settings.BrowserTakeover;
        ExtensionStoreUrlBox.Text = settings.ExtensionStoreUrl;
        SeedMinutesBox.Value = settings.SeedMinutes;
        _suppressThemeChange = true;
        try
        {
            ThemeBox.SelectedIndex = settings.ThemeId is >= 0 and <= 2 ? settings.ThemeId : 0;
        }
        finally
        {
            _suppressThemeChange = false;
        }
    }

    /// <summary>切换主题：即时生效并落盘（水墨为系列默认）。</summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeChange || ThemeBox.SelectedIndex < 0)
        {
            return;
        }

        App.Settings.ThemeId = ThemeBox.SelectedIndex;
        ThemeService.Apply(App.Main, App.Settings.ThemeId);
        try
        {
            App.SettingsStore.Save(App.Settings);
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"主题已切换但保存失败：{ex.Message}";
        }
    }

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings;
        var dir = DownloadDirBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            PageStatus.Text = "下载根目录不能为空。";
            return;
        }

        settings.DownloadDir = dir;
        settings.MaxConcurrent = (int)Math.Clamp(MaxConcurrentBox.Value, 1, 16);
        settings.SpeedLimitKB = (int)Math.Max(0, SpeedLimitBox.Value);
        settings.AutoClassify = AutoClassifySwitch.IsOn;
        settings.ClipboardMonitor = ClipboardMonitorSwitch.IsOn;
        settings.CloseBehavior = CloseBehaviorBox.SelectedIndex;
        settings.ProtocolTakeover = ProtocolTakeoverSwitch.IsOn;
        settings.StartWithWindows = StartWithWindowsSwitch.IsOn;
        settings.BrowserTakeover = BrowserTakeoverSwitch.IsOn;
        settings.ExtensionStoreUrl = ExtensionStoreUrlBox.Text?.Trim() ?? string.Empty;
        settings.SeedMinutes = (int)Math.Clamp(SeedMinutesBox.Value, 0, 10080);

        try
        {
            App.SettingsStore.Save(settings);
            if (App.Queue is { } queue)
            {
                queue.MaxConcurrent = settings.MaxConcurrent;
            }

            App.ApplyTakeoverOptions();
            PageStatus.Text = "设置已保存。链接接管与开机自启即刻生效；并发数立即生效；下载目录、速度上限与做种时长在重启 TaoDown 后由引擎生效。";
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>检测并挂载全部已知内核浏览器：接管通道即刻登记，浏览器重启后生效。</summary>
    private void OnMountBrowsers(object sender, RoutedEventArgs e)
    {
        try
        {
            var families = NativeHostRegistrar.RegisterAll();
            var written = App.RefreshBrowserExtensionEntries();
            PageStatus.Text = $"已为 {families.Count} 个浏览器内核挂好接管通道，插件外链已登记 {written} 处；重启浏览器即自动挂载插件。";
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"挂载失败：{ex.Message}";
        }
    }

    /// <summary>打开随软件分发的插件文件夹（浏览器「加载已解压的扩展程序」选这里）。</summary>
    private void OnOpenExtensionFolder(object sender, RoutedEventArgs e)
    {
        var extDir = System.IO.Path.Combine(AppContext.BaseDirectory, "extension");
        if (!System.IO.Directory.Exists(extDir))
        {
            PageStatus.Text = "插件文件夹缺失，请重新安装 TaoDown。";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{extDir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"打开失败：{ex.Message}";
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = DownloadDirBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            PageStatus.Text = "目录不存在，请先保存有效路径。";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"打开失败：{ex.Message}";
        }
    }
}
