using System.Text;

namespace TaoDown.Engine;

/// <summary>
/// 迅雷/快车/QQ旋风加密链接解析：base64 还原真实下载地址。
/// 不接入迅雷 P2SP 加速网络，仅做本地字符串解码。
/// </summary>
public static class ThunderLinkParser
{
    public static string? Resolve(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var trimmed = link.Trim();
        string payload;
        if (trimmed.StartsWith("thunder://", StringComparison.OrdinalIgnoreCase))
        {
            payload = trimmed["thunder://".Length..];
        }
        else if (trimmed.StartsWith("flashget://", StringComparison.OrdinalIgnoreCase))
        {
            payload = trimmed["flashget://".Length..];
        }
        else if (trimmed.StartsWith("qqdl://", StringComparison.OrdinalIgnoreCase))
        {
            payload = trimmed["qqdl://".Length..];
        }
        else
        {
            return null;
        }

        // flashget 可能带 [EM=0&...] 之类后缀
        var amp = payload.IndexOf('&', StringComparison.Ordinal);
        if (amp >= 0)
        {
            payload = payload[..amp];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        // 迅雷格式首尾包裹 AA / ZZ
        if (text.Length > 4 && text.StartsWith("AA", StringComparison.Ordinal) && text.EndsWith("ZZ", StringComparison.Ordinal))
        {
            text = text[2..^2];
        }

        // 快车格式首尾包裹 [FLASHGET]
        if (text.StartsWith("[FLASHGET]", StringComparison.OrdinalIgnoreCase) &&
            text.EndsWith("[FLASHGET]", StringComparison.OrdinalIgnoreCase) &&
            text.Length > 20)
        {
            text = text[10..^10];
        }

        return IsValidTarget(text) ? text : null;
    }

    private static bool IsValidTarget(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase);
    }
}
