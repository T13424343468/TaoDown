using System.Buffers.Binary;
using System.Text.Json;

namespace TaoDown.Core;

/// <summary>
/// 浏览器扩展经 Native Messaging 转发的下载请求（批次五）。
/// </summary>
public sealed record BrowserRequest(string Url, string Referrer, string Filename, DateTime ReceivedAt)
{
    /// <summary>允许接管的协议（浏览器下载主要是 http/https/ftp，其余兜底放行给链接分类器）。</summary>
    private static readonly string[] AllowedSchemes = ["http", "https", "ftp", "magnet", "ed2k"];

    /// <summary>解析宿主转发的 JSON 请求；失败返回错误原因。</summary>
    public static bool TryParse(string json, out BrowserRequest? request, out string error)
    {
        request = null;
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // v2.58 真凶修复：宿主匿名对象序列化为 PascalCase（Url/Referrer/Filename），
            // 旧解析只认小写 → 真机收件箱条目全部「缺少下载地址」（v35 留痕日志坐实）。
            // 解析改为大小写不敏感，新旧包互通。
            var url = GetStringIgnoreCase(root, "url") ?? string.Empty;
            var referrer = GetStringIgnoreCase(root, "referrer") ?? string.Empty;
            var filename = GetStringIgnoreCase(root, "filename") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(url))
            {
                error = "缺少下载地址";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                error = "下载地址无效";
                return false;
            }

            if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            {
                error = $"不支持的协议: {uri.Scheme}";
                return false;
            }

            request = new BrowserRequest(url, referrer, SanitizeFilename(filename), DateTime.Now);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>大小写不敏感取字符串属性（宿主 PascalCase、插件 camelCase 兼容收）。</summary>
    private static string? GetStringIgnoreCase(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    /// <summary>Windows 文件名非法字符（显式声明，跨平台行为一致）。</summary>
    private static readonly char[] InvalidFileNameChars =
    [
        '"', '<', '>', '|', ':', '*', '?', '\\', '/', '\0',
        '\x01', '\x02', '\x03', '\x04', '\x05', '\x06', '\x07', '\x08', '\x09',
        '\x0A', '\x0B', '\x0C', '\x0D', '\x0E', '\x0F', '\x10', '\x11', '\x12',
        '\x13', '\x14', '\x15', '\x16', '\x17', '\x18', '\x19', '\x1A', '\x1B',
        '\x1C', '\x1D', '\x1E', '\x1F',
    ];

    /// <summary>去掉文件名中的路径分隔与非法字符，避免目录穿越。</summary>
    public static string SanitizeFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var chars = name.Split('\\', '/').Last().Select(c => InvalidFileNameChars.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }
}

/// <summary>
/// Native Messaging 帧编解码：4 字节小端长度 + UTF-8 JSON（Chrome 协议）。
/// </summary>
public static class NativeMessageFrame
{
    /// <summary>Chrome 限制单帧上限 4MB，防御性截断。</summary>
    public const int MaxLength = 4 * 1024 * 1024;

    /// <summary>读取一帧；流结束返回 null；超长抛异常。</summary>
    public static byte[]? TryRead(Stream input)
    {
        Span<byte> header = stackalloc byte[4];
        var read = 0;
        while (read < 4)
        {
            var n = input.Read(header[read..]);
            if (n == 0)
            {
                return read == 0 ? null : throw new IOException("帧头不完整");
            }

            read += n;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxLength)
        {
            throw new IOException($"帧长度越界: {length}");
        }

        var payload = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var n = input.Read(payload, offset, length - offset);
            if (n == 0)
            {
                throw new IOException("帧体不完整");
            }

            offset += n;
        }

        return payload;
    }

    /// <summary>写出一帧。</summary>
    public static void Write(Stream output, byte[] payload)
    {
        if (payload.Length > MaxLength)
        {
            throw new IOException($"载荷超长: {payload.Length}");
        }

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        output.Write(header);
        output.Write(payload);
        output.Flush();
    }
}
