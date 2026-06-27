namespace AudioText.Infrastructure.Services;

/// <summary>
/// 应用目录服务。
/// Application directory service that creates known output folders.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
public static class AppDirectoryService
{
    /// <summary>
    /// 确保运行目录下的默认输出文件夹存在。
    /// Ensure default output directories exist under the runtime base directory.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    public static AppDirectories EnsureDefaultDirectories(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var directories = new AppDirectories(
            BaseDirectory: baseDirectory,
            DownloadedAudioDirectory: Path.Combine(baseDirectory, "下载音频"),
            TranscriptDirectory: Path.Combine(baseDirectory, "输出文本"),
            MemoryDatabaseDirectory: Path.Combine(baseDirectory, "记忆数据库"),
            LogDirectory: Path.Combine(baseDirectory, "日志"),
            TempDirectory: Path.Combine(baseDirectory, "临时文件"),
            ToolsDirectory: Path.Combine(baseDirectory, "tools"));

        Directory.CreateDirectory(directories.DownloadedAudioDirectory);
        Directory.CreateDirectory(directories.TranscriptDirectory);
        Directory.CreateDirectory(directories.MemoryDatabaseDirectory);
        Directory.CreateDirectory(directories.LogDirectory);
        Directory.CreateDirectory(directories.TempDirectory);
        Directory.CreateDirectory(directories.ToolsDirectory);

        return directories;
    }
}
