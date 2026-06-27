using AudioText.Core.Interfaces;

namespace AudioText.Verification.Services;

/// <summary>
/// 联网搜索服务工厂。
/// Factory for creating web search adapters.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public static class WebSearchServiceFactory
{
    /// <summary>
    /// 根据配置创建搜索服务。
    /// Create a search service from the current settings.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public static IWebSearchService Create(AiVerificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var googleSearchService = new GoogleWebSearchService();
        var bingSearchService = new BingWebSearchService();
        var baiduSearchService = new BaiduWebSearchService();
        var duckDuckGoSearchService = new DuckDuckGoLiteWebSearchService();
        var baiduThenDuckDuckGoSearchService = new FallbackWebSearchService(baiduSearchService, duckDuckGoSearchService);
        var bingThenBaiduSearchService = new FallbackWebSearchService(bingSearchService, baiduThenDuckDuckGoSearchService);
        var internationalFallbackSearchService = new FallbackWebSearchService(googleSearchService, bingThenBaiduSearchService);
        IWebSearchService existingSearchService = internationalFallbackSearchService;
        if (!string.IsNullOrWhiteSpace(settings.SearxngEndpoint))
        {
            var searxngSearchService = new SearxngWebSearchService(settings.SearxngEndpoint);
            existingSearchService = new FallbackWebSearchService(searxngSearchService, internationalFallbackSearchService);
        }

        if (string.IsNullOrWhiteSpace(settings.BochaWebSearchApiKey))
        {
            return existingSearchService;
        }

        var bochaSearchService = new BochaWebSearchService(settings.BochaWebSearchApiKey);
        return new FallbackWebSearchService(bochaSearchService, existingSearchService);
    }
}
