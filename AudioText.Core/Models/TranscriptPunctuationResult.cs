namespace AudioText.Core.Models;

/// <summary>
/// AI 转写文本断句与错别字修正结果。
/// AI transcript punctuation and typo-correction result.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="PunctuatedText">已断句并保守纠错的文本。Punctuated and conservatively corrected transcript text.</param>
/// <param name="RawModelOutput">模型原始输出，便于排查格式问题。Raw model output for debugging.</param>
public sealed record TranscriptPunctuationResult(
    string PunctuatedText,
    string RawModelOutput);
