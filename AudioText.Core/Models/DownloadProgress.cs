namespace AudioText.Core.Models;

/// <summary>
/// 下载进度信息。
/// Download progress information for UI display and logs.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="Percent">进度百分比，未知时为空。Progress percentage, null when unknown.</param>
/// <param name="Message">当前状态说明。Current status message.</param>
/// <param name="ReceivedBytes">已接收字节数。Received bytes.</param>
/// <param name="TotalBytes">总字节数，未知时为空。Total bytes, null when unknown.</param>
public sealed record DownloadProgress(
    double? Percent,
    string Message,
    long ReceivedBytes = 0,
    long? TotalBytes = null);
