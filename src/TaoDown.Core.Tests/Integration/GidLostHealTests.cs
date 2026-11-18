using System;
using System.Threading.Tasks;
using TaoDown.Core;
using TaoDown.Engine;
using Xunit;

namespace TaoDown.Tests.Integration;

/// <summary>
/// 修红 run 44230657598 两条实锤的钉子用例：
/// 1) 续传被拒自愈重下必须带 out 显式名（M18 中文名 82 字截断）；
/// 2) 引擎侧 gid 失联时任务必须转等待重提（B 段 file12 静默挂死 28 分钟）。
/// </summary>
public class GidLostHealTests
{
    [Fact]
    public async Task ResumeRejected_RestartFresh_CarriesOutName()
    {
        using var stub = new StubAria2Server();
        await stub.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(stub));
        using var service = new Aria2DownloadService(client);
        var task = StubAria2Server.NewTask();
        await service.AddAsync(task);
        Assert.Equal("g1", task.EngineId);

        // 已持久化 OutName（AddAsync 探测成功即存；此处直接注入模拟早前成功）
        const string longName = "中文超长文件名超长文件名超长文件名超长文件名超长文件名超长文件名超长文件名超长文件名.bin";
        task.OutName = longName;

        stub.TellStatusPayload = _ =>
            "{\"status\":\"error\",\"errorMessage\":\"...cannot be continued: the server ignores ranges\",\"totalLength\":\"100\",\"completedLength\":\"0\",\"downloadSpeed\":\"0\",\"connections\":\"0\"}";
        await service.RefreshAsync();
        await StubAria2Server.WaitForAsync(() => stub.AddUriCalls >= 2, TimeSpan.FromSeconds(5));

        // 重下必须复用显式名，绕开引擎 Content-Disposition 截断
        Assert.Equal(2, stub.AddUriCalls);
        Assert.True(stub.AddedOptions[1].TryGetValue("out", out var outName), "重下选项缺 out 显式名");
        Assert.Equal(longName, outName);
        Assert.Equal("false", stub.AddedOptions[1]["continue"]);
        Assert.Equal("true", stub.AddedOptions[1]["allow-overwrite"]);
        Assert.Equal("g2", task.EngineId);
    }

    [Fact]
    public async Task GidLostOnEngine_TaskResubmitsInsteadOfHanging()
    {
        using var stub = new StubAria2Server();
        await stub.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(stub));
        using var service = new Aria2DownloadService(client);
        var task = StubAria2Server.NewTask();
        await service.AddAsync(task);
        Assert.Equal("g1", task.EngineId);
        Assert.Equal(DownloadStatus.Active, task.Status);

        // 引擎侧已不认这个 gid（自愈换引擎/换号竞态的映射失联）
        stub.TellStatusError = "GID g1 is not found";
        await service.RefreshAsync();

        // 必须转等待态并请求重提，绝不静默挂在 Active
        Assert.Equal(DownloadStatus.Waiting, task.Status);
        Assert.Null(task.EngineId);

        // 重提通道走通：App 侧同一通道补提交，引擎给新号
        await service.AddAsync(task);
        Assert.Equal("g2", task.EngineId);
        Assert.Equal(DownloadStatus.Active, task.Status);
    }
}
