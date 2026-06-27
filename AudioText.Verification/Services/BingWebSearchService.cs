using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// Bing 联网搜索适配器。
/// Bing web-search adapter used as an international search fallback.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class BingWebSearchService : IWebSearchService
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

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
            "https://www.bing.com/search"
            + $"?format=rss&q={Uri.EscapeDataString(request.Query)}"
            + $"&count={resultCount}"
            + "&mkt=en-US&setlang=en-US");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
        httpRequest.Headers.UserAgent.ParseAdd("AudioText-Verifier/1.0");
        httpRequest.Headers.Accept.ParseAdd("application/rss+xml, application/xml, text/xml, text/html;q=0.8");
        httpRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9,zh-CN;q=0.8,zh;q=0.7,ja;q=0.6,ko;q=0.6");

        using var response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken);
        var xml = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Bing 搜索失败：HTTP {(int)response.StatusCode}。");
        }

        if (IsBingChallengePage(xml))
        {
            throw new InvalidOperationException("Bing 搜索触发人机验证或访问限制。");
        }

        return ParseRssResults(xml, resultCount);
    }

    /// <summary>
    /// 从 Bing RSS XML 中提取标题、URL 和摘要。
    /// Extract title, URL, and snippet from Bing RSS XML.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlyList<WebSearchResult> ParseRssResults(string xml, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var document = XDocument.Parse(xml);
        var items = document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase));

        foreach (var item in items)
        {
            if (results.Count >= Math.Max(1, maxResults))
            {
                break;
            }

            var title = CleanText(ReadChildValue(item, "title"));
            var url = NormalizeBingUrl(ReadChildValue(item, "link"));
            var snippet = CleanText(ReadChildValue(item, "description"));
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (results.Any(result => result.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            results.Add(new WebSearchResult(title, url, snippet, "Bing Search"));
        }

        return results;
    }

    /// <summary>
    /// 读取 XML 子元素文本；按 LocalName 兼容默认命名空间。
    /// Read an XML child element by LocalName so default namespaces remain compatible.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string ReadChildValue(XElement item, string localName)
    {
        return item
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?? string.Empty;
    }

    /// <summary>
    /// 还原 Bing 跳转链接中的真实目标地址。
    /// Restore the real target URL from Bing redirect links.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? NormalizeBingUrl(string rawUrl)
    {
        var decodedUrl = WebUtility.HtmlDecode(rawUrl).Trim();
        if (decodedUrl.StartsWith("//", StringComparison.Ordinal))
        {
            decodedUrl = "https:" + decodedUrl;
        }
        else if (decodedUrl.StartsWith("/", StringComparison.Ordinal))
        {
            decodedUrl = "https://www.bing.com" + decodedUrl;
        }

        if (!Uri.TryCreate(decodedUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase))
        {
            if (TryReadQueryParameter(uri.Query, "u", out var encodedTargetUrl)
                && TryDecodeBingRedirectTarget(encodedTargetUrl, out var decodedTargetUrl)
                && Uri.TryCreate(decodedTargetUrl, UriKind.Absolute, out _))
            {
                return decodedTargetUrl;
            }

            if (TryReadQueryParameter(uri.Query, "url", out var targetUrl)
                && Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
            {
                return targetUrl;
            }

            return null;
        }

        return uri.ToString();
    }

    /// <summary>
    /// 解码 Bing /ck/a 链接中 base64url 形式的 u 参数。
    /// Decode the base64url u parameter used by Bing /ck/a redirect links.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool TryDecodeBingRedirectTarget(string encodedTargetUrl, out string decodedTargetUrl)
    {
        decodedTargetUrl = string.Empty;
        var encoded = WebUtility.UrlDecode(encodedTargetUrl).Trim();
        if (encoded.StartsWith("a1", StringComparison.OrdinalIgnoreCase))
        {
            encoded = encoded[2..];
        }

        encoded = encoded.Replace('-', '+').Replace('_', '/');
        var padding = encoded.Length % 4;
        if (padding > 0)
        {
            encoded = encoded.PadRight(encoded.Length + 4 - padding, '=');
        }

        try
        {
            decodedTargetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return decodedTargetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || decodedTargetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
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
    /// 判断 Bing 是否返回了人机验证页而不是 RSS 结果。
    /// Check whether Bing returned a challenge page instead of RSS results.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool IsBingChallengePage(string text)
    {
        return text.Contains("One last step", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Please solve the challenge", StringComparison.OrdinalIgnoreCase)
            || text.Contains("challenge/verify", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 清理 XML/HTML 实体和标签，保留纯文本证据摘要。
    /// Clean XML/HTML entities and tags to keep plain-text evidence snippets.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string CleanText(string text)
    {
        var withoutTags = HtmlTagRegex.Replace(text, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);

        return Regex.Replace(decoded, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
    }
}
