using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>引擎报错人话翻译：常见 aria2 报错必须翻成用户可执行的指引。</summary>
public class EngineErrorTextTests
{
    [Fact]
    public void RangesError_TranslatesToResumeHint()
    {
        Assert.Contains(
            "不支持断点续传",
            TaoDown.Engine.EngineErrorText.Friendly(
                "D:/Program Files/Downloads/TaoDown/QQPCDownload310222.exe cannot be continued: the server ignores ranges"));
    }

    [Fact]
    public void ExistsError_TranslatesToRenameHint()
    {
        Assert.Contains("已存在", TaoDown.Engine.EngineErrorText.Friendly("File already exists"));
    }

    [Fact]
    public void PauseError_TranslatesToWaitHint()
    {
        Assert.Contains("稍等", TaoDown.Engine.EngineErrorText.Friendly("cannot be paused now"));
    }

    [Fact]
    public void UnknownError_ReturnsRaw()
    {
        Assert.Equal("some other engine failure", TaoDown.Engine.EngineErrorText.Friendly("some other engine failure"));
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TaoDown.Engine.EngineErrorText.Friendly(""));
    }
}
