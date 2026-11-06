using TaoDown.Core;
using Xunit;

namespace TaoDown.Tests.Unit;

/// <summary>数据根：D 盘优先、老数据搬家、路径拼接。</summary>
public class DataRootTests
{
    [Fact]
    public void PreferredRoot_IsDDrive()
    {
        Assert.Equal(@"D:\TaoDownData\TaoDown", DataRoot.PreferredRoot);
    }

    [Fact]
    public void Root_NeverThrows_AndCreatesDirectory()
    {
        var root = DataRoot.Root();
        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void CopyTree_CopiesFilesAndSubdirs()
    {
        var src = Path.Combine(Path.GetTempPath(), $"dr-src-{Guid.NewGuid():N}");
        var dst = Path.Combine(Path.GetTempPath(), $"dr-dst-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "settings.json"), "{}");
            File.WriteAllText(Path.Combine(src, "sub", "data.bin"), "x");
            DataRoot.CopyTree(src, dst);
            Assert.True(File.Exists(Path.Combine(dst, "settings.json")));
            Assert.True(File.Exists(Path.Combine(dst, "sub", "data.bin")));
        }
        finally
        {
            Directory.Delete(src, true);
            if (Directory.Exists(dst))
            {
                Directory.Delete(dst, true);
            }
        }
    }

    [Fact]
    public void Migrate_Skips_WhenTargetAlreadyHasData()
    {
        var src = Path.Combine(Path.GetTempPath(), $"dr-old-{Guid.NewGuid():N}");
        var dst = Path.Combine(Path.GetTempPath(), $"dr-new-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "settings.json"), "old");
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, "settings.json"), "new");
            DataRoot.MigrateLegacy(src, dst);
            Assert.Equal("new", File.ReadAllText(Path.Combine(dst, "settings.json")));
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(dst, true);
        }
    }

    [Fact]
    public void Migrate_Copies_WhenTargetEmpty()
    {
        var src = Path.Combine(Path.GetTempPath(), $"dr-old-{Guid.NewGuid():N}");
        var dst = Path.Combine(Path.GetTempPath(), $"dr-new-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "accounts.json"), "{}");
            DataRoot.MigrateLegacy(src, dst);
            Assert.True(File.Exists(Path.Combine(dst, "accounts.json")));
        }
        finally
        {
            Directory.Delete(src, true);
            if (Directory.Exists(dst))
            {
                Directory.Delete(dst, true);
            }
        }
    }
}
