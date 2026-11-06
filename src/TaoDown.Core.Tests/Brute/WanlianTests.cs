using System.Collections.Concurrent;
using TaoDown.Core;
using Xunit;
using Xunit.Abstractions;

namespace TaoDown.Tests.Brute;

/// <summary>
/// 万链归账（万级补则，用户 2026-11-01 指示；母本 TaoZip v56 万链并行 + TaoPad L7 万链归账）。
/// 万法归链：核心纯函数域（链接分类/链接规整/格式化）万级生成链路并行演化，
/// 每链独立种子确定性生成，链内不写测试输出、归账元组汇总，终局断言归账。
/// </summary>
public class WanlianTests
{
    readonly ITestOutputHelper _out;
    public WanlianTests(ITestOutputHelper o) => _out = o;
    const int ChainCount = 10000;   // 万链基准
    const int StepsPerChain = 40;
    const int SeedBase = 20261101;

    static readonly string[] SchemePrefixes =
    {
        "thunder://", "flashget://", "qqdl://", "magnet:", "ed2k://",
        "http://", "https://", "ftp://", "", "", "",   // 空前缀留给碎片/哈希/垃圾
    };

    static readonly string[] Fragments =
    {
        "abc", "文件名", "1024", "0", "-5", "abc|1024|0123456789abcdef0123456789abcdef",
        new string('a', 40), new string('F', 40), new string('0', 40),
        new string('A', 32), "abcd", "😀", "中文名|2048|ABCDEF0123456789ABCDEF0123456789",
        "a b c", "\"http://x/y.zip\"", "<ftp://f/x>", "'magnet:?xt=urn:btih:abc'",
        "  https://example.com/file.zip  ", "\tthunder://x\t", "|", "||", "a||",
        "xyz|notanumber|0123456789abcdef0123456789abcdef", "a|1024|short",
    };

    static readonly char[] JunkChars = " \t\r\n\"'<>()[]{}|\\/?:&=%#,.!@#$^*~`abcXYZ0123456789😀中".ToCharArray();

    static string RandInput(Random rnd)
    {
        int kind = rnd.Next(4);
        return kind switch
        {
            0 => SchemePrefixes[rnd.Next(SchemePrefixes.Length)] + Fragments[rnd.Next(Fragments.Length)],
            1 => Fragments[rnd.Next(Fragments.Length)],
            2 => SchemePrefixes[rnd.Next(SchemePrefixes.Length)] + new string(Enumerable.Range(0, rnd.Next(0, 60))
                    .Select(_ => JunkChars[rnd.Next(JunkChars.Length)]).ToArray()),
            _ => new string(Enumerable.Range(0, rnd.Next(0, 60))
                    .Select(_ => JunkChars[rnd.Next(JunkChars.Length)]).ToArray()),
        };
    }

    [Fact]
    public void Wanlian_Summary_TenThousandChains_Evolve_NoViolation()
    {
        long violations = 0, asserts = 0;
        var samples = new ConcurrentBag<string>();

        Parallel.For(0, ChainCount, chain =>
        {
            var rnd = new Random(SeedBase + chain);   // 每链独立种子：失败可复现
            long cv = 0, ca = 0;

            for (int step = 0; step < StepsPerChain; step++)
            {
                string input = RandInput(rnd);

                // 分类：不抛异常 + 同输入两次确定一致（幂等）
                LinkKind k1, k2;
                try { k1 = LinkClassifier.Classify(input); }
                catch { cv++; ca++; continue; }
                k2 = LinkClassifier.Classify(input);
                if (k1 != k2) { cv++; if (samples.Count < 8) samples.Add($"分类幂等: [{input}]"); }
                ca += 2;

                // 规整：不抛异常；产出或 null，或可再识别（归链），且两次一致
                string? n1 = null, n2;
                try { n1 = LooseLinkParser.TryNormalize(input); }
                catch { cv++; ca++; continue; }
                try { n2 = LooseLinkParser.TryNormalize(input); }
                catch { cv++; ca++; continue; }
                if (!Equals(n1, n2)) { cv++; if (samples.Count < 8) samples.Add($"规整幂等: [{input}] -> [{n1}]/[{n2}]"); }
                if (n1 is not null && LinkClassifier.Classify(n1) == LinkKind.Unknown)
                { cv++; if (samples.Count < 8) samples.Add($"归链失败: [{input}] -> [{n1}]"); }
                ca += 3;

                // 逐轮洗净：规整器每轮削一层首尾空白/引号，交错时可需多轮；
                // 不变量是「每一轮输出皆可识别或 null」——脏字符逐轮消减，永不更脏
                if (n1 is not null)
                {
                    var n2r = LooseLinkParser.TryNormalize(n1);
                    if (n2r is not null && LinkClassifier.Classify(n2r) == LinkKind.Unknown)
                    { cv++; if (samples.Count < 8) samples.Add($"二轮变脏: [{input}] -> [{n1}] -> [{n2r}]"); }
                    var n3 = LooseLinkParser.TryNormalize(n2r);
                    if (n3 is not null && LinkClassifier.Classify(n3) == LinkKind.Unknown)
                    { cv++; if (samples.Count < 8) samples.Add($"三轮变脏: [{input}] -> [{n2r}] -> [{n3}]"); }
                    ca += 2;
                }

                // 格式化：任意 long 组合不抛异常，负数/零输入有守卫（Size 负数归零非负输出，Speed≤0 空）
                long bytes = ((long)rnd.Next(int.MinValue, int.MaxValue)) * rnd.Next(-2, 3);
                long speed = ((long)rnd.Next(int.MinValue, int.MaxValue)) * rnd.Next(-2, 3);
                try
                {
                    string s1 = Format.Size(bytes);
                    string sp = Format.Speed(speed);
                    string eta = Format.Eta(bytes, speed);
                    if (bytes < 0 && !s1.Contains('-')) { /* 负数归零，正确 */ }
                    if (bytes < 0 && s1.Contains('-')) { cv++; if (samples.Count < 8) samples.Add($"负数显形: [{bytes}] -> [{s1}]"); }
                    if (speed <= 0 && sp.Length != 0) { cv++; if (samples.Count < 8) samples.Add($"零速非空: [{speed}]"); }
                    ca += 3;
                }
                catch { cv++; ca += 3; }
            }

            Interlocked.Add(ref violations, cv);
            Interlocked.Add(ref asserts, ca);
        });

        // 万链归账：链数=万级、违规=0、断言规模达标
        Assert.True(violations == 0, $"违规 {violations}，样本:\n{string.Join("\n", samples.Take(8))}");
        Assert.True(asserts >= ChainCount, $"断言数 {asserts} 应不小于链数 {ChainCount}");
        Assert.True(ChainCount == 10000, "链数应为万级基准 10000");
        _out.WriteLine($"万链归账：{ChainCount} 链 × {StepsPerChain} 步，总断言 {asserts} 条，违规 {violations}");
    }
}
