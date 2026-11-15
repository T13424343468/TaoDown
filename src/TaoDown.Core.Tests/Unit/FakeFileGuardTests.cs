using Xunit;

using TaoDown.Engine;

namespace TaoDown.Tests.Unit;

/// <summary>红1 回归断言（2026-11-15 修红班 R2）：假文件判定只拦「二进制后缀 × 文本内容」冲突，文本类后缀一律放行。</summary>
public class FakeFileGuardTests
{
    [Theory]
    [InlineData(".json", "{\"name\": \"合法 JSON 文件\"}")]     // 曾被 100% 误杀的场景
    [InlineData(".xml", "<?xml version=\"1.0\"?>")]
    [InlineData(".svg", "<svg xmlns=\"http://www.w3.org/2000/svg\">")]
    [InlineData(".txt", "<看，纯文本也可能是标签开头>")]
    [InlineData(".zip", "PK\x03\x04")]                            // 正常 zip 头，放行
    [InlineData(".exe", "MZ")]                                    // 正常 exe 头，放行
    public void TextLikeAndRealBinary_Pass(string ext, string head)
    {
        Assert.False(Aria2DownloadService.IsSuspiciousHeader(ext, head));
    }

    [Theory]
    [InlineData(".zip", "<!DOCTYPE html><html>限流页面</html>")]  // 真机实证过的假文件形态
    [InlineData(".exe", "{\"error\": \"too many requests\"}")]
    [InlineData(".msi", "<html>404 Not Found</html>")]
    public void BinaryExtWithHtmlOrJsonContent_IsFlagged(string ext, string head)
    {
        Assert.True(Aria2DownloadService.IsSuspiciousHeader(ext, head));
    }
}
