using System.Text.Json;

using TaoDown.Core;



namespace TaoDown.Engine;



/// <summary>

/// 基于双引擎（aria2-next + 经典 aria2）的下载服务实现：

/// HTTP(S)/BT/磁力/ed2k 路由到 aria2-next（原生 ED2K），FTP 路由到经典 aria2；

/// 任务 <-> gid 映射，轮询状态并转发 Updated 事件。

/// </summary>

public sealed class Aria2DownloadService : IDownloadService, IDisposable

{

    private Aria2Client _client;

    private Aria2Client? _ftpClient; // 经典 aria2，仅 FTP 任务使用；null 时 FTP 给明确错误

    private readonly Dictionary<string, DownloadTask> _tasks = []; // taskId -> task

    private readonly Dictionary<string, string> _gidToTaskId = []; // gid -> taskId

    private readonly Dictionary<string, Aria2Client> _gidToClient = []; // gid -> 所属引擎客户端

    private readonly object _gate = new();



    public event EventHandler<DownloadTask>? Updated;



    /// <inheritdoc />

    public event EventHandler<string>? Removed;



    /// <summary>

    /// 跟随系统代理的 HTTP 代理地址（App 启动时注入；null=引擎直连）。

    /// 仅对 HTTP(S)/FTP 任务生效（按任务下发的 all-proxy），BT/ED2K 不经过代理。

    /// 背景：系统代理/VPN 场景下浏览器走代理正常、程序直连被黑洞——真机「正在连接服务器」卡死主因之一。

    /// </summary>

    public string? AllProxy { get; set; }



    public Aria2DownloadService(Aria2Client client, Aria2Client? ftpClient = null, DownloadTaskStore? store = null)

    {

        _client = client;

        _ftpClient = ftpClient;

        _store = store;

    }

    private readonly DownloadTaskStore? _store;



    private int _mainFailStreak;

    private int _ftpFailStreak;



    /// <summary>主引擎连续 RPC 失败次数（任一成功即归零）；达到阈值由界面层触发自愈重启。</summary>

    public int MainRpcFailStreak

    {

        get { lock (_gate) { return _mainFailStreak; } }

    }



    /// <summary>FTP 引擎连续 RPC 失败次数（任一成功即归零）。</summary>

    public int FtpRpcFailStreak

    {

        get { lock (_gate) { return _ftpFailStreak; } }

    }



    public async Task<string> AddAsync(DownloadTask task, CancellationToken ct = default)

    {

        var kind = task.Kind == LinkKind.Unknown ? LinkClassifier.Classify(task.Url) : task.Kind;

        var downloadOptions = new Dictionary<string, string>();

        if (!string.IsNullOrWhiteSpace(task.SaveDir))

        {

            downloadOptions["dir"] = task.SaveDir;

        }



        var client = kind switch

        {

            LinkKind.Ftp => _ftpClient ?? throw new InvalidOperationException(

                "FTP 引擎组件缺失（aria2c.exe），请重新安装完整版后重试。"),

            _ => _client,

        };



        // 代理只跟 HTTP(S)/FTP 走：BT/ED2K 走代理反而拖累 DHT/节点发现

        if ((kind == LinkKind.Http || kind == LinkKind.Ftp) && !string.IsNullOrWhiteSpace(AllProxy))

        {

            downloadOptions["all-proxy"] = AllProxy;

        }



        var gid = kind switch

        {

            LinkKind.BitTorrent when task.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) =>

                await client.AddUriAsync([task.Url], downloadOptions, ct),

            LinkKind.Http or LinkKind.Ftp or LinkKind.BitTorrent or LinkKind.Ed2k =>

                await client.AddUriAsync([task.Url], downloadOptions, ct),

            _ => throw new InvalidOperationException($"暂不支持的链接类型：{kind}"),

        };



        task.EngineId = gid;

        task.Status = DownloadStatus.Active;

        lock (_gate)

        {

            _tasks[task.Id] = task;

            _gidToTaskId[gid] = task.Id;

            _gidToClient[gid] = client;

        }

