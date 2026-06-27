namespace AudioText.Core.Models;

/// <summary>
/// AI 核查时可引用的往期视频记忆摘要。
/// Historical video memory summary that can be referenced during AI verification.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
/// <param name="Title">往期视频标题。Historical video title.</param>
/// <param name="SourceUrl">往期视频来源链接，可为空。Historical source URL, optional.</param>
/// <param name="Website">视频平台或网站。Video platform or website.</param>
/// <param name="PublisherName">发布人名称，可为空。Publisher/uploader name, optional.</param>
/// <param name="LastAnalyzedAt">最近一次完成分析的时间。Last analysis time.</param>
/// <param name="TaskCount">该来源累计进入任务流程的次数。Number of task runs for this source.</param>
/// <param name="Category">最近一次 AI 评价类别，可为空。Latest AI category, optional.</param>
/// <param name="OverallScore">最近一次综合价值分，可为空。Latest overall score, optional.</param>
/// <param name="Verdict">最近一次 AI 结论，可为空。Latest AI verdict, optional.</param>
/// <param name="KeyClaims">最近一次提取的关键主张。Latest key claims.</param>
/// <param name="ClaimEvaluations">最近一次逐条主张五级评价。Latest five-level claim evaluations.</param>
/// <param name="Warnings">最近一次限制与风险提示。Latest limitations and warnings.</param>
public sealed record VideoMemoryReference(
    string Title,
    string? SourceUrl,
    string Website,
    string? PublisherName,
    DateTimeOffset LastAnalyzedAt,
    int TaskCount,
    string? Category,
    int? OverallScore,
    string? Verdict,
    IReadOnlyList<string> KeyClaims,
    IReadOnlyList<AiClaimEvaluation> ClaimEvaluations,
    IReadOnlyList<string> Warnings);
