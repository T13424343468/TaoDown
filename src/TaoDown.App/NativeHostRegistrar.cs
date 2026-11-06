using Microsoft.Win32;
using System.Text.Json;
using TaoDown.Core;

namespace TaoDown.App;

/// <summary>
/// 浏览器插件接管通道登记（批次五升级）：
/// ① Native Messaging 宿主对全部已知 Chromium 内核家族登记（HKCU 免管理员，HKLM 可写则双写；每家按键组登记，全部含 Chrome 兜底键）；
/// ② 插件上架官方商店后，写商店外链登记，新装/未来浏览器自动静默挂载（迅雷同款机制）。
/// </summary>
public static class NativeHostRegistrar
{
    public const string HostName = "com.tao.taodown";

    /// <summary>与扩展 manifest.json 内置 key 对应的稳定扩展 ID（已校验配对）。</summary>
    public const string ExtensionId = "dchnmdakoleaofgpkhddhmohcoomlimp";

    /// <summary>
    /// v2.50 前的旧扩展 ID（旧签名私钥未存档被迫换新）。旧 ID 保留在宿主 allowed_origins：
    /// 用户真机上手动加载过的旧版插件继续可用，避免其取消浏览器下载后转发失败丢文件；
    /// 登记 key 层面旧 ID 全部清干净，防止新旧插件同时挂载重复接管。
    /// </summary>
    public const string LegacyExtensionId = "mhbhaogipooncclfkmcdfhldfbpjakgi";

    /// <summary>宿主 manifest 的 allowed_origins：新旧两个 ID 都放行（升级过渡期并存）。</summary>
    public static string[] AllowedOrigins { get; } =
    [
        $"chrome-extension://{ExtensionId}/",
        $"chrome-extension://{LegacyExtensionId}/",
    ];

    /// <summary>接管开关状态文件（主程序写、宿主读，precheck 用）。</summary>
    public static string TakeoverStatePath =>
        Path.Combine(DataRoot.Sub("native_host"), "takeover_state.json");

