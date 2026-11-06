using TaoDown.Core;
using TaoDown.Engine;
using TaoDown.Tests.Integration;
using Xunit;

namespace TaoDown.Tests.System;

/// <summary>系统测试：端到端链路（链接→补全→分类→入队→引擎→状态回流）与故障恢复。</summary>
public class SystemTests : IDisposable
{
    private readonly StubAria2Server _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task EndToEnd_FragmentLink_CompletesWithFilePath()
    {
        await _server.StartAsync();
        _server.TellStatusPayload = _ =>
            "{\"status\":\"complete\",\"totalLength\":\"1632086016\",\"completedLength\":\"1632086016\",\"downloadSpeed\":\"0\",\"files\":[{\"path\":\"C:\\\\Users\\\\me\\\\Downloads\\\\TaoDown\\\\019_enterprise_x64_dvd_2bfe815a.iso\"}]}";

        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);
        using var queue = new DownloadQueue(service) { MaxConcurrent = 3 };

        // 用户场景：粘贴 eMule 碎片（用户真实反馈的输入）
        const string fragment = "019_enterprise_x64_dvd_2bfe815a.iso|1632086016|58C258FF0F1D006DD3C1F5F17AF3EAFD";
        var normalized = LooseLinkParser.TryNormalize(fragment);
        Assert.NotNull(normalized);
        var kind = LinkClassifier.Classify(normalized!);
        Assert.Equal(LinkKind.Ed2k, kind);

        var task = new DownloadTask { Url = normalized!, Kind = kind, SaveDir = @"C:\Users\me\Downloads\TaoDown" };
        await queue.EnqueueAsync(task);
        Assert.Contains(normalized!, _server.AddedUris); // 引擎收到完整 ed2k 链接

        await service.RefreshAsync();
        var list = await service.ListAsync();
        Assert.Equal(DownloadStatus.Complete, list[0].Status);
        Assert.Equal("019_enterprise_x64_dvd_2bfe815a.iso", list[0].FileName);
        Assert.Equal(1632086016, list[0].TotalBytes);
    }

    [Fact]
    public async Task EndToEnd_BareHash_BecomesMagnet()
    {
        await _server.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);
        using var queue = new DownloadQueue(service) { MaxConcurrent = 3 };

        var normalized = LooseLinkParser.TryNormalize("0123456789ABCDEF0123456789ABCDEF01234567");
        Assert.NotNull(normalized);
        var task = new DownloadTask { Url = normalized!, Kind = LinkClassifier.Classify(normalized!) };
        await queue.EnqueueAsync(task);

        Assert.Contains("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", _server.AddedUris);
    }

    [Fact]
    public async Task EngineFailure_RetainsState_ThenRecovers()
    {
        await _server.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);
        var task = await service.AddAsync(StubAria2Server.NewTask());
        await service.RefreshAsync();
        var listBefore = await service.ListAsync();
        var bytesBefore = listBefore[0].CompletedBytes;

        // 引擎故障：全部请求回错——刷新不得抛异常，状态保留
        _server.FailAll = true;
        var ex = await Record.ExceptionAsync(() => service.RefreshAsync());
        Assert.Null(ex);
        var listDuring = await service.ListAsync();
        Assert.Equal(DownloadStatus.Active, listDuring[0].Status);
        Assert.Equal(bytesBefore, listDuring[0].CompletedBytes);

        // 更狠的故障：服务端直接 500（模拟进程挂掉）——同样不得抛异常
        _server.FailAll = false;
        _server.ThrowAll = true;
        ex = await Record.ExceptionAsync(() => service.RefreshAsync());
        Assert.Null(ex);

        // 引擎恢复：刷新后状态继续更新
        _server.ThrowAll = false;
        await service.RefreshAsync();
        var listAfter = await service.ListAsync();
        Assert.Equal(DownloadStatus.Active, listAfter[0].Status);
        Assert.Equal(10, listAfter[0].CompletedBytes);
    }

    [Fact]
    public async Task EngineFailure_RemoveStillFails_Gracefully()
    {
        await _server.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);
        var task = await service.AddAsync(StubAria2Server.NewTask());

        _server.ThrowAll = true;
        // 引擎没响应时任务也必须从列表消失（真机反馈“删除没反应”的根治契约）
        await service.RemoveAsync(task);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task FullBatchFlow_PauseAllResumeAll()
    {
        await _server.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(_server));
        using var service = new Aria2DownloadService(client);
        using var queue = new DownloadQueue(service) { MaxConcurrent = 5 };

        await queue.EnqueueAsync(StubAria2Server.NewTask("http://a.com/1.zip"));
        await queue.EnqueueAsync(StubAria2Server.NewTask("http://b.com/2.zip"));
        Assert.Equal(2, _server.AddedUris.Count);

        var paused = await service.PauseAllAsync();
        Assert.Equal(2, paused);

        _server.TellStatusPayload = _ =>
            "{\"status\":\"paused\",\"totalLength\":\"100\",\"completedLength\":\"10\",\"downloadSpeed\":\"0\",\"connections\":\"0\"}";
        await service.RefreshAsync(); // 状态回流为 paused

        var resumed = await service.ResumeAllAsync();
        Assert.Equal(2, resumed);
    }
}
