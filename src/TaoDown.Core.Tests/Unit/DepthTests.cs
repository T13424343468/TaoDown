using System.Text;
using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>链接识别边界：大小写、空白、IPv6、端口、无参数磁力。</summary>
public class LinkClassifierEdgeTests
{
    [Theory]
    [InlineData("  http://a.com/f.zip  ", LinkKind.Http)] // 首尾空白必须容忍
    [InlineData("HTTP://A.COM", LinkKind.Http)] // 协议大小写不敏感
    [InlineData("HttpS://a.com", LinkKind.Http)]
    [InlineData("http://[::1]:8080/f.bin", LinkKind.Http)] // IPv6 与端口
    [InlineData("http://a.com:443/x", LinkKind.Http)]
    [InlineData("FTP://a.com", LinkKind.Ftp)]
    [InlineData("MAGNET:?x=1", LinkKind.BitTorrent)]
    [InlineData("ED2K://|file|a|1|2|/", LinkKind.Ed2k)]
    [InlineData("Thunder://QUFodHRw", LinkKind.Thunder)]
    [InlineData("FlashGet://QUFodHRw", LinkKind.Thunder)]
    [InlineData("QQDL://QUFodHRw", LinkKind.Thunder)]
    [InlineData("magnet", LinkKind.Unknown)] // 缺冒号不算
    [InlineData("http:", LinkKind.Unknown)] // 缺斜杠不算
    [InlineData("https:/a.com", LinkKind.Unknown)] // 单斜杠不算
    [InlineData("xhttp://a.com", LinkKind.Unknown)] // 前缀杂讯不算
    public void Classify_EdgeInputs(string url, LinkKind expected) =>
        Assert.Equal(expected, LinkClassifier.Classify(url));
}

/// <summary>格式化边界：单位换算临界值、负数、零、无法估算。</summary>
public class FormatEdgeTests
{
    [Theory]
    [InlineData(-5, "0 B")]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1025, "1.0 KB")]
    [InlineData(1048575, "1024.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1073741823, "1024.0 MB")]
    [InlineData(1073741824, "1.00 GB")]
    [InlineData(5368709120, "5.00 GB")]
    public void Size_Boundaries(long bytes, string expected) => Assert.Equal(expected, Format.Size(bytes));

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "1 B/s")]
    [InlineData(1023, "1023 B/s")]
    [InlineData(1024, "1 KB/s")]
    [InlineData(1048576, "1.0 MB/s")]
    public void Speed_Boundaries(long speed, string expected) => Assert.Equal(expected, Format.Speed(speed));

    [Theory]
    [InlineData(59000, 1000, "剩余 59 秒")] // 不足一分钟只显示秒
    [InlineData(60000, 1000, "剩余 1 分")]
    [InlineData(61000, 1000, "剩余 1 分 1 秒")]
    [InlineData(3600000, 1000, "剩余 1 小时")] // 整小时不带分
    [InlineData(3660000, 1000, "剩余 1 小时 1 分")]
    [InlineData(5400000, 1000, "剩余 1 小时 30 分")]
    public void Eta_Boundaries(long remaining, long speed, string expected) =>
        Assert.Equal(expected, Format.Eta(remaining, speed));

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void Eta_Undeterminable_Empty(long remaining, long speed) =>
        Assert.Equal(string.Empty, Format.Eta(remaining, speed));
}

/// <summary>宽松补全边界：非法哈希长度、base32 混合、碎片空白。</summary>
public class LooseLinkParserEdgeTests
{
    [Theory]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456")] // 39 位：不足
    [InlineData("0123456789ABCDEF0123456789ABCDEF012345678")] // 41 位：超出
    [InlineData("0123456789ABCDEF0123456789ABCDEG0123456789")] // 40 位含非十六进制 G
    [InlineData("0123456789ABCDEFGHIJKLMNOPQRSTUV0123456789")] // 40 位非 base32 集（含 0/1/8/9）
    public void BareHash_InvalidLengthOrAlphabet_ReturnsNull(string hash)
    {
        Assert.Null(LooseLinkParser.TryNormalize(hash));
    }

    [Fact]
    public void Base32Lowercase_WrapsToMagnet()
    {
        var hash = "abcdefghijklmnopqrstuvwxyz234567"; // 32 位 base32 小写
        Assert.Equal("magnet:?xt=urn:btih:" + hash.ToUpperInvariant(), LooseLinkParser.TryNormalize(hash));
    }

