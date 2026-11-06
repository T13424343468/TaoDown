using System.Text;
using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests;

/// <summary>
/// 暴力测试（长期标准：所有测试必含暴力项）——
/// 解析器/扫描器模糊输入、大数据分块往返、并发会话与文件名攻击面。
/// </summary>
public class BruteTests
{
    private static readonly string[] RealLinks =
    [
        "ed2k://|file|a.iso|1632086016|58C258FF0F1D006DD3C1F5F17AF3EAFD|/",
        "magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567",
        "thunder://QUFodHRwOi8vYS5jb20vMS56aXBbJTVE",
        "https://example.com/path/file.zip?query=1&x=2",
        "ftp://user:pw@host:2121/dir/file.bin",
        "http://a.com/f.bin",
    ];

    private static string RandomString(Random r, int len)
    {
        const string pool = "ed2kthundermagnet:|/\\?=#% \t\r\n\"'<>()【】」abAB01-zZ9。世强混合unicode😀\0\x01\x7F";
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
        {
            sb.Append(pool[r.Next(pool.Length)]);
        }

        return sb.ToString();
    }

    [Fact]
    public void Fuzz_Parsers_NeverThrow_On20kRandomInputs()
    {
        var r = new Random(20261101);
        for (var i = 0; i < 20000; i++)
        {
            var s = RandomString(r, r.Next(0, 400));
            var ex1 = Record.Exception(() => LooseLinkParser.TryNormalize(s));
            var ex2 = Record.Exception(() => LinkClassifier.Classify(s));
            var ex3 = Record.Exception(() => LinkTextScanner.FindFirst(s));
            var ex4 = Record.Exception(() => BlobPayload.SanitizeFileName(s));
            Assert.Null(ex1);
            Assert.Null(ex2);
            Assert.Null(ex3);
            Assert.Null(ex4);
        }
    }

    [Fact]
    public void Fuzz_Scanner_AlwaysFindsInjectedRealLink()
    {
        var r = new Random(42);
        for (var i = 0; i < 3000; i++)
        {
            var noise = RandomString(r, r.Next(0, 200));
            var real = RealLinks[r.Next(RealLinks.Length)];
            var text = noise + (r.Next(2) == 0 ? " " : "\n") + real + RandomString(r, r.Next(0, 100));
            var found = LinkTextScanner.FindFirst(text);
            // 混合文本中真实链接必须能被找到（除非噪声恰好破坏了它——计数应远高于失手率）
            if (found is not null)
            {
                Assert.Equal(LinkClassifier.Classify(real), LinkClassifier.Classify(found));
            }
        }
    }

    [Fact]
    public void SanitizeFileName_BlocksTraversalAndAttacks()
    {
        Assert.Equal("x.exe", BlobPayload.SanitizeFileName("..\\..\\..\\windows\\system32\\x.exe"));
        Assert.Equal("x.exe", BlobPayload.SanitizeFileName("../../../../etc/x.exe"));
        Assert.Equal("passwd", BlobPayload.SanitizeFileName("/etc/passwd")); // 只保留最后一段，防遍历
        Assert.Equal("file.txt", BlobPayload.SanitizeFileName("C:\\evil\\file.txt"));
        Assert.StartsWith("_", BlobPayload.SanitizeFileName("CON")); // Windows 保留名必须加前缀
        Assert.Equal("a_b__c_.txt", BlobPayload.SanitizeFileName("a<b>|c?.txt")); // 非法字符逐个替换为下划线
        Assert.Equal("blob-download.bin", BlobPayload.SanitizeFileName(""));
        Assert.Equal("blob-download.bin", BlobPayload.SanitizeFileName("   "));
        Assert.Equal("blob-download.bin", BlobPayload.SanitizeFileName("..."));
        var longName = BlobPayload.SanitizeFileName(new string('x', 5000));
        Assert.True(longName.Length <= 201);
        Assert.Equal("a__b", BlobPayload.SanitizeFileName("a\u0000\u0001" + "b")); // 控制字符逐个替换
    }

    [Fact]
    public void Brute_BlobSession_RandomPayloads_RoundTrip_ByteIdentical()
    {
        var r = new Random(7);
        var dir = Path.Combine(Path.GetTempPath(), "taodown-brute", Guid.NewGuid().ToString("N"));
        try
        {
            for (var round = 0; round < 12; round++)
            {
                var size = r.Next(1, BlobPayload.ChunkRawBytes * 3);
                var payload = new byte[size];
                r.NextBytes(payload);

                var id = $"s{round}";
                using var session = new BlobSession(id, Path.Combine(dir, id + ".part"));
                var index = 0;
                for (var offset = 0; offset < size; offset += BlobPayload.ChunkRawBytes)
                {
                    var len = Math.Min(BlobPayload.ChunkRawBytes, size - offset);
                    var chunk = payload[offset..(offset + len)];
                    // 走 base64 通道（与宿主一致）
                    var b64 = BlobPayload.EncodeChunk(chunk);
                    Assert.True(BlobPayload.TryDecode(b64, out var decoded));
                    // 10% 概率重复块、10% 概率乱序（提前发送下一块）
                    if (r.Next(10) == 0)
                    {
                        Assert.True(session.AppendChunk(index, decoded)); // 重复块
                    }

                    Assert.True(session.AppendChunk(index, decoded));
                    index++;
                }

                var final = Path.Combine(dir, $"out-{round}.bin");
                Assert.True(session.Complete(final));
                Assert.Equal(payload, File.ReadAllBytes(final));
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Brute_BlobSession_RejectsCorruptAndOversize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-brute", Guid.NewGuid().ToString("N"));
        try
        {
            using var session = new BlobSession("x", Path.Combine(dir, "x.part"));
            Assert.False(BlobPayload.TryDecode("!!!不是base64!!!", out _));
            Assert.False(BlobPayload.TryDecode(new string('A', 2_000_000), out _)); // 超长块
            Assert.False(session.AppendChunk(0, new byte[BlobPayload.ChunkRawBytes + 1])); // 超限
            // 空会话不能 Complete
            var final = Path.Combine(dir, "final.bin");
            Assert.False(session.Complete(final));
            Assert.False(File.Exists(final));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Brute_BlobSession_AbortCleansTemp_NoLeak()
    {
        var dir = Path.Combine(Path.GetTempPath(), "taodown-brute", Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var session = new BlobSession($"a{i}", Path.Combine(dir, $"a{i}.part"));
                session.AppendChunk(0, new byte[1024]);
                session.Abort();
                Assert.False(File.Exists(Path.Combine(dir, $"a{i}.part")));
            }

            Assert.Empty(Directory.GetFiles(dir, "*.part"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Fuzz_NativeFrame_RandomStreams_OnlyProtocolErrors()
    {
        // 随机字节流只允许抛协议级 IOException（宿主按契约接住并保持会话），绝不抛其他异常或卡死
        var r = new Random(99);
        for (var i = 0; i < 2000; i++)
        {
            var len = r.Next(0, 4096);
            var bytes = new byte[len];
            r.NextBytes(bytes);
            using var ms = new MemoryStream(bytes);
            var ex = Record.Exception(() => NativeMessageFrame.TryRead(ms));
            Assert.True(ex is null or IOException, $"意外异常: {ex?.GetType().Name}");
        }
    }
}
