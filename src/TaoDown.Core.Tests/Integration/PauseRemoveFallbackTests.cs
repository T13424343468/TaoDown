using TaoDown.Core;
using TaoDown.Engine;
using Xunit;

namespace TaoDown.Tests.Integration;

/// <summary>
/// 暂停/删除双保险（真机 v2.22 反馈：状态不同步时 pause 被拒、已完成任务删除无反应）：
/// 引擎拒绝第一手段时必须自动落到兜底方法，且任务一定从服务中移除。
/// </summary>
public class PauseRemoveFallbackTests
{
    [Fact]
    public async Task Pause_FallsBackToForcePause_WhenEngineRejects()
    {
        using var stub = new StubAria2Server();
        await stub.StartAsync();
        stub.FailMethods.Add("aria2.pause"); // 模拟“GID cannot be paused now”
        var client = new Aria2Client(StubAria2Server.OptionsFor(stub));
        using var service = new Aria2DownloadService(client);
        var id = await service.AddAsync(StubAria2Server.NewTask());

        await service.PauseAsync(id); // 不应抛：自动落到 forcePause

        Assert.True(stub.ForcePauseCalls >= 1);
        var task = Assert.Single(await service.ListAsync());
        Assert.Equal(DownloadStatus.Paused, task.Status);
    }

    [Fact]
    public async Task Remove_FallsBackToRemoveResult_WhenForceRemoveRejected()
    {
        using var stub = new StubAria2Server();
        await stub.StartAsync();
        stub.FailMethods.Add("aria2.forceRemove"); // 模拟已完成任务“cannot be removed”
        var client = new Aria2Client(StubAria2Server.OptionsFor(stub));
        using var service = new Aria2DownloadService(client);
        var id = await service.AddAsync(StubAria2Server.NewTask());

        await service.RemoveAsync(id); // 不应抛：自动落到 removeDownloadResult

        Assert.True(stub.RemoveResultCalls >= 1);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task Remove_StillRemovesLocally_WhenEngineFullyDown()
    {
        using var stub = new StubAria2Server();
        await stub.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(stub));
        using var service = new Aria2DownloadService(client);
        var id = await service.AddAsync(StubAria2Server.NewTask());

        stub.FailAll = true; // 引擎整个拒绝：两条路都失败
        // 关键语义：引擎没响应时任务也必须从列表消失，绝不“点了没反应”
        await service.RemoveAsync(id);
        Assert.Empty(await service.ListAsync());
    }
}
