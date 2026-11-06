using System.Text.Json;
using TaoDown.Core;

namespace TaoDown.Host;

/// <summary>
/// TaoDown 浏览器插件 Native Messaging 宿主（批次五 + v2.12 blob/data 支持）。
/// 由 Chrome/Edge 拉起，stdin/stdout 按 Native Messaging 帧协议收发；
/// 两种请求：①download —— 下载链接转交主程序；②download-data-* —— 扩展把
/// blob/data 内容分块（base64）送来，宿主落盘成文件后写收件箱，主程序弹保存框。
/// </summary>
internal static class Program
{
    /// <summary>宿主与扩展的约定名称（与注册表、host manifest 一致）。</summary>
    public const string HostName = "com.tao.taodown";

    private static readonly Dictionary<string, BlobSession> Sessions = new();
    private static readonly object Gate = new();

    private static string InboxDir => DataRoot.Sub("inbox");

    private static string TempDir => DataRoot.Sub("blobtmp");

    private static int Main()
    {
        // v2.55 连接自证：宿主被拉起即记一行（v2.55 真机排障——host.log 无新行 = 浏览器没走到宿主这步）
        HostLog.Write($"宿主被拉起：args={string.Join(" ", Environment.GetCommandLineArgs()[1..])}");
        // 浏览器只给宿主传一个来源参数，与协议无关
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        while (true)
        {
            byte[]? payload;
            try
            {
                payload = NativeMessageFrame.TryRead(stdin);
            }
            catch (IOException)
            {
                // 帧损坏或超长：回错误并继续，不断开（保持与浏览器会话）
                Reply(stdout, ok: false, "消息帧损坏");
                continue;
            }
            catch (Exception)
            {
                // v2.54：流/内存等其他异常不崩宿主——宿主一死，插件端所有请求都会变成「没反应」。
                // 退出后浏览器会按需重新拉起宿主，比带病运行更干净
                return 1;
            }

            if (payload is null)
            {
                HostLog.Write("浏览器断开，宿主退出");
                return 0; // 浏览器断开
            }

            var json = System.Text.Encoding.UTF8.GetString(payload);
            if (json.Contains("download", StringComparison.OrdinalIgnoreCase))
            {
                HostLog.Write($"收到插件请求：{json[..Math.Min(200, json.Length)]}");
            }

            Handle(stdout, json);
        }
    }

    private static void Handle(Stream stdout, string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            Reply(stdout, ok: false, "JSON 解析失败");
            return;
        }

        var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "download";
        try
        {
            switch (type)
            {
                case "precheck":
                    // v2.50：插件在取消浏览器下载前先问一句「接管还开着吗」。
                    // 主程序把设置页的开关落到 takeover_state.json，这里读它：
                    // 关了→回 takeover:false，插件放行浏览器自下，绝不取消；
                    // 读不到（首次安装、主程序还没跑过）→默认接管，与「装完即接管」一致。
                    ReplyTakeover(stdout, ReadTakeoverEnabled());
                    break;
                case "ping":
                    // 插件弹窗的连通性探测：宿主独立于主程序工作（只写收件箱），
                    // 收到即回 ok，弹窗据此显示“已连接”。此前 ping 落入 default
                    // 被当成下载请求解析失败回 ok:false，弹窗恒显“响应异常”。
                    // v2.52：应答带上接管开关状态，弹窗把「已关闭」直接告诉用户，不用猜。
                    ReplyPing(stdout, ReadTakeoverEnabled());
                    break;
                case "download-data-start":
                    HandleDataStart(stdout, doc.RootElement);
                    break;
                case "download-data-chunk":
                    HandleDataChunk(stdout, doc.RootElement);
                    break;
                case "download-data-end":
                    HandleDataEnd(stdout, doc.RootElement);
                    break;
                case "download-data-abort":
                    HandleDataAbort(stdout, doc.RootElement);
                    break;
                default:
                    if (BrowserRequest.TryParse(json, out var request, out var error) && request is not null)
                    {
                        var path = SaveToInbox(request);
                        Reply(stdout, ok: true, Path.GetFileName(path));
                    }
                    else
                    {
                        Reply(stdout, ok: false, error ?? "请求不完整");
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Reply(stdout, ok: false, $"处理失败: {ex.Message}");
        }
    }

    private static void HandleDataStart(Stream stdout, JsonElement root)
    {
        var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
        var filename = root.TryGetProperty("filename", out var f) ? f.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            Reply(stdout, ok: false, "缺少会话 id");
            return;
        }

        lock (Gate)
        {
            if (Sessions.ContainsKey(id))
            {
                Reply(stdout, ok: false, "会话 id 重复");
                return;
            }

            Sessions[id] = new BlobSession(id, Path.Combine(TempDir, $"{id}.part"));
        }

        Reply(stdout, ok: true, BlobPayload.SanitizeFileName(filename));
    }

