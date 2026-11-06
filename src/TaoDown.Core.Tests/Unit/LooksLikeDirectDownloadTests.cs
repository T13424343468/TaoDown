using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>v2.60：剪贴板/接管只对「像直接下载的文件」弹窗——网页地址不算。</summary>
public class LooksLikeDirectDownloadTests
{
    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("ed2k://|file|setup.exe|1|abcdef|/")]
    [InlineData("thunder://QUFodHRw")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("https://example.com/downloads/QQPCDownload310222.exe")]
    [InlineData("https://example.com/pack/Film.2026.mkv")]
    [InlineData("https://oss.example.com/a/b.zip?response-content-disposition=attachment%3Bfilename%3Dapp.apk")]
    [InlineData("https://example.com/get?filename=installer.exe&token=x")]
    public void DownloadLikeLinks_AreTrue(string url) =>
        Assert.True(LinkClassifier.LooksLikeDirectDownload(url));

    [Theory]
    [InlineData("https://www.baidu.com/")]
    [InlineData("https://www.baidu.com/s?wd=hello")]
    [InlineData("https://example.com/page#downloads")]
    [InlineData("https://example.com/downloads")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a link at all")]
    public void WebPageLinks_AreFalse(string? url) =>
        Assert.False(LinkClassifier.LooksLikeDirectDownload(url));
}
