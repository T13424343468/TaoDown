using System.Text;

using TaoDown.Engine;

using Xunit;

namespace TaoDown.Tests.Unit;

public class ContentDispositionNameTests
{
    [Theory]
    [InlineData("attachment; filename*=UTF-8''%E4%B8%AD%E6%96%87.bin", "中文.bin")]
    [InlineData("attachment; filename=\"report.zip\"", "report.zip")]
    [InlineData("attachment; filename=plain.bin", "plain.bin")]
    [InlineData("ATTACHMENT; FILENAME*=utf-8''%E8%B6%85%E9%95%BF.bin", "超长.bin")]
    public void 常规格式解析(string header, string expected)
    {
        var name = ContentDispositionName.ParseName(header);
        Assert.NotNull(name);
        Assert.Equal(expected, name);
    }

    [Fact]
    public void 长中文名不被截断_156字符完整保留()
    {
        // M18 实锤口径：'中文' + '超长文件名' * 30 + '.bin' = 156 字符
        var name156 = "中文" + string.Concat(Enumerable.Repeat("超长文件名", 30)) + ".bin";
        var encoded = Uri.EscapeDataString(name156);
        var header = $"attachment; filename*=UTF-8''{encoded}";
        var parsed = ContentDispositionName.ParseName(header);
        Assert.NotNull(parsed);
        Assert.Equal(156, parsed.Length);
        Assert.Equal(name156, parsed);
    }

    [Fact]
    public void 超上限名字截到200字符()
    {
        var header = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(new string('a', 500));
        var parsed = ContentDispositionName.ParseName(header);
        Assert.NotNull(parsed);
        Assert.Equal(200, parsed.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("attachment")]
    [InlineData("attachment; filename*=UTF-8''")]
    public void 无效输入返回Null(string? header)
    {
        Assert.Null(ContentDispositionName.ParseName(header));
    }

    [Theory]
    [InlineData("attachment; filename=\"a/b\\\\c.txt\"", "a_b_c.txt")]
    [InlineData("attachment; filename=\"con....\"", "con")]
    [InlineData("attachment; filename=\"a<b>c|d?.bin\"", "a_b_c_d_.bin")]
    public void 非法字符与结尾点号清理(string header, string expected)
    {
        var parsed = ContentDispositionName.ParseName(header);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void 带注释分号不误切引号内内容()
    {
        var parsed = ContentDispositionName.ParseName("attachment; filename=\"we;ird name.bin\"");
        Assert.NotNull(parsed);
        Assert.Equal("we;ird name.bin", parsed);
    }
}
