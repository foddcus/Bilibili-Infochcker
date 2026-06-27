using System.Net;
using System.Text.RegularExpressions;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// DuckDuckGo Lite 联网搜索适配器。
/// DuckDuckGo Lite web search adapter.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class DuckDuckGoLiteWebSearchService : IWebSearchService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Regex ResultLinkRegex = new(
        @"<a(?<attrs>[^>]*class\s*=\s*[""']result-link[""'][^>]*)>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SnippetRegex = new(
        @"class\s*=\s*[""']result-snippet[""'][^>]*>(?<snippet>.*?)</",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HtmlTagRegex = new(
        "<.*?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var requestUri = new Uri($"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(request.Query)}");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
        httpRequest.Headers.UserAgent.ParseAdd("AudioText-Verifier/1.0");
        httpRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        httpRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7,ja;q=0.6,ko;q=0.6");

        using var response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"DuckDuckGo Lite 搜索失败：HTTP {(int)response.StatusCode}。");
        }

        if (IsDuckDuckGoChallengePage(html))
        {
            throw new InvalidOperationException("DuckDuckGo Lite 搜索触发人机验证或访问限制。");
        }

        return ParseSearchResults(html, request.MaxResults);
    }

    /// <summary>
    /// 从 DuckDuckGo Lite HTML 中提取标题、URL 和摘要。
    /// Extract title, URL, and snippet from DuckDuckGo Lite HTML.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlyList<WebSearchResult> ParseSearchResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var matches = ResultLinkRegex.Matches(html);

        for (var index = 0; index < matches.Count && results.Count < Math.Max(1, maxResults); index++)
        {
            var match = matches[index];
            var nextIndex = index + 1 < matches.Count
                ? matches[index + 1].Index
                : html.Length;
            var blockLength = Math.Max(0, nextIndex - match.Index);
            var block = html.Substring(match.Index, blockLength);

            var href = HrefRegex.Match(match.Groups["attrs"].Value);
            if (!href.Success)
            {
                continue;
            }

            var url = NormalizeDuckDuckGoUrl(href.Groups["url"].Value);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var title = CleanHtml(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var snippetMatch = SnippetRegex.Match(block);
            var snippet = snippetMatch.Success
                ? CleanHtml(snippetMatch.Groups["snippet"].Value)
                : string.Empty;

            if (results.Any(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new WebSearchResult(title, url, snippet, "DuckDuckGo Lite"));
        }

        return results;
    }

    /// <summary>
    /// 还原 DuckDuckGo 跳转链接中的真实目标地址。
    /// Restore the real target URL from DuckDuckGo redirect links.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? NormalizeDuckDuckGoUrl(string rawUrl)
    {
        var decodedUrl = WebUtility.HtmlDecode(rawUrl).Trim();
        if (decodedUrl.Contains("/y.js?", StringComparison.OrdinalIgnoreCase)
            || decodedUrl.Contains("ad_domain=", StringComparison.OrdinalIgnoreCase)
            || decodedUrl.Contains("ad_provider=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (decodedUrl.StartsWith("//", StringComparison.Ordinal))
        {
            decodedUrl = "https:" + decodedUrl;
        }
        else if (decodedUrl.StartsWith("/", StringComparison.Ordinal))
        {
            decodedUrl = "https://duckduckgo.com" + decodedUrl;
        }

        if (!Uri.TryCreate(decodedUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            && TryReadQueryParameter(uri.Query, "uddg", out var targetUrl)
            && Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
        {
            if (IsDuckDuckGoInternalOrAdUrl(targetUrl))
            {
                return null;
            }

            return targetUrl;
        }

        return uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            ? null
            : uri.ToString();
    }

    /// <summary>
    /// 判断 DuckDuckGo 是否返回了人机验证页而不是搜索结果。
    /// Check whether DuckDuckGo returned a challenge page instead of search results.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool IsDuckDuckGoChallengePage(string text)
    {
        return text.Contains("Unfortunately, bots use DuckDuckGo too", StringComparison.OrdinalIgnoreCase)
            || text.Contains("anomaly.js", StringComparison.OrdinalIgnoreCase)
            || text.Contains("challenge-form", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 过滤 DuckDuckGo 内部跳转和广告落地链接，避免广告被当作证据。
    /// Filter DuckDuckGo internal redirects and ad landing links so ads are not used as evidence.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool IsDuckDuckGoInternalOrAdUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return true;
        }

        return uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("ad_domain=", StringComparison.OrdinalIgnoreCase)
            || url.Contains("ad_provider=", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/aclick", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读取 URL 查询参数。
    /// Read one query-string parameter from a URL.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool TryReadQueryParameter(string query, string name, out string value)
    {
        value = string.Empty;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawValue = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
            value = WebUtility.UrlDecode(rawValue);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 清理 HTML 标签和实体，保留纯文本证据摘要。
    /// Clean HTML tags and entities to keep plain-text evidence snippets.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string CleanHtml(string html)
    {
        var withoutTags = HtmlTagRegex.Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);

        return Regex.Replace(decoded, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }
}
