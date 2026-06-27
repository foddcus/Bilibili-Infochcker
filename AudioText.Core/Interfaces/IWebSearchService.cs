using AudioText.Core.Models;

namespace AudioText.Core.Interfaces;

/// <summary>
/// 联网搜索服务接口。
/// Web search service contract used by AI verification modules.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public interface IWebSearchService
{
    /// <summary>
    /// 执行一次关键词搜索，并返回可供 AI 评价引用的网页摘要证据。
    /// Execute one keyword search and return web snippets usable as evidence.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    /// <param name="request">搜索请求。Search request.</param>
    /// <param name="cancellationToken">取消令牌。Cancellation token.</param>
    /// <returns>搜索结果列表。Search result list.</returns>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken);
}
