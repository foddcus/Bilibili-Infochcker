namespace AudioText.Core.Models;

/// <summary>
/// 联网搜索请求。
/// Web search request.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="Query">搜索词。Search query.</param>
/// <param name="MaxResults">最多返回结果数。Maximum number of returned results.</param>
public sealed record WebSearchRequest(
    string Query,
    int MaxResults = 5);
