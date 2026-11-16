using System.Net;
using System.Net.Sockets;

namespace TaoDown.Core;

/// <summary>
/// 代理豁免规则：回环、私网、链路本地及单标签本地主机名不走系统代理（直连）。
/// 背景：跟随系统代理后，本地源（127.0.0.1 等）的请求被送进不可用代理，
/// 下载界面停在「正在连接服务器」且 0 B——引擎侧对这些目标一律豁免 all-proxy。
/// 语义对齐浏览器「跳过本地地址的代理服务器」（<c>&lt;local&gt;</c>）。
/// </summary>
public static class ProxyBypass
{
    /// <summary>判定下载目标 URL 是否应绕过代理（直连）。解析失败或无主机名时返回 false（按需代理）。</summary>
    public static bool ShouldBypass(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 单标签主机名（无点号）：Windows「跳过本地地址」语义按本地处理，本地测试源常见形态
        if (!host.Contains('.') && Uri.CheckHostName(host) != UriHostNameType.Unknown)
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && IsLocalAddress(ip);
    }

    /// <summary>IPv4/IPv6 回环、私网、链路本地与唯一本地地址判断。</summary>
    public static bool IsLocalAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16/12
                || (b[0] == 192 && b[1] == 168) // 192.168/16
                || (b[0] == 169 && b[1] == 254); // 链路本地
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return true;
            }

            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 唯一本地
        }

        return false;
    }
}
