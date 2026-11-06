using Microsoft.UI.Xaml;
using Microsoft.Win32;
using TaoDown.Core;
using TaoDown.Engine;

namespace TaoDown.App;

/// <summary>应用入口：装配引擎、队列、设置与浏览器接管。</summary>
public partial class App : Application
{
    public static Aria2DownloadService? Downloads { get; private set; }

    public static Aria2EngineHost? EngineHost { get; private set; }

    /// <summary>FTP 专用经典引擎宿主；组件缺失时为 null。</summary>
    public static Aria2EngineHost? FtpEngineHost { get; private set; }

    /// <summary>下载队列：并发上限、优先级、定时开始。</summary>
    public static DownloadQueue? Queue { get; private set; }

    public static SettingsStore SettingsStore { get; private set; } = null!;

    public static DownloadSettings Settings { get; private set; } = null!;

    /// <summary>剪贴板监视捕获的待添加链接。</summary>
    public static string? PendingClipboardLink { get; set; }

    /// <summary>外部唤起载荷：命令行链接/种子或二次启动转发的待下载内容。</summary>
    private static LaunchPayload? _launchPayload;

    /// <summary>BROWSER-EXTENSION下载请求监听（批次五）。</summary>
    public static BrowserInboxWatcher? Inbox { get; private set; }

    public static MainWindow? Main { get; set; }

    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activateEvent;
    private static int _shutdownDone;

