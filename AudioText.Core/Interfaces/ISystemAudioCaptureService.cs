namespace AudioText.Core.Interfaces;

/// <summary>
/// 系统声音采集服务接口。
/// System audio loopback capture service abstraction.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public interface ISystemAudioCaptureService
{
    /// <summary>
    /// 开始采集系统播放声音，并将音频切片发送给消费者。
    /// Start capturing system playback audio and send chunks to the consumer.
    /// </summary>
    Task StartAsync(IAudioChunkConsumer consumer, CancellationToken cancellationToken);

    /// <summary>
    /// 停止采集。
    /// Stop capture.
    /// </summary>
    Task StopAsync();
}
