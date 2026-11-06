using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TaoDown.Engine;

/// <summary>
/// 下载引擎进程托管：以子进程启动 aria2-next（aria2 分支，兼容 JSON-RPC，
/// 原生支持 ED2K/eD2k 与 thunder），独占 RPC 端口与随机密钥，
/// 等待 RPC 就绪后返回客户端；进程退出时负责清理。
/// 端口被占用时自动顺延重试；提供崩溃后自动重启（EnsureRunningAsync）。
/// </summary>
public sealed class Aria2EngineHost : IDisposable
{
    private const int MaxPortAttempts = 20;
    private const int MaxStartAttempts = 3;

    private readonly Aria2Options _options;
    private readonly string _exePath;
    private Process? _process;
    private readonly object _gate = new();

    /// <summary>实际生效的 RPC 端口（端口冲突顺延后可能不是初始值）。</summary>
    public int ActivePort => _options.Port;

    /// <summary>引擎崩溃并完成自动重启后触发（带上新客户端）。</summary>
    public event EventHandler<Aria2Client>? Restarted;

    public Aria2EngineHost(Aria2Options? options = null, string? exePath = null)
    {
        _options = options ?? new Aria2Options();
        _exePath = exePath ?? DefaultExePath();
    }

    public Aria2Options Options => _options;

    private static string DefaultExePath() =>
        Path.Combine(AppContext.BaseDirectory, "engine", "aria2-next.exe");

    /// <summary>组装引擎启动参数（公开给测试断言）。</summary>
    public static string BuildArguments(Aria2Options options) =>
        $"--enable-rpc --rpc-listen-all=false --rpc-listen-port={options.Port} --rpc-secret={options.Secret}" +
        $" --dir=\"{options.DownloadDir}\" --continue=true" +
        $" --max-connection-per-server={options.MaxConnectionsPerServer} --split={options.Split}" +
        $" --min-split-size=1M --file-allocation=none" +
        $" --log=\"{options.LogPath}\" --log-level=info" +
        $" --connect-timeout=15 --timeout=60 --max-tries=5 --retry-wait=2" +
        // 浏览器标准 UA：个别 CDN 对 aria2 默认 UA 有差别对待（限流/拒绝），统一伪装规避
        $" --user-agent=\"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36\"" +
        // max-tries=5：连接持续失败约 90 秒内报错到任务上，不再无限静默重试（真机「正在连接服务器」卡死的放大器）
        (options.OverallSpeedLimitKB > 0
            ? $" --max-overall-download-limit={options.OverallSpeedLimitKB}K "
            : " ") +
        $" --enable-dht=true --dht-entry-point=router.bittorrent.com:6881" +
        $" --bt-save-metadata=true --seed-time={options.SeedMinutes} --follow-torrent=mem";

    /// <summary>检查本机端口是否空闲（公开给测试）。</summary>
    public static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>启动引擎并等待 RPC 就绪；端口被占用自动顺延，最多重试 3 次启动。</summary>
    public async Task<Aria2Client> StartAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_exePath))
        {
            throw new FileNotFoundException(
                $"未找到下载引擎文件 {Path.GetFileName(_exePath)}，请检查安装完整性。", _exePath);
        }

        Directory.CreateDirectory(_options.DownloadDir);

        InvalidOperationException? lastError = null;
        for (var attempt = 0; attempt < MaxStartAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            PickFreePort();
            try
            {
                return await StartProcessAsync(ct);
            }
            catch (InvalidOperationException ex) when (attempt < MaxStartAttempts - 1)
            {
                lastError = ex;
                await Task.Delay(300, ct);
            }
        }

        throw lastError ?? new InvalidOperationException("aria2 引擎启动失败。");
    }

    /// <summary>
    /// 崩溃自动重启：引擎进程已退出时重新拉起并返回新客户端；运行中则原样返回。
    /// 返回值带 Restarted 标记：true=引擎确实重启过（任务映射需要重置），false=引擎活着（调用方须丢弃临时客户端）。
    /// </summary>
    public async Task<(Aria2Client Client, bool Restarted)> EnsureRunningAsync(CancellationToken ct = default)
    {
        var alive = false;
        lock (_gate)
        {
            alive = _process is { HasExited: false };
        }

        if (alive)
        {
            return (new Aria2Client(_options), false);
        }

        var client = await StartAsync(ct);
        Restarted?.Invoke(this, client);
        return (client, true);
    }

    private void PickFreePort()
    {
        lock (_gate)
        {
            for (var i = 0; i < MaxPortAttempts; i++)
            {
                if (IsPortFree(_options.Port))
                {
                    return;
                }

                _options.Port++;
            }
        }
    }

    private async Task<Aria2Client> StartProcessAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = BuildArguments(_options),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        lock (_gate)
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("aria2 引擎启动失败。");
        }

        var client = new Aria2Client(_options);
        // 轮询等待 RPC 就绪，最多 10 秒；等待失败（超时/进程退出）必须把半起的引擎杀干净，
        // 否则自愈重试会在系统里堆积孤儿 aria2 进程与被占端口（v2.54 卡死专项）
        try
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Process? current;
                lock (_gate)
                {
                    current = _process;
                }

                if (current is null || current.HasExited)
                {
                    throw new InvalidOperationException($"aria2 引擎异常退出，退出码 {current?.ExitCode}。");
                }

                try
                {
                    await client.GetVersionAsync(ct);
                    return client;
                }
                catch (Exception) when (attempt < 39)
                {
                    await Task.Delay(250, ct);
                }
            }

            throw new TimeoutException("aria2 引擎 RPC 就绪等待超时。");
        }
        catch
        {
            KillStartedProcess();
            throw;
        }
    }

    /// <summary>杀掉本次拉起但未就绪的引擎进程（幂等）；自愈重试不留孤儿进程。</summary>
    private void KillStartedProcess()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false } process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 进程已自行退出
                }
            }

            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已自行退出，忽略
            }
        }

        _process?.Dispose();
    }
}
