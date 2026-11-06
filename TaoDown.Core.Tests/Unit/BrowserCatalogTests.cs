using System.Security.Cryptography;
using System.Text.Json;
using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>浏览器接管目录：扩展 ID 推导、内核家族清单、商店外链登记项。</summary>
public class BrowserCatalogTests
{
    /// <summary>与 src/extension/manifest.json 的 key 一致；ID 必须由公钥哈希推出（已知答案校验）。</summary>
    private const string ManifestPublicKeySpkiBase64 =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA2QZ9+88wjsFEwSmck4OB3j0mPN31Rxt2M5KG4F5IT5bXcTVNFq+VnpZecepXBrMctt3ClaQQwR6VdfZSbmS2AEjTpEabwZR7woO9sMyQWwzWPYor1A3Fdfd2TGWKf2GETzRJQLKV5Z9gY92s93Ny1a3uxho9ykL7lJDMP71/VhY+wGuyL1cNB640VVxGw/FKZRG2iF4h8yBDPuqEALx5zGbY1HpOdkFCoy1rwIbMTtEELt5NB6XYVOaashfSfQGhjxZWC85junkNSpOR5ZuslYTOoB0u59vVmsUKIE6m0US1TTFA32OVBV5ANPn7Y+wa7FJEjA1cs67DD8qNTA9DWQIDAQAB";

    [Fact]
    public void ExtensionId_DerivesFromManifestPublicKey()
    {
        var spki = Convert.FromBase64String(ManifestPublicKeySpkiBase64);
        Assert.Equal("dchnmdakoleaofgpkhddhmohcoomlimp", BrowserCatalog.IdFromPublicKeySpki(spki));
    }

