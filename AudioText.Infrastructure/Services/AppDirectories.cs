namespace AudioText.Infrastructure.Services;

/// <summary>
/// 应用默认目录集合。
/// Default application directories used by the desktop app.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
/// <param name="BaseDirectory">运行基准目录。Runtime base directory.</param>
/// <param name="DownloadedAudioDirectory">下载音频目录。Downloaded audio directory.</param>
/// <param name="TranscriptDirectory">输出文本目录。Transcript output directory.</param>
/// <param name="MemoryDatabaseDirectory">记忆数据库目录。Memory database directory.</param>
/// <param name="LogDirectory">日志目录。Log directory.</param>
/// <param name="TempDirectory">临时文件目录。Temporary file directory.</param>
/// <param name="ToolsDirectory">外部工具目录。External tools directory.</param>
public sealed record AppDirectories(
    string BaseDirectory,
    string DownloadedAudioDirectory,
    string TranscriptDirectory,
    string MemoryDatabaseDirectory,
    string LogDirectory,
    string TempDirectory,
    string ToolsDirectory);
