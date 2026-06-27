namespace AudioText.Core.Models;

/// <summary>
/// 完整转写文档。
/// Complete transcript document used by exporters and UI.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="Title">标题。Title.</param>
/// <param name="Source">来源说明。Source description or URL.</param>
/// <param name="CreatedAt">创建时间。Creation time.</param>
/// <param name="Segments">带时间戳的文本片段。Timestamped transcript segments.</param>
public sealed record TranscriptDocument(
    string Title,
    string Source,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptSegment> Segments)
{
    /// <summary>
    /// 合并后的纯文本。
    /// Plain text merged from all transcript segments.
    /// </summary>
    public string PlainText => string.Join(Environment.NewLine, Segments.Select(segment => segment.Text));
}