    public App()
    {
        InitializeComponent();
        StartupLog.Write("应用构造完成");
        // 界面线程未处理异常：记录后不闪退，交给用户重启
        Current.UnhandledException += (_, e) =>
        {
            StartupLog.Write("界面未处理异常", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            StartupLog.Write("后台未处理异常", e.ExceptionObject as Exception ?? new Exception("未知"));
        // 兜底：无论从哪条路径退出进程，都确保子进程被清理（幂等）
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 点链接/种子文件唤起：先把命令行解析成待下载内容，再走单实例（二次启动会把内容转交给主实例）
        var launchPayload = LaunchArgs.Parse(Environment.GetCommandLineArgs(), path => File.Exists(path));
        if (!AcquireSingleInstance(launchPayload))
        {
            // 已有实例在运行：AcquireSingleInstance 内部已转发内容并强制退出本进程
            return;
        }

        _launchPayload = launchPayload;

        try
        {
            StartupLog.Write($"开始启动（构建标记 {BuildInfo.Mark}）");
            SettingsStore = new SettingsStore();
            Settings = SettingsStore.Load();
            StartupLog.Write($"设置已读取 (DownloadDir={Settings.DownloadDir})");
            EngineHost = new Aria2EngineHost(new Aria2Options
            {
                DownloadDir = Settings.DownloadDir,
                LogPath = Path.Combine(StartupLog.LogDir, "engine-main.log"),
                MaxConnectionsPerServer = Settings.MaxConnectionsPerServer,
                Split = Settings.Split,
                OverallSpeedLimitKB = Settings.SpeedLimitKB,
                SeedMinutes = Settings.SeedMinutes,
            });
            var primaryClient = await EngineHost.StartAsync();
            StartupLog.Write("主引擎已启动");

            // FTP 走经典 aria2（aria2-next 已移除 FTP 支持），RPC 端口顺延即可错开；失败不阻断主功能
            Aria2Client? ftpClient = null;
            try
            {
                FtpEngineHost = new Aria2EngineHost(
                    new Aria2Options
                    {
                        DownloadDir = Settings.DownloadDir,
                        LogPath = Path.Combine(StartupLog.LogDir, "engine-ftp.log"),
                        MaxConnectionsPerServer = Settings.MaxConnectionsPerServer,
                        Split = Settings.Split,
                        OverallSpeedLimitKB = Settings.SpeedLimitKB,
                        SeedMinutes = Settings.SeedMinutes,
                    },
                    Path.Combine(AppContext.BaseDirectory, "engine", "aria2c.exe"));
                ftpClient = await FtpEngineHost.StartAsync();
                StartupLog.Write("FTP 引擎已启动");
            }
            catch (Exception ex)
            {
                StartupLog.Write("FTP 引擎启动失败(不阻断)", ex);
                FtpEngineHost = null;
                ftpClient = null;
            }

            Downloads = new Aria2DownloadService(primaryClient, ftpClient);
            // 跟随系统代理（App 启动时读取；引擎重启不影响——代理按任务下发）。
            // 真机教训：系统代理/VPN 环境下浏览器走代理正常、程序直连被黑洞，任务永远停在连接中。
            var proxyAddress = SystemProxy.Parse(ReadSystemProxyServer());
            Downloads.AllProxy = proxyAddress;
            StartupLog.Write(proxyAddress is null
                ? "系统代理：未启用或无 http 条目（引擎直连）"
                : $"系统代理已跟随：{proxyAddress}");
            Queue = new DownloadQueue(Downloads) { MaxConcurrent = Math.Max(1, Settings.MaxConcurrent) };
            StartupLog.Write("下载队列就绪");

            // 窗口先亮: 浏览器接管等放后台, 不让慢启动堵住主界面
            Main = new MainWindow();
            Main.Activate();
            ThemeService.Apply(Main, Settings.ThemeId); // 系列主题：水墨默认自带
            StartupLog.Write("主窗口已显示");

            _ = StartBackgroundAsync();

            // 外部唤起（点链接/双击种子/二次启动转发）：自动入队，不让用户再点一遍
            if (_launchPayload is { HasAny: true } payload)
            {
                Main.DispatcherQueue.TryEnqueue(() => _ = Main.HandleLaunchPayloadAsync(payload));
            }

            // 首次启动引导：检测浏览器并指路装插件（一次性）
            Main.DispatcherQueue.TryEnqueue(() => _ = Main.ShowBrowserGuideAsync());
        }
        catch (Exception ex)
        {
            StartupLog.Write("启动失败", ex);
            // 排错增强: 列出安装目录的引擎组件现状
            try
            {
                foreach (var sub in new[] { "engine" })
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, sub);
                    if (!Directory.Exists(dir))
                    {
                        StartupLog.Write($"诊断: {sub} 目录不存在 ({dir})");
                        continue;
                    }

                    var files = Directory.GetFiles(dir).Select(Path.GetFileName);
                    StartupLog.Write($"诊断: {sub} 现有文件: {string.Join(", ", files)}");
                }
            }
            catch (Exception dex)
            {
                StartupLog.Write("诊断枚举失败", dex);
            }

            // v2.54：启动失败不再重抛——async void 里重抛 = 窗口未起即闪退（引擎组件缺失正是此路径）。
            // 弹系统级错误框说明原因与日志位置，用户确认后干净退出，不留无窗口僵尸进程占单实例锁。
            try
            {
                _ = MessageBoxW(nint.Zero,
                    $"TaoDown 启动失败：{ex.Message}\r\n\r\n排查信息已写入：\r\n{StartupLog.LogFile}\r\n\r\n把该日志发给开发者可快速定位。点击「确定」后程序退出。",
                    "TaoDown", 0x10);
            }
            catch
            {
                // 弹窗失败也要走退出，不能留僵尸进程
            }

            Shutdown();
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// 单实例保护：首次启动持有命名互斥体并监听激活事件；
    /// 二次启动会通知已有实例恢复窗口后强制退出本进程，防止双托盘、双引擎。
    /// 上个实例异常死亡留下的废弃互斥体会被自动接管，不会卡死启动。
    /// </summary>
    private bool AcquireSingleInstance(LaunchPayload? forwardPayload = null)
    {
        try
        {
            _singleInstanceMutex = new Mutex(false, @"Global\TaoDown.SingleInstance");
            var owned = false;
            try
            {
                owned = _singleInstanceMutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // 上个实例没释放就死了：互斥体已归我们，按首实例继续
                owned = true;
                StartupLog.Write("接管废弃的单实例互斥体");
            }

            if (!owned)
            {
                try
                {
                    if (forwardPayload is { HasAny: true })
                    {
                        LaunchForward.Write(forwardPayload);
                        StartupLog.Write($"二次启动转发 {forwardPayload.Links.Count + forwardPayload.TorrentFiles.Count} 项待下载内容");
                    }

                    using var evt = EventWaitHandle.OpenExisting(@"Global\TaoDown.ShowWindow");
                    evt.Set();
                }
                catch (Exception)
                {
                    // 事件不存在（旧版本遗留实例）时只退出新进程
                }

                StartupLog.Write("检测到已有实例，退出当前进程");
                // Application.Exit 对桌面应用不可靠，可能留下无窗口僵尸进程占着锁；
                // 必须强制终止本进程
                Environment.Exit(0);
            }

            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\TaoDown.ShowWindow");
            _ = Task.Run(() =>
            {
                // 循环等待二次启动的激活请求；退出时事件被释放，等待抛出即结束循环
                try
                {
                    while (_activateEvent!.WaitOne())
                    {
                        Main?.DispatcherQueue?.TryEnqueue(() =>
                        {
                            Main?.RestoreFromTray();
                            var forwarded = LaunchForward.TryRead();
                            if (forwarded.HasAny)
                            {
                                _ = Main?.HandleLaunchPayloadAsync(forwarded);
                            }
                        });
                    }
                }
                catch (Exception)
                {
                    // 事件已释放或等待失败：监听线程安静退出
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            // 单实例检测失败（如权限受限）不阻断启动，按首实例处理
            StartupLog.Write("单实例检测失败(不阻断)", ex);
            return true;
        }
    }

    /// <summary>
    /// 统一退出清理（幂等）：停浏览器监听与引擎，
    /// 杀掉整个进程树，确保退出后无残留进程。所有退出路径都必须经过这里。
    /// </summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownDone, 1) == 1)
        {
            return;
        }

        try { Inbox?.Dispose(); } catch (Exception ex) { StartupLog.Write("收件箱清理失败", ex); }
        try { FtpEngineHost?.Dispose(); } catch (Exception ex) { StartupLog.Write("FTP 引擎清理失败", ex); }
        try { EngineHost?.Dispose(); } catch (Exception ex) { StartupLog.Write("主引擎清理失败", ex); }

        try
        {
            _activateEvent?.Dispose();
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception)
        {
            // 互斥体释放失败不影响退出
        }
    }

    /// <summary>后台启动浏览器接管；每步写日志、失败不阻断。</summary>
    private static async Task StartBackgroundAsync()
    {
        try
        {
            // 批次五：注册浏览器插件宿主并监听下载请求（失败不阻断主功能）
            try
            {
                var families = NativeHostRegistrar.RegisterAll();
                StartupLog.Write($"浏览器接管通道已登记：{string.Join(",", families)}");
                var extWritten = RefreshBrowserExtensionEntries();
                StartupLog.Write($"插件外链已登记 {extWritten} 处，浏览器重启后自动挂载插件。");
            }
            catch (Exception ex)
            {
                StartupLog.Write("浏览器宿主注册失败(不阻断)", ex);
            }

            Inbox = new BrowserInboxWatcher();
            Inbox.Start();
            StartupLog.Write("浏览器收件箱监听已启动");

            // 链接协议接管与开机自启：按当前设置刷新注册表（装完即接管，失败不阻断）
            ApplyTakeoverOptions();
        }
        catch (Exception ex)
        {
            StartupLog.Write("后台启动流程异常(不阻断)", ex);
        }
    }

    /// <summary>按当前设置刷新接管注册表（协议关联 + 开机自启）；设置页保存后也走这里。</summary>
    public static void ApplyTakeoverOptions()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TaoDown.exe");
            if (Settings is { ProtocolTakeover: true })
            {
                ProtocolRegistrar.Apply(exe);
            }
            else
            {
                ProtocolRegistrar.Remove();
            }

            ProtocolRegistrar.ApplyAutoStart(Settings?.StartWithWindows ?? false, exe);
            NativeHostRegistrar.WriteTakeoverState(Settings is { BrowserTakeover: true });
            var extWritten = RefreshBrowserExtensionEntries();
            StartupLog.Write($"接管注册表已刷新 (协议={Settings?.ProtocolTakeover}, 自启={Settings?.StartWithWindows}, 插件外链={extWritten})");
        }
        catch (Exception ex)
        {
            StartupLog.Write("接管注册表维护失败(不阻断)", ex);
        }
    }

    /// <summary>
    /// 按当前设置刷新浏览器插件外链登记（v2.50）：
    /// 接管开启且商店地址非空→写商店 update_url 外链；未上架→把安装目录里的已签名
    /// 插件包（extension\taodown.crx）写成 path+version 外链（各浏览器重启后自动挂载，
    /// 无需开发者模式、无需用户操作）；插件包缺失时退回目录型登记（开发态兜底）；
    /// 接管关闭→摘净全部外链登记。返回写成功的登记项数。
    /// </summary>
    public static int RefreshBrowserExtensionEntries()
    {
        try
        {
            if (Settings is not { BrowserTakeover: true })
            {
                NativeHostRegistrar.RemoveExternalExtensionEntries();
                NativeHostRegistrar.RemoveLocalExtensionEntries();
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(Settings.ExtensionStoreUrl))
            {
                NativeHostRegistrar.RemoveLocalExtensionEntries();
                return NativeHostRegistrar.WriteExternalExtensionEntries(Settings.ExtensionStoreUrl);
            }

            NativeHostRegistrar.RemoveExternalExtensionEntries();
            var crx = Path.Combine(AppContext.BaseDirectory, "extension", "taodown.crx");
            // v2.56 修复：自证先走 return 就读不到了（v31 真机日志坐实），改为先记结果、自证必跑、最后统一返回
            var written = File.Exists(crx)
                ? NativeHostRegistrar.WriteLocalExtensionEntries(crx)
                : (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "extension"))
                    ? NativeHostRegistrar.WriteLocalExtensionEntries(Path.Combine(AppContext.BaseDirectory, "extension"))
                    : 0);
            // v2.55 挂载自证：登记后回读 Edge/Chrome 键，真机证据落盘（自动挂载排障不再靠猜）
            try
            {
                foreach (var id in new[] { NativeHostRegistrar.ExtensionId })
                {
                    foreach (var view in new[] { Registry.CurrentUser, Registry.LocalMachine })
                    {
                        using var key = view.OpenSubKey($@"Software\Microsoft\Edge\Extensions\{id}");
                        var pathVal = key?.GetValue("path");
                        var verVal = key?.GetValue("version");
                        StartupLog.Write(
                            $"[挂载自证] {(view == Registry.CurrentUser ? "HKCU" : "HKLM")} Edge 键回读：" +
                            $"path={pathVal ?? "(空)"} version={verVal ?? "(空)"}");
                    }
                }
            }
            catch (Exception ex)
            {
                StartupLog.Write($"[挂载自证] 回读异常：{ex.GetType().Name} {ex.Message}");
            }

            return written;
        }
        catch (Exception)
        {
            // 外链登记属于增强项，失败不阻断主流程
            return 0;
        }
    }
    private static long _lastReviveTicks;

