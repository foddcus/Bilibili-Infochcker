namespace AudioText.Core.Models;

/// <summary>
/// 语音识别请求。
/// Transcription request for a local audio file or temporary captured segment.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="InputAudioPath">待识别音频路径。Input audio path.</param>
/// <param name="ModelPath">本地识别模型路径。Local speech model path.</param>
/// <param name="Title">任务标题。Task title.</param>
/// <param name="Language">语言代码，例如 zh 或 en；自动检测时为空。Language code, null for auto detection.</param>
/// <param name="SourceUrl">来源网页或音频链接，可为空。Optional source URL.</param>
public sealed record TranscriptionRequest(
    string InputAudioPath,
    string ModelPath,
    string Title,
    string? Language = "zh",
    string? SourceUrl = null);