    private static void HandleDataChunk(Stream stdout, JsonElement root)
    {
        var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
        var index = root.TryGetProperty("index", out var x) ? x.GetInt32() : -1;
        var b64 = root.TryGetProperty("data", out var d) ? d.GetString() : null;
        if (string.IsNullOrEmpty(id) || !BlobPayload.TryDecode(b64 ?? string.Empty, out var bytes))
        {
            AbortLocked(id);
            Reply(stdout, ok: false, "块数据无效");
            return;
        }

        BlobSession? session;
        lock (Gate)
        {
            Sessions.TryGetValue(id, out session);
        }

        if (session is null)
        {
            Reply(stdout, ok: false, "会话不存在（可能已失败）");
            return;
        }

        if (!session.AppendChunk(index, bytes))
        {
            lock (Gate)
            {
                Sessions.Remove(id);
            }

            session.Abort();
            Reply(stdout, ok: false, "文件过大或会话已中止");
            return;
        }

        Reply(stdout, ok: true, $"{session.TotalBytes}");
    }

    private static void HandleDataEnd(Stream stdout, JsonElement root)
    {
        var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
        var filename = root.TryGetProperty("filename", out var f) ? f.GetString() : null;
        BlobSession? session;
        lock (Gate)
        {
            if (string.IsNullOrEmpty(id) || !Sessions.Remove(id, out session))
            {
                Reply(stdout, ok: false, "会话不存在");
                return;
            }
        }

        var safeName = BlobPayload.SanitizeFileName(filename);
        var dataDir = Path.Combine(InboxDir, "files");
        Directory.CreateDirectory(dataDir);
        var finalPath = Path.Combine(dataDir, $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{safeName}");
        if (!session.Complete(finalPath))
        {
            Reply(stdout, ok: false, "没有收到任何数据");
            return;
        }

        var envelope = JsonSerializer.Serialize(new
        {
            Url = $"blob:transfer:{safeName}",
            Referrer = string.Empty,
            Filename = safeName,
            ReceivedAt = DateTime.Now,
            DataFile = finalPath,
        });
        var inboxPath = Path.Combine(InboxDir, $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        File.WriteAllText(inboxPath, envelope);
        Reply(stdout, ok: true, safeName);
    }

    private static void HandleDataAbort(Stream stdout, JsonElement root)
    {
        var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
        AbortLocked(id);
        Reply(stdout, ok: true, "aborted");
    }

    private static void AbortLocked(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        BlobSession? session;
        lock (Gate)
        {
            if (!Sessions.Remove(id, out session))
            {
                return;
            }
        }

        session.Abort();
    }

    /// <summary>请求落盘：临时文件 + 原子替换，主程序监视收件箱目录。</summary>
    private static string SaveToInbox(BrowserRequest request)
    {
        Directory.CreateDirectory(InboxDir);
        var name = $"{request.ReceivedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        var final = Path.Combine(InboxDir, name);
        var temp = final + ".tmp";
        var envelope = JsonSerializer.Serialize(new
        {
            request.Url,
            request.Referrer,
            request.Filename,
            request.ReceivedAt,
        });
        File.WriteAllText(temp, envelope);
        File.Move(temp, final, overwrite: true);
        return final;
    }

    private static void Reply(Stream stdout, bool ok, string detail)
    {
        // v2.54：应答写失败（浏览器侧管道已断）不再让宿主崩溃
        try
        {
            NativeMessageFrame.Write(stdout, System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = ok, ["detail"] = detail })));
        }
        catch (Exception)
        {
            // 应答丢了就丢了，下一次请求浏览器会重新发起
        }
    }

    private static void ReplyPing(Stream stdout, bool takeover)
    {
        // v2.54：写失败不再崩宿主（应答结构不变：ok/detail/takeover，弹窗按 takeover 显示连通状态）
        try
        {
            NativeMessageFrame.Write(stdout, System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["detail"] = "pong",
                    ["takeover"] = takeover,
                })));
        }
        catch (Exception)
        {
            // 管道已断：应答丢失
        }
    }

    private static void ReplyTakeover(Stream stdout, bool takeover)
    {
        // v2.54：写失败不再崩宿主；插件按 ok=false 处理即可
        try
        {
            NativeMessageFrame.Write(stdout, System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new Dictionary<string, object> { ["ok"] = true, ["takeover"] = takeover })));
        }
        catch (Exception)
        {
            // 管道已断：应答丢失
        }
    }

    /// <summary>接管开关是否开启；状态文件缺失/损坏默认开启（装完即接管，失败不丢下载）。</summary>
    private static bool ReadTakeoverEnabled()
    {
        try
        {
            var path = Path.Combine(DataRoot.Sub("native_host"), "takeover_state.json");
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("browser_takeover", out var v))
            {
                return v.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception)
        {
            // 读不到按开启处理
        }

        return true;
    }
}

/// <summary>宿主连接日志（v2.55）：落盘 logs/host.log，通道排障唯一真相。</summary>
internal static class HostLog
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var dir = TaoDown.Core.DataRoot.Sub("logs");
            Directory.CreateDirectory(dir);
            lock (Gate)
            {
                File.AppendAllText(
                    System.IO.Path.Combine(dir, "host.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
