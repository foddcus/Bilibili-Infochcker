using System.Net;
using System.Text.RegularExpressions;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// Google 网页搜索适配器。
/// Google web-search adapter used as the international search source.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class GoogleWebSearchService : IWebSearchService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly Regex ResultAnchorRegex = new(
        @"<a[^>]+href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>.*?<h3[^>]*>(?<title>.*?)</h3>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SnippetRegex = new(
        @"<div[^>]+class\s*=\s*[""'][^""']*(?:VwiC3b|yXK7lf|MUxGbd|lyLwlc)[^""']*[""'][^>]*>(?<snippet>.*?)</div>",
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
            "https://www.google.com/search"
            + $"?q={Uri.EscapeDataString(request.Query)}"
            + $"&num={resultCount}"
            + "&hl=en&pws=0&safe=off");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
        httpRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        httpRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        httpRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7,ja;q=0.6,ko;q=0.6");

        using var response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google 搜索失败：HTTP {(int)response.StatusCode}。");
        }

        if (html.Contains("Our systems have detected unusual traffic", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/sorry/", StringComparison.OrdinalIgnoreCase)
            || html.Contains("If you're having trouble accessing Google Search", StringComparison.OrdinalIgnoreCase)
            || html.Contains("SG_REL", StringComparison.OrdinalIgnoreCase)
            || html.Contains("/gen_204?cad=sg_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Google 搜索触发验证码、访问验证或流量限制。");
        }

        return ParseSearchResults(html, resultCount);
    }

    /// <summary>
    /// 从 Google HTML 中提取标题、URL 和摘要。
    /// Extract title, URL, and snippet from Google HTML.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlyList<WebSearchResult> ParseSearchResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var matches = ResultAnchorRegex.Matches(html);

        for (var index = 0; index < matches.Count && results.Count < Math.Max(1, maxResults); index++)
        {
            var match = matches[index];
            var url = NormalizeGoogleUrl(match.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var title = CleanHtml(match.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            if (results.Any(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var nextIndex = index + 1 < matches.Count
                ? matches[index + 1].Index
                : html.Length;
            var blockLength = Math.Max(0, nextIndex - match.Index);
            var block = html.Substring(match.Index, blockLength);
            var snippet = ExtractSnippet(block);

            results.Add(new WebSearchResult(title, url, snippet, "Google Search"));
        }

        return results;
    }

    /// <summary>
    /// 还原 Google 跳转链接中的真实目标地址。
    /// Restore the real target URL from Google redirect links.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? NormalizeGoogleUrl(string rawHref)
    {
        var href = WebUtility.HtmlDecode(rawHref).Trim();
        if (href.StartsWith("//", StringComparison.Ordinal))
        {
            href = "https:" + href;
        }

        if (href.StartsWith("/url?", StringComparison.OrdinalIgnoreCase)
            && TryReadQueryParameter(href[(href.IndexOf('?', StringComparison.Ordinal) + 1)..], "q", out var targetUrl))
        {
            href = targetUrl;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase)
            ? null
            : uri.ToString();
    }

    /// <summary>
    /// 从搜索结果块中读取摘要文本。
    /// Read snippet text from one search-result block.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string ExtractSnippet(string block)
    {
        var snippetMatch = SnippetRegex.Match(block);
        return snippetMatch.Success
            ? CleanHtml(snippetMatch.Groups["snippet"].Value)
            : string.Empty;
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
