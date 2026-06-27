namespace AudioText.Core.Models;

/// <summary>
/// AI 转写文本断句与错别字修正进度。
/// AI transcript punctuation and typo-correction progress.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="Percent">进度百分比，未知时为空。Progress percentage, null when unknown.</param>
/// <param name="Message">当前状态说明。Current status message.</param>
public sealed record TranscriptPunctuationProgress(
    double? Percent,
    string Message);
