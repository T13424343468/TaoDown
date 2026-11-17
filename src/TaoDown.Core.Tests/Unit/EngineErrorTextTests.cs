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

    [Theory]
    [InlineData("Failed to connect to 127.0.0.1:18100 after 2028 ms: Could not connect to server", true)]
    [InlineData("Connection stalled", true)]
    [InlineData("Connection refused", true)]
    [InlineData("The download timed out", true)]
    [InlineData("Temporary failure in name resolution", true)]
    [InlineData("No space left on device", false)]
    [InlineData("File allocation failed", false)]
    [InlineData("File already exists", false)]
    [InlineData("cannot be continued: the server ignores ranges", false)]
    public void Transient_Classification_MatchesOutageWindowEvidence(
        string raw, bool expected)
    {
        Assert.Equal(expected, TaoDown.Engine.EngineErrorText.Transient(raw));
    }
}
