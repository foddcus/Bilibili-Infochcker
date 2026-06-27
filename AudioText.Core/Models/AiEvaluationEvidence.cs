namespace AudioText.Core.Models;

/// <summary>
/// AI 评价所使用的外部证据条目。
/// External evidence item used by the AI evaluation result.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="Title">网页标题。Page title.</param>
/// <param name="Url">网页地址。Page URL.</param>
/// <param name="Snippet">网页摘要。Search snippet or extracted short summary.</param>
/// <param name="Query">触发该结果的搜索词。Search query that produced this result.</param>
/// <param name="Source">返回该结果的搜索来源，例如 SearXNG、Google Search、Bing Search、Baidu Search 或 DuckDuckGo Lite。Search provider that returned this result.</param>
public sealed record AiEvaluationEvidence(
    string Title,
    string Url,
    string Snippet,
    string Query,
    string Source);
