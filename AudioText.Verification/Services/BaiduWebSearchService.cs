using System.Net;
using System.Text.RegularExpressions;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// Baidu 联网搜索适配器。
/// Baidu web-search adapter used before the DuckDuckGo fallback.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class BaiduWebSearchService : IWebSearchService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Regex ResultBlockRegex = new(
        @"<div[^>]+class\s*=\s*[""'][^""']*(?:result|c-container)[^""']*[""'][^>]*>.*?(?=<div[^>]+class\s*=\s*[""'][^""']*(?:result|c-container)[^""']*[""']|<div[^>]+id\s*=\s*[""']page[""']|</body>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TitleAnchorRegex = new(
        @"<h3[^>]*>.*?<a(?<attrs>[^>]*)>(?<title>.*?)</a>.*?</h3>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex MuRegex = new(
        @"\bmu\s*=\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SnippetRegex = new(
        @"<(?:div|span)[^>]+class\s*=\s*[""'][^""']*(?:c-abstract|c-line-clamp|content-right)[^""']*[""'][^>]*>(?<snippet>.*?)</(?:div|span)>",
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

        var resultCount = Math.Clamp(request.MaxResults, 1, 10);
        var requestUri = new Uri(
            "https://www.baidu.com/s"
            + $"?wd={Uri.EscapeDataString(request.Query)}"
            + $"&rn={resultCount}"
            + "&ie=utf-8");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
        httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        httpRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        httpRequest.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");

        using var response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Baidu 搜索失败：HTTP {(int)response.StatusCode}。");
        }

        if (IsBaiduChallengePage(html, response.RequestMessage?.RequestUri))
        {
            throw new InvalidOperationException("Baidu 搜索触发验证码或访问限制。");
        }

        return ParseSearchResults(html, resultCount);
    }

    /// <summary>
    /// 从 Baidu HTML 中提取标题、URL 和摘要。
    /// Extract title, URL, and snippet from Baidu HTML.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlyList<WebSearchResult> ParseSearchResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var matches = ResultBlockRegex.Matches(html);

        foreach (Match match in matches)
        {
            if (results.Count >= Math.Max(1, maxResults))
            {
                break;
            }

            var block = match.Value;
            var titleMatch = TitleAnchorRegex.Match(block);
            if (!titleMatch.Success)
            {
                continue;
            }

            var url = ReadBaiduResultUrl(block, titleMatch.Groups["attrs"].Value);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var title = CleanHtml(titleMatch.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            if (results.Any(result => result.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var snippetMatch = SnippetRegex.Match(block);
            var snippet = snippetMatch.Success
                ? CleanHtml(snippetMatch.Groups["snippet"].Value)
                : string.Empty;

            results.Add(new WebSearchResult(title, url, snippet, "Baidu Search"));
        }

        return results;
    }

    /// <summary>
    /// 优先读取 Baidu 结果块的真实来源 mu 属性，缺失时才退回 href。
    /// Prefer the real-source mu attribute in a Baidu result block, then fall back to href.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? ReadBaiduResultUrl(string block, string anchorAttributes)
    {
        var muMatch = MuRegex.Match(block);
        if (muMatch.Success)
        {
            var normalizedMuUrl = NormalizeBaiduUrl(muMatch.Groups["url"].Value);
            if (!string.IsNullOrWhiteSpace(normalizedMuUrl))
            {
                return normalizedMuUrl;
            }
        }

        var hrefMatch = HrefRegex.Match(anchorAttributes);
        return hrefMatch.Success
            ? NormalizeBaiduUrl(hrefMatch.Groups["url"].Value)
            : null;
    }

    /// <summary>
    /// 标准化 Baidu 结果 URL；过滤无法还原真实来源的 Baidu 内部跳转。
    /// Normalize Baidu result URLs and filter Baidu redirect links without a real source.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? NormalizeBaiduUrl(string rawUrl)
    {
        var url = WebUtility.HtmlDecode(rawUrl).Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        else if (url.StartsWith("/", StringComparison.Ordinal))
        {
            url = "https://www.baidu.com" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.EndsWith("baidu.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/link", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.ToString();
    }

    /// <summary>
    /// 判断 Baidu 是否返回了验证码页而不是搜索结果。
    /// Check whether Baidu returned a captcha page instead of search results.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool IsBaiduChallengePage(string text, Uri? finalUri)
    {
        return finalUri?.Host.Contains("wappass.baidu.com", StringComparison.OrdinalIgnoreCase) == true
            || text.Contains("wappass.baidu.com/static/captcha", StringComparison.OrdinalIgnoreCase)
            || text.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || text.Contains("安全验证", StringComparison.OrdinalIgnoreCase)
            || text.Contains("请输入验证码", StringComparison.OrdinalIgnoreCase);
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