        SaveSnapshot();

        return task.Id;

    }



    /// <summary>添加本地种子文件，返回任务 Id。</summary>

    public async Task<string> AddTorrentAsync(byte[] torrentBytes, string saveDir, CancellationToken ct = default)

    {

        var task = new DownloadTask

        {

            Url = "torrent:" + Guid.NewGuid().ToString("N"),

            Kind = LinkKind.BitTorrent,

            SaveDir = saveDir,

        };

        var gid = await _client.AddTorrentAsync(torrentBytes, ct);

        task.EngineId = gid;

        task.Status = DownloadStatus.Active;

        lock (_gate)

        {

            _tasks[task.Id] = task;

            _gidToTaskId[gid] = task.Id;

            _gidToClient[gid] = _client;

        }

        SaveSnapshot();

        return task.Id;

    }



    public async Task PauseAsync(string taskId, CancellationToken ct = default)

    {

        var gid = RequireGid(taskId);

        try

        {

            await ResolveClient(gid).PauseAsync(gid, ct);

        }

        catch (Exception)

        {

            // 引擎侧状态不同步（如任务刚完成/引擎刚重启）：强制暂停兜底，真实状态由刷新纠正

            try

            {

                await ResolveClient(gid).ForcePauseAsync(gid, ct);

            }

            catch (Exception)

            {

                // v2.54：正在连接中的任务在连接建立前普通/强制暂停都会被引擎拒绝（真机实证），

                // 不把生硬报错甩给界面，给一句人话指引；想立刻停止该任务走删除（forceRemove 永远可用）

                throw new InvalidOperationException("任务正在连接服务器，这几秒暂停不了；稍等片刻再暂停，或直接删除任务。");

            }

        }



        MarkStatus(taskId, DownloadStatus.Paused);

    }



    public async Task ResumeAsync(string taskId, CancellationToken ct = default)

    {

        var gid = RequireGid(taskId);

        await ResolveClient(gid).ResumeAsync(gid, ct);

        MarkStatus(taskId, DownloadStatus.Active);

    }



    /// <summary>暂停所有进行中的任务；逐个尝试，个别失败不影响其余。</summary>

    public async Task<int> PauseAllAsync(CancellationToken ct = default)

    {

        List<DownloadTask> targets;

        lock (_gate)

        {

            targets = [.. _tasks.Values.Where(t =>

                t.EngineId is { } && t.Status is DownloadStatus.Waiting or DownloadStatus.Active)];

        }



        var done = 0;

        foreach (var task in targets)

        {

            try

            {

                await PauseAsync(task.Id, ct);

                done++;

            }

            catch (Exception)

            {

                // 单个失败继续处理下一个

            }

        }



        return done;

    }



    /// <summary>继续所有已暂停的任务；逐个尝试，个别失败不影响其余。</summary>

    public async Task<int> ResumeAllAsync(CancellationToken ct = default)

    {

        List<DownloadTask> targets;

        lock (_gate)

        {

            targets = [.. _tasks.Values.Where(t =>

                t.EngineId is { } && t.Status == DownloadStatus.Paused)];

        }



        var done = 0;

        foreach (var task in targets)

        {

            try

            {

                await ResumeAsync(task.Id, ct);

                done++;

            }

            catch (Exception)

            {

                // 单个失败继续处理下一个

            }

        }



        return done;

    }



    /// <summary>清理已完成与出错的任务（从列表移除，文件保留在磁盘）。</summary>

    public async Task<int> ClearFinishedAsync(CancellationToken ct = default)

    {

        List<DownloadTask> targets;

        lock (_gate)

        {

            targets = [.. _tasks.Values.Where(t => t.Status is DownloadStatus.Complete or DownloadStatus.Error)];

        }



        var removed = 0;

        foreach (var task in targets)

        {

            try

            {

                await RemoveAsync(task.Id, ct);

                removed++;

            }

            catch (Exception)

            {

                // 单个失败继续处理下一个

            }

        }



        return removed;

    }



    public async Task RemoveAsync(string taskId, CancellationToken ct = default)

    {

        var gid = RequireGid(taskId);

        var client = ResolveClient(gid);

        try

        {

            await client.RemoveAsync(gid, ct); // forceRemove：活动/等待/暂停都接受

        }

        catch (Exception)

        {

            // 已完成/出错的任务在引擎侧只剩“下载结果”，forceRemove 会拒绝：改走 removeDownloadResult；

            // 连引擎都没响应时任务必然早已不在跑，本地照常移除，绝不让“点了没反应”重演

            try

            {

                await client.RemoveResultAsync(gid, ct);

            }

            catch (Exception)

            {

                // 引擎侧清不掉：仅跳过，本地仍移除

            }

        }



        lock (_gate)

        {

            _gidToTaskId.Remove(gid);

            _gidToClient.Remove(gid);

            _tasks.Remove(taskId);

        }

        SaveSnapshot();

        // 通知队列释放并发名额，避免删除活动任务后名额永久占用

        Removed?.Invoke(this, taskId);

    }



    public Task<IReadOnlyList<DownloadTask>> ListAsync(CancellationToken ct = default)

    {

        lock (_gate)

        {

            IReadOnlyList<DownloadTask> list = [.. _tasks.Values];

            return Task.FromResult(list);

        }

    }



    /// <summary>刷新所有活动任务的进度（由界面定时器调用）。</summary>

    public async Task RefreshAsync(CancellationToken ct = default)

    {

        List<(string Gid, DownloadTask Task)> snapshot;

        try

        {

            lock (_gate)

            {

                snapshot = [.. _gidToTaskId.Select(kv => (kv.Key, _tasks[kv.Value]))];

            }

        }

        catch (Exception ex)

        {

            // 映射与任务表不一致这类结构性问题会被上层静默吞掉，先落黑匣子再原样上抛

            RefreshDiag.Write($"轮询快照构建失败：{ex.GetType().Name}: {ex.Message}");

            throw;

        }



        RefreshDiag.Heartbeat(snapshot.Count);



        foreach (var (gid, task) in snapshot)

        {

            ct.ThrowIfCancellationRequested();

            // 终态任务（已完成/出错）不会再变化，跳过轮询，把请求留给活动任务

            if (task.Status is DownloadStatus.Complete or DownloadStatus.Error)

            {

                continue;

            }



            var client = ResolveClient(gid);

            try

            {

                var status = await client.TellStatusAsync(gid, ct);

                lock (_gate)

                {

                    if (ReferenceEquals(client, _client))

                    {

                        _mainFailStreak = 0;

                    }

                    else

                    {

                        _ftpFailStreak = 0;

                    }

                }



                if (_diagPolled.Add(gid))

                {

                    RefreshDiag.Write($"TellStatus 成功：{gid[..Math.Min(8, gid.Length)]}…，状态已交给界面");

                }



                ApplyStatus(task, status);

                DetectStall(task);

            }

            catch (OperationCanceledException) when (ct.IsCancellationRequested)

            {

                throw;

            }

            catch (Exception ex)

            {

                // RPC 暂不可用/网络抖动时保留上一状态，下轮再刷新；绝不把异常抛给界面层。

                // 连续失败按引擎分别计数，供界面层触发引擎自愈（v2.54：引擎死了不再无声挂掉）

                lock (_gate)

                {

                    if (ReferenceEquals(client, _client))

                    {

                        _mainFailStreak++;

                    }

                    else

                    {

                        _ftpFailStreak++;

                    }

                }



                // 真机黑匣子：首次失败与每第 20 次连续失败落原文，定位「页面不更新」卡在哪一环

                var streak = ReferenceEquals(client, _client) ? _mainFailStreak : _ftpFailStreak;

                if (streak == 1 || streak % 20 == 0)

                {

                    RefreshDiag.Write($"TellStatus 失败（连续第 {streak} 次）{gid[..Math.Min(8, gid.Length)]}…：{ex.Message}");

                }

            }

        }

    }



    /// <summary>

    /// 主引擎自愈重启后替换客户端并重置映射（aria2 无会话恢复，引擎侧任务已随进程消失）：

    /// 未终态任务如实标记为出错并给出下一步指引，绝不留在旧状态里无声无息。

    /// </summary>

    public void SwapPrimaryClient(Aria2Client client)

    {

        lock (_gate)

        {

            _client = client;

            _gidToTaskId.Clear();

            _gidToClient.Clear();

            _mainFailStreak = 0;

            foreach (var task in _tasks.Values.Where(t =>

                t.Status is not (DownloadStatus.Complete or DownloadStatus.Error)).ToList())

            {

                task.Status = DownloadStatus.Error;

                task.Error = "引擎已自动重启，本任务中断；请删除本任务后重新添加下载。";

                Updated?.Invoke(this, task);

            }

        }

    }



    /// <summary>FTP 引擎自愈重启后替换客户端；只重置 FTP 引擎的任务映射，主引擎任务不受影响。</summary>

    public void SwapFtpClient(Aria2Client? client)

    {

        lock (_gate)

        {

            _ftpClient = client;

            _ftpFailStreak = 0;

            // 只清 FTP 引擎名下的映射：与主引擎客户端不同的都是 FTP 任务的

            var deadGids = _gidToClient.Where(kv => !ReferenceEquals(kv.Value, _client))

                .Select(kv => kv.Key).ToList();

            foreach (var gid in deadGids)

            {

                _gidToClient.Remove(gid);

                if (_gidToTaskId.Remove(gid, out var taskId) &&

                    _tasks.TryGetValue(taskId, out var task) &&

                    task.Status is not (DownloadStatus.Complete or DownloadStatus.Error))

                {

                    task.Status = DownloadStatus.Error;

                    task.Error = "FTP 引擎已自动重启，本任务中断；请删除本任务后重新添加下载。";

                    Updated?.Invoke(this, task);

                }

            }

        }

    }



    private Aria2Client ResolveClient(string gid)

    {

        lock (_gate)

        {

            return _gidToClient.TryGetValue(gid, out var client) ? client : _client;

        }

    }



    private readonly HashSet<string> _autoRestarted = new();



    // ---- 停滞自愈（2026-11-03 真机反馈「下载中 45.6% 却永远不动，文件也没下完」）----

    // RPC 正常、引擎进程活着，个别连接挂死后任务一直停在「下载中」零速度。连续 30 秒零速度

    // 的 HTTP/FTP 活动任务，自动换新号续传重下（进度不丢）；每任务最多 2 次，超限如实标错。

    private const int StallRestartLimit = 2;

    private readonly Dictionary<string, DateTime> _stallSince = new(); // taskId -> 首次零速时刻

    private readonly Dictionary<string, int> _stallRestarts = new();   // taskId -> 已自动续传次数



    private void DetectStall(DownloadTask task)

    {

        // 只盯 HTTP/FTP 下载中的零速任务：BT/磁力初期找种没速度是常态，不能误伤

        var stalled = task.Status == DownloadStatus.Active && task.SpeedBytesPerSec <= 0 &&

                      (task.Kind == LinkKind.Http || task.Kind == LinkKind.Ftp);

        if (!stalled)

        {

            _stallSince.Remove(task.Id);

            return;

        }



        var now = DateTime.UtcNow;

        if (!_stallSince.TryGetValue(task.Id, out var since))

        {

            _stallSince[task.Id] = now;

            return;

        }



        if ((now - since).TotalSeconds < 30)

        {

            return;

        }



        if (_stallRestarts.TryGetValue(task.Id, out var used) && used >= StallRestartLimit)

        {

            task.Status = DownloadStatus.Error;

            task.Error = "下载停滞且自动续传多次未恢复；建议删除本任务后重新下载。";

            Updated?.Invoke(this, task);

            return;

        }



        _stallSince.Remove(task.Id);

        _stallRestarts[task.Id] = used + 1;

        RefreshDiag.Write($"下载停滞超过 30 秒，自动换号续传（第 {used + 1} 次）：{task.FileName ?? task.Url}");

        _ = RestartFreshAsync(task, resume: true);

    }



    /// <summary>续传被拒后的自动自愈：同一地址从头重下（覆盖旧文件），每任务至多一次，失败则回落为错误+人话指引。

    /// resume=true 时改为续传重下（进度不丢），用于「下载中却停滞不动」的任务级自愈。</summary>

    private async Task RestartFreshAsync(DownloadTask task, bool resume = false)

    {

        try

        {

            var oldGid = task.EngineId;

            var client = oldGid is { } g ? ResolveClient(g) : _client;

            var options = new Dictionary<string, string>

            {

                ["dir"] = task.SaveDir,

                ["continue"] = resume ? "true" : "false",

                ["allow-overwrite"] = "true",

            };

            var newGid = await client.AddUriAsync([task.Url], options, CancellationToken.None);

            lock (_gate)

            {

                if (oldGid is { } og)

                {

                    _gidToTaskId.Remove(og);

                    _gidToClient.Remove(og);

                }



                _tasks[task.Id] = task;

                _gidToTaskId[newGid] = task.Id;

                _gidToClient[newGid] = client;

            }



            task.EngineId = newGid;

            task.Status = DownloadStatus.Active;

            task.Error = null;

            task.CompletedBytes = 0;

            task.TotalBytes = 0;

            task.SpeedBytesPerSec = 0;

            SaveSnapshot();
        }

        catch (Exception ex)

        {

            RefreshDiag.Write($"自动从头重下未成：{ex.Message}；保留错误态供用户手动处理");

            _autoRestarted.Remove(task.Id);

            task.Status = DownloadStatus.Error;

            task.Error = EngineErrorText.Friendly("cannot be continued");

        }

    }



    private void ApplyStatus(DownloadTask task, JsonElement status)

    {

        if (status.TryGetProperty("totalLength", out var total) && long.TryParse(total.GetString(), out var totalBytes))

        {

            task.TotalBytes = totalBytes;

        }



        if (status.TryGetProperty("completedLength", out var done) && long.TryParse(done.GetString(), out var doneBytes))

        {

            task.CompletedBytes = doneBytes;

        }



        if (status.TryGetProperty("downloadSpeed", out var speed) && long.TryParse(speed.GetString(), out var speedBytes))

        {

            task.SpeedBytesPerSec = speedBytes;

        }



        if (status.TryGetProperty("uploadSpeed", out var up) && long.TryParse(up.GetString(), out var upBytes))

        {

            task.UploadSpeedBytesPerSec = upBytes;

        }



        if (status.TryGetProperty("connections", out var conn) && int.TryParse(conn.GetString(), out var connections))

        {

            task.Connections = connections;

        }



        if (status.TryGetProperty("numSeeders", out var seed) && int.TryParse(seed.GetString(), out var seeders))

        {

            task.Seeders = seeders;

        }



        if (status.TryGetProperty("errorMessage", out var err) && err.GetString() is { } msg && msg.Length > 0)

        {

            // 自动应对（2026-11-02 用户定调：要结果与反馈，不只给报错）：

            // 服务器拒绝续传（同名文件重下/不支持 Range）→ 不甩报错，自动从头重下一次（覆盖旧文件）。

            if (EngineErrorText.ResumeRejected(msg) && _autoRestarted.Add(task.Id))

            {

                RefreshDiag.Write($"续传被拒，自动从头重下（覆盖旧文件）：{task.FileName ?? task.Url}");

                _ = RestartFreshAsync(task);

                return;

            }



            task.Error = EngineErrorText.Friendly(msg); // 行内展示人话；原文仍在引擎日志

        }



        if (status.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)

        {

            var first = files.EnumerateArray().FirstOrDefault();

            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("path", out var path))

            {

                var p = path.GetString();

                if (!string.IsNullOrEmpty(p))

                {

                    task.FilePath = p;

                    // 兼容 Windows 反斜杠与正斜杠（跨平台路径安全）

                    var sep = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));

                    task.FileName = sep >= 0 ? p[(sep + 1)..] : p;

                }

            }

        }



        task.Status = status.TryGetProperty("status", out var st) && st.GetString() is string s

            ? s switch

            {

                "active" => DownloadStatus.Active,

                "waiting" => DownloadStatus.Waiting,

                "paused" => DownloadStatus.Paused,

                "error" => DownloadStatus.Error,

                "complete" => DownloadStatus.Complete,

                _ => task.Status,

            }

            : task.Status;



        Updated?.Invoke(this, task);

        // v2.55：进度拍走后台节流写盘，终态（暂停/出错/完成）立即同步落盘
        if (task.Status is DownloadStatus.Paused or DownloadStatus.Error or DownloadStatus.Complete)
        {
            SaveSnapshot();
        }
        else
        {
            RequestSnapshotSave();
        }

        // 完成→回读文件头识别假文件（只校验一次）

        if (task.Status == DownloadStatus.Complete &&

            task.FilePath is { } filePath && _contentChecked.Add(task.Id))

        {

            _ = Task.Run(() => ValidateDownloadedFile(task, filePath));

        }

    }



    private readonly HashSet<string> _contentChecked = new();



    /// <summary>黑匣子用：记录已成功轮询过的 gid，只落首次成功一条，避免刷屏。</summary>

    private readonly HashSet<string> _diagPolled = new();



    /// <summary>

    /// 下载完成后的「假文件」识别（v2.54）：个别服务器在限流/异常时会返回一个小网页冒充文件

    /// （真机实证：ark.m.qq.com 返回 1KB 名为 Downloader 的 HTML），aria2 照单全收标完成。

    /// 完成时回读文件头，发现是网页就如实把任务标错并提示重下，不让用户拿到打不开的空文件。

    /// </summary>

    private void ValidateDownloadedFile(DownloadTask task, string filePath)

    {

        try

        {

            if (!File.Exists(filePath))

            {

                return;

            }



            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (fs.Length == 0)

            {

                task.Status = DownloadStatus.Error;

                task.Error = "下载到的文件是空的（服务器未给出真实文件）；建议删除本任务后重试。";

                Updated?.Invoke(this, task);

                return;

            }



            var buf = new byte[Math.Min(512, fs.Length)];

            var read = fs.Read(buf, 0, buf.Length);

            var head = System.Text.Encoding.UTF8.GetString(buf, 0, read).TrimStart();

            if (head.Length > 0 && (head[0] == '<' || head.StartsWith("{", StringComparison.Ordinal)))

            {

                task.Status = DownloadStatus.Error;

                task.Error = "服务器返回的是网页而不是文件（常见于限流或链接过期）；建议删除本任务后重新下载。";

                Updated?.Invoke(this, task);

            }

        }

        catch (Exception)

        {

            // 识别属于增强校验，任何失败都不影响任务本身

        }

    }



    /// <summary>把当前任务快照落盘（异步旁路，失败只落黑匣子，不影响下载主链路）。</summary>
    private void SaveSnapshot()
    {
        if (_store is null)
        {
            return;
        }

        List<DownloadTask> snapshot;
        lock (_gate)
        {
            snapshot = [.. _tasks.Values];
        }

        try
        {
            _store.Save(snapshot);
        }
        catch (Exception)
        {
            // Store.Save 内部已自捕异常，这里再兜一层，绝不让快照打断下载
        }
    }

    private readonly object _saveDebounceGate = new();
    private bool _saveDebounceQueued;

    /// <summary>
    /// v2.55 修复「下载页卡死、任务删不掉」：状态回写路径每 800ms 在界面线程同步写盘
    /// （序列化+临时文件+File.Replace），下载高峰时界面整线程被磁盘拖死，删除/暂停全部无响应。
    /// 进度快照改为后台节流合并写：500ms 防抖合并；进度本体有 .aria2 控制文件兜底，
    /// 被杀进程最多丢几秒的列表进度数字，重启按实际文件恢复，任务不丢。
    /// 终态（完成/出错/暂停/删除）仍走同步 SaveSnapshot，保证关键状态不悬空。
    /// </summary>
    private void RequestSnapshotSave()
    {
        if (_store is null)
        {
            return;
        }

        lock (_saveDebounceGate)
        {
            if (_saveDebounceQueued)
            {
                return;
            }
            _saveDebounceQueued = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                SaveSnapshot();
            }
            catch (Exception)
            {
                // 快照保存失败不追溯也不打断下载，下次状态变化再写
            }
            finally
            {
                lock (_saveDebounceGate)
                {
                    _saveDebounceQueued = false;
                }
            }
        });
    }

    /// <summary>
    /// 应用启动时的会话恢复：把上次退出前的任务列表接回来。
    /// 未终态任务重新提交引擎（aria2 无跨进程会话，旧 gid 已失效，一律换新号续传，进度靠 .aria2 控制文件不丢）；
    /// 暂停任务重提交后立即再暂停；本地种子（torrent: 前缀，种子文件不落盘）无法重提交，如实标错并给出指引；
    /// 终态任务只回填列表条目。返回成功恢复的未终态任务数。
    /// </summary>
    public async Task<int> RestorePendingAsync(CancellationToken ct = default)
    {
        if (_store is null)
        {
            return 0;
        }

        var snapshot = _store.Load();
        var restored = 0;
        foreach (var task in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            if (_tasks.ContainsKey(task.Id))
            {
                continue; // 已在列表（如恢复与新增竞态）：以现有为准，不重复
            }

            if (task.Status is DownloadStatus.Complete or DownloadStatus.Error)
            {
                lock (_gate)
                {
                    _tasks[task.Id] = task;
                }
                continue;
            }

            if (task.Url.StartsWith("torrent:", StringComparison.OrdinalIgnoreCase))
            {
                task.Status = DownloadStatus.Error;
                task.Error = "本地种子任务随上次退出中断（种子文件不落盘）；请重新添加该种子继续下载。";
                lock (_gate)
                {
                    _tasks[task.Id] = task;
                }
                Updated?.Invoke(this, task);
                SaveSnapshot();
                continue;
            }

            try
            {
                var wasPaused = task.Status == DownloadStatus.Paused; // AddAsync 会把状态改写为 Active，先记住原状
                await AddAsync(task, ct);
                if (wasPaused)
                {
                    try
                    {
                        await PauseAsync(task.Id, ct);
                    }
                    catch (Exception)
                    {
                        // 任务刚提交还在连接期，暂停会被引擎拒绝（v2.54 真机实证）；
                        // 下一轮刷新以引擎真实状态为准，恢复语义不因这几秒打折
                    }
                }
                restored++;
            }
            catch (Exception ex)
            {
                // 单个恢复失败不拖累整批；保留原状态并如实标错
                task.Status = DownloadStatus.Error;
                task.Error = EngineErrorText.Friendly(ex.Message);
                lock (_gate)
                {
                    _tasks[task.Id] = task;
                }
                Updated?.Invoke(this, task);
                SaveSnapshot();
                RefreshDiag.Write($"任务恢复失败：{task.FileName ?? task.Url}：{ex.Message}");
            }
        }

        SaveSnapshot();
        if (restored > 0)
        {
            RefreshDiag.Write($"会话恢复：{restored} 个未完成任务已重新提交引擎续传");
        }
        return restored;
    }

    private string RequireGid(string taskId)

    {

        lock (_gate)

        {

            if (_tasks.TryGetValue(taskId, out var task) && task.EngineId is { } gid)

            {

                return gid;

            }

        }



        throw new KeyNotFoundException($"任务不存在或未提交引擎：{taskId}");

    }



    private void MarkStatus(string taskId, DownloadStatus status)

    {

        lock (_gate)

        {

            if (_tasks.TryGetValue(taskId, out var task))

            {

                task.Status = status;

                Updated?.Invoke(this, task);

            }

        }

        SaveSnapshot();

    }



    public void Dispose()

    {

        _client.Dispose();

        _ftpClient?.Dispose();

    }

}

