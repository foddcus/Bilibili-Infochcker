namespace AudioText.Core.Models;

/// <summary>
/// AI 视频文字评价请求。
/// AI video transcript evaluation request.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
/// <param name="TranscriptText">视频转写文本。Transcript text extracted from the video.</param>
/// <param name="Title">视频标题，可为空。Optional video title.</param>
/// <param name="SourceUrl">视频来源链接，可为空。Optional source URL.</param>
/// <param name="CreatedAt">请求创建时间。Request creation time.</param>
/// <param name="MaxSearchRounds">最多搜索查询数；int.MaxValue 表示不主动限制，用于兼容查验力度设置。Maximum search query count; int.MaxValue means uncapped for verification-intensity compatibility.</param>
/// <param name="MemoryReferences">同一发布人的往期视频记忆摘要，仅作背景参考。Historical video memory references from the same publisher, for background only.</param>
public sealed record AiVideoEvaluationRequest(
    string TranscriptText,
    string? Title,
    string? SourceUrl,
    DateTimeOffset CreatedAt,
    int MaxSearchRounds = 4,
    IReadOnlyList<VideoMemoryReference>? MemoryReferences = null);