    /// <summary>
    /// 引擎自愈（v2.54）：连续 RPC 失败时重启引擎并替换客户端；15 秒节流防崩溃循环空转。
    /// 重启成功后放行队列继续泵任务（等待中的任务不必等下一次事件才恢复）。
    /// </summary>
    public static async Task ReviveEngineAsync()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastReviveTicks);
        if (now - last < TimeSpan.FromSeconds(15).Ticks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReviveTicks, now);
        try
        {
            if (EngineHost is not null && Downloads is not null && Downloads.MainRpcFailStreak >= 5)
            {
                var (client, restarted) = await EngineHost.EnsureRunningAsync();
                if (restarted)
                {
                    Downloads.SwapPrimaryClient(client);
                    StartupLog.Write("主引擎自愈重启完成，中断任务已如实标记");
                    _ = Queue?.PumpAsync();
                }
                else
                {
                    client.Dispose();
                }
            }

            if (FtpEngineHost is not null && Downloads is not null && Downloads.FtpRpcFailStreak >= 5)
            {
                var (client, restarted) = await FtpEngineHost.EnsureRunningAsync();
                if (restarted)
                {
                    Downloads.SwapFtpClient(client);
                    StartupLog.Write("FTP 引擎自愈重启完成，中断任务已如实标记");
                    _ = Queue?.PumpAsync();
                }
                else
                {
                    client.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            // 自愈失败不致命：下一轮刷新达到阈值会再次尝试（节流内跳过）
            StartupLog.Write("引擎自愈重启失败（稍后自动重试）", ex);
        }
    }

    /// <summary>读 Windows 用户级系统代理原始值（未启用返回 null；读注册表失败不阻断启动）。</summary>
    private static string? ReadSystemProxyServer()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null)
            {
                return null;
            }

            var enabled = key.GetValue("ProxyEnable") as int? ?? 0;
            if (enabled == 0)
            {
                return null;
            }

            return key.GetValue("ProxyServer") as string;
        }
        catch (Exception ex)
        {
            StartupLog.Write("系统代理读取失败（按未启用处理）", ex);
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

}