    [Fact]
    public void ExtensionId_IsStable32CharAlphabet()
    {
        using var rsa = RSA.Create(2048);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var id = BrowserCatalog.IdFromPublicKeySpki(spki);
        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.InRange(c, 'a', 'p'));
    }

    [Fact]
    public void ChromiumFamilies_CoverMainBrowsers()
    {
        var names = BrowserCatalog.ChromiumFamilies.Select(f => f.Name).ToList();
        Assert.Contains("Chrome", names);
        Assert.Contains("Edge", names);
        Assert.Contains("夸克", names);
        Assert.All(BrowserCatalog.ChromiumFamilies, f =>
        {
            Assert.NotEmpty(f.NativeMessagingRoots);
            Assert.All(f.NativeMessagingRoots, r => Assert.EndsWith("NativeMessagingHosts", r));
        });
    }

    /// <summary>全家族统一含 Chrome 兜底键（Edge 官方回退链、Opera 官方文档、KeePassXC 实践）；Brave 双候选键齐备。</summary>
    [Fact]
    public void ChromiumFamilies_EveryFamilyHasChromeFallback()
    {
        Assert.All(BrowserCatalog.ChromiumFamilies, f =>
            Assert.Contains(BrowserCatalog.ChromeFallbackRoot, f.NativeMessagingRoots));

        var brave = BrowserCatalog.ChromiumFamilies.First(f => f.Name == "Brave");
        Assert.Contains(@"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts", brave.NativeMessagingRoots);

        var opera = BrowserCatalog.ChromiumFamilies.First(f => f.Name == "Opera");
        // Opera 官方帮助明确写 Chrome 键，兜底键排首位
        Assert.Equal(BrowserCatalog.ChromeFallbackRoot, opera.NativeMessagingRoots[0]);
    }

    [Fact]
    public void ExternalEntries_EmptyWhenNotPublished()
    {
        Assert.Empty(BrowserCatalog.ExternalExtensionEntries("id", ""));
        Assert.Empty(BrowserCatalog.ExternalExtensionEntries("id", "  "));
    }

    [Fact]
    public void ExternalEntries_CoverChromeEdgeWhenPublished()
    {
        var entries = BrowserCatalog.ExternalExtensionEntries("abc", "https://clients2.google.com/service/update2/crx");
        Assert.Contains(entries, e => e.RegistryPath.Contains(@"Google\Chrome\Extensions\abc"));
        Assert.Contains(entries, e => e.RegistryPath.Contains(@"Microsoft\Edge\Extensions\abc"));
        Assert.All(entries, e => Assert.Equal("https://clients2.google.com/service/update2/crx", e.UpdateUrl));
    }

    /// <summary>本地插件外链键组（v2.47）：与 NativeMessagingRoots 同套同源，尾段换为 Extensions\&lt;ID&gt;。</summary>
    [Fact]
    public void LocalExtensionRegistryPaths_CoverFamiliesAndUseExtensionsTail()
    {
        var id = "dchnmdakoleaofgpkhddhmohcoomlimp";
        var paths = BrowserCatalog.LocalExtensionRegistryPaths(id);
        Assert.NotEmpty(paths);
        Assert.Contains($@"Software\Google\Chrome\Extensions\{id}", paths);
        Assert.Contains($@"Software\Microsoft\Edge\Extensions\{id}", paths);
        Assert.Contains($@"Software\BraveSoftware\Brave\Extensions\{id}", paths);
        Assert.Contains($@"Software\BraveSoftware\Brave-Browser\Extensions\{id}", paths);
        Assert.Contains($@"Software\Vivaldi\Extensions\{id}", paths);
        Assert.Contains($@"Software\Quark\Extensions\{id}", paths);
        Assert.Contains($@"Software\360Chrome\Chrome\Extensions\{id}", paths);
        Assert.Contains($@"Software\360ChromeX\Chrome\Extensions\{id}", paths);
        Assert.Contains($@"Software\360SE\Chrome\Extensions\{id}", paths);
        Assert.Contains($@"Software\360se6\Chrome\Extensions\{id}", paths);
        Assert.Contains($@"Software\Quark\Quark\Extensions\{id}", paths);
        Assert.Contains($@"Software\UC\Quark\Extensions\{id}", paths);
        Assert.Contains($@"Software\QuarkBrowser\Extensions\{id}", paths);
        Assert.All(paths, p =>
        {
            Assert.DoesNotContain("NativeMessagingHosts", p);
            Assert.EndsWith($@"\Extensions\{id}", p);
        });
        Assert.Equal(paths.Count, paths.Distinct().Count());

        // v2.50: 键组唯一来源 tools/extension/registry_paths.json —— C# 推导与 JSON 必须完全一致,
        // MSI 安装期登记读取同一 JSON；两边漂移会让安装包登记的键与运行时登记的键分家。
        var jsonPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "extension", "registry_paths.json"));
        Assert.True(File.Exists(jsonPath), $"找不到 {jsonPath}（相对 BaseDirectory 五级向上）");
        var cfg = JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement;
        var jsonPaths = cfg.GetProperty("paths").EnumerateArray()
            .Select(e => e.GetString()!).Select(p => $@"{p}\{id}").ToList();
        Assert.True(jsonPaths.SequenceEqual(paths), "registry_paths.json 与 BrowserCatalog.LocalExtensionRegistryPaths 不一致");
        Assert.Equal(id, cfg.GetProperty("extension_id").GetString());
    }

    /// <summary>扩展页地址：夸克用户真机（v7.4.2.1020）确证须带 entry=mainmenu，否则打开空标签页；360 安全用 se://。</summary>
    [Fact]
    public void ExtensionsPageArgs_MatchVerifiedRealMachineValues()
    {
        var quark = new[] { "夸克", "夸克(UC)", "夸克浏览器" };
        Assert.All(quark, n =>
            Assert.Equal("quark://extensions/?entry=mainmenu",
                BrowserCatalog.ChromiumFamilies.First(f => f.Name == n).ExtensionsPageArg));
        Assert.Equal("se://extensions",
            BrowserCatalog.ChromiumFamilies.First(f => f.Name == "360安全").ExtensionsPageArg);
        Assert.Equal("chrome://extensions",
            BrowserCatalog.ChromiumFamilies.First(f => f.Name == "360极速").ExtensionsPageArg);
        Assert.Equal("browser://extensions",
            BrowserCatalog.ChromiumFamilies.First(f => f.Name == "Yandex").ExtensionsPageArg);
        // 全家族扩展页都以 extensions 收尾（打开的都是扩展管理页，不会白开）
        Assert.All(BrowserCatalog.ChromiumFamilies, f =>
            Assert.Contains("extensions", f.ExtensionsPageArg));
    }

    [Fact]
    public void ExtensionStoreUrl_RoundTripsThroughStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taodown-ext-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new DownloadSettings { ExtensionStoreUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx" });
            Assert.Equal("https://edge.microsoft.com/extensionwebstorebase/v1/crx", store.Load().ExtensionStoreUrl);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
