using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using TaoDown.Core;
using TaoDown.Engine;

using Xunit;

namespace TaoDown.Tests.Integration;

/// <summary>
/// 会话恢复（2026-11-13 第一刀）定向回归：
/// 快照保存/读取往返、损坏文件容错、重启后未终态任务重提交续传、
/// 暂停任务恢复后保持暂停、本地种子任务如实标错、终态任务只回填不重提交。
/// </summary>
public sealed class TaskRestoreTests : IDisposable
{
    private readonly StubAria2Server _server = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "taodown-restore-" + Guid.NewGuid().ToString("N"));

    public TaskRestoreTests()
    {
        _server.StartAsync().Wait();
    }

    public void Dispose()
    {
        _server.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private DownloadTaskStore NewStore() => new(_dir);

    [Fact]
    public void S1_SnapshotRoundTrip_KeepsFields()
    {
        var store = NewStore();
        var task = new DownloadTask
        {
            Url = "http://a.com/f.zip",
            Kind = LinkKind.Http,
            SaveDir = @"D:\Downloads",
            Status = DownloadStatus.Paused,
            EngineId = "g1",
            FileName = "f.zip",
            FilePath = @"D:\Downloads\f.zip",
            TotalBytes = 1000,
            CompletedBytes = 400,
            Error = null,
        };
        store.Save([task]);

        var loaded = store.Load();
        var back = Assert.Single(loaded);
        Assert.Equal(task.Id, back.Id);
        Assert.Equal(task.Url, back.Url);
        Assert.Equal(task.Kind, back.Kind);
        Assert.Equal(task.SaveDir, back.SaveDir);
        Assert.Equal(DownloadStatus.Paused, back.Status);
        Assert.Equal("g1", back.EngineId);
        Assert.Equal("f.zip", back.FileName);
        Assert.Equal(400, back.CompletedBytes);
        Assert.Equal(1000, back.TotalBytes);
    }

    [Fact]
    public void S2_CorruptSnapshot_ReturnsEmpty()
    {
        var store = NewStore();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "{ not valid json");
        Assert.Empty(store.Load());
    }

    [Fact]
    public void S3_MissingSnapshot_ReturnsEmpty()
    {
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public async Task S4_Restore_ResubmitsPendingTasks()
    {
        // 第一段：上一次会话，两个未终态任务落快照
        var store = NewStore();
        using (var old = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            await old.AddAsync(StubAria2Server.NewTask("http://a.com/one.zip"));
            var paused = StubAria2Server.NewTask("http://a.com/two.zip");
            await old.AddAsync(paused);
            await old.PauseAsync(paused.Id);
        }

        var afterQuit = store.Load();
        Assert.Equal(2, afterQuit.Count);
        _server.AddUriCalls = 0;

        // 第二段：重启后新服务实例，恢复未终态任务
        using (var fresh = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            var restored = await fresh.RestorePendingAsync();
            Assert.Equal(2, restored);
            Assert.Equal(2, _server.AddUriCalls); // 未终态任务全部重提交

            var list = await fresh.ListAsync();
            Assert.Equal(2, list.Count);
        }
    }

    [Fact]
    public async Task S5_Restore_PausedTaskStaysPaused()
    {
        var store = NewStore();
        using (var old = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            var paused = StubAria2Server.NewTask("http://a.com/hold.zip");
            await old.AddAsync(paused);
            await old.PauseAsync(paused.Id);
        }

        var pauseCallsBefore = _server.PauseCalls;
        using (var fresh = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            await fresh.RestorePendingAsync();
            Assert.Equal(pauseCallsBefore + 1, _server.PauseCalls); // 恢复后立即再暂停
        }
    }

    [Fact]
    public async Task S6_Restore_LocalTorrentMarkedErrorNotResubmitted()
    {
        var store = NewStore();
        var torrent = new DownloadTask
        {
            Url = "torrent:" + Guid.NewGuid().ToString("N"),
            Kind = LinkKind.BitTorrent,
            SaveDir = @"D:\Downloads",
        };
        store.Save([torrent]);
        _server.AddUriCalls = 0;

        using (var fresh = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            var restored = await fresh.RestorePendingAsync();
            Assert.Equal(0, restored);
            Assert.Equal(0, _server.AddUriCalls); // 种子任务不重提交

            var list = await fresh.ListAsync();
            var back = Assert.Single(list);
            Assert.Equal(DownloadStatus.Error, back.Status);
            Assert.Contains("重新添加", back.Error);
        }
    }

    [Fact]
    public async Task S7_Restore_FinishedTasksOnlyBackfilled()
    {
        var store = NewStore();
        var done = new DownloadTask
        {
            Url = "http://a.com/done.zip",
            Kind = LinkKind.Http,
            Status = DownloadStatus.Complete,
            SaveDir = @"D:\Downloads",
        };
        var failed = new DownloadTask
        {
            Url = "http://a.com/bad.zip",
            Kind = LinkKind.Http,
            Status = DownloadStatus.Error,
            SaveDir = @"D:\Downloads",
        };
        store.Save([done, failed]);
        _server.AddUriCalls = 0;

        using (var fresh = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            var restored = await fresh.RestorePendingAsync();
            Assert.Equal(0, restored);
            Assert.Equal(0, _server.AddUriCalls);

            var list = await fresh.ListAsync();
            Assert.Equal(2, list.Count); // 条目照常回填，用户看得到历史
        }
    }

    [Fact]
    public async Task S8_Restore_WithoutStore_NoOp()
    {
        using var service = new Aria2DownloadService(new Aria2Client(StubAria2Server.OptionsFor(_server)));
        Assert.Equal(0, await service.RestorePendingAsync());
    }

    [Fact]
    public async Task S9_Restore_ReplaceEngineGid_ResubmitsWithFreshGid()
    {
        // 重启恢复后任务拿到新 gid：aria2 无跨进程会话，旧 gid 必然失效，重提交是新号
        var store = NewStore();
        using (var old = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            var task = StubAria2Server.NewTask("http://a.com/new-gid.zip");
            await old.AddAsync(task);
            var oldGid = task.EngineId;
            Assert.False(string.IsNullOrEmpty(oldGid));
        }

        _server.AddUriCalls = 0;
        using (var fresh = new Aria2DownloadService(
                   new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store))
        {
            await fresh.RestorePendingAsync();
            var list = await fresh.ListAsync();
            var back = Assert.Single(list);
            // 恢复即重新提交引擎（stub 每次新任务从 g1 起算），拿到的号是本会话新提交的号；
            // 引擎无跨进程会话，旧号必然失效，语义上新号即新任务
            Assert.Equal($"g{_server.AddUriCalls}", back.EngineId);
            Assert.Equal(DownloadStatus.Active, back.Status); // 重提交后进入活动态，进度靠续传控制文件接回
        }
    }
    [Fact]
    public void S10_RegisterWaiting_PersistsSnapshotAsWaiting()
    {
        // 2026-11-17 云批实锤：等待队列此前纯内存，进程被杀 40 个排队任务全灭。
        // 等待登记必须立刻进快照（状态 Waiting），崩溃后才有得恢复。
        var store = NewStore();
        using var svc = new Aria2DownloadService(
            new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store);
        var task = StubAria2Server.NewTask("http://a.com/queued.zip");
        svc.RegisterWaiting(task);

        var snap = store.Load();
        var back = Assert.Single(snap);
        Assert.Equal(task.Id, back.Id);
        Assert.Equal(DownloadStatus.Waiting, back.Status);
    }

    [Fact]
    public async Task S11_Restore_WaitingTaskGoesToQueueNotEngine()
    {
        // 等待任务恢复时不直塞引擎：交 App 侧队列按并发上限派发
        var store = NewStore();
        var queued = StubAria2Server.NewTask("http://a.com/waiting.zip");
        queued.Status = DownloadStatus.Waiting;
        store.Save([queued]);
        _server.AddUriCalls = 0;

        using var fresh = new Aria2DownloadService(
            new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store);
        var restored = await fresh.RestorePendingAsync();
        Assert.Equal(1, restored);
        Assert.Equal(0, _server.AddUriCalls); // 未提交引擎
        var taken = Assert.Single(fresh.TakeRestoredWaiting());
        Assert.Equal(queued.Id, taken.Id);
        Assert.Equal(DownloadStatus.Waiting, taken.Status);
        Assert.Empty(fresh.TakeRestoredWaiting()); // 取后清空，不重复交回
    }

    [Fact]
    public async Task S12_TransientError_NotTerminal_SchedulesBackoffResubmit()
    {
        // 云批 44022149029 实锤：崩溃恢复后断源窗口内连接拒绝把重试烧光被判终态失败，
        // 源恢复后对账只重泵等待队列，7 个任务永久滞留——暂时性失败必须转等待退避重试。
        var store = NewStore();
        using var svc = new Aria2DownloadService(
            new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store);
        var task = StubAria2Server.NewTask("http://a.com/flaky.zip");
        var resubmits = new List<DownloadTask>();
        svc.NeedsResubmit += (_, t) => resubmits.Add(t);

        await svc.AddAsync(task);
        _server.TellStatusPayload = _ => "{\"status\":\"error\",\"errorMessage\":\"Failed to connect to 127.0.0.1:18100 after 2028 ms: Could not connect to server\"}";

        await svc.RefreshAsync();

        Assert.Equal(DownloadStatus.Waiting, task.Status); // 不终态化
        Assert.Null(task.Error); // 未终态，不留错误文案
        Assert.True(svc.GetResubmitDelay(task) > TimeSpan.Zero); // 已排退避重提
        Assert.Single(resubmits); // App 侧据此定时入队
    }

    [Fact]
    public async Task S13_TransientError_Exhausted_BecomesTerminalError()
    {
        // 重试到顶必须收口成终态失败，不许无限循环：上限 = MaxTransientRetries，超出即判死。
        var store = NewStore();
        using var svc = new Aria2DownloadService(
            new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store)
        {
            MaxTransientRetries = 2,
        };
        var task = StubAria2Server.NewTask("http://a.com/dead.zip");
        _server.TellStatusPayload = _ => "{\"status\":\"error\",\"errorMessage\":\"Connection stalled\"}";

        for (var round = 1; round <= 3; round++)
        {
            await svc.AddAsync(task); // 每轮由队列重提一次（新 gid）
            await svc.RefreshAsync();
            if (round <= 2)
            {
                Assert.Equal(DownloadStatus.Waiting, task.Status); // 前两轮仍在退避重试
            }
        }

        Assert.Equal(DownloadStatus.Error, task.Status); // 第 3 次（超上限）判终态
        Assert.Equal(TimeSpan.Zero, svc.GetResubmitDelay(task)); // 账目清零
    }

    [Fact]
    public async Task S14_ResourceError_StaysTerminal()
    {
        // 资源类错误（磁盘满）不在暂时性清单里：不该被退避逻辑拖成无限重试。
        var store = NewStore();
        using var svc = new Aria2DownloadService(
            new Aria2Client(StubAria2Server.OptionsFor(_server)), null, store);
        var task = StubAria2Server.NewTask("http://a.com/full-disk.zip");
        var resubmits = new List<DownloadTask>();
        svc.NeedsResubmit += (_, t) => resubmits.Add(t);

        await svc.AddAsync(task);
        _server.TellStatusPayload = _ => "{\"status\":\"error\",\"errorMessage\":\"No space left on device\"}";

        await svc.RefreshAsync();

        Assert.Equal(DownloadStatus.Error, task.Status);
        Assert.Empty(resubmits);
    }

}
