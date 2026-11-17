using System;
using System.Collections.Generic;
using System.Linq;

namespace TaoDown.Engine;

/// <summary>
/// 引擎报错的人话翻译（v2.54 真机反馈）：任务行展示的是用户读得懂的指引，
/// 而不是 aria2 原文；原始报错仍完整保留在 engine-main.log 供排查。
/// </summary>
public static class EngineErrorText
{
    /// <summary>把常见引擎报错翻译成用户可执行的中文指引；未识别的原样返回。</summary>
    public static string Friendly(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        foreach (var (keyword, hint) in Known)
        {
            if (raw.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return hint;
            }
        }

        return raw;
    }

    /// <summary>服务器拒绝续传（同名文件重下/不支持 Range）：可自动从头重下，属"可自愈"类。</summary>
    public static bool ResumeRejected(string raw) =>
        !string.IsNullOrWhiteSpace(raw) &&
        (raw.Contains("cannot be continued", StringComparison.OrdinalIgnoreCase)
         || raw.Contains("ignores ranges", StringComparison.OrdinalIgnoreCase));

    /// <summary>暂时性网络错误（连接拒绝/超时/停滞/解析失败等）：断源窗口与网络抖动内
    /// 不得终态化。云批 44022149029 实锤：崩溃恢复后本地源约 4 分钟未起，
    /// 连接拒绝把重试烧光、任务被判终态失败；源恢复后对账只重泵等待队列，
    /// 7 个任务此后 26 分钟零动静。暂时性失败必须退避重试到源恢复。</summary>
    public static bool Transient(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        foreach (var keyword in TransientKeywords)
        {
            if (raw.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>暂时性网络错误关键字；磁盘满、文件冲突、续传被拒等资源类错误不在其列。</summary>
    public static readonly string[] TransientKeywords =
    [
        "failed to connect",
        "could not connect",
        "connection refused",
        "connection timed out",
        "timeout",
        "timed out",
        "connection stalled",
        "temporary failure",
        "temporarily",
        "name resolution",
        "name or service not known",
        "network is unreachable",
        "network is down",
    ];
    /// <summary>关键字 → 人话。匹配按序首个命中。</summary>
    public static readonly (string Keyword, string Hint)[] Known =
    [
        ("ignores ranges", "该服务器不支持断点续传；请删除旧的下载文件后重新添加下载。"),
        ("already exists", "同名文件已存在；删除已下载的同名文件或换个文件名后重试。"),
        ("cannot be paused now", "任务正在连接服务器，这几秒暂停不了；稍等一两秒再试。"),
        ("not found", "引擎已重启或任务已结束；请重新添加下载。"),
        ("timeout", "连接服务器超时；请检查网络后重试。"),
        ("Connection refused", "连接被拒绝；请确认网络后重试。"),
        ("File allocation failed", "磁盘空间不足或写入失败；请清理磁盘后重试。"),
        ("No space left", "磁盘空间不足；请清理下载盘后重试。"),
    ];
}
