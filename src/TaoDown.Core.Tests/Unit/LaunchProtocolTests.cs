using TaoDown.Core;
using Xunit;

namespace TaoDown.Core.Tests;

public class LaunchArgsTests
{
    [Fact]
    public void Parse_ExtractsLinksTorrentsAndSkipsExe()
    {
        var torrent = Path.Combine(Path.GetTempPath(), $"taodown-launch-{Guid.NewGuid():N}.torrent");
        try
        {
            using var _ = File.Create(torrent);
            var argv = new[]
            {
                "TaoDown.exe",
                "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
                torrent,
                "ed2k://|file|demo.bin|1048576|0123456789abcdef0123456789abcdef|/",
                "not-a-download-arg",
            };

            var payload = LaunchArgs.Parse(argv, path => File.Exists(path));

            Assert.Contains("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", payload.Links);
            Assert.Contains("ed2k://|file|demo.bin|1048576|0123456789abcdef0123456789abcdef|/", payload.Links);
            Assert.Equal(2, payload.Links.Count);
            Assert.Equal([torrent], payload.TorrentFiles);
            Assert.True(payload.HasAny);
        }
        finally
        {
            File.Delete(torrent);
        }
    }

    [Fact]
    public void Parse_NoArgs_YieldsEmptyPayload()
    {
        var payload = LaunchArgs.Parse(["TaoDown.exe"]);
        Assert.False(payload.HasAny);
        Assert.Empty(payload.Links);
        Assert.Empty(payload.TorrentFiles);
    }

    [Fact]
    public void Parse_TorrentWithoutExistingFile_IsNotTorrent()
    {
        var ghost = Path.Combine(Path.GetTempPath(), "taodown-missing.torrent");
        var payload = LaunchArgs.Parse(["TaoDown.exe", ghost], path => File.Exists(path));
        Assert.Empty(payload.TorrentFiles);
    }
}

public class LaunchForwardTests
{
    [Fact]
    public void Write_Read_Clear_Roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-fwd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var payload = new LaunchPayload();
            payload.Links.Add("magnet:?xt=urn:btih:abc");
            payload.TorrentFiles.Add(@"D:\种子\demo.torrent");
            LaunchForward.Write(payload, dir);
            // 多文件模式：写入即生成 launch-forward-*.txt 独立收件文件
            Assert.Contains(
                Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "launch-forward*.txt") : [],
                f => f.EndsWith(".txt", StringComparison.Ordinal));

            var read = LaunchForward.TryRead(dir);
            Assert.Equal(["magnet:?xt=urn:btih:abc"], read.Links);
            Assert.Equal([@"D:\种子\demo.torrent"], read.TorrentFiles);
            Assert.Empty(Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "launch-forward*.txt") : []);

            // 空转发不写文件
            LaunchForward.Write(new LaunchPayload(), dir);
            Assert.Empty(Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "launch-forward*.txt") : []);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // 修红回归（run 44071283035 大文件 B 段 30/40 实锤）：旧版单文件覆盖写——
    // 二次实例 A 写完、主实例还没读，二次实例 B 又覆盖同一文件，A 的载荷永久丢失。
    // 本用例在旧实现下必丢一个载荷（B 覆盖 A），新实现两次 Write 独立文件、TryRead 一次读回两个。
    [Fact]
    public void Write_Twice_WithoutRead_TryRead_ReturnsBothPayloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-fwd-race-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new LaunchPayload();
            first.Links.Add("magnet:?xt=urn:btih:first");
            var second = new LaunchPayload();
            second.Links.Add("magnet:?xt=urn:btih:second");

            LaunchForward.Write(first, dir);
            LaunchForward.Write(second, dir); // 不读取，模拟主实例 UI 线程延迟

            var read = LaunchForward.TryRead(dir);
            Assert.Equal(["magnet:?xt=urn:btih:first", "magnet:?xt=urn:btih:second"], read.Links);
            Assert.Empty(Directory.EnumerateFiles(dir, "launch-forward*.txt"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // 旧版单文件 launch-forward.txt 兼容：升级后残留的旧收件文件仍能读回并清理
    [Fact]
    public void TryRead_LegacySingleFile_IsStillRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-fwd-legacy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                LaunchForward.FilePath(dir),
                "L\tmagnet:?xt=urn:btih:legacy" + Environment.NewLine +
                "T\tD:\\种子\\old.torrent" + Environment.NewLine);

            var read = LaunchForward.TryRead(dir);
            Assert.Equal(["magnet:?xt=urn:btih:legacy"], read.Links);
            Assert.Equal(["D:\\种子\\old.torrent"], read.TorrentFiles);
            Assert.False(File.Exists(LaunchForward.FilePath(dir)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryRead_MissingFile_YieldsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-fwd-none-" + Guid.NewGuid().ToString("N"));
        var payload = LaunchForward.TryRead(dir);
        Assert.False(payload.HasAny);
    }
}

public class ProtocolRegistrarTests
{
    private const string Exe = @"D:\Program Files\TaoDown\TaoDown.exe";

    [Fact]
    public void PlannedWrites_CoversAllProtocolsAndTorrent()
    {
        var writes = ProtocolRegistrar.PlannedWrites(Exe);
        foreach (var p in ProtocolRegistrar.Protocols)
        {
            var root = @"Software\Classes\" + p;
            Assert.Contains(writes, w => w.Key == root && w.Name == "" && w.Value == "URL:TaoDown 下载");
            Assert.Contains(writes, w => w.Key == root && w.Name == "URL Protocol");
            Assert.Contains(writes, w => w.Key == root + @"\shell\open\command" && w.Value.Contains('"' + Exe + '"') && w.Value.Contains("%1"));
            Assert.Contains(writes, w => w.Key == root + @"\DefaultIcon" && w.Value.Contains(Exe));
        }

        Assert.Contains(writes, w => w.Key == @"Software\Classes\.torrent" && w.Value == "TaoDown.Torrent");
        Assert.Contains(writes, w => w.Key == @"Software\Classes\TaoDown.Torrent\shell\open\command");
        Assert.Equal(ProtocolRegistrar.Protocols.Length * 4 + 4, writes.Count);
    }

    [Fact]
    public void PlannedRemovals_CoversSameScope()
    {
        var removals = ProtocolRegistrar.PlannedRemovals();
        foreach (var p in ProtocolRegistrar.Protocols)
        {
            Assert.Contains(@"Software\Classes\" + p, removals);
        }

        Assert.Contains(@"Software\Classes\.torrent", removals);
    }

    [Fact]
    public void PlannedAutoStart_UsesRunKeyAndQuotedPath()
    {
        var (key, name, value) = ProtocolRegistrar.PlannedAutoStart(Exe);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", key);
        Assert.Equal("TaoDown", name);
        Assert.Equal("\"" + Exe + "\"", value);
    }
}
