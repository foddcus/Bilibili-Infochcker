using AudioText.Core.Interfaces;

namespace AudioText.Capture.Services;

/// <summary>
/// 系统声音采集占位服务。
/// Placeholder system audio capture service before NAudio/WASAPI is integrated.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public sealed class StubSystemAudioCaptureService : ISystemAudioCaptureService
{
    /// <inheritdoc />
    public Task StartAsync(IAudioChunkConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        throw new NotSupportedException("系统声音实时采集模块尚未接入。System loopback capture has not been integrated yet.");
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        return Task.CompletedTask;
    }
}
