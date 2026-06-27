using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// Bocha Web Search API adapter used as the paid primary evidence source.
/// Last modified: 2026-06-24; maintainer: GG.
/// </summary>
public sealed class BochaWebSearchService : IWebSearchService
{
    private const string SearchEndpoint = "https://api.bochaai.com/v1/web-search";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _apiKey;

    /// <summary>
    /// Create a Bocha Web Search API adapter.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    /// <param name="apiKey">Bocha Web Search API key.</param>
    public BochaWebSearchService(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey.Trim();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var resultCount = Math.Clamp(request.MaxResults, 1, 10);
        var apiRequest = new BochaSearchRequestDto
        {
            Query = request.Query,
            Count = resultCount,
            Freshness = "noLimit",
            Summary = true
        };
        var requestJson = JsonSerializer.Serialize(apiRequest, ApiJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, SearchEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Headers.UserAgent.ParseAdd("AudioText-Verifier/1.0");
        httpRequest.Headers.Accept.ParseAdd("application/json");
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await SharedHttpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Bocha Web Search API request failed: HTTP {(int)response.StatusCode}. {TruncateText(responseText, 500)}");
        }

        return ParseResults(responseText, resultCount);
    }

    /// <summary>
    /// Parse the Bocha Web Search API JSON response.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static IReadOnlyList<WebSearchResult> ParseResults(string json, int maxResults)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            ThrowIfApiCodeFailed(document.RootElement);

            var results = new List<WebSearchResult>();
            foreach (var resultsElement in EnumerateKnownResultArrays(document.RootElement))
            {
                AppendResults(results, resultsElement, maxResults);
                if (results.Count >= Math.Max(1, maxResults))
                {
                    break;
                }
            }

            return results;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Bocha Web Search API returned invalid JSON.", ex);
        }
    }

    /// <summary>
    /// Check the Bocha business code and let non-success codes trigger fallback.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static void ThrowIfApiCodeFailed(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var codeElement))
        {
            return;
        }

        var isSuccess = codeElement.ValueKind switch
        {
            JsonValueKind.Number => codeElement.TryGetInt32(out var code) && code is 0 or 200,
            JsonValueKind.String => IsSuccessCodeText(codeElement.GetString()),
            _ => false
        };
        if (isSuccess)
        {
            return;
        }

        var message = ReadStringProperty(root, "msg")
            ?? ReadStringProperty(root, "message")
            ?? "unknown API error";
        throw new InvalidOperationException($"Bocha Web Search API business code failed: {codeElement}; {message}");
    }

    /// <summary>
    /// Enumerate result arrays using Bocha documented and common-compatible shapes.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateKnownResultArrays(JsonElement root)
    {
        var paths = new[]
        {
            new[] { "data", "webPages", "value" },
            new[] { "webPages", "value" },
            new[] { "data", "results" },
            new[] { "results" },
            new[] { "data", "value" },
            new[] { "value" }
        };

        foreach (var path in paths)
        {
            if (TryGetNestedProperty(root, path, out var element)
                && element.ValueKind == JsonValueKind.Array)
            {
                yield return element;
            }
        }
    }

    /// <summary>
    /// Convert a result array to the internal WebSearchResult shape.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static void AppendResults(
        List<WebSearchResult> results,
        JsonElement resultsElement,
        int maxResults)
    {
        foreach (var item in resultsElement.EnumerateArray())
        {
            if (results.Count >= Math.Max(1, maxResults))
            {
                break;
            }

            var title = ReadFirstStringProperty(item, "name", "title");
            var url = ReadFirstStringProperty(item, "url", "link");
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

            var snippet = ReadFirstStringProperty(item, "summary", "snippet", "content", "description")
                ?? string.Empty;
            results.Add(new WebSearchResult(title, url, snippet, "Bocha Web Search"));
        }
    }

    /// <summary>
    /// Read the first non-empty string property in order.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static string? ReadFirstStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadStringProperty(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Safely read a string field from a JSON object.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static string? ReadStringProperty(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    /// <summary>
    /// Safely read a nested JSON property.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static bool TryGetNestedProperty(JsonElement root, IReadOnlyList<string> path, out JsonElement element)
    {
        element = root;
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(name, out element))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Check whether a string business code means success.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static bool IsSuccessCodeText(string? codeText)
    {
        return string.Equals(codeText, "0", StringComparison.Ordinal)
            || string.Equals(codeText, "200", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codeText, "success", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codeText, "ok", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Truncate long HTTP error payloads.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "...";
    }

    /// <summary>
    /// Bocha Web Search API request DTO.
    /// Last modified: 2026-06-24; maintainer: GG.
    /// </summary>
    private sealed class BochaSearchRequestDto
    {
        [JsonPropertyName("query")]
        public string Query { get; init; } = string.Empty;

        [JsonPropertyName("freshness")]
        public string Freshness { get; init; } = "noLimit";

        [JsonPropertyName("summary")]
        public bool Summary { get; init; }

        [JsonPropertyName("count")]
        public int Count { get; init; }
    }
}
