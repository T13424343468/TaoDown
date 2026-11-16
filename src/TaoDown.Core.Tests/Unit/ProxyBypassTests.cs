using System.Net;
using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>代理豁免规则：回环/私网/单标签主机名直连，公网目标仍走代理。</summary>
public class ProxyBypassTests
{
    [Theory]
    [InlineData("http://127.0.0.1:18100/big.bin", true)]
    [InlineData("http://localhost:8080/f.bin", true)]
    [InlineData("http://myserver/file.bin", true)] // 单标签主机名按本地处理（<local> 语义）
    [InlineData("http://10.0.0.5/f.bin", true)]
    [InlineData("http://172.16.0.1/f.bin", true)]
    [InlineData("http://172.31.255.255/f.bin", true)]
    [InlineData("http://192.168.1.10/f.bin", true)]
    [InlineData("http://169.254.1.1/f.bin", true)]
    [InlineData("ftp://192.168.1.1:2121/file.bin", true)]
    [InlineData("http://[::1]:18100/f.bin", true)]
    [InlineData("http://example.com/f.bin", false)]
    [InlineData("http://8.8.8.8/f.bin", false)]
    [InlineData("http://172.32.0.1/f.bin", false)] // 172.32 不在 172.16/12
    public void ShouldBypass_ClassifiesTargets(string url, bool expected)
    {
        Assert.Equal(expected, ProxyBypass.ShouldBypass(url));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)] // 链路本地
    [InlineData("fd12::1", true)] // fc00::/7 唯一本地
    [InlineData("203.0.113.5", false)]
    [InlineData("2a01::1", false)]
    public void IsLocalAddress_CoversPrivateRanges(string text, bool expected)
    {
        Assert.Equal(expected, ProxyBypass.IsLocalAddress(IPAddress.Parse(text)));
    }

    [Fact]
    public void ShouldBypass_ToleratesBadInput()
    {
        Assert.False(ProxyBypass.ShouldBypass(null));
        Assert.False(ProxyBypass.ShouldBypass(""));
        Assert.False(ProxyBypass.ShouldBypass("not a url"));
        Assert.False(ProxyBypass.ShouldBypass("http:///no-host"));
    }
}
