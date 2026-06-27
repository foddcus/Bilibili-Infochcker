using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Download.Services;

/// <summary>
/// 直接音频链接下载服务。
/// Direct audio URL downloader for lightweight first-version tasks.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class DirectAudioLinkDownloadService : IAudioDownloadService
{
    private static readonly string[] KnownAudioExtensions =
    [
        ".mp3",
        ".m4a",
        ".wav",
        ".aac",
        ".flac",
        ".ogg",
        ".opus",
        ".webm"
    ];

    private readonly HttpClient _httpClient;

    /// <summary>
    /// 创建直接音频链接下载器。
    /// Create a direct audio URL downloader.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    public DirectAudioLinkDownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <inheritdoc />
    public async Task<AudioDownloadResult> DownloadAsync(
        AudioDownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);

        if (!Uri.TryCreate(request.SourceUrl, UriKind.Absolute, out var sourceUri))
        {
            throw new ArgumentException("下载链接不是有效的绝对 URL。The source URL is not a valid absolute URL.", nameof(request));
        }

        if (sourceUri.Scheme is not ("http" or "https"))
        {
            throw new NotSupportedException("第一版直接下载器仅支持 http/https 音频链接。Only http/https audio URLs are supported by this downloader.");
        }

        Directory.CreateDirectory(request.OutputDirectory);
        progress?.Report(new DownloadProgress(null, "开始请求音频链接。Requesting audio URL."));

        using var response = await _httpClient.GetAsync(sourceUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        var outputFilePath = BuildOutputFilePath(request, sourceUri);

        await using var inputStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var outputStream = File.Create(outputFilePath);

        var buffer = new byte[1024 * 64];
        long receivedBytes = 0;

        while (true)
        {
            var readCount = await inputStream.ReadAsync(buffer, cancellationToken);
            if (readCount == 0)
            {
                break;
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, readCount), cancellationToken);
            receivedBytes += readCount;

            double? percent = totalBytes > 0 ? receivedBytes * 100.0 / totalBytes.Value : null;
            progress?.Report(new DownloadProgress(percent, "正在下载音频。Downloading audio.", receivedBytes, totalBytes));
        }

        progress?.Report(new DownloadProgress(100, "音频下载完成。Audio download completed.", receivedBytes, totalBytes));

        var title = Path.GetFileNameWithoutExtension(outputFilePath);
        return new AudioDownloadResult(
            outputFilePath,
            request.SourceUrl,
            title,
            SourceMetadata: new VideoSourceMetadata(InferWebsiteName(sourceUri)));
    }

    /// <summary>
    /// 生成安全的输出文件路径。
    /// Build a safe output file path for Windows file systems.
    /// </summary>
    private static string BuildOutputFilePath(AudioDownloadRequest request, Uri sourceUri)
    {
        var extension = Path.GetExtension(sourceUri.LocalPath);
        if (string.IsNullOrWhiteSpace(extension) || !KnownAudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            extension = ".audio";
        }

        var rawFileName = !string.IsNullOrWhiteSpace(request.PreferredFileName)
            ? request.PreferredFileName
            : Path.GetFileNameWithoutExtension(sourceUri.LocalPath);

        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            rawFileName = $"audio_{DateTimeOffset.Now:yyyyMMdd_HHmmss}";
        }

        var taskTime = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
        var safeFileName = SanitizeFileName($"{taskTime}_{rawFileName}");
        return BuildUniqueFilePath(request.OutputDirectory, safeFileName, extension);
    }

    /// <summary>
    /// 根据直接音频链接域名推断来源网站，供任务卡片在缺少平台元数据时显示。
    /// Infer the source website from a direct-audio URL host for the task card.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string InferWebsiteName(Uri sourceUri)
    {
        var host = sourceUri.Host.Trim().ToLowerInvariant();
        if (host.Length == 0)
        {
            return "直接音频链接";
        }

        if (host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase) || host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
        {
            return "B站";
        }

        if (host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("iesdouyin.com", StringComparison.OrdinalIgnoreCase))
        {
            return "抖音";
        }

        if (host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube";
        }

        if (host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            return "TikTok";
        }

        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;
    }

    /// <summary>
    /// 清理 Windows 文件名非法字符。
    /// Sanitize invalid Windows file-name characters.
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleanChars = fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        var cleanName = new string(cleanChars).Trim();

        return string.IsNullOrWhiteSpace(cleanName)
            ? $"audio_{DateTimeOffset.Now:yyyyMMdd_HHmmss}"
            : cleanName;
    }

    /// <summary>
    /// 生成不会覆盖已有文件的路径。
    /// Build a non-overwriting path for repeated downloads of the same URL.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string BuildUniqueFilePath(string directory, string fileNameWithoutExtension, string extension)
    {
        var candidatePath = Path.Combine(directory, $"{fileNameWithoutExtension}{extension}");
        var index = 1;

        while (File.Exists(candidatePath))
        {
            candidatePath = Path.Combine(directory, $"{fileNameWithoutExtension}_{index}{extension}");
            index++;
        }

        return candidatePath;
    }
}
