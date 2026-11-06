using Microsoft.UI.Xaml.Controls;
using System.Collections.Concurrent;
using System.Text.Json;
using TaoDown.Core;
using TaoDown.Engine;

namespace TaoDown.App;

/// <summary>
/// 浏览器下载请求收件箱监视（批次五）：
/// 宿主把浏览器转发的下载请求写到收件箱，这里弹出保存位置对话框
/// （默认选中设置页的下载根目录），确认后入队下载。
/// </summary>
public sealed class BrowserInboxWatcher : IDisposable
{
    private readonly ConcurrentQueue<string> _pending = new();
    // v2.58 关键修复：UI 队列必须在构造时（UI 线程）捕获。旧版在泵线程上调
    // GetForCurrentThread() 拿到 null 再 TryEnqueue 直接空引用——保存框永远弹不出，
    // 体感就是「点了下载没反应」（真机 v33 日志坐实：请求全收到、任务零入队）。
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private readonly FileSystemWatcher _watcher;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(300));
    private readonly CancellationTokenSource _cts = new();

    /// <summary>内容级去重（v2.50）：同一 地址+文件名 的请求 3 秒内只处理一次。
    /// 旧版手动加载的插件与新版自动挂载插件短暂并存、个别内核对同一下载触发
    /// 两次回调时，不会弹两个框、入两条队列。</summary>
    private static readonly ConcurrentDictionary<string, DateTime> Recent = new();

    public BrowserInboxWatcher()
    {
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Directory.CreateDirectory(NativeHostRegistrar.InboxDir);
        _watcher = new FileSystemWatcher(NativeHostRegistrar.InboxDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = false,
        };
        // v2.60 关键修复：宿主用「临时文件+移动改名」落盘，移动只触发 Renamed
        // 而非 Created——旧版只挂 Created，运行期间的所有实时请求全部漏接，
        // 只有重启后的存量扫描能捞回（真机 host.log 坐实：请求 4 条全到、受理 0 条）。
        _watcher.Created += (_, e) => _pending.Enqueue(e.FullPath);
        _watcher.Renamed += (_, e) => _pending.Enqueue(e.FullPath);
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        // 启动先扫存量（v2.50）：主程序没在运行时点过的下载请求都躺在收件箱里，
        // 不扫就永远丢在盘上——此前「关着 TaoDown 点下载」等于白点。重叠部分
        // 靠 HandleAsync 的「文件已删即跳过」天然防重。
        try
        {
            foreach (var path in Directory.EnumerateFiles(NativeHostRegistrar.InboxDir, "*.json")
                         .OrderBy(File.GetLastWriteTime))
            {
                _pending.Enqueue(path);
            }

            if (_pending.Count > 0)
            {
                StartupLog.Write($"收件箱存量待处理 {_pending.Count} 项（主程序未运行期间的请求）");
            }
        }
        catch (Exception)
        {
            // 扫存量失败不影响后续实时监听
        }

        _ = PumpAsync(_cts.Token);
    }

    private async Task PumpAsync(CancellationToken token)
    {
        // v2.54：泵循环任何异常都不能整泵退出——泵一死，浏览器后续所有接管请求都无声无息
        try
        {
            var sinceSweep = 0;
            while (await _timer.WaitForNextTickAsync(token))
            {
                // 周期兜底扫描（约 2 秒一轮）：文件系统事件再丢也有网兜着，
                // 已处理文件会删除，不会重复弹出
                if (++sinceSweep >= 7)
                {
                    sinceSweep = 0;
                    try
                    {
                        foreach (var path in Directory.EnumerateFiles(NativeHostRegistrar.InboxDir, "*.json"))
                        {
                            _pending.Enqueue(path);
                        }
                    }
                    catch (Exception)
                    {
                        // 扫描失败不影响队列里已排队的条目
                    }
                }

                while (_pending.TryDequeue(out var path))
                {
                    try
                    {
                        // 宿主用临时文件+替换写入，这里稍等避免读到半截
                        await Task.Delay(80, token);
                        await HandleAsync(path);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        // 单条处理失败跳过并留痕，泵必须活着
                        StartupLog.Write("收件箱条目处理失败（跳过该条）", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 退出清理时正常结束
        }
        catch (Exception ex)
        {
            StartupLog.Write("收件箱泵异常退出（监听失效）", ex);
        }
    }

    private async Task HandleAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(path);
            File.Delete(path);
            if (!BrowserRequest.TryParse(json, out var request, out var parseError) || request is null)
            {
                // v2.58：解析失败不再无声——留痕才能定位（此前这里静默返回是排障盲区）
                StartupLog.Write($"收件箱条目解析失败（跳过）：{parseError}");
                return;
            }
            StartupLog.Write($"收件箱受理：{request.Url}");

            // 内容级去重：只挡毫秒级的内核重复回调（v2.62 收短到 0.8 秒）。用户每次点击
            // 下载都是独立确认——取消后再点、连点不同链接都必须各自弹窗（用户定案），
            // 去重窗口一旦盖住人工操作节奏就是「取消后不再弹」的帮凶，宁短勿长。
            var dedupeKey = $"{request.Url}|{request.Filename}";
            var now = DateTime.UtcNow;
            if (Recent.TryGetValue(dedupeKey, out var seen) && (now - seen).TotalMilliseconds < 800)
            {
                return;
            }

            Recent[dedupeKey] = now;
            if (Recent.Count > 256)
            {
                // 防无限增长：清掉 60 秒前的旧键
                foreach (var key in Recent.Where(kv => (now - kv.Value).TotalSeconds > 60)
                             .Select(kv => kv.Key).ToList())
                {
                    Recent.TryRemove(key, out _);
                }
            }

            if (!App.Settings.BrowserTakeover)
            {
                return; // 设置里关闭了浏览器接管
            }

            // blob/data 抓取的文件已由宿主落盘：读出数据文件路径，走直接交付
            string? dataFile = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("DataFile", out var df) && df.GetString() is { } dfPath)
                {
                    dataFile = dfPath;
                }
            }
            catch (JsonException)
            {
                // 信封异常按普通链接处理
            }

            var queue = _uiQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                StartupLog.Write("收件箱无可用 UI 队列（跳过该条）");
                return;
            }

            queue.TryEnqueue(() => _ = ShowAndEnqueueAsync(request, dataFile));
            StartupLog.Write("收件箱已转交界面弹框");
        }
        catch (IOException)
        {
            // 文件占用等瞬态错误，放弃本条（浏览器可重新发起）
        }
        catch (Exception ex)
        {
            // v2.54：任何其他异常也不能穿出到泵循环（保持泵活着），留痕便于排查
            StartupLog.Write("收件箱条目处理异常（跳过该条）", ex);
        }
    }

    /// <summary>弹出保存位置对话框：默认定位到默认下载位置，确认后入队。
    /// blob/data 抓取的文件（dataFile 非空）则直接移动到所选目录。</summary>
    private static async Task ShowAndEnqueueAsync(BrowserRequest request, string? dataFile = null)
    {
        // v2.54：浏览器侧已经收到「已接管」应答，这里任何无声失败都是「点了没反应」——全程兜底留痕
        try
        {
            await ShowAndEnqueueCoreAsync(request, dataFile);
        }
        catch (Exception ex)
        {
            StartupLog.Write("浏览器接管保存框处理失败", ex);
        }
    }

    private static async Task ShowAndEnqueueCoreAsync(BrowserRequest request, string? dataFile = null)
    {
        if (!string.IsNullOrEmpty(dataFile))
        {
            await DeliverDataFileAsync(request, dataFile);
            return;
        }

        StartupLog.Write($"询问窗唤起：{(string.IsNullOrWhiteSpace(request.Filename) ? request.Url : request.Filename)}");
        // v2.59：改用顶级置询问窗（独立窗口、置顶、屏幕居中），主窗最小化也看得见。
        // 确认后主窗回下载页并置顶。
        if (!await AskWindow.ShowAsync("TaoDown 接管下载", BuildDetail(request.Filename, request.Url), request.Url))
        {
            StartupLog.Write("用户选择暂不，请求放弃");
            return;
        }

        try
        {
            var task = new DownloadTask
            {
                Url = request.Url,
                Kind = LinkClassifier.Classify(request.Url),
                SaveDir = App.Settings.DownloadDir,
                FileName = string.IsNullOrEmpty(request.Filename) ? null : request.Filename,
            };
            await App.Queue!.EnqueueAsync(task);
            App.Main!.RestoreFromTray();
            App.Main!.ShowDownloads();
            StartupLog.Write($"已入队：{(string.IsNullOrWhiteSpace(request.Filename) ? request.Url : request.Filename)}");
        }
        catch (Exception ex)
        {
            StartupLog.Write("接管任务入队失败", ex);
        }
    }

    /// <summary>已抓取文件：弹保存框后移动到位（重名自动加序号），不进下载队列。</summary>
    private static async Task DeliverDataFileAsync(BrowserRequest request, string dataFile)
    {
        try
        {
            await DeliverDataFileCoreAsync(request, dataFile);
        }
        catch (Exception ex)
        {
            StartupLog.Write("网页取回文件交付失败", ex);
        }
    }

    private static async Task DeliverDataFileCoreAsync(BrowserRequest request, string dataFile)
    {
        // v2.59：改用顶级置顶询问窗，确认后移动到默认下载目录；「暂不」保留文件等用户重试。
        var name = string.IsNullOrWhiteSpace(request.Filename) ? "网页文件" : request.Filename;
        if (!await AskWindow.ShowAsync("网页文件已取回", $"{name}\n该文件由浏览器页面生成，TaoDown 扩展已完整取回。"))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(App.Settings.DownloadDir);
            var targetName = string.IsNullOrEmpty(request.Filename) ? Path.GetFileName(dataFile) : request.Filename;
            targetName = BlobPayload.SanitizeFileName(targetName);
            var target = Path.Combine(App.Settings.DownloadDir, targetName);
            var stem = Path.GetFileNameWithoutExtension(targetName);
            var ext = Path.GetExtension(targetName);
            for (var n = 1; File.Exists(target); n++)
            {
                target = Path.Combine(App.Settings.DownloadDir, $"{stem} ({n}){ext}");
            }

            File.Move(dataFile, target);
            App.Main!.RestoreFromTray();
            App.Main!.ShowDownloads();
        }
        catch (Exception)
        {
            // 移动失败时保留数据文件，等用户重试
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删除失败时保留，交由系统清理
        }
    }

    private static string BuildDetail(string filename, string url)
    {
        var name = string.IsNullOrWhiteSpace(filename) ? url : filename;
        return $"{name}\n来源：{url}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher.Dispose();
        _timer.Dispose();
    }
}
