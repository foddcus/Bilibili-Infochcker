namespace AudioText.Core.Models;

/// <summary>
/// 视频来源元数据。
/// Video source metadata shown on the task card.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="Website">视频网站或平台名称。Video website or platform name.</param>
/// <param name="PublisherName">发布人名称，未知时为空。Publisher/uploader name when known.</param>
/// <param name="ViewCount">浏览量，未知时为空。View count when known.</param>
/// <param name="Title">视频标题，未知时为空。Video title when known.</param>
public sealed record VideoSourceMetadata(
    string Website,
    string? PublisherName = null,
    long? ViewCount = null,
    string? Title = null);
