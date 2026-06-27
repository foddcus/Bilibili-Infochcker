namespace AudioText.Core.Models;

/// <summary>
/// 联网搜索结果。
/// Web search result.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="Title">网页标题。Page title.</param>
/// <param name="Url">网页地址。Page URL.</param>
/// <param name="Snippet">网页摘要。Search snippet.</param>
/// <param name="Source">搜索来源，例如 SearXNG、Google Search、Bing Search、Baidu Search 或 DuckDuckGo Lite。Search provider name.</param>
public sealed record WebSearchResult(
    string Title,
    string Url,
    string Snippet,
    string Source);
