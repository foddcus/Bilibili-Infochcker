namespace AudioText.Core.Models;

/// <summary>
/// 语音识别结果。
/// Transcription result returned by the speech recognition service.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="Document">完整转写文档。Complete transcript document.</param>
/// <param name="RawOutputPath">识别引擎原始输出路径，可为空。Optional raw engine output path.</param>
public sealed record TranscriptionResult(
    TranscriptDocument Document,
    string? RawOutputPath = null);
