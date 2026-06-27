using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Transcription.Services;

/// <summary>
/// 语音识别占位服务。
/// Placeholder transcription service before whisper.cpp is integrated.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public sealed class StubTranscriptionService : ITranscriptionService
{
    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new TranscriptionProgress(100, "语音识别占位服务已返回示例结果。Stub transcription result returned."));

        var document = new TranscriptDocument(
            Title: request.Title,
            Source: request.SourceUrl ?? request.InputAudioPath,
            CreatedAt: DateTimeOffset.Now,
            Segments:
            [
                new TranscriptSegment(
                    StartTime: TimeSpan.Zero,
                    EndTime: TimeSpan.Zero,
                    Text: "语音识别模块尚未接入，当前文本为框架占位输出。")
            ]);

        return Task.FromResult(new TranscriptionResult(document));
    }
}
