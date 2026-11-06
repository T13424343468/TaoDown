using System.Text.Json;
using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests;

/// <summary>系统测试补充：blob/data 端到端管线（分块→宿主落盘→信封→应用交付）。</summary>
public class BlobDeliveryTests
{
    [Fact]
    public async Task EndToEnd_BlobPipeline_ByteIdentical_Delivery()
    {
        var root = Path.Combine(Path.GetTempPath(), "taodown-sys-blob", Guid.NewGuid().ToString("N"));
        try
        {
            // 1) 模拟扩展：1.5MB 随机内容分块 base64
            var r = new Random(2026);
            var payload = new byte[1_500_000];
            r.NextBytes(payload);
            var chunks = new List<string>();
            for (var i = 0; i < payload.Length; i += BlobPayload.ChunkRawBytes)
            {
                var len = Math.Min(BlobPayload.ChunkRawBytes, payload.Length - i);
                chunks.Add(BlobPayload.EncodeChunk(payload[i..(i + len)]));
            }

            // 2) 模拟宿主：会话落盘 + 写收件箱信封（含 DataFile）
            var tmpDir = Path.Combine(root, "blobtmp");
            var inbox = Path.Combine(root, "inbox");
            var id = "sys-1";
            using (var session = new BlobSession(id, Path.Combine(tmpDir, $"{id}.part")))
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    Assert.True(BlobPayload.TryDecode(chunks[i], out var bytes));
                    Assert.True(session.AppendChunk(i, bytes));
                }

                var safeName = BlobPayload.SanitizeFileName("report 最终版<v2>.pdf");
                var dataDir = Path.Combine(inbox, "files");
                Directory.CreateDirectory(dataDir);
                var finalPath = Path.Combine(dataDir, $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{safeName}");
                Assert.True(session.Complete(finalPath));

                var envelope = JsonSerializer.Serialize(new
                {
                    Url = $"blob:transfer:{safeName}",
                    Referrer = string.Empty,
                    Filename = safeName,
                    ReceivedAt = DateTime.Now,
                    DataFile = finalPath,
                });
                File.WriteAllText(Path.Combine(inbox, $"{id}.json"), envelope);
                var delivered = File.ReadAllBytes(finalPath);
                Assert.Equal(payload, delivered); // 字节级一致
                Assert.Equal("report 最终版_v2_.pdf", safeName);
            }

            // 3) 模拟应用交付：重名自动加序号
            var targetDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(targetDir);
            var name = BlobPayload.SanitizeFileName("report 最终版_v2_.pdf");
            var target = Path.Combine(targetDir, name);
            await File.WriteAllBytesAsync(target, [1]);
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var n = 1; File.Exists(target); n++)
            {
                target = Path.Combine(targetDir, $"{stem} ({n}){ext}");
            }

            var src = Path.Combine(inbox, "files");
            File.Move(Directory.GetFiles(src)[0], target);
            Assert.Equal("report 最终版_v2_ (1).pdf", Path.GetFileName(target));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void EndToEnd_DataUrlText_FormsDecode()
    {
        // data: 纯文本形式（扩展内 decodeURIComponent + UTF8 编码）
        const string text = "中文内容 hello 世界";
        var encoded = "data:text/plain;charset=utf-8," + Uri.EscapeDataString(text);
        var comma = encoded.IndexOf(',');
        var decoded = Uri.UnescapeDataString(encoded[(comma + 1)..]);
        Assert.Equal(text, decoded);
    }
}
