using Microsoft.Win32;
using TaoDown.Core;

namespace TaoDown.App;

/// <summary>
/// 首次启动浏览器引导：检测本机已安装的 Chromium 系浏览器，
/// 弹一次性对话框引导加载插件（浏览器安全限制：扩展本身必须由用户在浏览器里加载一次）。
/// </summary>
internal static class BrowserGuide
{
    // v2.62：引导标记改为按版本记录——每装一个新版本，首次启动必弹一次引导。
    // 旧逻辑「标记文件不存在才弹」导致升级安装后再也不弹（真机反馈：上版本首启引导未出现），
    // 而引导是新版本验收浏览器挂载的固定入口，应当随版本可靠出现。
    private static string MarkerPath =>
        Path.Combine(DataRoot.Sub("guides"), "browser_guide_shown.flag");

    public static bool ShouldShow()
    {
        try
        {
            return !File.Exists(MarkerPath) ||
                   File.ReadAllText(MarkerPath).Trim() != BuildInfo.Mark;
        }
        catch
        {
            return false;
        }
    }

    public static void MarkShown()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, BuildInfo.Mark);
        }
        catch
        {
            // 记录失败不影响主流程，下次启动会再弹一次
        }
    }

    /// <summary>需要手动加载插件的浏览器（内核不认系统级自动挂载，只认开发者模式）。</summary>
    public static readonly string[] ManualLoadOnlyBrowsers = ["夸克浏览器", "360安全浏览器", "360极速浏览器"];

    /// <summary>按检测到的浏览器生成对的指引（v2.50）：自动挂载是主路，手动只留给夸克等少数内核。</summary>
    public static string BuildGuideText(List<string> browsers)
    {
        var lines = new List<string>
        {
            "TaoDown 已把插件登记进系统，重启浏览器后插件自动挂载——什么都不用点。",
        };
        if (browsers.Count > 0)
        {
            lines.Add($"检测到本机已安装：{string.Join("、", browsers)}。");
        }

        var manual = browsers.Where(b => ManualLoadOnlyBrowsers.Contains(b)).ToList();
        if (manual.Count > 0)
        {
            lines.Add($"{string.Join("、", manual)} 这类内核只认手动加载（一次即可）：");
            lines.Add("1. 在浏览器地址栏输入扩展页地址（夸克 quark://extensions/?entry=mainmenu，");
            lines.Add("   360 系 se://extensions，其余多为 chrome://extensions）；");
            lines.Add("2. 在扩展页开「开发者模式」→「加载已解压的扩展程序」→ 选「打开插件文件夹」里的目录");
            lines.Add("   （夸克的开关在扩展页左下角，不用进设置）。");
        }

        lines.Add("之后浏览器里点下载即由 TaoDown 接管，可在 TaoDown 设置页随时关掉。");
        return string.Join("\n", lines);
    }


    /// <summary>检测已安装浏览器（按名称去重）；检测不到返回空列表。</summary>
    public static List<string> DetectInstalled()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Registry.LocalMachine, Registry.CurrentUser,
        };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var root in roots)
        foreach (var view in views)
        {
            try
            {
                using var uninstall = RegistryKey.OpenBaseKey(root == Registry.LocalMachine
                    ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view)
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    var name = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (name.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
                        (name.Contains("Chrome", StringComparison.OrdinalIgnoreCase) &&
                         !name.Contains("Chromium", StringComparison.OrdinalIgnoreCase) &&
                         !name.Contains("App", StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add("Chrome");
                    }
                    else if (name.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add("Edge");
                    }
                    else if (name.Contains("夸克", StringComparison.Ordinal) ||
                             name.Contains("Quark", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add("夸克浏览器");
                    }
                    else if (name.Contains("360安全浏览器", StringComparison.Ordinal))
                    {
                        found.Add("360安全浏览器");
                    }
                    else if (name.Contains("360极速浏览器", StringComparison.Ordinal))
                    {
                        found.Add("360极速浏览器");
                    }
                    else if (name.Contains("Brave", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add("Brave");
                    }
                    else if (name.Contains("Vivaldi", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add("Vivaldi");
                    }
                    else if (name.Contains("Opera", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add("Opera");
                    }
                }
            }
            catch
            {
                // 读某个注册表分支失败就跳过
            }
        }

        return found.ToList();
    }
}
