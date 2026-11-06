using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

public class LooseLinkParserTests
{
    [Theory]
    [InlineData("019_enterprise_x64_dvd_2bfe815a.iso|1632086016|58C258FF0F1D006DD3C1F5F17AF3EAFD",
        "ed2k://|file|019_enterprise_x64_dvd_2bfe815a.iso|1632086016|58C258FF0F1D006DD3C1F5F17AF3EAFD|/")]
    [InlineData(" 短片.mp4 | 12345678 | 0123456789abcdef0123456789abcdef ",
        "ed2k://|file|短片.mp4|12345678|0123456789ABCDEF0123456789ABCDEF|/")]
    public void EmuleFragment_WrapsToEd2k(string input, string expected) =>
        Assert.Equal(expected, LooseLinkParser.TryNormalize(input));

    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567", "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567")]
    [InlineData("MFRGGZDFMZTWQ2LKNFRGGZDFMZTWQ2LK", "magnet:?xt=urn:btih:MFRGGZDFMZTWQ2LKNFRGGZDFMZTWQ2LK")]
    public void BareHash_WrapsToMagnet(string input, string expected) =>
        Assert.Equal(expected, LooseLinkParser.TryNormalize(input));

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("https://example.com/file.zip")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("ed2k://|file|a.iso|1|0123456789ABCDEF0123456789ABCDEF|/")]
    [InlineData("thunder://QUFodHRw")]
    public void StandardLinks_Unchanged(string input) =>
        Assert.Equal(input, LooseLinkParser.TryNormalize(input));

    [Theory]
    [InlineData("blob:https://example.com/46e65003")]
    [InlineData("随便一段文字")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("58C258FF0F1D006DD3C1F5F17AF3EAFD")] // 32 位 hex 不是 BT 哈希，不猜
    public void Unrecognized_ReturnsNull(string? input) =>
        Assert.Null(LooseLinkParser.TryNormalize(input));
}
