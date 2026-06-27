using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// 带降级能力的联网搜索服务。
/// Web search service with fallback behavior.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class FallbackWebSearchService : IWebSearchService
{
    private readonly IWebSearchService _primarySearchService;
    private readonly IWebSearchService _fallbackSearchService;

    /// <summary>
    /// 创建搜索降级适配器。
    /// Create a fallback search adapter.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public FallbackWebSearchService(
        IWebSearchService primarySearchService,
        IWebSearchService fallbackSearchService)
    {
        _primarySearchService = primarySearchService;
        _fallbackSearchService = fallbackSearchService;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var primaryResults = await _primarySearchService.SearchAsync(request, cancellationToken);
            if (primaryResults.Count > 0)
            {
                return primaryResults;
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return await _fallbackSearchService.SearchAsync(request, cancellationToken);
    }
}
