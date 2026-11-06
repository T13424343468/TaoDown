using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using TaoDown.Core;
using TaoDown.Engine;

namespace TaoDown.App;

/// <summary>下载页：添加任务、任务列表（双行卡片）、批量操作与筛选。</summary>
public sealed partial class DownloadsPage : Page, IThemeRepaintable
{
    private readonly ObservableCollection<TaskRow> _rows = []; // 当前筛选下可见的行
    private readonly Dictionary<string, TaskRow> _all = []; // 全量行（按任务 Id）
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dq;
    private int _filterIndex; // 0 全部 / 1 进行中 / 2 已完成

    public DownloadsPage()
    {
        InitializeComponent();
        _dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        TaskList.ItemsSource = _rows;
        RepaintTheme();
    }

    /// <summary>根面板底色直涂（IThemeRepaintable）：换肤后由 MainWindow.PaintTheme 回调，不赌重估时机。</summary>
    public void RepaintTheme()
    {
        try
        {
            if (ThemeService.Brush("LayerFillColorDefaultBrush") is { } brush)
            {
                Root.Background = brush;
            }
        }
        catch
        {
            // 外观增强, 失败不阻断
        }
    }

    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
        if (App.PendingClipboardLink is { } link)
        {
            SetClipboardLink(link);
        }
    }

    /// <summary>进入页面时按服务侧任务重建列表（保持跨页状态）。</summary>
    private async Task ReloadAsync()
    {
        try
        {
            var tasks = await App.Downloads!.ListAsync();
            var seen = new HashSet<string>();
            foreach (var task in tasks)
            {
                seen.Add(task.Id);
                UpsertRow(task);
            }

            // 队列中尚未提交引擎的任务也显示出来（排队中）
            foreach (var waiting in App.Queue!.Waiting())
            {
                seen.Add(waiting.Id);
                UpsertRow(waiting);
            }

            // 服务侧已消失的任务（如清理后），从列表移除
            foreach (var id in _all.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                DropRow(_all[id]);
            }
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"加载任务失败：{ex.Message}";
        }
        UpdateSummary();
    }

    /// <summary>剪贴板监视把链接填入输入框。</summary>
    public void SetClipboardLink(string link)
    {
        LinkBox.Text = link;
        PageStatus.Text = "检测到剪贴板链接，点击「添加下载」开始。";
    }

    /// <summary>v2.59：剪贴板确认框选「立即下载」后直接入队（旧版只填框不弹询问，用户以为没反应）。</summary>
    public async Task AddFromClipboardAsync(string link)
    {
        LinkBox.Text = link;
        await AddLinkAsync(link);
    }

    private async void OnAddDownload(object sender, RoutedEventArgs e)
    {
        var link = LinkBox.Text?.Trim();
        if (string.IsNullOrEmpty(link))
        {
            return;
        }

        LinkBox.Text = string.Empty;
        try
        {
            await AddLinkAsync(link);
        }
        catch (Exception ex)
        {
            // v2.54：粘贴畸形内容任何解析异常都不能穿出 async void（穿出即闪退）
            PageStatus.Text = $"添加失败：{ex.Message}";
        }
    }

    /// <summary>处理外部唤起：种子文件与链接统一自动入队（URL 协议/二次启动转发/拖拽共用）。</summary>
    public async Task AcceptLaunchAsync(LaunchPayload payload)
    {
        var added = 0;
        foreach (var file in payload.TorrentFiles)
        {
            try
            {
                await AddTorrentFileAsync(file);
                added++;
            }
            catch (Exception ex)
            {
                PageStatus.Text = $"种子添加失败：{ex.Message}";
            }
        }

        foreach (var link in payload.Links)
        {
            var before = _all.Count;
            await AddLinkAsync(link);
            if (_all.Count > before)
            {
                added++;
            }
        }

        if (added > 0)
        {
            PageStatus.Text = $"外部唤起：已自动加入 {added} 个下载任务。";
        }
    }

    /// <summary>添加种子文件（拖拽/双击种子文件唤起与选择器共用）。</summary>
    public async Task AddTorrentFileAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        await App.Downloads!.AddTorrentAsync(bytes, App.EngineHost!.Options.DownloadDir);
        await ReloadAsync();
    }

    /// <summary>添加链接任务：解析迅雷链并经队列提交（并发/优先级统一管理）。
    /// v2.54：解析函数原本在 try 之外，畸形输入抛异常会一路穿到 async void（闪退）并中断外部唤起批量入队。</summary>
    private async Task AddLinkAsync(string link)
    {
        try
        {
            await AddLinkCoreAsync(link);
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"添加失败：{ex.Message}";
        }
    }

    private async Task AddLinkCoreAsync(string link)
    {
        // 粘贴内容可能带前后缀/换行/多条链接：先从文本里提取第一条可识别的链接
        var extracted = LinkTextScanner.FindFirst(link);
        if (extracted is not null)
        {
            link = extracted;
        }
        else
        {
            // 宽松补全：裸哈希/「文件名|大小|哈希」碎片自动规范成完整链接
            var normalized = LooseLinkParser.TryNormalize(link);
            if (normalized is not null)
            {
                link = normalized;
            }
        }

        var kind = LinkClassifier.Classify(link);
        if (kind == LinkKind.Unknown)
        {
            var u = (link ?? "").Trim();
            if (u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                PageStatus.Text = "blob/data 是网页内部临时链接，不能直接粘贴；请在浏览器里重试下载，装好的 TaoDown 扩展会自动接手。";
                return;
            }

            PageStatus.Text = "暂不支持的链接类型：目前支持 http/https、FTP、BT/磁力、ed2k、迅雷/快车/旋风链接（sftp、file 等暂不支持）。";
            return;
        }

        if (kind == LinkKind.Thunder)
        {
            var resolved = ThunderLinkParser.Resolve(link);
            if (resolved is null)
            {
                PageStatus.Text = "链接无法解析，请确认是否为有效的迅雷/快车/旋风链接。";
                return;
            }

            kind = LinkClassifier.Classify(resolved);
            link = resolved;
        }

        try
        {
            var task = new DownloadTask { Url = link, Kind = kind, SaveDir = App.EngineHost!.Options.DownloadDir };
            await App.Queue!.EnqueueAsync(task);
            UpsertRow(task);
            PageStatus.Text = "任务已加入队列。";
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"添加失败：{ex.Message}";
        }
    }

    private async void OnPickTorrent(object sender, RoutedEventArgs e)
    {
        // v2.54：选择器初始化/弹窗任何一步异常都不许穿出 async void（闪退），一律落状态栏
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add(".torrent");
            // WinUI 桌面应用需初始化父窗口句柄
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.Main!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            try
            {
                await AddTorrentFileAsync(file.Path);
                PageStatus.Text = "种子已添加。";
            }
            catch (Exception ex)
            {
                PageStatus.Text = $"添加种子失败：{ex.Message}";
            }
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"选择种子失败：{ex.Message}";
        }
    }

    // ---- 拖拽接入：链接或种子文件拖进窗口即开始下载 ----

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var torrents = items.OfType<Windows.Storage.StorageFile>()
                    .Where(f => f.Name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (torrents.Count > 0)
                {
                    var ok = 0;
                    foreach (var f in torrents)
                    {
                        try
                        {
                            await AddTorrentFileAsync(f.Path);
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            PageStatus.Text = $"种子添加失败：{ex.Message}";
                        }
                    }

                    PageStatus.Text = ok > 0 ? $"已添加 {ok} 个种子。" : "种子未能添加。";
                    return;
                }
            }

            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = (await e.DataView.GetTextAsync()).Trim();
                var extracted = LinkTextScanner.FindFirst(text) ?? LooseLinkParser.TryNormalize(text);
                if (extracted is not null && LinkClassifier.Classify(extracted) != LinkKind.Unknown)
                {
                    await AddLinkAsync(extracted);
                    PageStatus.Text = "链接已加入队列。";
                    return;
                }
            }

            PageStatus.Text = "拖进来的内容不能直接下载（支持链接与 .torrent 种子文件）。";
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"拖拽处理失败：{ex.Message}";
        }
    }

    // ---- 批量操作 ----

    private async void OnPauseAll(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = await App.Downloads!.PauseAllAsync();
            PageStatus.Text = n > 0 ? $"已暂停 {n} 个任务。" : "没有可暂停的任务。";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"暂停失败：{ex.Message}";
        }
    }

    private async void OnResumeAll(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = await App.Downloads!.ResumeAllAsync();
            PageStatus.Text = n > 0 ? $"已继续 {n} 个任务。" : "没有已暂停的任务。";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"继续失败：{ex.Message}";
        }
    }

    private async void OnClearFinished(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = await App.Downloads!.ClearFinishedAsync();
            PageStatus.Text = n > 0 ? $"已清理 {n} 个任务（文件保留在磁盘）。" : "没有可清理的任务。";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"清理失败：{ex.Message}";
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SummaryText is null)
        {
            return; // 构造期事件，跳过
        }

        _filterIndex = Math.Max(0, FilterBox.SelectedIndex);
        foreach (var row in _all.Values.ToList())
        {
            ApplyVisibility(row);
        }

        UpdateSummary();
    }

    private bool MatchesFilter(TaskRow row) => _filterIndex switch
    {
        1 => row.Status is DownloadStatus.Waiting or DownloadStatus.Active or DownloadStatus.Error,
        2 => row.Status == DownloadStatus.Complete,
        _ => true,
    };

    private void ApplyVisibility(TaskRow row)
    {
        var show = MatchesFilter(row);
        var contains = _rows.Contains(row);
        if (show && !contains)
        {
            _rows.Add(row);
        }
        else if (!show && contains)
        {
            _rows.Remove(row);
        }
    }

    // ---- 行内操作（按钮 Tag 传行） ----

    private async void OnRowToggle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TaskRow row)
        {
            return;
        }

        try
        {
            if (row.Status is DownloadStatus.Active or DownloadStatus.Waiting)
            {
                await App.Downloads!.PauseAsync(row.Id);
                PageStatus.Text = "已暂停。";
            }
            else
            {
                await App.Downloads!.ResumeAsync(row.Id);
                PageStatus.Text = "已继续。";
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"操作失败：{ex.Message}";
        }
    }

    private void OnRowFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TaskRow row)
        {
            OpenFolderFor(row);
        }
    }

    private void OpenFolderFor(TaskRow row)
    {
        try
        {
            // aria2 上报的路径是正斜杠（D:/xxx），资源管理器 /select 参数遇正斜杠会失效、
            // 退回打开默认视图（真机实证：跳到桌面）。先归一化成反斜杠再交给系统。
            var filePath = NormalizeWinPath(row.FilePath);
            var dirPath = NormalizeWinPath(row.SaveDir);

            // 优先选中文件；文件还不存在则打开所在文件夹
            var target = string.Empty;
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                target = $"/select,\"{filePath}\"";
            }
            else if (!string.IsNullOrWhiteSpace(dirPath) && Directory.Exists(dirPath))
            {
                target = $"\"{dirPath}\"";
            }

            if (target.Length > 0)
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = target, UseShellExecute = true });
            }
            else
            {
                PageStatus.Text = "文件尚未开始下载，暂无可打开的位置。";
            }
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"打开文件夹失败：{ex.Message}";
        }
    }

    /// <summary>把引擎上报的正斜杠路径统一成 Windows 反斜杠；空值原样返回。</summary>
    private static string NormalizeWinPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('/', '\\');

    private async void OnRowRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TaskRow row)
        {
            return;
        }

        await RemoveRowAsync(row);
    }

    /// <summary>
    /// 删除任务：所有状态的任务先确认，可勾选同时删除本地文件；已完成/出错任务同样先问再删。
    /// 任何一步失败都会在页面底部给出提示，绝不无声无息。
    /// </summary>
    private async Task RemoveRowAsync(TaskRow row)
    {
        var deleteFiles = false;
        try
        {
            // 所有状态的任务删除前都确认；已完成任务同样要问一句要不要连本地文件一起删
            var panel = new StackPanel { Spacing = 12, MinWidth = 280 };
            var statusHint = row.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused
                ? "还没下载完，删除后任务不再继续。"
                : row.Status is DownloadStatus.Error
                    ? "是出错任务，删除记录不会影响磁盘文件。"
                    : "已下载完成，删除记录后文件仍保留在磁盘。";
            panel.Children.Add(new TextBlock
            {
                Text = $"「{row.Name}」{statusHint}",
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            });
            var check = new CheckBox { Content = "同时删除本地文件（含续传记录）", IsChecked = false };
            panel.Children.Add(check);

            var dialog = new ContentDialog
            {
                Title = "删除任务",
                Content = panel,
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            deleteFiles = check.IsChecked == true;

            try
            {
                await App.Downloads!.RemoveAsync(row.Id);
            }
            catch (Exception)
            {
                // 尚未提交引擎的等待任务：从队列移除即可；引擎侧失败则继续走本地清理与提示
                App.Queue?.Dequeue(row.Id);
            }

            if (deleteFiles)
            {
                var failed = await DeleteLocalFilesAsync(row);
                PageStatus.Text = failed.Length > 0
                    ? $"任务已删除，但本地文件清理失败：{failed}"
                    : "任务已删除，本地文件已清理。";
            }
            else
            {
                PageStatus.Text = "任务已删除（已下载文件保留）。";
            }
        }
        catch (Exception ex)
        {
            // 确认框或清理流程任何异常都落到状态栏，保持可见
            PageStatus.Text = $"删除失败：{ex.Message}";
        }

        await ReloadAsync();
    }

    /// <summary>删除已下载的本地文件（含 aria2 续传控制文件）；返回失败摘要，全部成功返回空。
    /// v2.54：递归删除大目录可能耗时数秒，放后台线程执行，绝不阻塞界面。</summary>
    private static Task<string> DeleteLocalFilesAsync(TaskRow row) =>
        Task.Run(() => DeleteLocalFilesCore(row));

    private static string DeleteLocalFilesCore(TaskRow row)
    {
        var failures = new List<string>();
        var targets = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.FilePath))
        {
            targets.Add(row.FilePath);
            targets.Add(row.FilePath + ".aria2"); // 续传进度控制文件
        }

        foreach (var path in targets)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path) &&
                         !string.Equals(path, row.SaveDir, StringComparison.OrdinalIgnoreCase))
                {
                    // BT 多文件任务是目录：只有目录不等于下载根目录时才整体删除
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}：{ex.Message}");
            }
        }

        return string.Join("; ", failures);
    }

    /// <summary>双击任务：进行中暂停，其他继续。</summary>
    private TaskRow? _contextRow;

    /// <summary>右键弹菜单前先选中该行，菜单动作按选中行执行。</summary>
    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskRow row)
        {
            _contextRow = row;
            TaskList.SelectedItem = row;
        }
    }

    private void OnMenuFolder(object sender, RoutedEventArgs e)
    {
        if (_contextRow is { } row)
        {
            OpenFolderFor(row);
        }
    }

    private async void OnMenuToggle(object sender, RoutedEventArgs e)
    {
        if (_contextRow is not { } row ||
            row.Status is DownloadStatus.Complete)
        {
            return;
        }

        try
        {
            if (row.Status is DownloadStatus.Active or DownloadStatus.Waiting)
            {
                await App.Downloads!.PauseAsync(row.Id);
                PageStatus.Text = "已暂停。";
            }
            else
            {
                await App.Downloads!.ResumeAsync(row.Id);
                PageStatus.Text = "已继续。";
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"操作失败：{ex.Message}";
        }
    }

    private async void OnMenuRemove(object sender, RoutedEventArgs e)
    {
        if (_contextRow is { } row)
        {
            await RemoveRowAsync(row);
        }
    }

    private void OnTaskDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row)
        {
            return;
        }

        // 双击统一打开所在文件夹；暂停/继续走按钮或右键菜单，避免误触暂停
        try
        {
            OpenFolderFor(row);
        }
        catch (Exception ex)
        {
            PageStatus.Text = $"打开文件夹失败：{ex.Message}";
        }
    }

    /// <summary>由主窗口转发的任务更新（进度/状态变化）。</summary>
    public void UpsertRow(DownloadTask task)
    {
        if (_dq.HasThreadAccess)
        {
            ApplyUpsert(task);
        }
        else
        {
            _dq.TryEnqueue(() => ApplyUpsert(task));
        }
    }

    private void ApplyUpsert(DownloadTask task)
    {
        if (!_all.TryGetValue(task.Id, out var row))
        {
            row = new TaskRow(task.Id);
            _all[task.Id] = row;
        }

        row.Update(task);
        ApplyVisibility(row);
        UpdateSummary();
    }

    private void DropRow(TaskRow row)
    {
        if (_dq.HasThreadAccess)
        {
            ApplyDrop(row);
        }
        else
        {
            _dq.TryEnqueue(() => ApplyDrop(row));
        }
    }

    private void ApplyDrop(TaskRow row)
    {
        _all.Remove(row.Id);
        _rows.Remove(row);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        // 页面构造期间 SelectionChanged 可能先于后置控件就绪触发，此时跳过，等首次真实刷新再汇总
        if (SummaryText is null || EmptyHint is null)
        {
            return;
        }

        var active = 0;
        var paused = 0;
        var done = 0;
        long totalSpeed = 0;
        foreach (var row in _all.Values)
        {
            switch (row.Status)
            {
                case DownloadStatus.Active or DownloadStatus.Waiting:
                    active++;
                    totalSpeed += row.SpeedRaw;
                    break;
                case DownloadStatus.Paused:
                    paused++;
                    break;
                case DownloadStatus.Complete:
                    done++;
                    break;
            }
        }

        SummaryText.Text = Format.Summary(active, paused, done, totalSpeed);

        // 空态：一个任务都没有时给一句「怎么开始」，而不是对着一整块空白发呆
        EmptyHint.Visibility = _all.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
    }
}

