using AudioText.Core.Models;

namespace AudioText.Core.Interfaces;

/// <summary>
/// 音频下载服务接口。
/// Audio download service abstraction.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public interface IAudioDownloadService
{
    /// <summary>
    /// 下载或提取音频，并返回本地文件路径。
    /// Download or extract audio and return a local file path.
    /// </summary>
    Task<AudioDownloadResult> DownloadAsync(
        AudioDownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken);
}
