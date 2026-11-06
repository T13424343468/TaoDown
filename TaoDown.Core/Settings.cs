using System.Text.Json;

namespace TaoDown.Core;

/// <summary>下载设置（批次三：设置页读写，JSON 持久化）。</summary>
public sealed class DownloadSettings
{
    /// <summary>旧默认下载根目录（用户机器上已存过的设置值，读入时迁移到新默认）。</summary>
    public static readonly string LegacyDownloadDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "TaoDown");

    /// <summary>下载根目录（默认 D:\Program Files\Downloads\TaoDown）。</summary>
    public string DownloadDir { get; set; } = @"D:\Program Files\Downloads\TaoDown";

    /// <summary>同时下载任务数上限。</summary>
    public int MaxConcurrent { get; set; } = 3;

    /// <summary>单文件最大连接数（分段并发）。</summary>
    public int MaxConnectionsPerServer { get; set; } = 16;

    public int Split { get; set; } = 16;

    /// <summary>全局速度上限（KB/s），0 表示不限速。</summary>
    public int SpeedLimitKB { get; set; }

    /// <summary>自动按类型分目录（视频/音频/压缩包/文档/安装包/图片）。</summary>
    public bool AutoClassify { get; set; } = true;

    /// <summary>剪贴板监视：复制链接即提示添加任务。</summary>
    public bool ClipboardMonitor { get; set; } = true;

    /// <summary>浏览器下载接管：插件转发的请求弹出保存位置（批次五）。</summary>
    public bool BrowserTakeover { get; set; } = true;

    /// <summary>链接协议接管：magnet/ed2k/迅雷/种子文件点击后由 TaoDown 打开（v2.45）。</summary>
    public bool ProtocolTakeover { get; set; } = true;

    /// <summary>插件官方商店链接（上架后填入；非空时浏览器自动静默挂载插件，迅雷同款）。</summary>
    public string ExtensionStoreUrl { get; set; } = string.Empty;

    /// <summary>BT 做种时长（分钟），0 表示不做种。</summary>
    public int SeedMinutes { get; set; }

    /// <summary>开机会自动启动下载（跟随系统，预留）。</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>关闭主窗口行为：0=每次询问，1=最小化到托盘，2=直接退出。</summary>
    public int CloseBehavior { get; set; }

    /// <summary>界面主题：0=水墨（默认自带），1=浅色，2=深色。</summary>
    public int ThemeId { get; set; }
}

/// <summary>设置存储：JSON 落盘，写入采用临时文件+替换，避免半写损坏。</summary>
public sealed class SettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath();
    }

    public DownloadSettings Load()
    {
        // v2.54：设置文件损坏/被占用（半写、磁盘故障、手工编辑坏 JSON）一律回默认值，
        // 绝不让异常穿到启动流程造成闪退；用户在设置页重新保存即覆盖恢复
        try
        {
            return LoadCore();
        }
        catch (Exception)
        {
            return new DownloadSettings();
        }
    }

    private DownloadSettings LoadCore()
    {
        if (!File.Exists(_filePath))
        {
            return new DownloadSettings();
        }

        var settings = JsonSerializer.Deserialize<DownloadSettings>(File.ReadAllText(_filePath), _jsonOptions) ??
                       new DownloadSettings();

        // 旧版本存过“用户目录\Downloads\TaoDown”的一并迁到新默认，避免老用户改不动
        if (string.IsNullOrWhiteSpace(settings.DownloadDir) ||
            string.Equals(settings.DownloadDir, DownloadSettings.LegacyDownloadDir, StringComparison.OrdinalIgnoreCase))
        {
            settings.DownloadDir = new DownloadSettings().DownloadDir;
        }

        return settings;
    }

    public void Save(DownloadSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, _jsonOptions));
        File.Move(temp, _filePath, overwrite: true);
    }

    private static string DefaultPath()
    {
        return Path.Combine(DataRoot.Root(), "settings.json");
    }
}
