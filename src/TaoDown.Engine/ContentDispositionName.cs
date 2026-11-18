using System.Net;
using System.Net.Http;
using System.Text;

namespace TaoDown.Engine;

/// <summary>
/// 从 Content-Disposition 解析真实文件名（修红 run 44061254563 M18 实锤：
/// 源头给 156 字符中文名，aria2 引擎原生解析按 UTF-16 字节截断成 82 字符落盘）。
/// 提交前先探一次响应头，拿到文件名后经 downloadOptions["out"] 显式指定，
/// 绕开引擎截断；探不到就维持引擎默认行为（从 URL 推断）。
/// </summary>
public static class ContentDispositionName
{
    private const int MaxNameLength = 200; // 留足目录深度余量，避开 NTFS 255 上限

    /// <summary>从 Content-Disposition 头取文件名；无效时返回 null（含清理后的安全名）。</summary>
    public static string? ParseName(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        string? name = null;
        var sawStar = false;

        // RFC 5987 filename*=charset''percent-encoded（优先，编码信息最全）
        foreach (var part in SplitParts(headerValue))
        {
            var star = FindNamed(part, "filename*");
            if (star is not null)
            {
                sawStar = true;
                name = DecodeExtended(star);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name) && !sawStar)
        {
            foreach (var part in SplitParts(headerValue))
            {
                var plain = FindNamed(part, "filename");
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    name = UnescapeQuoted(plain.Trim().Trim('"')).Trim();
                    break;
                }
            }
        }

        return Sanitize(name);
    }

    /// <summary>
    /// 探测 HTTP 源的 Content-Disposition 文件名。失败/无头一律返回 null（调用方回退引擎默认命名），
    /// 绝不让探测失败挡住任务提交；超时 6 秒，Range 0-0 控制流量。
    /// </summary>
    public static async Task<string?> ResolveOutAsync(string url, string? allProxy, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        // 修红（run 44128258709 实锤）：探测偶发一次超时/异常（runner 冷启动网络栈抖动）就静默
        // 回退引擎原生命名，长中文名被截断——同一 URL 网络层瞬断不值得放弃，重试一次再交回退。
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var proxy = ToWebProxy(allProxy);
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    Proxy = proxy,
                    UseProxy = proxy is not null,
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                req.Headers.TryAddWithoutValidation("User-Agent", "TaoDown/1.0 filename-probe");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!resp.Content.Headers.TryGetValues("Content-Disposition", out var values))
                {
                    // 服务器明确没带头：重试也是同样答案，直接回退
                    return null;
                }

                return ParseName(string.Join(", ", values));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null; // 提交方取消，不重试
            }
            catch
            {
                // 网络类异常：第一次失败再试一次；仍失败不挡提交
                if (attempt == 2)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static WebProxy? ToWebProxy(string? allProxy)
    {
        if (string.IsNullOrWhiteSpace(allProxy))
        {
            return null;
        }

        var value = allProxy.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "http://" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var p) ? new WebProxy(p) : null;
    }

    private static IEnumerable<string> SplitParts(string header)
    {
        var depth = 0;
        var inQuote = false;
        var start = 0;
        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c == '"' && (i == 0 || header[i - 1] != '\\'))
            {
                inQuote = !inQuote;
            }
            else if (!inQuote && c == '(')
            {
                depth++;
            }
            else if (!inQuote && c == ')' && depth > 0)
            {
                depth--;
            }
            else if (!inQuote && depth == 0 && c == ';')
            {
                yield return header[start..i];
                start = i + 1;
            }
        }

        yield return header[start..];
    }

    private static string? FindNamed(string part, string key)
    {
        var idx = part.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var eq = part.IndexOf('=', idx + key.Length);
        if (eq < 0)
        {
            return null;
        }

        var value = part[(eq + 1)..].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>引号内 <c>\\</c> 与 <c>\"</c> 转义还原（RFC 2616 quoted-string）。</summary>
    private static string UnescapeQuoted(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && (value[i + 1] == '\\' || value[i + 1] == '"'))
            {
                sb.Append(value[i + 1]);
                i++;
            }
            else
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    private static string? DecodeExtended(string value)
    {
        // 形如 UTF-8''%E4%B8%AD.bin（lang 段可空）
        var quote = value.IndexOf("''", StringComparison.Ordinal);
        if (quote < 0)
        {
            return null;
        }

        var charset = value[..quote].Trim().Trim('"');
        var encoded = value[(quote + 2)..];
        try
        {
            var encoding = GetEncoding(charset);
            return encoding.GetString(PercentDecode(encoded));
        }
        catch
        {
            try
            {
                return Encoding.UTF8.GetString(PercentDecode(encoded));
            }
            catch
            {
                return null;
            }
        }
    }

    private static Encoding GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static byte[] PercentDecode(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length &&
                Hex(s[i + 1]) is var h1 and not -1 && Hex(s[i + 2]) is var h2 and not -1)
            {
                bytes.Add((byte)((h1 << 4) | h2));
                i += 2;
            }
            else
            {
                bytes.Add((byte)s[i]);
            }
        }

        return [.. bytes];
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // 平台无关的文件名非法集：Path.GetInvalidFileNameChars 在 Linux 上只含 / 与 \0，
    // 名字要落 Windows 盘（也要在 Linux 单测里可验证），非法集必须显式固定。
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(InvalidNameChars, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned.All(c => c == '_'))
        {
            return null;
        }

        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength] : cleaned;
    }
}
