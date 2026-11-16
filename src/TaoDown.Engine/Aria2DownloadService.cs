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

        // 代理只跟 HTTP(S)/FTP 走：BT/ED2K 走代理反而拖累 DHT/节点发现；
        // 本地源（回环/私网/单标签主机名）豁免代理直连——跟随系统代理后本地请求被送进
        // 不可用代理会卡「正在连接服务器」0 B（云批实证），语义对齐浏览器「跳过本地地址」
        if ((kind == LinkKind.Http || kind == LinkKind.Ftp) && !string.IsNullOrWhiteSpace(AllProxy)
            && !ProxyBypass.ShouldBypass(task.Url))
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

    /// <summary>修红班 R3 P1-3：出错任务的一键重试。彻底断网后任务约 90 秒标错，

    /// 此前无自动恢复也无重试入口，用户只能删了重下。重试按 60 秒频控（防止连环自动重试）；

    /// 网络未恢复时重试仍会失败，如实报错，不承诺恢复时间。</summary>

    public async Task RetryFromErrorAsync(string taskId, CancellationToken ct = default)

    {

        DownloadTask? task;

        lock (_gate)

        {

            if (!_tasks.TryGetValue(taskId, out task) || task.Status != DownloadStatus.Error)

            {

                throw new InvalidOperationException("任务不存在或当前状态不可重试。");

            }

            var now = Environment.TickCount64;

            if (!RetryAllowed(now, _errorRetryTicks.TryGetValue(taskId, out var last) ? last : null, ErrorRetryCooldownMs))

            {

                throw new InvalidOperationException("重试过于频繁，请稍后再试（约 1 分钟一次）。");

            }

            _errorRetryTicks[taskId] = now;

        }

        RefreshDiag.Write($"手动重试出错任务（60 秒频控内放行一次）：{task.FileName ?? task.Url}");

        await RestartFreshAsync(task, resume: true);

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

            // 2026-11-15 修红班 R2 黄6：自愈/校验集合随任务删除一并清理，防长会话缓慢累积。

            _stallSince.Remove(taskId);

            _stallRestarts.Remove(taskId);

            _autoRestarted.Remove(taskId);

            _errorRetryTicks.Remove(taskId);

            _contentChecked.Remove(taskId);

            _diagPolled.Remove(taskId);

        }

        // 通知队列释放并发名额，避免删除活动任务后名额永久占用

        SaveSnapshot();
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

                try

                {

                    ApplyStatus(task, status);

                    DetectStall(task);

                }

                catch (Exception applyEx)

                {

                    // 状态已取回但回写/停滞判定抛异常：Updated 在 ApplyStatus 末尾才触发，
                    // 这里炸了界面就永远收不到更新（真机「正在连接服务器」卡死嫌疑链）。
                    // 必须与 RPC 失败区分开并落异常类型，空消息也要能定位。

                    if (_diagPolled.Add($"apply:{task.Id}"))

                    {

                        RefreshDiag.Write($"状态回写失败：{applyEx.GetType().Name}: {applyEx.Message} :: {applyEx.StackTrace?.Split('\n')[0]}");

                    }

                }

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

                    var reason = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;

                    RefreshDiag.Write($"TellStatus 失败（连续第 {streak} 次）{gid[..Math.Min(8, gid.Length)]}…：{reason}");

                }

            }

        }

    }

    /// <summary>

    /// 主引擎自愈重启后替换客户端并重置映射：

    /// 审查 2026-11-14 红1 落地——未终态任务不再判死，改回等待态并经 NeedsResubmit 请求重新提交

    /// （aria2 --continue 保文件断点，任务列表不丢）；由 App 侧队列接管重提。

    /// </summary>

    public void SwapPrimaryClient(Aria2Client client)

    {

        // 2026-11-15 修红班 R2 红3：锁内只收集待通知快照，事件退出锁后再触发（锁内回调处理器链直达 App 线，潜在死锁）。

        List<DownloadTask> toNotify;

        lock (_gate)

        {

            _client = client;

            _gidToTaskId.Clear();

            _gidToClient.Clear();

            _mainFailStreak = 0;

            toNotify = _tasks.Values.Where(t =>

                t.Status is DownloadStatus.Active or DownloadStatus.Waiting or DownloadStatus.Paused).ToList();

            foreach (var task in toNotify)

            {

                task.EngineId = null;

                task.Status = DownloadStatus.Waiting;

            }

        }

        foreach (var task in toNotify)

        {

            Updated?.Invoke(this, task);

            NeedsResubmit?.Invoke(this, task);

        }

    }

    /// <summary>FTP 引擎自愈重启后替换客户端；只重置 FTP 引擎的任务映射，主引擎任务不受影响。</summary>

    public void SwapFtpClient(Aria2Client? client)

    {

        // 2026-11-15 修红班 R2 红3：同 SwapPrimaryClient——锁内改状态收集快照，锁外再触发事件。

        List<DownloadTask> toNotify;

        lock (_gate)

        {

            _ftpClient = client;

            _ftpFailStreak = 0;

            // 只清 FTP 引擎名下的映射：与主引擎客户端不同的都是 FTP 任务的

            var deadGids = _gidToClient.Where(kv => !ReferenceEquals(kv.Value, _client))

                .Select(kv => kv.Key).ToList();

            toNotify = new List<DownloadTask>();

            foreach (var gid in deadGids)

            {

                _gidToClient.Remove(gid);

                if (_gidToTaskId.Remove(gid, out var taskId) &&

                    _tasks.TryGetValue(taskId, out var task) &&

                    task.Status is not (DownloadStatus.Complete or DownloadStatus.Error))

                {

                    // 审查红1 同口径：引擎重启不判死，回等待态重提

                    task.EngineId = null;

                    task.Status = DownloadStatus.Waiting;

                    toNotify.Add(task);

                }

            }

        }

        foreach (var task in toNotify)

        {

            Updated?.Invoke(this, task);

            NeedsResubmit?.Invoke(this, task);

        }

    }

    /// <summary>引擎自愈重启后：未终态任务需重新提交（App 侧接 DownloadQueue.EnqueueAsync）。</summary>

    public event EventHandler<DownloadTask>? NeedsResubmit;

    /// <summary>当前任务快照（供持久化落盘）。</summary>

    public IReadOnlyList<DownloadTask> TasksSnapshot

    {

        get { lock (_gate) { return _tasks.Values.ToList(); } }

    }

    /// <summary>

    /// 启动回灌（审查红1：重启不丢任务列表）：把快照任务重新登记；

    /// 未终态任务置回等待态由 App 侧队列重新提交，终态任务原样保留作历史。

    /// </summary>

    private readonly object _saveDebounceGate = new();
    private bool _saveDebounceQueued;
    /// <summary>修红班 R3 P0-1：快照节流窗口（毫秒）。活跃下载期间防抖被高频重触发，
    /// 节流保证每个窗口内至少落盘一次，崩溃/强杀最多丢一个窗口的进度。</summary>
    private const long SnapshotThrottleMs = 5000;
    private long _lastSnapshotSaveTick = long.MinValue / 2;
    /// <summary>退出冲刷：同步落盘当前快照（修红班 R2 黄2，App.Shutdown 调用，失败不阻断退出）。</summary>
    public void ForceSnapshotSave()
    {
        try { SaveSnapshot(); } catch { /* 快照保存失败不阻断退出 */ }
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

/// <summary>
    /// v2.55 修复「下载页卡死、任务删不掉」：状态回写路径每 800ms 在界面线程同步写盘
    /// （序列化+临时文件+File.Replace），下载高峰时界面整线程被磁盘拖死，删除/暂停全部无响应。
    /// 进度快照改为后台节流合并写：500ms 防抖合并；进度本体有 .aria2 控制文件兜底，
    /// 被杀进程最多丢几秒的列表进度数字，重启按实际文件恢复，任务不丢。
    /// 终态（完成/出错/暂停/删除）仍走同步 SaveSnapshot，保证关键状态不悬空。
    /// 修红班 R3 P0-1：防抖之外加 5 秒节流双闸——活跃下载期间 Updated 每秒重触发防抖，
    /// 原实现 timer 永不到期，tasks.json 整个下载期间零落盘，崩溃/强杀后会话恢复为空；
    /// 节流窗口（距上次成功落盘 5 秒）到点必写，防抖窗口内仍合并，高频不放大 IO。
    /// </summary>
    private void RequestSnapshotSave()
    {
        if (_store is null)
        {
            return;
        }
        lock (_saveDebounceGate)
        {
            var now = Environment.TickCount64;
            // 节流：距上次落盘不足 5 秒且已有防抖在途，不另起 timer（防抖到点会带上最新状态）
            if (SnapshotThrottleRequired(_saveDebounceQueued, now, _lastSnapshotSaveTick, SnapshotThrottleMs))
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
                lock (_saveDebounceGate)
                {
                    _lastSnapshotSaveTick = Environment.TickCount64;
                }
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

/// <summary>恢复链路交回的等待任务（App 侧创建队列后取走转交下载队列）。</summary>
    private readonly List<DownloadTask> _restoredWaiting = [];

    /// <summary>
    /// 等待队列登记：任务尚未提交引擎就进列表并落盘（状态 Waiting）。
    /// 2026-11-17 云批实锤：等待队列此前纯内存，进程被杀 40 个排队任务全灭，
    /// 快照里没有它们，恢复恒为 0，重启后 0/40 永不下载。
    /// </summary>
    public void RegisterWaiting(DownloadTask task)
    {
        bool added;
        lock (_gate)
        {
            added = _tasks.TryAdd(task.Id, task);
        }
        if (added)
        {
            task.Status = DownloadStatus.Waiting;
            Updated?.Invoke(this, task);
            SaveSnapshot(); // 关键状态同步落盘（终态同口径）：登记后即被杀也要能恢复
        }
    }

    /// <summary>取走会话恢复出的等待任务，由 App 侧转交下载队列按并发上限派发；取后清空。</summary>
    public IReadOnlyList<DownloadTask> TakeRestoredWaiting()
    {
        lock (_gate)
        {
            var list = _restoredWaiting.ToList();
            _restoredWaiting.Clear();
            return list;
        }
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
            if (task.Status == DownloadStatus.Waiting)
            {
                // 等待任务不直塞引擎：登记回列表并交 App 侧队列按并发上限派发
                lock (_gate)
                {
                    _tasks[task.Id] = task;
                    _restoredWaiting.Add(task);
                }
                restored++;
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

    public void RestorePending(IEnumerable<DownloadTask> restored)

    {

        // 2026-11-15 修红班 R2 红3：同前——锁内登记，锁外统一通知。

        List<DownloadTask> toNotify;

        lock (_gate)

        {

            toNotify = new List<DownloadTask>();

            foreach (var task in restored)

            {

                if (_tasks.ContainsKey(task.Id)) continue;

                if (task.Status is not (DownloadStatus.Complete or DownloadStatus.Error))

                {

                    task.EngineId = null;

                    task.Status = DownloadStatus.Waiting;

                }

                _tasks[task.Id] = task;

                toNotify.Add(task);

            }

        }

        foreach (var task in toNotify)

        {

            Updated?.Invoke(this, task);

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

    /// <summary>修红班 R3 P1-3：错误任务手动重试的上次尝试时刻（Ticks=TickCount64 毫秒），60 秒频控。</summary>

    private readonly Dictionary<string, long> _errorRetryTicks = new();

    private const long ErrorRetryCooldownMs = 60_000;

    // ---- 停滞自愈（2026-11-03 真机反馈「下载中 45.6% 却永远不动，文件也没下完」）----

    // RPC 正常、引擎进程活着，个别连接挂死后任务一直停在「下载中」零速度。连续 30 秒零速度

    // 的 HTTP/FTP 活动任务，自动换新号续传重下（进度不丢）；每任务最多 2 次，超限如实标错。

    private const int StallRestartLimit = 2;

    private readonly Dictionary<string, DateTime> _stallSince = new(); // taskId -> 首次零速时刻

    private readonly Dictionary<string, int> _stallRestarts = new();   // taskId -> 已自动续传次数

    private void DetectStall(DownloadTask task)

    {

        // 只盯 HTTP/FTP 下载中的零速任务：BT/磁力初期找种没速度是常态，不能误伤

        // 2026-11-15 修红班 R2 红2：停滞字典与阈值判定整体收进 _gate，与轮询/自愈并发流互斥。

        bool restart = false;

        int usedNext = 0;

        DownloadTask? exhausted = null;

        lock (_gate)

        {

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

                // 红3 同口径：只改状态，事件留给锁外统一触发（exhausted 非空即有待通知任务）。

                task.Status = DownloadStatus.Error;

                task.Error = "下载停滞且自动续传多次未恢复；建议删除本任务后重新下载。";

                exhausted = task;

                return;

            }

            _stallSince.Remove(task.Id);

            usedNext = used + 1;

            _stallRestarts[task.Id] = usedNext;

            restart = true;

        }

        if (exhausted != null)

        {

            Updated?.Invoke(this, exhausted);

            return;

        }

        if (!restart) return;

        RefreshDiag.Write($"下载停滞超过 30 秒，自动换号续传（第 {usedNext} 次）：{task.FileName ?? task.Url}");

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

            // 2026-11-15 修红班 R2 黄5：续传前先 forceRemove 旧 gid，掐掉旧连接与新任务（allow-overwrite）的竞争窗口。
            if (oldGid is { } og2)

            {

                try { await client.RemoveAsync(og2, CancellationToken.None); }

                catch (Exception removeEx) { RefreshDiag.Write($"停滞自愈移除旧 gid 未成（继续换号重下）：{removeEx.Message}"); }

            }

            var options = new Dictionary<string, string>

            {

                ["dir"] = task.SaveDir,

                ["continue"] = resume ? "true" : "false",

                ["allow-overwrite"] = "true",

            };

            var newGid = await client.AddUriAsync([task.Url], options, CancellationToken.None);

            // 2026-11-15 修红班 R2 红2：task 对象字段的改写整体收进 _gate，堵住与轮询流并发读写造成的状态跳变窗口。

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

                task.EngineId = newGid;

                task.Status = DownloadStatus.Active;

                task.Error = null;

                task.CompletedBytes = 0;

                task.TotalBytes = 0;

                task.SpeedBytesPerSec = 0;

            }

        }

        catch (Exception ex)

        {

            RefreshDiag.Write($"自动从头重下未成：{ex.Message}；保留错误态供用户手动处理");

            lock (_gate)

            {

                _autoRestarted.Remove(task.Id);

                task.Status = DownloadStatus.Error;

            }

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

    /// <summary>
    /// 假文件判定（2026-11-15 修红班 R2 红1 收口）：只在「扩展名预期二进制而内容是 HTML/JSON」冲突时判假；
    /// .json/.xml/.html/.svg 等文本类文件合法以 { 或 < 开头，不参与首字节否决。
    /// public static 供回归断言直接覆盖（对齐 BuildArguments 先例）。
    /// </summary>
    /// <summary>修红班 R3 P0-1 回归口径：防抖在途且距上次落盘不足节流窗口时才延缓落盘。</summary>
    public static bool SnapshotThrottleRequired(bool debounceQueued, long nowTick, long lastSaveTick, long throttleMs)
    {
        return debounceQueued && nowTick - lastSaveTick < throttleMs;
    }

    /// <summary>修红班 R3 P1-2 回归口径：下载文件内容校验的判定结论（空文件/假文件返回人话文案，正常返回 null）。</summary>
    public static string? SuspiciousFileError(long fileLength, string head, string ext)
    {
        if (fileLength == 0)
        {
            return "下载到的文件是空的（服务器未给出真实文件）；建议删除本任务后重试。";
        }
        if (head.Length > 0 && IsSuspiciousHeader(ext, head))
        {
            return "服务器返回的是网页而不是文件（常见于限流或链接过期）；建议删除本任务后重新下载。";
        }
        return null;
    }

    /// <summary>修红班 R3 P1-3 回归口径：出错任务手动重试是否放行（60 秒频控，首次重试放行）。</summary>
    public static bool RetryAllowed(long nowTick, long? lastRetryTick, long cooldownMs)
    {
        return lastRetryTick is null || nowTick - lastRetryTick.Value >= cooldownMs;
    }

    public static bool IsSuspiciousHeader(string ext, string head)

    {

        var expectBinary = ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)

                        || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)

                        || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase)

                        || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase)

                        || ext.Equals(".rar", StringComparison.OrdinalIgnoreCase)

                        || ext.Equals(".apk", StringComparison.OrdinalIgnoreCase)

                        || ext.Equals(".iso", StringComparison.OrdinalIgnoreCase)

                        || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

        return expectBinary && (head[0] == '<' || head.StartsWith("{", StringComparison.Ordinal));

    }

    private void ValidateDownloadedFile(DownloadTask task, string filePath)

    {

        // 修红班 R3 P1-2：本函数跑在线程池线程，原实现在锁外直接改写共享任务状态并触发 Updated，

        // 与红2/红3 同型竞态。改为：锁内落账状态与错误文案，锁外再触发 Updated（快照先建，回调不进锁）。

        string? errorText = null;

        try

        {

            if (!File.Exists(filePath))

            {

                return;

            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(512, fs.Length)];
        var read = fs.Read(buf, 0, buf.Length);
        var head = System.Text.Encoding.UTF8.GetString(buf, 0, read).TrimStart();

        // 2026-11-15 修红班 R2 红1：假文件识别只在「扩展名与内容冲突」时判错——
        // .json/.xml/.html/.svg 等文本类文件本身合法以 { 或 < 开头，不能按首字节一票否决（曾 100% 误杀 .json 下载）。
        // 修红班 R3 P1-2：判定结论收进纯函数（FixR3Tests 定向回归），本函数只负责文件读取与状态落账。
        errorText = SuspiciousFileError(fs.Length, head, Path.GetExtension(filePath));
    }

        catch (Exception)

        {

            // 识别属于增强校验，任何失败都不影响任务本身

        }

        if (errorText is null)

        {

            return;

        }

        lock (_gate)

        {

            task.Status = DownloadStatus.Error;

            task.Error = errorText;

        }

        Updated?.Invoke(this, task);

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

        DownloadTask? task;

        lock (_gate)

        {

            _tasks.TryGetValue(taskId, out task);

            if (task != null)

            {

                task.Status = status;

            }

        }

        if (task != null)

        {

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
        }

    }

    public void Dispose()

    {

        // 生命周期终点同步冲刷快照：防抖窗口内未落盘的最后状态不能随进程消失（合流班口径，与退出冲刷同语义）。
        SaveSnapshot();
        _client.Dispose();

        _ftpClient?.Dispose();

    }

}
