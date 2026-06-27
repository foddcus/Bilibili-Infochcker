using AudioText.Core.Models;

namespace AudioText.Core.Interfaces;

/// <summary>
/// 实时音频切片消费者。
/// Audio chunk consumer used by capture services to decouple capture and transcription.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public interface IAudioChunkConsumer
{
    /// <summary>
    /// 接收一个音频切片。
    /// Consume one captured audio chunk.
    /// </summary>
    Task ConsumeAsync(AudioChunk chunk, CancellationToken cancellationToken);
}
