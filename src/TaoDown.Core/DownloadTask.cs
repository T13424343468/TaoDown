namespace TaoDown.Core;

/// <summary>下载任务状态。</summary>
public enum DownloadStatus
{
    Waiting,
    Active,
    Paused,
    Error,
    Complete,
}

/// <summary>下载任务。</summary>
public sealed class DownloadTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Url { get; init; }

    /// <summary>引擎任务标识（gid），未提交引擎前为空。</summary>
    public string? EngineId { get; set; }

    public string? FileName { get; set; }

    public string SaveDir { get; set; } = string.Empty;

    public DownloadStatus Status { get; set; } = DownloadStatus.Waiting;

    public long TotalBytes { get; set; }

    public long CompletedBytes { get; set; }

    public long SpeedBytesPerSec { get; set; }

    /// <summary>BT 上传速度（字节/秒），非 BT 任务为 0。</summary>
    public long UploadSpeedBytesPerSec { get; set; }

    /// <summary>当前连接数（aria2），无数据为 0。</summary>
    public int Connections { get; set; }

    /// <summary>BT 做种数/可用来源数，非 BT 任务为 0。</summary>
    public int Seeders { get; set; }

    /// <summary>文件保存路径（含文件名），引擎回报后填充。</summary>
    public string? FilePath { get; set; }

    public LinkKind Kind { get; init; } = LinkKind.Unknown;

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public string? Error { get; set; }
}

/// <summary>链接类型。</summary>
public enum LinkKind
{
    Unknown,
    Http,
    Ftp,
    BitTorrent,
    Magnet,
    Ed2k,
    Thunder,
}
