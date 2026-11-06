using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>汇总行与托盘提示文案。</summary>
public class FormatSummaryTests
{
    [Fact]
    public void Summary_BasicCounts()
    {
        Assert.Equal("下载中 2 · 已暂停 1 · 已完成 5", Format.Summary(2, 1, 5, 0));
    }

    [Fact]
    public void Summary_WithSpeed()
    {
        Assert.Equal("下载中 1 · 已暂停 0 · 已完成 0 · 总速度 256 KB/s",
            Format.Summary(1, 0, 0, 256 * 1024));
    }

    [Fact]
    public void Summary_AllZero_NoSpeed()
    {
        Assert.Equal("下载中 0 · 已暂停 0 · 已完成 0", Format.Summary(0, 0, 0, 0));
        Assert.Equal("下载中 0 · 已暂停 0 · 已完成 0", Format.Summary(0, 0, 0, -1)); // 负速度按无效处理
    }

    [Fact]
    public void TrayTip_Active_First()
    {
        Assert.Equal("TaoDown — 下载中 3", Format.TrayTip(3, 2));
    }

    [Fact]
    public void TrayTip_OnlyPaused()
    {
        Assert.Equal("TaoDown — 已暂停 2，可继续", Format.TrayTip(0, 2));
    }

    [Fact]
    public void TrayTip_Idle()
    {
        Assert.Equal("TaoDown — 没有进行中的下载", Format.TrayTip(0, 0));
    }
}
