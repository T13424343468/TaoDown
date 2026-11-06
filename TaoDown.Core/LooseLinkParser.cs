namespace TaoDown.Core;

/// <summary>
/// 宽松链接补全：把网页上常见的「不完整链接碎片」规范化为完整链接。
/// 支持：
/// - 裸 BT 哈希（40 位十六进制）→ magnet:?xt=urn:btih:哈希
/// - 32 位 Base32 哈希 → magnet:?xt=urn:btih:哈希
/// - eMule 碎片（文件名|大小|32 位十六进制哈希）→ ed2k://|file|...|/
/// 已是标准链接（http/ftp/magnet/ed2k/thunder）原样返回；无法识别返回 null。
/// </summary>
public static class LooseLinkParser
{
    public static string? TryNormalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var u = input.Trim().Trim('"', '\'', '<', '>', '“', '”', '‘', '’');
        if (LinkClassifier.Classify(u) != LinkKind.Unknown)
        {
            return u;
        }

        // eMule 碎片：文件名|大小|哈希
        var parts = u.Split('|');
        if (parts.Length == 3)
        {
            var name = parts[0].Trim();
            var size = parts[1].Trim();
            var hash = parts[2].Trim();
            if (name.Length > 0 && long.TryParse(size, out var sz) && sz > 0 && IsHex(hash, 32))
            {
                return $"ed2k://|file|{name}|{sz}|{hash.ToUpperInvariant()}|/";
            }
        }

        // 去空格后识别裸哈希
        var compact = new string(u.Where(char.IsLetterOrDigit).ToArray());
        if (IsHex(compact, 40))
        {
            return $"magnet:?xt=urn:btih:{compact.ToLowerInvariant()}";
        }

        if (compact.Length == 32 && compact.All(c => char.IsAsciiLetterOrDigit(c)) &&
            compact.All(c => "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".Contains(char.ToUpperInvariant(c))))
        {
            return $"magnet:?xt=urn:btih:{compact.ToUpperInvariant()}";
        }

        return null;
    }

    private static bool IsHex(string s, int length) =>
        s.Length == length && s.All(c => Uri.IsHexDigit(c));
}
