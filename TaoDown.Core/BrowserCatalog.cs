using System.Security.Cryptography;

namespace TaoDown.Core;

/// <summary>Chromium 系浏览器家族（接管通道登记键组与扩展页地址）。</summary>
/// <param name="NativeMessagingRoots">
/// 登记键组：自家候选键在前、Chrome 兜底键殿后（Opera 以官方文档为准、Chrome 键在前）。
/// 各键都指向同一 manifest，浏览器按其搜索顺序取到任一键即生效，多写无害。
/// </param>
public sealed record BrowserFamily(string Name, IReadOnlyList<string> NativeMessagingRoots, string ExtensionsPageArg);

/// <summary>
/// 浏览器接管目录：内核家族清单、扩展 ID 推导（公钥 → a–p 字母表）、
/// 商店外链登记项构造。纯逻辑，注册表读写由 App 层完成。
/// </summary>
public static class BrowserCatalog
{
    /// <summary>
    /// Chrome 兜底键。核验依据：微软官方文档确认 Edge 有完整回退链（Edge→Chromium→Chrome）；
    /// Opera 官方帮助明确写 Windows 下登记在 Chrome 键；KeePassXC 源码在 Windows 对 Brave/Vivaldi 也只写 Chrome 键。
    /// 扩展 ID 由公钥推导、全部 Chromium 系通用，同一 manifest 对所有家族有效。
    /// </summary>
    public const string ChromeFallbackRoot = @"Software\Google\Chrome\NativeMessagingHosts";

    /// <summary>已知 Chromium 内核家族（新装浏览器在软件启动时按这份清单自动补登记）。</summary>
    public static IReadOnlyList<BrowserFamily> ChromiumFamilies { get; } =
    [
        new("Chrome", [ChromeFallbackRoot], "chrome://extensions"),
        new("Edge", [@"Software\Microsoft\Edge\NativeMessagingHosts", ChromeFallbackRoot], "edge://extensions"),
        new("Chromium", [@"Software\Chromium\NativeMessagingHosts", ChromeFallbackRoot], "chrome://extensions"),
        // Brave：策略键惯例为 BraveSoftware\Brave；社区常见写法还有带 -Browser 后缀的键；
        // KeePassXC 实际只写 Chrome 键也能被 Brave 识别——三键全写，认哪个都能连上。
        new("Brave", [
            @"Software\BraveSoftware\Brave\NativeMessagingHosts",
            @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
            ChromeFallbackRoot,
        ], "brave://extensions"),
        // Vivaldi：KeePassXC 只写 Chrome 键；社区资料指导写自家键——两键都写。
        new("Vivaldi", [@"Software\Vivaldi\NativeMessagingHosts", ChromeFallbackRoot], "vivaldi://extensions"),
        // Opera：官方帮助文档明确写 Windows 下登记在 Chrome 键；自家键作为可选候选保留。
        new("Opera", [ChromeFallbackRoot, @"Software\Opera Software\NativeMessagingHosts"], "opera://extensions"),
        // Yandex：官方仅泛提组策略，未见登记键权威文档；两个常见候选键 + Chrome 兜底。
        new("Yandex", [
            @"Software\Yandex\YandexBrowser\NativeMessagingHosts",
            @"Software\YandexBrowser\NativeMessagingHosts",
            ChromeFallbackRoot,
        ], "browser://extensions"),
        // 国产/套壳内核登记位置无权威文档，多写候选键，认哪个都能连上，多写无害。
        // 扩展页地址：夸克用户真机（v7.4.2.1020，基于 Chromium 开源项目）确证
        // quark://extensions 打开的是空新标签页，真地址是 quark://extensions/?entry=mainmenu。
        // 360 安全浏览器扩展页为 se://extensions（社区教程与用户侧一致），360 极速用 chrome://extensions。
        new("夸克", [
            @"Software\Quark\NativeMessagingHosts",
            @"Software\Quark\Quark\NativeMessagingHosts",
            ChromeFallbackRoot,
        ], "quark://extensions/?entry=mainmenu"),
        new("夸克(UC)", [@"Software\UC\Quark\NativeMessagingHosts", ChromeFallbackRoot], "quark://extensions/?entry=mainmenu"),
        new("夸克浏览器", [@"Software\QuarkBrowser\NativeMessagingHosts", ChromeFallbackRoot], "quark://extensions/?entry=mainmenu"),
        new("360极速", [
            @"Software\360Chrome\Chrome\NativeMessagingHosts",
            @"Software\360ChromeX\Chrome\NativeMessagingHosts",
            ChromeFallbackRoot,
        ], "chrome://extensions"),
        new("360安全", [
            @"Software\360SE\Chrome\NativeMessagingHosts",
            @"Software\360se6\Chrome\NativeMessagingHosts",
            ChromeFallbackRoot,
        ], "se://extensions"),
    ];

    /// <summary>扩展 ID：公钥（X.509 SPKI DER）SHA-256 前 16 字节按 a–p 字母表展开，共 32 字符。</summary>
    public static string IdFromPublicKeySpki(byte[] spkiDer)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(spkiDer);
        const string alphabet = "abcdefghijklmnop";
        var chars = new char[16 * 2];
        for (var i = 0; i < 16; i++)
        {
            chars[i * 2] = alphabet[hash[i] >> 4];
            chars[i * 2 + 1] = alphabet[hash[i] & 0xF];
        }

        return new string(chars);
    }

    /// <summary>
    /// 商店外链登记项：插件上架官方商店后，浏览器据此外链自动静默安装（迅雷同款机制）。
    /// 返回 (注册表路径, 值名, update_url)；商店地址为空表示尚未上架，返回 null。
    /// </summary>
    public static IReadOnlyList<(string RegistryPath, string UpdateUrl)> ExternalExtensionEntries(
        string extensionId, string storeUrl)
    {
        if (string.IsNullOrWhiteSpace(storeUrl))
        {
            return [];
        }

        return
        [
            ($@"Software\Google\Chrome\Extensions\{extensionId}", storeUrl),
            ($@"Software\Chromium\Extensions\{extensionId}", storeUrl),
            ($@"Software\Microsoft\Edge\Extensions\{extensionId}", storeUrl),
            ($@"Software\BraveSoftware\Brave\Extensions\{extensionId}", storeUrl),
            ($@"Software\Vivaldi\Extensions\{extensionId}", storeUrl),
        ];
    }

    /// <summary>
    /// 本地插件外链登记键组（v2.47）：未上架商店时，把安装目录里的插件目录写进
    /// Extensions\&lt;扩展ID&gt; 键（path=插件目录 + version=清单版本），浏览器重启后自动挂载插件，
    /// 无需开发者模式、无需用户操作。键组与 NativeMessagingRoots 同套同源：
    /// 尾段 NativeMessagingHosts 替换为 Extensions（自家候选+Chrome 兜底，多写无害）。
    /// </summary>
    public static IReadOnlyList<string> LocalExtensionRegistryPaths(string extensionId)
    {
        var paths = new List<string>();
        foreach (var family in ChromiumFamilies)
        {
            foreach (var root in family.NativeMessagingRoots)
            {
                const string tail = "NativeMessagingHosts";
                if (!root.EndsWith(tail))
                {
                    continue;
                }

                paths.Add($@"{root[..^tail.Length]}Extensions\{extensionId}");
            }
        }

        return paths.Distinct().ToList();
    }
}
