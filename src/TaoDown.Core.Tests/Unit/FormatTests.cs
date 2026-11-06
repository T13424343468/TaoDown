using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

public class FormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5.0 MB")]
    [InlineData(2684354560L, "2.50 GB")]
    [InlineData(-5, "0 B")]
    public void Size_FormatsReadable(long bytes, string expected) =>
        Assert.Equal(expected, Format.Size(bytes));

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "1 B/s")]
    [InlineData(2048, "2 KB/s")]
    [InlineData(4 * 1024 * 1024, "4.0 MB/s")]
    public void Speed_FormatsReadable(long bytesPerSec, string expected) =>
        Assert.Equal(expected, Format.Speed(bytesPerSec));

    [Theory]
    [InlineData(21, 1, "剩余 21 秒")]
    [InlineData(90, 1, "剩余 1 分 30 秒")]
    [InlineData(120, 1, "剩余 2 分")]
    [InlineData(3 * 3600 + 5 * 60, 1, "剩余 3 小时 5 分")]
    [InlineData(7200, 1, "剩余 2 小时")]
    public void Eta_FormatsReadable(long remaining, long speed, string expected) =>
        Assert.Equal(expected, Format.Eta(remaining, speed));

    [Theory]
    [InlineData(0, 1024, "")]
    [InlineData(100, 0, "")]
    [InlineData(-1, 1024, "")]
    public void Eta_Undeterminable_ReturnsEmpty(long remaining, long speed, string expected) =>
        Assert.Equal(expected, Format.Eta(remaining, speed));
}
