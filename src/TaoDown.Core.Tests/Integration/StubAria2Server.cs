using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TaoDown.Core;
using TaoDown.Engine;

namespace TaoDown.Tests.Integration;

/// <summary>
/// 进程内 aria2 JSON-RPC 假引擎（真实 HTTP 通道）：
/// 集成与系统测试共用，可按 gid 定制状态、注入故障（拒绝/异常）。
/// </summary>
public sealed class StubAria2Server : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    public int AddUriCalls;
    public int TellStatusCalls;
    public List<Dictionary<string, string>> AddedOptions { get; } = new();
    public int PauseCalls;
    public int ForcePauseCalls;
    public int RemoveCalls;
    public int RemoveResultCalls;

    /// <summary>按方法名注入 JSON-RPC 错误（模拟状态不同步/已完成任务拒绝操作）。</summary>
    public HashSet<string> FailMethods { get; } = new();

    /// <summary>按 gid 返回 tellStatus 的原始 JSON 对象；null 用默认活动状态。</summary>
    public Func<string, string?>? TellStatusPayload { get; set; }

    /// <summary>注入故障：所有方法回 JSON-RPC 错误（客户端抛 InvalidOperationException）。</summary>
    public bool FailAll { get; set; }

    /// <summary>注入故障：服务端直接 500（客户端抛 HttpRequestException，模拟引擎进程挂掉）。</summary>
    public bool ThrowAll { get; set; }

    public List<string> AddedUris { get; } = [];

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public Task StartAsync()
    {
        Port = FreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener!.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var method = doc.RootElement.GetProperty("method").GetString()!;

            string? result = null;
            string? error = null;
            if (ThrowAll)
            {
                throw new IOException("engine down");
            }

            if (FailAll || FailMethods.Contains(method))
            {
                error = FailMethods.Contains(method) ? $"{method} rejected" : "engine failure";
            }
            else
            {
                switch (method)
                {
                    case "aria2.addUri":
                        AddUriCalls++;
                        AddedUris.Add(doc.RootElement.GetProperty("params")[1][0].GetString()!);
                        if (doc.RootElement.GetProperty("params").GetArrayLength() > 2
                            && doc.RootElement.GetProperty("params")[2].ValueKind == JsonValueKind.Object)
                        {
                            var opts = new Dictionary<string, string>();
                            foreach (var kv in doc.RootElement.GetProperty("params")[2].EnumerateObject())
                            {
                                opts[kv.Name] = kv.Value.GetString() ?? "";
                            }

                            AddedOptions.Add(opts);
                        }
                        else
                        {
                            AddedOptions.Add(new Dictionary<string, string>());
                        }

                        result = $"\"g{AddUriCalls}\"";
                        break;
                    case "aria2.tellStatus":
                        TellStatusCalls++;
                        var gid = doc.RootElement.GetProperty("params")[1].GetString()!;
                        result = TellStatusPayload?.Invoke(gid) ??
                            "{\"status\":\"active\",\"totalLength\":\"100\",\"completedLength\":\"10\",\"downloadSpeed\":\"10\",\"connections\":\"3\",\"files\":[{\"path\":\"C:\\\\d\\\\f.bin\"}]}";
                        break;
                    case "aria2.pause":
                        PauseCalls++;
                        result = $"\"{doc.RootElement.GetProperty("params")[1].GetString()}\"";
                        break;
                    case "aria2.forcePause":
                        ForcePauseCalls++;
                        result = $"\"{doc.RootElement.GetProperty("params")[1].GetString()}\"";
                        break;
                    case "aria2.unpause":
                        result = $"\"{doc.RootElement.GetProperty("params")[1].GetString()}\"";
                        break;
                    case "aria2.forceRemove":
                        RemoveCalls++;
                        result = $"\"{doc.RootElement.GetProperty("params")[1].GetString()}\"";
                        break;
                    case "aria2.removeDownloadResult":
                        RemoveResultCalls++;
                        result = $"\"{doc.RootElement.GetProperty("params")[1].GetString()}\"";
                        break;
                    default:
                        error = "unknown method";
                        break;
                }
            }

            var payload = error is null
                ? $"{{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"result\":{result}}}"
                : $"{{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"error\":{{\"code\":-1,\"message\":\"{error}\"}}}}";
            ctx.Response.ContentType = "application/json";
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
        }
        catch (Exception)
        {
            try
            {
                ctx.Response.StatusCode = 500;
            }
            catch (Exception)
            {
                // 连接已断开则忽略
            }
        }
        finally
        {
            try
            {
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // 忽略关闭失败
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
            // 忽略停止失败
        }

        _cts?.Dispose();
    }

    public static Aria2Options OptionsFor(StubAria2Server server) => new() { Secret = "test", Port = server.Port };

    /// <summary>等待条件成立（集成/系统测试用），超时抛错。</summary>
    public static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("条件等待超时");
    }

    /// <summary>常用任务构造。</summary>
    public static DownloadTask NewTask(string url = "http://a.com/f.zip") =>
        new() { Url = url, Kind = LinkClassifier.Classify(url) };
}
