using System.Text.Json;

using TaoDown.Core;

namespace TaoDown.Engine;

/// <summary>
/// 任务列表持久化（会话恢复第一刀，2026-11-13）：
/// 应用退出前把任务快照落盘（数据根 state/tasks.json），重启后由服务层把未终态任务重新提交引擎续传，
/// 终态任务仅回填列表条目。文件损坏按空列表处理，绝不让「读不回快照」堵住启动。
/// </summary>
public sealed class DownloadTaskStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>快照文件所在目录（默认数据根 state 子目录；测试可注入临时目录）。</summary>
    private readonly string _directory;

    public DownloadTaskStore(string? directory = null)
    {
        _directory = directory ?? DataRoot.Sub("state");
    }

    /// <summary>快照文件完整路径。</summary>
    public string FilePath => Path.Combine(_directory, "tasks.json");

    /// <summary>把任务快照原子落盘（先写临时文件再替换，进程被杀不产生半截文件）。</summary>
    public void Save(IEnumerable<DownloadTask> tasks)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(tasks, JsonOpts));
            if (File.Exists(FilePath))
            {
                File.Replace(tmp, FilePath, null);
            }
            else
            {
                File.Move(tmp, FilePath);
            }
        }
        catch (Exception ex)
        {
            RefreshDiag.Write($"任务快照保存失败：{ex.Message}；下次恢复按旧快照或空列表处理");
        }
    }

    /// <summary>读回任务快照；文件缺失或损坏返回空列表（不抛，不堵启动）。</summary>
    public IReadOnlyList<DownloadTask> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            var tasks = JsonSerializer.Deserialize<List<DownloadTask>>(json, JsonOpts);
            return tasks ?? [];
        }
        catch (Exception ex)
        {
            RefreshDiag.Write($"任务快照读取失败：{ex.Message}；按空列表恢复");
            return [];
        }
    }
}
