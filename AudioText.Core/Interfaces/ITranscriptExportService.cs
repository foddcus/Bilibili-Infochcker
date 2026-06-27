using AudioText.Core.Models;

namespace AudioText.Core.Interfaces;

/// <summary>
/// 转写结果导出接口。
/// Transcript export service abstraction.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public interface ITranscriptExportService
{
    /// <summary>
    /// 导出普通文本。
    /// Export plain text.
    /// </summary>
    Task ExportTextAsync(TranscriptDocument document, string outputPath, CancellationToken cancellationToken);

    /// <summary>
    /// 导出 SRT 字幕。
    /// Export SRT subtitles.
    /// </summary>
    Task ExportSrtAsync(TranscriptDocument document, string outputPath, CancellationToken cancellationToken);
}
