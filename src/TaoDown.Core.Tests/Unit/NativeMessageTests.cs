using System.Text;
using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>批次五：Native Messaging 帧编解码与浏览器请求解析。</summary>
public class NativeMessageTests
{
    [Fact]
    public void Frame_Roundtrip_PreservesPayload()
    {
        using var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("{\"url\":\"https://example.com/a.zip\"}");
        NativeMessageFrame.Write(stream, payload);
        stream.Position = 0;

        var read = NativeMessageFrame.TryRead(stream);

        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public void Frame_TwoMessages_ReadSequentially()
    {
        using var stream = new MemoryStream();
        NativeMessageFrame.Write(stream, [1, 2, 3]);
        NativeMessageFrame.Write(stream, [4, 5]);
        stream.Position = 0;

        Assert.Equal(new byte[] { 1, 2, 3 }, NativeMessageFrame.TryRead(stream));
        Assert.Equal(new byte[] { 4, 5 }, NativeMessageFrame.TryRead(stream));
        Assert.Null(NativeMessageFrame.TryRead(stream));
    }

    [Fact]
    public void Frame_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(NativeMessageFrame.TryRead(stream));
    }

    [Fact]
    public void Frame_OversizeLength_Throws()
    {
        using var stream = new MemoryStream();
        var header = new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }; // 超过 4MB 上限
        stream.Write(header);
        stream.Position = 0;

        Assert.Throws<IOException>(() => NativeMessageFrame.TryRead(stream));
    }

    [Fact]
    public void Frame_IncompleteBody_Throws()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 10, 0, 0, 0 }); // 声称 10 字节但没写正文
        stream.Position = 0;

        Assert.Throws<IOException>(() => NativeMessageFrame.TryRead(stream));
    }

    [Fact]
    public void Request_TryParse_ValidHttp()
    {
        var ok = BrowserRequest.TryParse(
            "{\"url\":\"https://example.com/file.zip\",\"referrer\":\"https://example.com/\",\"filename\":\"file.zip\"}",
            out var request, out var error);

        Assert.True(ok, error);
        Assert.Equal("https://example.com/file.zip", request!.Url);
        Assert.Equal("file.zip", request.Filename);
    }

    [Fact]
    public void Request_TryParse_PascalCase_Envelope_Parses()
    {
        // v2.58 回归：宿主落盘信封是 PascalCase（Url/Referrer/Filename），
        // 旧解析只认小写导致真机全部「缺少下载地址」。
        var ok = BrowserRequest.TryParse(
            "{\"Url\":\"https://example.com/file.zip\",\"Referrer\":\"https://example.com/\",\"Filename\":\"file.zip\"}",
            out var request, out var error);

        Assert.True(ok, error);
        Assert.Equal("https://example.com/file.zip", request!.Url);
        Assert.Equal("file.zip", request.Filename);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"referrer\":\"x\"}")]
    public void Request_TryParse_Invalid_ReturnsError(string json)
    {
        var ok = BrowserRequest.TryParse(json, out var request, out var error);

        Assert.False(ok);
        Assert.Null(request);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("ftp://host/file.bin", true)]
    [InlineData("magnet:?xt=urn:btih:abc", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    public void Request_TryParse_SchemeWhitelist(string url, bool expected)
    {
        var ok = BrowserRequest.TryParse($"{{\"url\":\"{url}\"}}", out var request, out _);

        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.NotNull(request);
        }
    }

    [Theory]
    [InlineData("..\u005Cevil.exe", "evil.exe")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("name<>.zip", "name__.zip")]
    public void SanitizeFilename_StripsTraversalAndInvalidChars(string input, string expected)
    {
        Assert.Equal(expected, BrowserRequest.SanitizeFilename(input));
    }

    [Fact]
    public void SanitizeFilename_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, BrowserRequest.SanitizeFilename(""));
    }
}
