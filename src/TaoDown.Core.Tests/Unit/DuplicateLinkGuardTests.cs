using System.Text;

using TaoDown.Core;

using Xunit;

namespace TaoDown.Tests.Unit;

public class DuplicateLinkGuardTests
{
    private static DownloadTask T(string url, DownloadStatus status) => new()
    {
        Url = url,
        Status = status,
    };

    [Fact]
    public void 同URL活动任务命中()
    {
        var tasks = new List<DownloadTask>
        {
            T("http://127.0.0.1:2121/m16.bin", DownloadStatus.Active),
            T("http://127.0.0.1:2121/other.bin", DownloadStatus.Waiting),
        };
        var dup = DuplicateLinkGuard.FindDuplicate(tasks, "http://127.0.0.1:2121/m16.bin");
        Assert.NotNull(dup);
        Assert.Equal(DownloadStatus.Active, dup.Status);
    }

    [Fact]
    public void 首尾空白与大小写不敏感()
    {
        var tasks = new List<DownloadTask> { T("HTTP://EXAMPLE.COM/file.bin", DownloadStatus.Complete) };
        var dup = DuplicateLinkGuard.FindDuplicate(tasks, "  http://example.com/file.bin  ");
        Assert.NotNull(dup);
        Assert.Equal(DownloadStatus.Complete, dup.Status);
    }

    [Fact]
    public void 完成任务也算占用_避免重复下载()
    {
        var tasks = new List<DownloadTask> { T("http://x/m16.bin", DownloadStatus.Complete) };
        Assert.NotNull(DuplicateLinkGuard.FindDuplicate(tasks, "http://x/m16.bin"));
    }

    [Fact]
    public void 失败任务不算占用_允许重试()
    {
        var tasks = new List<DownloadTask> { T("http://x/m16.bin", DownloadStatus.Error) };
        Assert.Null(DuplicateLinkGuard.FindDuplicate(tasks, "http://x/m16.bin"));
    }

    [Fact]
    public void 活动任务优先于完成任务返回()
    {
        var tasks = new List<DownloadTask>
        {
            T("http://x/m16.bin", DownloadStatus.Complete),
            T("http://x/m16.bin", DownloadStatus.Active),
        };
        var dup = DuplicateLinkGuard.FindDuplicate(tasks, "http://x/m16.bin");
        Assert.NotNull(dup);
        Assert.Equal(DownloadStatus.Active, dup.Status);
    }

    [Fact]
    public void 空URL或无命中返回Null()
    {
        var tasks = new List<DownloadTask> { T("http://x/a.bin", DownloadStatus.Active) };
        Assert.Null(DuplicateLinkGuard.FindDuplicate(tasks, "http://x/b.bin"));
        Assert.Null(DuplicateLinkGuard.FindDuplicate(tasks, "   "));
        Assert.Null(DuplicateLinkGuard.FindDuplicate(tasks, null));
    }
}
