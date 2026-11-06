using System.Text;
using System.Text.Json;
using TaoDown.Core;
using TaoDown.Engine;
using Xunit;

namespace TaoDown.Tests.Unit;

public class LinkClassifierTests
{
    [Theory]
    [InlineData("http://a.com/f.zip", LinkKind.Http)]
    [InlineData("HTTPS://a.com/f.zip", LinkKind.Http)]
    [InlineData("ftp://a.com/f.zip", LinkKind.Ftp)]
    [InlineData("magnet:?xt=urn:btih:abc", LinkKind.BitTorrent)]
    [InlineData("ed2k://|file|a.zip|1|2|/", LinkKind.Ed2k)]
    [InlineData("thunder://QUFodHRw", LinkKind.Thunder)]
    [InlineData("flashget://QUFodHRw", LinkKind.Thunder)]
    [InlineData("qqdl://QUFodHRw", LinkKind.Thunder)]
    [InlineData("", LinkKind.Unknown)]
    [InlineData(null, LinkKind.Unknown)]
    [InlineData("hello world", LinkKind.Unknown)]
    public void Classify_ReturnsExpectedKind(string? url, LinkKind expected) =>
        Assert.Equal(expected, LinkClassifier.Classify(url));
}

public class ThunderLinkParserTests
{
    private static string Encode(string raw) => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void Resolve_ThunderLink_ReturnsInnerUrl()
    {
        var link = "thunder://" + Encode("AAhttp://example.com/file.zipZZ");
        Assert.Equal("http://example.com/file.zip", ThunderLinkParser.Resolve(link));
    }

    [Fact]
    public void Resolve_FlashgetLink_StripsOptionSuffix()
    {
        var link = "flashget://" + Encode("AAftp://a.com/f.isoZZ") + "&EM=0";
        Assert.Equal("ftp://a.com/f.iso", ThunderLinkParser.Resolve(link));
    }

    [Fact]
    public void Resolve_QqdlLink_ReturnsInnerUrl()
    {
        var link = "qqdl://" + Encode("AAmagnet:?xt=urn:btih:abcZZ");
        Assert.Equal("magnet:?xt=urn:btih:abc", ThunderLinkParser.Resolve(link));
    }

    [Fact]
    public void Resolve_FlashgetWrappedVariant_ReturnsInnerUrl()
    {
        // 快车变体：base64 内容为 [FLASHGET]真实地址[FLASHGET]
        var link = "flashget://" + Encode("[FLASHGET]http://a.com/setup.exe[FLASHGET]");
        Assert.Equal("http://a.com/setup.exe", ThunderLinkParser.Resolve(link));
    }

    [Fact]
    public void Resolve_FlashgetWrappedPlusSuffix_ReturnsInnerUrl()
    {
        var link = "flashget://" + Encode("[FLASHGET]ftp://a.com/f.iso[FLASHGET]") + "&EM=0";
        Assert.Equal("ftp://a.com/f.iso", ThunderLinkParser.Resolve(link));
    }

    [Fact]
    public void Resolve_FlashgetWrappedNotUrl_ReturnsNull()
    {
        // 包裹剥掉后不是有效下载地址：必须拒绝
        var link = "flashget://" + Encode("[FLASHGET]hello world[FLASHGET]");
        Assert.Null(ThunderLinkParser.Resolve(link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("thunder://!!!not-base64!!!")]
    [InlineData("thunder://" + "QQ==")] // base64 合法但内容不是有效地址
    [InlineData("http://plain-link.com")] // 非加密链接类型
    public void Resolve_InvalidInput_ReturnsNull(string? link) =>
        Assert.Null(ThunderLinkParser.Resolve(link));
}

public class Aria2RequestTests
{
    [Fact]
    public void BuildRequest_PutsSecretTokenFirst()
    {
        var request = Aria2Client.BuildRequest("aria2.addUri", "secret123", new[] { "http://a.com/f.zip" });
        var json = JsonSerializer.Serialize(request);
        Assert.Contains("\"token:secret123\"", json);
        Assert.Contains("aria2.addUri", json);
    }

    [Fact]
    public void BuildArguments_ContainsResumeAndDht()
    {
        var args = Aria2EngineHost.BuildArguments(new Aria2Options { Secret = "s3" });
        Assert.Contains("--continue=true", args);
        Assert.Contains("--enable-dht=true", args);
        Assert.Contains("--rpc-secret=s3", args);
        Assert.Contains("--seed-time=0", args);
        Assert.Contains("--log=", args);
        Assert.Contains("--connect-timeout=15", args);
        Assert.Contains("--max-tries=5", args);
        Assert.Contains("--user-agent=\"Mozilla/5.0", args);
        Assert.DoesNotContain("--max-overall-download-limit", args);
    }

    [Fact]
    public void BuildArguments_SpeedLimitAndSeedFromSettings()
    {
        var args = Aria2EngineHost.BuildArguments(new Aria2Options
        {
            Secret = "s3",
            OverallSpeedLimitKB = 2048,
            SeedMinutes = 30,
        });
        Assert.Contains("--max-overall-download-limit=2048K", args);
        Assert.Contains("--seed-time=30", args);
    }


    [Theory]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("http://127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("http=192.168.1.2:8080;https=192.168.1.2:8080", "http://192.168.1.2:8080")]
    [InlineData("http=p:8080;ftp=q:21", "http://p:8080")]
    public void SystemProxy_Parse_ValidForms(string raw, string expected)
    {
        Assert.Equal(expected, SystemProxy.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("socks=127.0.0.1:1080")]
    [InlineData("127.0.0.1")] // 无端口：无法构成可用代理
    public void SystemProxy_Parse_InvalidForms(string? raw)
    {
        Assert.Null(SystemProxy.Parse(raw));
    }
}
