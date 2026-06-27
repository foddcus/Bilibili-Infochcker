using AudioText.Core.Models;

namespace AudioText.Core.Interfaces;

/// <summary>
/// AI 视频文字评价服务接口。
/// AI service contract for evaluating a video's transcript text.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public interface IAiVideoEvaluationService
{
    /// <summary>
    /// 根据转写文本、联网搜索证据和大模型判断输出视频内容评价。
    /// Evaluate transcript content with web-search evidence and LLM reasoning.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    /// <param name="request">视频文字评价请求。Video transcript evaluation request.</param>
    /// <param name="progress">评价进度回调。Progress callback for UI updates.</param>
    /// <param name="cancellationToken">取消令牌。Cancellation token.</param>
    /// <returns>结构化评价结果。Structured evaluation result.</returns>
    Task<AiVideoEvaluationResult> EvaluateAsync(
        AiVideoEvaluationRequest request,
        IProgress<AiVideoEvaluationProgress>? progress,
        CancellationToken cancellationToken);
}
