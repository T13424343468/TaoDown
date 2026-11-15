using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>修红班 R3 定向回归（2026-11-15）：六缺口逐项断言。</summary>
public class FixR3Tests
{
    // P1-1：接管入口与手贴入口同一把尺——迅雷/快车/旋风链接分类为 Thunder，可被 ThunderLinkParser 还原
    [Theory]
    [InlineData("thunder://QUFodHRwOi8vZXhhbXBsZS5jb20vYS56aXBaWg==")]
    [InlineData("flashget://W0ZMQVNIR0VUXWh0dHA6Ly9leGFtcGxlLmNvbS9hLnppcFtGTU=1&amp;size=1")]
    public void ThunderFamilyLinks_ClassifiedAsThunder(string link)
    {
        var kind = TaoDown.Core.LinkClassifier.Classify(link);
        Assert.Equal(TaoDown.Core.LinkKind.Thunder, kind);
    }

    [Fact]
    public void ValidThunderLink_ResolvesToRealUrl()
    {
        var resolved = TaoDown.Engine.ThunderLinkParser.Resolve("thunder://QUFodHRwOi8vZXhhbXBsZS5jb20vYS56aXBaWg==");
        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.StartsWith("http", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidThunderLink_ReturnsNull()
    {
        Assert.Null(TaoDown.Engine.ThunderLinkParser.Resolve("thunder://!!!not-base64!!!"));
        Assert.Null(TaoDown.Engine.ThunderLinkParser.Resolve("thunder://"));
        Assert.Null(TaoDown.Engine.ThunderLinkParser.Resolve(null));
    }

    // P0-1：快照节流双闸——防抖在途且距上次落盘不足窗口才延缓；窗口到点、无防抖一律放行
    [Fact]
    public void SnapshotThrottle_WithinWindowDelays_ExpiredWindowAllows()
    {
        long throttleMs = 5000;
        // 防抖在途 + 3 秒前刚落盘 → 延缓（活跃下载高频触发防抖时不再被饿死，窗口到点必写）
        Assert.True(TaoDown.Engine.Aria2DownloadService.SnapshotThrottleRequired(true, 10_000, 7_000, throttleMs));
        // 防抖在途但距上次落盘已 6 秒（窗口到点）→ 放行必写
        Assert.False(TaoDown.Engine.Aria2DownloadService.SnapshotThrottleRequired(true, 13_000, 7_000, throttleMs));
        // 无防抖在途 → 放行
        Assert.False(TaoDown.Engine.Aria2DownloadService.SnapshotThrottleRequired(false, 10_000, 7_000, throttleMs));
    }

    [Fact]
    public void DownloadTaskStore_SaveLoad_Roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-r3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TaoDown.Engine.DownloadTaskStore(dir);
            var task = new TaoDown.Core.DownloadTask { Url = "https://example.com/a.zip", Kind = TaoDown.Core.LinkKind.Http, SaveDir = dir };
            store.Save([task]);
            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal(task.Id, loaded[0].Id);
            Assert.Equal(task.Url, loaded[0].Url);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DownloadTaskStore_CorruptedFile_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-r3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TaoDown.Engine.DownloadTaskStore(dir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(store.FilePath, "{ 不是合法 JSON");
            Assert.Empty(store.Load());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // P1-2：文件内容校验判定收进纯函数——空文件/假文件给文案，文本类放行，正常文件 null
    [Fact]
    public void SuspiciousFileError_EmptyFile_ReturnsEmptyHint()
    {
        var err = TaoDown.Engine.Aria2DownloadService.SuspiciousFileError(0, "", ".zip");
        Assert.NotNull(err);
        Assert.Contains("空的", err);
    }

    [Fact]
    public void SuspiciousFileError_FakeHtmlForBinaryExt_ReturnsFakeHint()
    {
        var err = TaoDown.Engine.Aria2DownloadService.SuspiciousFileError(1024, "<html><body>", ".zip");
        Assert.NotNull(err);
        Assert.Contains("网页", err);
    }

    [Fact]
    public void SuspiciousFileError_JsonAndTextFiles_AlwaysPass()
    {
        Assert.Null(TaoDown.Engine.Aria2DownloadService.SuspiciousFileError(256, "{\"name\":\"x\"}", ".json"));
        Assert.Null(TaoDown.Engine.Aria2DownloadService.SuspiciousFileError(256, "<svg xmlns=\"x\">", ".svg"));
        Assert.Null(TaoDown.Engine.Aria2DownloadService.SuspiciousFileError(256, "<html>hi", ".html"));
        Assert.Null(TaoDown.Engine.Aria2DownloadService.SuspiciousFileError(256, "MZ退出", ".exe"));
    }

    // P1-3：重试频控——首次放行、60 秒内拦下、到期放行；不可重试状态直接拒绝（不触引擎）
    [Fact]
    public void RetryAllowed_CooldownSemantics()
    {
        long cooldown = 60_000;
        Assert.True(TaoDown.Engine.Aria2DownloadService.RetryAllowed(100_000, null, cooldown)); // 首次
        Assert.False(TaoDown.Engine.Aria2DownloadService.RetryAllowed(99_999, 40_000, cooldown)); // 不足 60 秒
        Assert.True(TaoDown.Engine.Aria2DownloadService.RetryAllowed(100_000, 40_000, cooldown)); // 恰到 60 秒放行
    }

    [Fact]
    public async Task RetryFromErrorAsync_NonExistentTask_ThrowsWithoutEngine()
    {
        var client = new TaoDown.Engine.Aria2Client(new TaoDown.Engine.Aria2Options());
        using var service = new TaoDown.Engine.Aria2DownloadService(client);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RetryFromErrorAsync("no-such-task"));
    }

    // P1-4 / P2-1：源码合同断言——WiX 32 位拦截与收件箱 4MB 上限必须随源码在位（App 壳沙箱不可编译，合同先钉住）
    [Fact]
    public void SourceContract_WixLaunchCondition_AndInboxLimit_InPlace()
    {
        var repoRoot = FindRepoRoot();
        var wxs = File.ReadAllText(Path.Combine(repoRoot, "tools", "msi", "package.ps1"));
        Assert.Contains("Launch Condition", wxs);
        Assert.Contains("VersionNT64", wxs);
        Assert.Contains("64 位 Windows", wxs);

        var watcher = File.ReadAllText(Path.Combine(repoRoot, "src", "TaoDown.App", "BrowserInboxWatcher.cs"));
        Assert.Contains("InboxMaxJsonBytes = 4 * 1024 * 1024", watcher);
        Assert.Contains("info.Length > InboxMaxJsonBytes", watcher);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