    [Fact]
    public void EmuleFragment_WithSpaces_StillParses()
    {
        Assert.Equal(
            "ed2k://|file|name.zip|1024|0123456789ABCDEF0123456789ABCDEF|/",
            LooseLinkParser.TryNormalize(" name.zip | 1024 | 0123456789abcdef0123456789abcdef "));
    }

    [Fact]
    public void QuotedLink_Unwrapped()
    {
        Assert.Equal("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            LooseLinkParser.TryNormalize("\"0123456789abcdef0123456789abcdef01234567\""));
    }
}

/// <summary>混合文本提取维度：HTML 残片、中文标点、多链接取最早出现、换行列表。</summary>
public class LinkTextScannerDepthTests
{
    [Fact]
    public void HtmlFragment_ExtractsLink()
    {
        const string text = "<a href=\"https://a.com/setup.exe\">下载</a>";
        Assert.Equal("https://a.com/setup.exe", LinkTextScanner.FindFirst(text));
    }

    [Fact]
    public void ChinesePunctuation_StopsExtraction()
    {
        const string text = "链接：https://a.com/x.zip，点此下载。";
        Assert.Equal("https://a.com/x.zip", LinkTextScanner.FindFirst(text));
    }

    [Fact]
    public void MultipleLinks_ReturnsEarliestInText()
    {
        const string text = "备选 magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567 或 http://a.com/f.zip";
        Assert.Equal("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
            LinkTextScanner.FindFirst(text));
    }

    [Fact]
    public void LineList_ReturnsFirst()
    {
        const string text = "文件列表：\r\nftp://a.com/1.bin\r\nhttp://a.com/2.bin";
        Assert.Equal("ftp://a.com/1.bin", LinkTextScanner.FindFirst(text));
    }

    [Fact]
    public void FullWidthParenthesis_Stopped()
    {
        const string text = "（https://a.com/x.bin）";
        Assert.Equal("https://a.com/x.bin", LinkTextScanner.FindFirst(text));
    }
}

/// <summary>原生帧维度：零长帧、大帧、逐字节到达、坏流。</summary>
public class NativeFrameDepthTests
{
    [Fact]
    public void Frame_ZeroLength_Roundtrip()
    {
        using var ms = new MemoryStream();
        NativeMessageFrame.Write(ms, []);
        ms.Position = 0;
        var frame = NativeMessageFrame.TryRead(ms);
        Assert.NotNull(frame);
        Assert.Empty(frame);
    }

    [Fact]
    public void Frame_OneMegabyte_Roundtrip()
    {
        var payload = new byte[1024 * 1024];
        new Random(1).NextBytes(payload);
        using var ms = new MemoryStream();
        NativeMessageFrame.Write(ms, payload);
        ms.Position = 0;
        Assert.Equal(payload, NativeMessageFrame.TryRead(ms));
    }

    [Fact]
    public void Frame_ByteByByte_StillParses()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        using var big = new MemoryStream();
        NativeMessageFrame.Write(big, payload);
        // 单字节流包装：模拟管道逐字节到达
        using var slow = new OneByteAtATimeStream(big.ToArray());
        Assert.Equal(payload, NativeMessageFrame.TryRead(slow));
    }

    [Fact]
    public void Frame_TruncatedHeader_ThrowsIOException()
    {
        var ex = Record.Exception(() => NativeMessageFrame.TryRead(new MemoryStream([1, 2, 3])));
        Assert.IsType<IOException>(ex);
    }

    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private int _pos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count <= 0)
            {
                return 0;
            }

            buffer[offset] = data[_pos++];
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _pos = (int)offset;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => _pos = (int)value; }
    }
}

/// <summary>文件名清理补充维度：盘符正斜杠、纯点、超长截断后尾点清理。</summary>
public class SanitizeFileNameDepthTests
{
    [Fact]
    public void DriveWithForwardSlash_Stripped()
    {
        Assert.Equal("x.txt", BlobPayload.SanitizeFileName("C:/evil/x.txt"));
    }

    [Fact]
    public void LongName_TruncatedTo200_NoTrailingDot()
    {
        var name = new string('a', 199) + "." + new string('b', 50);
        var result = BlobPayload.SanitizeFileName(name);
        Assert.True(result.Length <= 200);
        Assert.False(result.EndsWith("."));
    }

    [Fact]
    public void ReservedWithExtension_Prefixed()
    {
        Assert.StartsWith("_", BlobPayload.SanitizeFileName("com1.dll"));
    }

    [Fact]
    public void UnicodeName_Kept()
    {
        Assert.Equal("铁磁力 设置 v2.txt", BlobPayload.SanitizeFileName("铁磁力 设置 v2.txt"));
    }
}
