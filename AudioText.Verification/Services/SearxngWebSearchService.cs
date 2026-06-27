using System.Text.Json;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// SearXNG JSON 搜索适配器。
/// SearXNG JSON web search adapter.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class SearxngWebSearchService : IWebSearchService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly string _endpoint;

    /// <summary>
    /// 创建 SearXNG 搜索适配器。
    /// Create a SearXNG search adapter.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    /// <param name="endpoint">SearXNG 实例地址，可为根地址或 /search 地址。SearXNG root or /search endpoint.</param>
    public SearxngWebSearchService(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        _endpoint = endpoint.Trim();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var requestUri = BuildSearchUri(request.Query);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
        httpRequest.Headers.UserAgent.ParseAdd("AudioText-Verifier/1.0");
        httpRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7,ja;q=0.6,ko;q=0.6");

        using var response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"SearXNG 搜索失败：HTTP {(int)response.StatusCode}。");
        }

        return ParseResults(json, request.MaxResults);
    }

    /// <summary>
    /// 构建 SearXNG JSON 搜索 URL。
    /// Build the SearXNG JSON search URL.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private Uri BuildSearchUri(string query)
    {
        var endpoint = _endpoint.EndsWith("/search", StringComparison.OrdinalIgnoreCase)
            ? _endpoint
            : _endpoint.TrimEnd('/') + "/search";
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var url = endpoint
            + separator
            + $"q={Uri.EscapeDataString(query)}&format=json&language=all&engines=google,bing,duckduckgo&safesearch=0";

        return new Uri(url);
    }

    /// <summary>
    /// 解析 SearXNG JSON 响应。
    /// Parse the SearXNG JSON response.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlyList<WebSearchResult> ParseResults(string json, int maxResults)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var resultsElement)
            || resultsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<WebSearchResult>();
        }

        var results = new List<WebSearchResult>();
        foreach (var item in resultsElement.EnumerateArray())
        {
            if (results.Count >= Math.Max(1, maxResults))
            {
                break;
            }

            var title = ReadStringProperty(item, "title");
            var url = ReadStringProperty(item, "url");
            var snippet = ReadStringProperty(item, "content");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                continue;
            }

            if (results.Any(result => result.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new WebSearchResult(title, url, snippet ?? string.Empty, "SearXNG"));
        }

        return results;
    }

    /// <summary>
    /// 从 JSON 对象中安全读取字符串字段。
    /// Safely read a string property from a JSON object.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? ReadStringProperty(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }
}
