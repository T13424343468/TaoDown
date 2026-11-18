using System.Text;

using TaoDown.Engine;

using Xunit;

namespace TaoDown.Tests.Unit;

public class ContentDispositionNameTests
{
    [Theory]
    [InlineData("attachment; filename*=UTF-8''%E4%B8%AD%E6%96%87.bin", "中文.bin")]
    [InlineData("attachment; filename=\"report.zip\"", "report.zip")]
    [InlineData("attachment; filename=plain.bin", "plain.bin")]
    [InlineData("ATTACHMENT; FILENAME*=utf-8''%E8%B6%85%E9%95%BF.bin", "超长.bin")]
    public void 常规格式解析(string header, string expected)
    {
        var name = ContentDispositionName.ParseName(header);
        Assert.NotNull(name);
        Assert.Equal(expected, name);
    }

    [Fact]
    public void 长中文名不被截断_156字符完整保留()
    {
        // M18 实锤口径：'中文' + '超长文件名' * 30 + '.bin' = 156 字符
        var name156 = "中文" + string.Concat(Enumerable.Repeat("超长文件名", 30)) + ".bin";
        var encoded = Uri.EscapeDataString(name156);
        var header = $"attachment; filename*=UTF-8''{encoded}";
        var parsed = ContentDispositionName.ParseName(header);
        Assert.NotNull(parsed);
        Assert.Equal(156, parsed.Length);
        Assert.Equal(name156, parsed);
    }

    [Fact]
    public void 超上限名字截到200字符()
    {
        var header = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(new string('a', 500));
        var parsed = ContentDispositionName.ParseName(header);
        Assert.NotNull(parsed);
        Assert.Equal(200, parsed.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("attachment")]
    [InlineData("attachment; filename*=UTF-8''")]
    public void 无效输入返回Null(string? header)
    {
        Assert.Null(ContentDispositionName.ParseName(header));
    }

    [Theory]
    [InlineData("attachment; filename=\"a/b\\\\c.txt\"", "a_b_c.txt")]
    [InlineData("attachment; filename=\"con....\"", "con")]
    [InlineData("attachment; filename=\"a<b>c|d?.bin\"", "a_b_c_d_.bin")]
    public void 非法字符与结尾点号清理(string header, string expected)
    {
        var parsed = ContentDispositionName.ParseName(header);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void 带注释分号不误切引号内内容()
    {
        var parsed = ContentDispositionName.ParseName("attachment; filename=\"we;ird name.bin\"");
        Assert.NotNull(parsed);
        Assert.Equal("we;ird name.bin", parsed);
    }
}

public class ContentDispositionProbeRetryTests
{
    /// <summary>
    /// 修红（run 44128258709 实锤）：首连被对端 RST（云机网络抖动的最小模拟）时，
    /// 探测必须重试一次并从第二次成功响应拿到文件名，不得直接回退引擎默认命名。
    /// </summary>
    [Fact]
    public async Task 首连被重置后重试一次拿到文件名()
    {
        var uri = await StartProbeServerAsync();
        try
        {
            using var cts = new global::System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var name = await ContentDispositionName.ResolveOutAsync(uri, null, cts.Token);

            Assert.NotNull(name);
            Assert.Equal("中文重试.bin", name);
        }
        finally
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly global::System.Net.Sockets.TcpListener _listener = new(global::System.Net.IPAddress.Loopback, 0);
    private string? _uri;

    private async Task<string> StartProbeServerAsync()
    {
        _listener.Start();
        var port = ((global::System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        _uri = $"http://127.0.0.1:{port}/probe.bin";
        _ = AcceptLoopAsync();
        return _uri;
    }

    private async Task AcceptLoopAsync()
    {
        var resets = 0;
        while (!_cts.IsCancellationRequested)
        {
            global::System.Net.Sockets.TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (global::System.Net.Sockets.SocketException) { break; }

            // 用独立 socket 连接处理，循环继续 accept
            _ = HandleAsync(client, resets++);
            if (resets >= 10) { break; }
        }
    }

    private async Task HandleAsync(global::System.Net.Sockets.TcpClient client, int index)
    {
        try
        {
            using (client)
            {
                var stream = client.Client;
                if (index == 0)
                {
                    // 第一次连接：强制 RST（Linger State true + 0 秒），模拟网络瞬断
                    stream.LingerState = new global::System.Net.Sockets.LingerOption(true, 0);
                    return; // Dispose 时发出 RST
                }

                // 第二次连接：返回带文件名的真实响应
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/octet-stream\r\n" +
                    $"Content-Disposition: attachment; filename*=UTF-8''{Uri.EscapeDataString("中文重试.bin")}\r\n" +
                    "Content-Length: 0\r\n" +
                    "\r\n");
                // 丢弃请求头（读到空行），再发响应
                stream.LingerState = new global::System.Net.Sockets.LingerOption(true, 10);
                var buf = new byte[8192];
                while (true)
                {
                    var read = await stream.ReceiveAsync(buf, _cts.Token);
                    if (read <= 0 || Encoding.ASCII.GetString(buf, 0, read).Contains("\r\n\r\n")) { break; }
                }
                await stream.SendAsync(head, _cts.Token);
            }
        }
        catch
        {
            // 测试专用连接，异常直接断开由重试兜底
        }
    }
}