    /// <summary>把当前「浏览器接管」开关落盘，宿主 precheck 据此放行或让浏览器自己下载。</summary>
    public static void WriteTakeoverState(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TakeoverStatePath)!);
            File.WriteAllText(TakeoverStatePath, JsonSerializer.Serialize(new
            {
                browser_takeover = enabled,
                updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }));
        }
        catch (Exception)
        {
            // 状态文件写失败不阻断；宿主读不到默认放行（装完即接管）
        }
    }

    /// <summary>登记全部已知内核家族，返回登记成功的家族名列表。</summary>
    public static IReadOnlyList<string> RegisterAll()
    {
        var hostExe = Path.Combine(AppContext.BaseDirectory, "TaoDown.Host.exe");
        if (!File.Exists(hostExe))
        {
            throw new FileNotFoundException("找不到 TaoDown.Host.exe", hostExe);
        }

        var manifestDir = DataRoot.Sub("native_host");
        Directory.CreateDirectory(manifestDir);

        var manifestPath = Path.Combine(manifestDir, $"{HostName}.json");
        var manifest = new
        {
            name = HostName,
            description = "TaoDown Browser Bridge",
            path = hostExe,
            type = "stdio",
            allowed_origins = AllowedOrigins,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

        var registered = new List<string>();
        foreach (var family in BrowserCatalog.ChromiumFamilies)
        {
            try
            {
                foreach (var root in family.NativeMessagingRoots)
                {
                    using var user = Registry.CurrentUser.CreateSubKey(Path.Combine(root, HostName));
                    user.SetValue(null, manifestPath);
                    try
                    {
                        using var machine = Registry.LocalMachine.CreateSubKey(Path.Combine(root, HostName));
                        machine.SetValue(null, manifestPath); // 管理员运行时全机生效，失败不阻断
                    }
                    catch (Exception)
                    {
                        // 非管理员：HKCU 已覆盖当前用户
                    }
                }

                registered.Add(family.Name);
            }
            catch (Exception)
            {
                // 单个家族失败不影响其他浏览器
            }
        }

        return registered;
    }

    /// <summary>
    /// 插件已上架官方商店时，写商店外链登记（迅雷同款自动注入）。
    /// 返回写成功的浏览器登记路径数；未上架（地址为空）返回 0。
    /// </summary>
    public static int WriteExternalExtensionEntries(string storeUrl)
    {
        var written = 0;
        foreach (var (path, url) in BrowserCatalog.ExternalExtensionEntries(ExtensionId, storeUrl))
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("update_url", url);
                written++;
            }
            catch (Exception)
            {
                // 单个登记失败不影响其他浏览器
            }
        }

        return written;
    }

    /// <summary>移除商店外链登记（关闭浏览器接管时调用）。</summary>
    public static void RemoveExternalExtensionEntries()
    {
        foreach (var (path, _) in BrowserCatalog.ExternalExtensionEntries(ExtensionId, "x"))
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
            }
            catch (Exception)
            {
                // 不存在即视为已移除
            }
        }
    }

    /// <summary>从安装目录插件清单读版本号（外链登记用）；读不到回退 1.1.0。</summary>
    private static string ReadExtensionVersion(string extDir)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<JsonElement>(
                File.ReadAllText(Path.Combine(extDir, "manifest.json")));
            if (manifest.TryGetProperty("version", out var version) && version.GetString() is { } ver)
            {
                return ver;
            }
        }
        catch (Exception)
        {
            // 读不到清单就用兜底版本
        }

        return "1.2.0";
    }

    /// <summary>
    /// 本地插件外链登记（v2.50）：未上架商店时把安装目录里的已签名插件包（taodown.crx）
    /// 写进 Extensions\<扩展ID> 键（path + version），各浏览器重启后自动挂载插件，
    /// 无需开发者模式、无需用户操作。v2.47 的目录型 path 只有 Chrome 认，Edge 官方
    /// 文档明确要求 path 指向打包文件——统一改为 .crx，全家族同一条路。
    /// 写 HKCU 为主（免管理员），HKLM 可写则双写；先清掉旧 ID 的全部登记键防重复挂载。
    /// 返回写成功的登记项数。
    /// </summary>
    public static int WriteLocalExtensionEntries(string extensionPath)
    {
        CleanupLegacyExtensionEntries();
        var written = 0;
        var version = ReadExtensionVersion(Path.GetDirectoryName(extensionPath) ?? extensionPath);
        foreach (var path in BrowserCatalog.LocalExtensionRegistryPaths(ExtensionId))
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(path);
                key.SetValue("path", extensionPath);
                key.SetValue("version", version);
                written++;
            }
            catch (Exception)
            {
                // 单个登记失败不影响其他浏览器
            }

            try
            {
                using var machine = Registry.LocalMachine.CreateSubKey(path);
                machine.SetValue("path", extensionPath);
                machine.SetValue("version", version);
            }
            catch (Exception)
            {
                // 非管理员：HKCU 已覆盖当前用户；机器级登记由安装包写入兜底
            }
        }

        return written;
    }

    /// <summary>
    /// 清掉旧扩展 ID（v2.50 前）的全部外链登记键：旧钥匙私钥未存档，插件包换新钥匙
    /// 后 ID 必变；不清理会导致浏览器同时挂载新旧两个插件、重复接管同一批下载。
    /// HKCU + HKLM（可写则删），不存在即视为已清。
    /// </summary>
    public static void CleanupLegacyExtensionEntries()
    {
        foreach (var path in BrowserCatalog.LocalExtensionRegistryPaths(LegacyExtensionId))
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
            }
            catch (Exception)
            {
                // 不存在即视为已清
            }

            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(path, false);
            }
            catch (Exception)
            {
                // 非管理员或不存在：跳过
            }
        }
    }

    /// <summary>移除本地插件外链登记（关闭浏览器接管或改用商店外链时调用）。</summary>
    public static void RemoveLocalExtensionEntries()
    {
        foreach (var id in new[] { ExtensionId, LegacyExtensionId })
        foreach (var path in BrowserCatalog.LocalExtensionRegistryPaths(id))
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, false);
            }
            catch (Exception)
            {
                // 不存在即视为已移除
            }
        }
    }

    /// <summary>浏览器下载请求收件箱目录（宿主与主程序的交接点）。</summary>
    public static string InboxDir => DataRoot.Sub("inbox");
}
