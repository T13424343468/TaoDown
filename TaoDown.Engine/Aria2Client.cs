using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TaoDown.Engine;

/// <summary>aria2 引擎连接配置。</summary>
public sealed class Aria2Options
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; set; } = 16800;

    /// <summary>RPC 密钥，进程启动时随机生成，防本机其他程序误连。</summary>
    public string Secret { get; init; } = Guid.NewGuid().ToString("N");

    public string DownloadDir { get; init; } = @"D:\Program Files\Downloads\TaoDown";

    /// <summary>引擎运行日志路径（排查“下载中但无进度”类问题）。</summary>
    public string LogPath { get; set; } = "aria2.log";

    /// <summary>单文件最大连接数（分段并发）。</summary>
    public int MaxConnectionsPerServer { get; init; } = 16;

    public int Split { get; init; } = 16;

    /// <summary>全局速度上限（KB/s），0 表示不限速。</summary>
    public int OverallSpeedLimitKB { get; init; }

    /// <summary>BT 做种时长（分钟），0 表示不做种。</summary>
    public int SeedMinutes { get; init; }
}

/// <summary>aria2 JSON-RPC 客户端（HTTP 传输）。</summary>
public sealed class Aria2Client
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly Aria2Options _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public Aria2Client(Aria2Options options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>构造 JSON-RPC 请求体（公开给测试用）。</summary>
    public static object BuildRequest(string method, string secret, params object[] parameters)
    {
        var fullParams = new List<object> { $"token:{secret}" };
        fullParams.AddRange(parameters);
        return new { jsonrpc = "2.0", id = $"t{Guid.NewGuid():N}", method, @params = fullParams };
    }

    public async Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken ct = default)
    {
        var request = BuildRequest(method, _options.Secret, parameters);
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        var url = $"http://{_options.Host}:{_options.Port}/jsonrpc";
        using var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
            throw new InvalidOperationException($"aria2 RPC 错误: {message}");
        }

        return root.GetProperty("result").Clone();
    }

    public Task<string> AddUriAsync(IEnumerable<string> urls, IDictionary<string, string>? downloadOptions = null, CancellationToken ct = default)
    {
        var parameters = new List<object> { urls.ToArray() };
        if (downloadOptions is { Count: > 0 })
        {
            parameters.Add(downloadOptions);
        }

        return CallAsync("aria2.addUri", [.. parameters], ct)
            .ContinueWith(t => t.Result.GetString()!, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public Task<string> AddTorrentAsync(byte[] torrentBytes, CancellationToken ct = default) =>
        CallAsync("aria2.addTorrent", [Convert.ToBase64String(torrentBytes)], ct)
            .ContinueWith(t => t.Result.GetString()!, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    public Task PauseAsync(string gid, CancellationToken ct = default) => CallAsync("aria2.pause", [gid], ct);

    /// <summary>强制暂停：引擎侧状态不同步（pause 被拒绝）时的兜底。</summary>
    public Task ForcePauseAsync(string gid, CancellationToken ct = default) => CallAsync("aria2.forcePause", [gid], ct);

    public Task ResumeAsync(string gid, CancellationToken ct = default) => CallAsync("aria2.unpause", [gid], ct);

    public Task RemoveAsync(string gid, CancellationToken ct = default) => CallAsync("aria2.forceRemove", [gid], ct);

    /// <summary>清理已完成/出错任务的下载结果：forceRemove 对这类任务会报“不可移除”，用这个兜底。</summary>
    public Task RemoveResultAsync(string gid, CancellationToken ct = default) => CallAsync("aria2.removeDownloadResult", [gid], ct);

    /// <summary>查询任务状态，返回 aria2 原始字段（gid/status/totalLength/completedLength/downloadSpeed/files 等）。</summary>
    public Task<JsonElement> TellStatusAsync(string gid, CancellationToken ct = default) =>
        CallAsync("aria2.tellStatus", [gid], ct);

    public Task<JsonElement> GetGlobalStatAsync(CancellationToken ct = default) => CallAsync("aria2.getGlobalStat", [], ct);

    public Task<JsonElement> GetVersionAsync(CancellationToken ct = default) => CallAsync("aria2.getVersion", [], ct);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