/// <summary>任务列表行视图模型：携带全部关键字段，供 XAML 绑定与行内操作使用。</summary>
public sealed class TaskRow : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private double _percent;
    private string _statusText = string.Empty;
    private Microsoft.UI.Xaml.Media.Brush _statusBrush = StatusBrushFor(DownloadStatus.Waiting);
    private string _actionGlyph = "\uE769";
    private Visibility _actionVisibility = Visibility.Visible;
    private string _infoLine = string.Empty;
    private string _percentText = string.Empty;
    private string _actionTip = string.Empty;

    public TaskRow(string id) => Id = id;

    public string Id { get; }

    /// <summary>自引用：供 DataTemplate 内按钮 Tag 绑定取回本行。</summary>
    public TaskRow Self => this;

    public DownloadStatus Status { get; private set; } = DownloadStatus.Waiting;

    public string Name { get => _name; private set => Set(ref _name, value); }

    public double Percent { get => _percent; private set => Set(ref _percent, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public Microsoft.UI.Xaml.Media.Brush StatusBrush
    {
        get => _statusBrush;
        private set => Set(ref _statusBrush, value);
    }

    public string ActionGlyph { get => _actionGlyph; private set => Set(ref _actionGlyph, value); }

    public Visibility ActionVisibility
    {
        get => _actionVisibility;
        private set => Set(ref _actionVisibility, value);
    }

    public string InfoLine { get => _infoLine; private set => Set(ref _infoLine, value); }

    /// <summary>右侧百分比：总大小未知时留空，避免出现无意义的 0%。</summary>
    public string PercentText
    {
        get => _percentText;
        private set => Set(ref _percentText, value);
    }

    /// <summary>暂停/继续按钮的悬停提示，随状态切换。</summary>
    public string ActionTip
    {
        get => _actionTip;
        private set => Set(ref _actionTip, value);
    }

    public long SpeedRaw { get; private set; }

    public string? Url { get; private set; }

    public string? SaveDir { get; private set; }

    public string? FilePath { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(DownloadTask task)
    {
        Name = task.FileName ?? task.Url;
        Status = task.Status;
        Percent = task.TotalBytes > 0
            ? Math.Clamp(task.CompletedBytes * 100.0 / task.TotalBytes, 0, 100)
            : 0;
        StatusText = FormatStatus(task.Status);
        StatusBrush = StatusBrushFor(task.Status);
        SpeedRaw = task.SpeedBytesPerSec;
        Url = task.Url;
        SaveDir = task.SaveDir;
        FilePath = task.FilePath;
        (ActionGlyph, ActionVisibility, ActionTip) = task.Status switch
        {
            DownloadStatus.Active or DownloadStatus.Waiting => ("\uE769", Visibility.Visible, "暂停"),
            DownloadStatus.Paused or DownloadStatus.Error => ("\uE768", Visibility.Visible, "继续"),
            _ => ("\uE768", Visibility.Collapsed, string.Empty),
        };
        // v2.55 完成态显示：完成行不再孤零零卡着 100%，右侧直接亮「✓ 已完成」
        PercentText = task.Status == DownloadStatus.Complete
            ? "✓ 已完成"
            : task.TotalBytes > 0 ? $"{Percent:0.#}%" : string.Empty;
        InfoLine = FormatInfo(task);
    }

    private static string FormatInfo(DownloadTask t) => t.Status switch
    {
        DownloadStatus.Active => BuildActiveInfo(t),
        DownloadStatus.Waiting => BuildProgressInfo(t) is { Length: > 0 } p ? $"{p} · 等待开始…" : "等待开始…",
        DownloadStatus.Paused => BuildProgressInfo(t) is { Length: > 0 } p2 ? $"{p2} · 已暂停" : "已暂停",
        DownloadStatus.Error => BuildErrorInfo(t),
        DownloadStatus.Complete => t.TotalBytes > 0
            ? $"{Format.Size(t.TotalBytes)} · 已完成"
            : "已完成",
        _ => string.Empty,
    };

    private static string BuildProgressInfo(DownloadTask t)
    {
        var parts = new List<string>();
        if (t.TotalBytes > 0)
        {
            parts.Add($"{Format.Size(t.CompletedBytes)} / {Format.Size(t.TotalBytes)}");
        }
        else if (t.CompletedBytes > 0)
        {
            parts.Add($"已下载 {Format.Size(t.CompletedBytes)}");
        }

        return string.Join(" · ", parts);
    }

    private static string BuildActiveInfo(DownloadTask t)
    {
        // 一个字节都没动且无速度：按链接类型说清在等什么，别让用户对着 0 B 发呆
        if (t.CompletedBytes == 0 && t.SpeedBytesPerSec <= 0)
        {
            return t.Kind switch
            {
                // P2P 需要先在共享网络里找源，冷门资源可能久等——这是正常现象，必须讲明白
                LinkKind.Ed2k => "正在寻找 P2P 源…（冷门资源可能较慢，属正常现象）",
                LinkKind.BitTorrent => "正在寻找做种节点…",
                _ => "正在连接服务器…",
            };
        }

        var parts = new List<string>();
        if (t.TotalBytes > 0)
        {
            parts.Add($"{Format.Size(t.CompletedBytes)} / {Format.Size(t.TotalBytes)}");
        }
        else if (t.CompletedBytes > 0)
        {
            parts.Add($"已下载 {Format.Size(t.CompletedBytes)}");
        }

        var speed = Format.Speed(t.SpeedBytesPerSec);
        if (speed.Length > 0)
        {
            parts.Add($"↓{speed}");
        }

        if (t.TotalBytes > t.CompletedBytes)
        {
            var eta = Format.Eta(t.TotalBytes - t.CompletedBytes, t.SpeedBytesPerSec);
            if (eta.Length > 0)
            {
                parts.Add(eta);
            }
        }

        if (t.Connections > 0)
        {
            parts.Add($"{t.Connections} 连接");
        }

        if (t.Seeders > 0)
        {
            parts.Add($"做种 {t.Seeders}");
        }

        if (t.UploadSpeedBytesPerSec > 0)
        {
            parts.Add($"↑{Format.Speed(t.UploadSpeedBytesPerSec)}");
        }

        return string.Join(" · ", parts);
    }

    private static string BuildErrorInfo(DownloadTask t)
    {
        var baseInfo = BuildProgressInfo(t);
        var msg = string.IsNullOrWhiteSpace(t.Error) ? "任务出错" : t.Error;
        return baseInfo.Length > 0 ? $"{baseInfo} · 出错：{msg}" : $"出错：{msg}";
    }

    private static string FormatStatus(DownloadStatus status) => status switch
    {
        DownloadStatus.Waiting => "等待",
        DownloadStatus.Active => "下载中",
        DownloadStatus.Paused => "已暂停",
        DownloadStatus.Error => "出错",
        DownloadStatus.Complete => "完成",
        _ => string.Empty,
    };

    private static Microsoft.UI.Xaml.Media.Brush StatusBrushFor(DownloadStatus status)
    {
        var color = status switch
        {
            DownloadStatus.Active => Microsoft.UI.Colors.SeaGreen,
            DownloadStatus.Complete => Microsoft.UI.Colors.RoyalBlue,
            DownloadStatus.Paused => Microsoft.UI.Colors.Gray,
            DownloadStatus.Error => Microsoft.UI.Colors.Crimson,
            _ => Microsoft.UI.Colors.DarkOrange,
        };
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
