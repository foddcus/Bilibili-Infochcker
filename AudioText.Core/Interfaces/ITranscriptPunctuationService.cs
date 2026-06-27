using AudioText.Core.Models;

namespace AudioText.Core.Interfaces;

/// <summary>
/// 转写文本断句与错别字修正服务接口。
/// Transcript punctuation and typo-correction service contract.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public interface ITranscriptPunctuationService
{
    /// <summary>
    /// 为无标点转写文本补充断句、基础标点，并保守修正明显错别字。
    /// Add sentence boundaries, basic punctuation, and conservative typo corrections without changing meaning.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    Task<TranscriptPunctuationResult> PunctuateAsync(
        TranscriptPunctuationRequest request,
        IProgress<TranscriptPunctuationProgress>? progress,
        CancellationToken cancellationToken);
}
