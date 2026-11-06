using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>界面主题设置：水墨为系列默认（0），存取保真。</summary>
public class ThemeSettingsTests
{
    [Fact]
    public void ThemeId_DefaultsToInk()
    {
        Assert.Equal(0, new DownloadSettings().ThemeId);
    }

    [Fact]
    public void ThemeId_RoundTripsThroughStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"taodown-theme-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new DownloadSettings { ThemeId = 2 });
            Assert.Equal(2, store.Load().ThemeId);

            store.Save(new DownloadSettings { ThemeId = 0 });
            Assert.Equal(0, store.Load().ThemeId);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
