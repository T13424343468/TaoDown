// ==== 补齐 XAML 生成器在真实工程里生成的部分类成员 (仅语法检查用) ====
// 注意: App.xaml.cs 不参与本检查 (App 静态成员与 XAML 编译强耦合),
// 通过本文件声明与真实 App 类一致的静态成员签名, 让页面代码可编译。
namespace TaoDown.App
{
    using global::System.Collections.Generic;
    using global::TaoDown.Core;
    using global::TaoDown.Engine;

    public partial class App
    {
        public static Aria2DownloadService? Downloads { get; private set; }
        public static Aria2EngineHost? EngineHost { get; private set; }
        public static DownloadQueue? Queue { get; private set; }
        public static SettingsStore SettingsStore { get; private set; } = null!;
        public static DownloadSettings Settings { get; private set; } = null!;
        public static string? PendingClipboardLink { get; set; }
        public static BrowserInboxWatcher? Inbox { get; private set; }
        public static MainWindow? Main { get; set; }

        public static void Shutdown() { }

        public static void ApplyTakeoverOptions() { }

        public static int RefreshBrowserExtensionEntries() { return 0; }

        public static System.Threading.Tasks.Task ReviveEngineAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    public partial class MainWindow
    {
        internal Microsoft.UI.Xaml.Controls.NavigationView Nav = null!;
        internal Microsoft.UI.Xaml.Controls.Frame PageFrame = null!;
        internal Microsoft.UI.Xaml.Controls.Grid RootGrid = null!;
    }

    public partial class DownloadsPage
    {
        internal Microsoft.UI.Xaml.Controls.Grid Root = null!;
        internal Microsoft.UI.Xaml.Controls.TextBox LinkBox = null!;
        internal Microsoft.UI.Xaml.Controls.Button AddButton = null!;
        internal Microsoft.UI.Xaml.Controls.Button TorrentButton = null!;
        internal Microsoft.UI.Xaml.Controls.TextBlock SummaryText = null!;
        internal Microsoft.UI.Xaml.Controls.Button PauseAllButton = null!;
        internal Microsoft.UI.Xaml.Controls.Button ResumeAllButton = null!;
        internal Microsoft.UI.Xaml.Controls.Button ClearButton = null!;
        internal Microsoft.UI.Xaml.Controls.ComboBox FilterBox = null!;
        internal Microsoft.UI.Xaml.Controls.ListView TaskList = null!;
        internal Microsoft.UI.Xaml.Controls.TextBlock PageStatus = null!;
        internal Microsoft.UI.Xaml.Controls.TextBlock EmptyHint = null!;
    }

    public partial class SettingsPage
    {
        internal Microsoft.UI.Xaml.Controls.ScrollViewer RootScroll = null!;
        internal Microsoft.UI.Xaml.Controls.RadioButtons ThemeBox = null!;
        internal Microsoft.UI.Xaml.Controls.TextBox DownloadDirBox = null!;
        internal Microsoft.UI.Xaml.Controls.NumberBox MaxConcurrentBox = null!;
        internal Microsoft.UI.Xaml.Controls.NumberBox SpeedLimitBox = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleSwitch AutoClassifySwitch = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleSwitch ClipboardMonitorSwitch = null!;
        internal Microsoft.UI.Xaml.Controls.RadioButtons CloseBehaviorBox = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleSwitch ProtocolTakeoverSwitch = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleSwitch StartWithWindowsSwitch = null!;
        internal Microsoft.UI.Xaml.Controls.ToggleSwitch BrowserTakeoverSwitch = null!;
        internal Microsoft.UI.Xaml.Controls.TextBox ExtensionStoreUrlBox = null!;
        internal Microsoft.UI.Xaml.Controls.Button MountBrowsersButton = null!;
        internal Microsoft.UI.Xaml.Controls.Button OpenExtFolderButton = null!;
        internal Microsoft.UI.Xaml.Controls.Button OpenExtPageButton = null!;
        internal Microsoft.UI.Xaml.Controls.NumberBox SeedMinutesBox = null!;
        internal Microsoft.UI.Xaml.Controls.Button SaveButton = null!;
        internal Microsoft.UI.Xaml.Controls.Button OpenFolderButton = null!;
        internal Microsoft.UI.Xaml.Controls.TextBlock PageStatus = null!;
    }
}
