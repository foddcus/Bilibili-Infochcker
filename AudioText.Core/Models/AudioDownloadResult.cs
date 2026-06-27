namespace AudioText.Core.Models;

/// <summary>
/// 音频下载结果。
/// Audio download result returned after the source audio is available locally.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="LocalFilePath">本地音频文件路径。Local downloaded audio file path.</param>
/// <param name="SourceUrl">原始来源链接。Original source URL.</param>
/// <param name="Title">任务标题或媒体标题。Task or media title.</param>
/// <param name="Duration">音频时长，未知时为空。Audio duration when known.</param>
/// <param name="SourceMetadata">视频来源元数据，直接音频或平台不返回时为空。Video source metadata when available.</param>
public sealed record AudioDownloadResult(
    string LocalFilePath,
    string SourceUrl,
    string Title,
    TimeSpan? Duration = null,
    VideoSourceMetadata? SourceMetadata = null);
