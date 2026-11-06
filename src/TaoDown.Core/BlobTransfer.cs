using System.Buffers.Text;
using System.Text;

namespace TaoDown.Core;

/// <summary>
/// blob/data 落地传输的编解码与安全工具（浏览器扩展 → 宿主分块传输用）。
/// </summary>
public static class BlobPayload
{
    /// <summary>单块原始字节数（base64 后约 683KB，低于 Native Messaging 1MB 上限）。</summary>
    public const int ChunkRawBytes = 512 * 1024;

    /// <summary>单文件大小上限（4GB）：超过则中止并提示。</summary>
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>校验并解码 base64 块。</summary>
    public static bool TryDecode(string base64, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(base64) || base64.Length > 1_400_000)
        {
            return false;
        }

        try
        {
            data = Convert.FromBase64String(base64);
            return data.Length <= ChunkRawBytes;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>编码一块原始字节。</summary>
    public static string EncodeChunk(ReadOnlySpan<byte> raw) => Convert.ToBase64String(raw);

    /// <summary>
    /// 清理浏览器传来的文件名：去路径、去非法字符、防盘符/遍历、防保留名；空则给默认名。
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        var s = (name ?? string.Empty).Trim();
        s = s.Replace("\\", "/").Replace(":", "/");
        s = Path.GetFileName(s); // 防目录遍历
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(char.IsControl(c) || c == '<' || c == '>' || c == '"' || c == '|' || c == '?' || c == '*' ? '_' : c);
        }

        s = sb.ToString().Trim('.', ' ');
        if (s.Length is 0 or > 200)
        {
            s = s.Length == 0 ? "blob-download.bin" : s[..200].TrimEnd('.', ' ');
        }

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
        var stem = s.Contains('.') ? s[..s.IndexOf('.')] : s;
        if (reserved.Contains(stem))
        {
            s = "_" + s;
        }

        return string.IsNullOrWhiteSpace(s) ? "blob-download.bin" : s;
    }
}

/// <summary>
/// blob 分块落盘会话（宿主与测试共用）：
/// 按序号收块，乱序/重复块容错重排，写临时文件，结束时原子改名交付。
/// </summary>
public sealed class BlobSession : IDisposable
{
    private readonly Dictionary<int, byte[]> _outOfOrder = new();
    private readonly string _tempPath;
    private FileStream? _stream;
    private long _totalBytes;
    private int _nextIndex;
    private bool _aborted;

    public string Id { get; }
    public string FinalPath { get; private set; } = string.Empty;
    public long TotalBytes => _totalBytes + _outOfOrder.Values.Sum(b => (long)b.Length);

    public BlobSession(string id, string tempPath)
    {
        Id = id;
        _tempPath = tempPath;
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
    }

    /// <summary>追加一块；块必须是下一个期望序号，乱序块暂存重排，重复块忽略。</summary>
    public bool AppendChunk(int index, byte[] data)
    {
        if (_aborted || data.Length > BlobPayload.ChunkRawBytes)
        {
            return false;
        }

        if (TotalBytes + data.Length > BlobPayload.MaxFileBytes)
        {
            Abort();
            return false;
        }

        if (index < _nextIndex)
        {
            return true; // 重复块
        }

        _outOfOrder[index] = data;
        while (_outOfOrder.TryGetValue(_nextIndex, out var chunk))
        {
            _outOfOrder.Remove(_nextIndex);
            _stream ??= new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            _stream.Write(chunk, 0, chunk.Length);
            _totalBytes += chunk.Length;
            _nextIndex++;
        }

        return true;
    }

    /// <summary>结束会话：关闭临时文件并原子改名到最终路径。</summary>
    public bool Complete(string finalPath)
    {
        if (_aborted || _stream is null)
        {
            Dispose();
            return false;
        }

        _stream.Dispose();
        _stream = null;
        FinalPath = finalPath;
        File.Move(_tempPath, finalPath, overwrite: true);
        return true;
    }

    public void Abort()
    {
        _aborted = true;
        if (_stream is not null)
        {
            _stream.Dispose();
            _stream = null;
        }

        try
        {
            if (File.Exists(_tempPath))
            {
                File.Delete(_tempPath);
            }
        }
        catch (IOException)
        {
            // 文件被占用时尽力清理
        }
    }

    public void Dispose()
    {
        if (_stream is not null)
        {
            _stream.Dispose();
            _stream = null;
        }
    }
}

/// <summary>
/// 从任意文本里提取第一条可识别的下载链接
/// （处理粘贴内容带前后缀、换行列表、引号包裹等情况）。
/// </summary>
public static class LinkTextScanner
{
    private static readonly string[] Schemes =
        { "ed2k://", "thunder://", "magnet:?", "magnet:", "https://", "http://", "ftp://" };

    /// <summary>返回文本中第一条可分类的链接；找不到返回 null。</summary>
    public static string? FindFirst(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var candidates = new List<KeyValuePair<int, string>>();
        foreach (var scheme in Schemes)
        {
            var rest = text;
            while (true)
            {
                var i = rest.IndexOf(scheme, StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                {
                    break;
                }

                var end = i;
                while (end < rest.Length && !char.IsWhiteSpace(rest[end]) && rest[end] != '"' && rest[end] != '\'' &&
                       rest[end] != '<' && rest[end] != '>' && rest[end] != ')' && rest[end] != '】' && rest[end] != '」' &&
                       rest[end] != '，' && rest[end] != '。' && rest[end] != '；' && rest[end] != '！' && rest[end] != '？' &&
                       rest[end] != '、' && rest[end] != '（' && rest[end] != '）')
                {
                    end++;
                }

                // ed2k/thunder 以 | 收尾，把整条链路吃全
                if (scheme.StartsWith("ed2k", StringComparison.OrdinalIgnoreCase) ||
                    scheme.StartsWith("thunder", StringComparison.OrdinalIgnoreCase))
                {
                    while (end < rest.Length && (rest[end] == '|' || rest[end] == '/'))
                    {
                        end++;
                    }
                }

                candidates.Add(new KeyValuePair<int, string>(i, rest[i..end]));
                rest = rest[Math.Max(end, i + scheme.Length)..];
            }
        }

        // 文本里有多条链接时按出现位置取最早一条（而非协议枚举顺序）
        foreach (var candidate in candidates.OrderBy(c => c.Key).Select(c => c.Value))
        {
            var normalized = LooseLinkParser.TryNormalize(candidate) ?? candidate;
            if (LinkClassifier.Classify(normalized) != LinkKind.Unknown)
            {
                return normalized;
            }
        }

        return null;
    }
}
