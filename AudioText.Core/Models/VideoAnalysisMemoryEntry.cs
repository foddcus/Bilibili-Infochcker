namespace AudioText.Core.Models;

/// <summary>
/// 视频分析记忆数据库条目。
/// Video analysis memory database entry.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
/// <param name="SourceKey">规范化来源链接的稳定哈希键。Stable hash key of the normalized source URL.</param>
/// <param name="SourceUrl">原始视频或音频来源链接。Original video or audio source URL.</param>
/// <param name="Title">视频标题或任务标题。Video or task title.</param>
/// <param name="Website">视频平台或网站。Video platform or website.</param>
/// <param name="PublisherName">发布人名称，未知时为空。Publisher/uploader name when known.</param>
/// <param name="ViewCount">浏览量，未知时为空。View count when known.</param>
/// <param name="TranscriptText">可复用的断句/纠错后转写文本。Reusable punctuated/corrected transcript text.</param>
/// <param name="TranscriptPath">最近一次转写文本文件路径，可为空。Latest transcript file path, optional.</param>
/// <param name="CreatedAt">首次写入记忆数据库时间。First memory creation time.</param>
/// <param name="LastAccessedAt">最近一次进入任务流程或复用时间。Last task access or reuse time.</param>
/// <param name="LastTranscribedAt">最近一次生成或更新转写文本时间。Latest transcription time.</param>
/// <param name="LastEvaluatedAt">最近一次完成 AI 核查时间。Latest AI evaluation time.</param>
/// <param name="TaskCount">该来源累计进入任务流程的次数。Number of task runs for this source.</param>
/// <param name="LastReportPath">最近一次 AI Markdown 报告路径，可为空。Latest AI Markdown report path, optional.</param>
/// <param name="Category">最近一次 AI 评价类别，可为空。Latest AI category, optional.</param>
/// <param name="OverallScore">最近一次综合价值分，可为空。Latest overall score, optional.</param>
/// <param name="TruthfulnessScore">最近一次真实性分，可为空。Latest truthfulness score, optional.</param>
/// <param name="TimelinessScore">最近一次时效性分，可为空。Latest timeliness score, optional.</param>
/// <param name="InformationProfessionalismScore">最近一次信息专业性分，可为空。Latest information professionalism score, optional.</param>
/// <param name="EntertainmentScore">最近一次娱乐性分，可为空。Latest entertainment score, optional.</param>
/// <param name="EmotionalGuidanceSuspicionScore">最近一次情绪引导嫌疑分，可为空。Latest emotional guidance suspicion score, optional.</param>
/// <param name="Verdict">最近一次 AI 结论，可为空。Latest AI verdict, optional.</param>
/// <param name="KeyClaims">最近一次提取的关键主张；数组格式便于 JSON 数据库稳定反序列化。Latest key claims; array form keeps JSON database deserialization stable.</param>
/// <param name="ClaimEvaluations">最近一次逐条主张五级评价；数组格式便于 JSON 数据库稳定反序列化。Latest five-level claim evaluations; array form keeps JSON database deserialization stable.</param>
/// <param name="Warnings">最近一次限制与风险提示；数组格式便于 JSON 数据库稳定反序列化。Latest limitations and warnings; array form keeps JSON database deserialization stable.</param>
public sealed record VideoAnalysisMemoryEntry(
    string SourceKey,
    string SourceUrl,
    string Title,
    string Website,
    string? PublisherName,
    long? ViewCount,
    string TranscriptText,
    string? TranscriptPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    DateTimeOffset? LastTranscribedAt,
    DateTimeOffset? LastEvaluatedAt,
    int TaskCount,
    string? LastReportPath,
    string? Category,
    int? OverallScore,
    int? TruthfulnessScore,
    int? TimelinessScore,
    int? InformationProfessionalismScore,
    int? EntertainmentScore,
    int? EmotionalGuidanceSuspicionScore,
    string? Verdict,
    string[] KeyClaims,
    AiClaimEvaluation[] ClaimEvaluations,
    string[] Warnings)
{
    /// <summary>
    /// 转换为给 AI 提示词使用的轻量历史摘要，避免把整段往期转写文本再次塞入请求。
    /// Convert to a lightweight prompt reference so old full transcripts are not resent to the model.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    public VideoMemoryReference ToReference()
    {
        return new VideoMemoryReference(
            Title,
            SourceUrl,
            Website,
            PublisherName,
            LastEvaluatedAt ?? LastTranscribedAt ?? LastAccessedAt,
            TaskCount,
            Category,
            OverallScore,
            Verdict,
            KeyClaims,
            ClaimEvaluations,
            Warnings);
    }
}
