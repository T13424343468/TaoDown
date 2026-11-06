namespace TaoDown.Core;

/// <summary>
/// 自动分类：按扩展名把文件归入类型目录（视频/音频/压缩包/文档/安装包/图片/其他）。
/// </summary>
public static class AutoClassifier
{
    /// <summary>扩展名（小写，含点）→ 分类目录名。</summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "视频", [".mkv"] = "视频", [".avi"] = "视频", [".mov"] = "视频",
        [".wmv"] = "视频", [".flv"] = "视频", [".ts"] = "视频", [".m4v"] = "视频",
        [".webm"] = "视频", [".rmvb"] = "视频",
        [".mp3"] = "音频", [".flac"] = "音频", [".wav"] = "音频", [".aac"] = "音频",
        [".ape"] = "音频", [".ogg"] = "音频", [".m4a"] = "音频", [".wma"] = "音频",
        [".zip"] = "压缩包", [".rar"] = "压缩包", [".7z"] = "压缩包", [".tar"] = "压缩包",
        [".gz"] = "压缩包", [".bz2"] = "压缩包", [".iso"] = "压缩包", [".xz"] = "压缩包",
        [".pdf"] = "文档", [".doc"] = "文档", [".docx"] = "文档", [".xls"] = "文档",
        [".xlsx"] = "文档", [".ppt"] = "文档", [".pptx"] = "文档", [".txt"] = "文档",
        [".md"] = "文档", [".epub"] = "文档", [".mobi"] = "文档", [".caj"] = "文档",
        [".exe"] = "安装包", [".msi"] = "安装包", [".apk"] = "安装包", [".dmg"] = "安装包",
        [".pkg"] = "安装包", [".deb"] = "安装包", [".rpm"] = "安装包", [".appx"] = "安装包",
        [".jpg"] = "图片", [".jpeg"] = "图片", [".png"] = "图片", [".gif"] = "图片",
        [".webp"] = "图片", [".bmp"] = "图片", [".heic"] = "图片", [".svg"] = "图片",
    };

    /// <summary>返回分类目录名；未匹配返回"其他"。</summary>
    public static string Classify(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "其他";
        }

        var ext = Path.GetExtension(fileName.Trim());
        return ext.Length > 0 && Map.TryGetValue(ext, out var category) ? category : "其他";
    }

    /// <summary>按设置解析最终保存目录：开启时落到"根目录/分类"，关闭时落根目录。</summary>
    public static string ResolveSaveDir(string fileName, string rootDir, bool autoClassifyEnabled)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
        {
            throw new ArgumentException("下载根目录不能为空。", nameof(rootDir));
        }

        return autoClassifyEnabled ? Path.Combine(rootDir, Classify(fileName)) : rootDir;
    }
}
