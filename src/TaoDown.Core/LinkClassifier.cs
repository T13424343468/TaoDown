namespace TaoDown.Core;

/// <summary>链接类型识别。</summary>
public static class LinkClassifier
{
    public static LinkKind Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return LinkKind.Unknown;
        }

        var u = url.Trim();
        if (u.StartsWith("thunder://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("flashget://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("qqdl://", StringComparison.OrdinalIgnoreCase))
        {
            return LinkKind.Thunder;
        }

        if (u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return LinkKind.BitTorrent;
        }

        if (u.StartsWith("ed2k://", StringComparison.OrdinalIgnoreCase))
        {
            return LinkKind.Ed2k;
        }

        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return LinkKind.Http;
        }

        if (u.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            return LinkKind.Ftp;
        }

        return LinkKind.Unknown;
    }

    private static readonly string[] DownloadExtensions =
    {
        ".exe", ".msi", ".msix", ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz",
        ".iso", ".img", ".apk", ".dmg", ".pkg", ".cab", ".deb", ".rpm", ".jar",
        ".mp4", ".mkv", ".avi", ".mov", ".flv", ".wmv", ".mp3", ".flac", ".wav", ".ape",
        ".pdf", ".epub", ".mobi", ".torrent",
    };

    /// <summary>是否「像直接下载的文件」（v2.60）：磁力/电驴/迅雷/FTP 一律算；
    /// http(s) 只有路径以常见安装包/压缩包/媒体等扩展名结尾，或链接带文件名参数
    /// （网盘签名下载、attachment 等）才算——普通网页地址不弹接管/剪贴板询问窗。</summary>
    public static bool LooksLikeDirectDownload(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var u = url.Trim();
        var kind = Classify(u);
        if (kind is LinkKind.BitTorrent or LinkKind.Ed2k or LinkKind.Thunder or LinkKind.Ftp)
        {
            return true;
        }

        if (kind != LinkKind.Http)
        {
            return false;
        }

        // 去掉 ?query 与 #fragment 后取路径末段
        var path = u;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        var last = path.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        if (DownloadExtensions.Any(ext => last.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 网盘签名下载（response-content-disposition）与 filename= 参数带扩展名的直链
        if (u.Contains("response-content-disposition", StringComparison.OrdinalIgnoreCase) ||
            u.Contains("attachment;", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var q = cut >= 0 ? u[(cut + 1)..] : string.Empty;
        foreach (var piece in q.Split('&'))
        {
            if (piece.StartsWith("filename=", StringComparison.OrdinalIgnoreCase) ||
                piece.StartsWith("fileName=", StringComparison.Ordinal))
            {
                var value = Uri.UnescapeDataString(piece["filename=".Length..]);
                if (DownloadExtensions.Any(ext => value.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
