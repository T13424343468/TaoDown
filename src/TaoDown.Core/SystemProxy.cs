namespace TaoDown.Core;

/// <summary>
/// Windows 系统代理（Internet Settings 的 ProxyServer 字段）解析：
/// 供下载引擎跟随系统代理使用。aria2 不支持 socks 协议，仅取 http/https 条目。
/// </summary>
public static class SystemProxy
{
    /// <summary>把 ProxyServer 原始值解析成引擎可用的 all-proxy 地址；无可用 http 代理时返回 null。</summary>
    public static string? Parse(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer))
        {
            return null;
        }

        var value = proxyServer.Trim();
        if (!value.Contains(';'))
        {
            // 单一形式："host:port" 或带 scheme 前缀；socks 开头引擎不支持，不采用
            var isHttp = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var isSocks = value.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
            if (isSocks || (!isHttp && !value.Contains(':')))
            {
                return null;
            }

            return Normalize(value);
        }

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var scheme = part[..eq].Trim().ToLowerInvariant();
            var address = part[(eq + 1)..].Trim();
            if (address.Length == 0)
            {
                continue;
            }

            if (scheme is "http" or "https")
            {
                return Normalize(address);
            }
        }

        return null;
    }

    private static string Normalize(string address) =>
        address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? address
            : "http://" + address;
}
