using TaoDown.Core;
using TaoDown.Engine;
using Xunit;

namespace TaoDown.Tests.Integration;

/// <summary>集成测试：客户端↔服务↔队列跨层协作（真实 HTTP 通道 + 进程内假引擎）。</summary>
public class IntegrationTests : IDisposable
{
    private readonly StubAria2Server _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Service_OverRealHttp_RefreshesStatus()
    {
        await _server.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);

        DownloadTask? updated = null;
        service.Updated += (_, t) => updated = t;

        await service.AddAsync(StubAria2Server.NewTask());
        await service.RefreshAsync();

        Assert.Equal(1, _server.AddUriCalls);
        Assert.Equal(1, _server.TellStatusCalls);
        Assert.NotNull(updated);
        Assert.Equal(DownloadStatus.Active, updated!.Status);
        Assert.Equal(10, updated.CompletedBytes);
        Assert.Equal(100, updated.TotalBytes);
        Assert.Equal("f.bin", updated.FileName);
    }

    [Fact]
    public async Task Queue_PlusService_CompletesFirstThenStartsNext()
    {
        await _server.StartAsync();
        _server.TellStatusPayload = gid => gid == "g1"
            ? "{\"status\":\"complete\",\"totalLength\":\"100\",\"completedLength\":\"100\",\"downloadSpeed\":\"0\",\"files\":[{\"path\":\"C:\\\\d\\\\f1.bin\"}]}"
            : null; // 其余任务默认活动状态

        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);
        using var queue = new DownloadQueue(service) { MaxConcurrent = 1 };

        var first = StubAria2Server.NewTask("http://a.com/1.zip");
        var second = StubAria2Server.NewTask("http://b.com/2.zip");
        await queue.EnqueueAsync(first);
        await queue.EnqueueAsync(second);

        Assert.Equal(["http://a.com/1.zip"], _server.AddedUris); // 并发=1，第二个排队

        await service.RefreshAsync(); // g1 → complete，名额释放
        await StubAria2Server.WaitForAsync(() => _server.AddedUris.Count >= 2, TimeSpan.FromSeconds(5));

        Assert.Contains("http://b.com/2.zip", _server.AddedUris); // 第二个自动开始
    }

    [Fact]
    public async Task PauseAndResume_OverRealHttp()
    {
        await _server.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);
        var task = await service.AddAsync(StubAria2Server.NewTask());

        await service.PauseAsync(task);
        await service.ResumeAsync(task);

        Assert.Equal(1, _server.PauseCalls);
    }
}
