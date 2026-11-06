using System.Collections.Generic;
using System.Threading.Tasks;
using TaoDown.Core;
using TaoDown.Engine;
using Xunit;
using Xunit.Abstractions;

namespace TaoDown.Tests.Integration;

/// <summary>续传被拒的自动应对（2026-11-02 用户定调：要结果与反馈，不只给报错）。</summary>
public class AutoRestartTests
{
    private readonly ITestOutputHelper _out;
    public AutoRestartTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ResumeRejected_AutoRestartsFresh_Once()
    {
        using var stub = new StubAria2Server();
        await stub.StartAsync();
        var client = new Aria2Client(StubAria2Server.OptionsFor(stub));
        using var service = new Aria2DownloadService(client);
        var task = StubAria2Server.NewTask();
        await service.AddAsync(task);
        Assert.Equal("g1", task.EngineId);

        // 引擎回报：续传被拒（同名文件重下真实场景）
        stub.TellStatusPayload = _ =>
            "{\"status\":\"error\",\"errorMessage\":\"...cannot be continued: the server ignores ranges\",\"totalLength\":\"100\",\"completedLength\":\"0\",\"downloadSpeed\":\"0\",\"connections\":\"0\"}";
        await service.RefreshAsync();
        await Task.Delay(300); // 自动重下是即发即忘，留一拍

        // 应自动重新提交一次，且选项明确"从头+覆盖"
        Assert.Equal(2, stub.AddUriCalls);
        var opts = stub.AddedOptions[1]; // 第二次提交（自动重下）的选项
        Assert.Equal("false", opts["continue"]);
        Assert.Equal("true", opts["allow-overwrite"]);
        Assert.Equal("g2", task.EngineId);
        Assert.Equal(DownloadStatus.Active, task.Status);
        Assert.Null(task.Error);

        // 第二轮仍报同错：不再重复重启，回落为错误 + 人话指引
        await service.RefreshAsync();
        await Task.Delay(300);
        Assert.Equal(2, stub.AddUriCalls);
        Assert.Equal(DownloadStatus.Error, task.Status);
        Assert.Contains("断点续传", task.Error);
        _out.WriteLine("自动重下一次后回落错误，符合预期");
    }
}
