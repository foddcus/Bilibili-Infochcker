namespace AudioText.Core.Models;

/// <summary>
/// 语音识别进度。
/// Transcription progress reported by the recognition engine.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="Percent">进度百分比，未知时为空。Progress percentage, null when unknown.</param>
/// <param name="Message">当前状态说明。Current status message.</param>
/// <param name="CurrentSegmentText">当前片段文本，可为空。Current segment text, optional.</param>
public sealed record TranscriptionProgress(
    double? Percent,
    string Message,
    string? CurrentSegmentText = null);
