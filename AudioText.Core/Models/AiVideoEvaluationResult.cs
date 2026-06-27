namespace AudioText.Core.Models;

/// <summary>
/// AI 视频文字评价结果。
/// Structured AI evaluation result for a video's transcript text.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
/// <param name="Category">总体类别，例如有意义信息视频、情绪引导嫌疑视频或低信息垃圾视频。Overall category.</param>
/// <param name="OverallScore">综合价值分，0-100，越高表示越值得观看。Overall value score from 0 to 100.</param>
/// <param name="TruthfulnessScore">真实性分，0-100，越高表示越符合证据。Truthfulness score from 0 to 100.</param>
/// <param name="TimelinessScore">时效性分，0-100，越高表示越新且时间敏感。Timeliness score from 0 to 100.</param>
/// <param name="InformationProfessionalismScore">信息专业性分，0-100，综合信息密度、重要性和观点新颖性。Information professionalism score from 0 to 100.</param>
/// <param name="EntertainmentScore">娱乐性分，0-100，越高表示用词越诙谐且文本越不让人打瞌睡。Entertainment score from 0 to 100.</param>
/// <param name="EmotionalGuidanceSuspicionScore">情绪引导嫌疑分，0-100，越高表示越像带节奏、煽动或标题党。Emotional guidance suspicion score from 0 to 100.</param>
/// <param name="Summary">兼容旧报告的摘要字段；新界面与 Markdown 报告不展示。Legacy summary field; new UI and Markdown reports do not display it.</param>
/// <param name="Verdict">评价结论。Final verdict.</param>
/// <param name="KeyClaims">关键待核查主张。Key claims extracted from the transcript.</param>
/// <param name="ClaimEvaluations">关键主张对应的五级评价。Five-level evaluation labels for key claims.</param>
/// <param name="SearchQueries">实际使用的搜索词。Search queries used for evidence collection.</param>
/// <param name="Evidences">外部搜索证据。External web evidence.</param>
/// <param name="Warnings">限制与风险提示。Limitations and warnings.</param>
/// <param name="RawModelOutput">模型原始输出，便于排查格式问题。Raw LLM output for debugging.</param>
public sealed record AiVideoEvaluationResult(
    string Category,
    int OverallScore,
    int TruthfulnessScore,
    int TimelinessScore,
    int InformationProfessionalismScore,
    int EntertainmentScore,
    int EmotionalGuidanceSuspicionScore,
    string Summary,
    string Verdict,
    IReadOnlyList<string> KeyClaims,
    IReadOnlyList<AiClaimEvaluation> ClaimEvaluations,
    IReadOnlyList<string> SearchQueries,
    IReadOnlyList<AiEvaluationEvidence> Evidences,
    IReadOnlyList<string> Warnings,
    string RawModelOutput);
