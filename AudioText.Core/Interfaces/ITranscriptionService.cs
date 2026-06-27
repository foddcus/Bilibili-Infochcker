using AudioText.Core.Models;

namespace AudioText.Core.Interfaces;

/// <summary>
/// 语音识别服务接口。
/// Speech transcription service abstraction.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// 将音频转写为文本。
    /// Transcribe an audio file into text.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken);
}
