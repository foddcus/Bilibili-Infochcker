namespace AudioText.Core.Models;

/// <summary>
/// AI 转写文本断句与错别字修正请求。
/// AI transcript punctuation and typo-correction request.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="PlainText">待断句和保守纠错的原始转写文本。Raw transcript text to punctuate and conservatively correct.</param>
/// <param name="Title">视频标题，可为空。Optional video title.</param>
/// <param name="SourceUrl">来源链接，可为空。Optional source URL.</param>
public sealed record TranscriptPunctuationRequest(
    string PlainText,
    string? Title,
    string? SourceUrl);
