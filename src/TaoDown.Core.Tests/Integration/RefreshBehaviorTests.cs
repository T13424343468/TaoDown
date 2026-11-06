using System.Net;
using System.Text;
using System.Text.Json;
using TaoDown.Core;
using TaoDown.Engine;
using Xunit;

namespace TaoDown.Tests.Integration;

/// <summary>RefreshAsync 行为回归：终态不轮询、引擎故障不抛出。</summary>
public class RefreshBehaviorTests
{
    private sealed class FakeRpcHandler : HttpMessageHandler
    {
        public int Calls;

        /// <summary>按方法名返回 result 的 JSON 片段；返回 null 表示回错误；抛异常模拟网络故障。</summary>
        public Func<string, string?>? Responder { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var method = doc.RootElement.GetProperty("method").GetString()!;

            string? result = null;
            var failed = false;
            Exception? thrown = null;
            try
            {
                result = Responder?.Invoke(method);
                failed = result is null;
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            if (thrown is not null)
            {
                throw new HttpRequestException("engine down", thrown);
            }

            // result 为原始 JSON 字面量（字符串或对象），直接拼接避免二次转义
            var payload = failed
                ? "{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"error\":{\"code\":-1,\"message\":\"boom\"}}"
                : "{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"result\":" + result + "}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static (Aria2DownloadService Service, FakeRpcHandler Handler) MakeService()
    {
        var handler = new FakeRpcHandler();
        var client = new Aria2Client(new Aria2Options { Secret = "test" }, new HttpClient(handler));
        return (new Aria2DownloadService(client), handler);
    }

    private const string CompleteStatus =
        "{\"status\":\"complete\",\"totalLength\":\"100\",\"completedLength\":\"100\",\"downloadSpeed\":\"0\",\"files\":[{\"path\":\"C:\\\\d\\\\f.zip\"}]}";

    [Fact]
    public async Task Refresh_SkipsTerminalTasks()
    {
        var (service, handler) = MakeService();
        handler.Responder = m => m switch
        {
            "aria2.addUri" => "\"g1\"",
            "aria2.tellStatus" => CompleteStatus,
            _ => null,
        };

        await service.AddAsync(new DownloadTask { Url = "http://a.com/f.zip", Kind = LinkKind.Http });
        await service.RefreshAsync(); // 本轮应轮询一次，任务转完成

        handler.Calls = 0;
        await service.RefreshAsync();
        Assert.Equal(0, handler.Calls); // 完成后不再轮询
    }

    [Fact]
    public async Task Refresh_EngineDown_DoesNotThrow()
    {
        var (service, handler) = MakeService();
        handler.Responder = m => m switch
        {
            "aria2.addUri" => "\"g1\"",
            _ => null, // tellStatus 回 RPC 错误
        };

        await service.AddAsync(new DownloadTask { Url = "http://a.com/f.zip", Kind = LinkKind.Http });
        handler.Calls = 0;
        var ex = await Record.ExceptionAsync(() => service.RefreshAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Refresh_NetworkFailure_DoesNotThrow()
    {
        var (service, handler) = MakeService();
        handler.Responder = m => m switch
        {
            "aria2.addUri" => "\"g1\"",
            _ => throw new HttpRequestException("engine down"), // 模拟引擎进程挂掉
        };

        await service.AddAsync(new DownloadTask { Url = "http://a.com/f.zip", Kind = LinkKind.Http });
        handler.Calls = 0;
        var ex = await Record.ExceptionAsync(() => service.RefreshAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task BatchAll_ToleratesIndividualFailures()
    {
        var (service, handler) = MakeService();
        handler.Responder = m => m switch
        {
            "aria2.addUri" => "\"g1\"",
            "aria2.pause" => throw new HttpRequestException("engine down"), // 暂停失败逐个容错
            _ => null,
        };

        await service.AddAsync(new DownloadTask { Url = "http://a.com/f.zip", Kind = LinkKind.Http });
        var ex = await Record.ExceptionAsync(() => service.PauseAllAsync());
        Assert.Null(ex);
    }
}
