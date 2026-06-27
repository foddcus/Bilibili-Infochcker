using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;
using AudioText.Core.Utilities;

namespace AudioText.Download.Services;

/// <summary>
/// yt-dlp 外部网页视频音频提取服务。
/// External yt-dlp adapter that extracts downloadable audio from supported web video pages.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class YtDlpAudioDownloadService : IAudioDownloadService
{
    private const string ProgressPrefix = "GG_PROGRESS|";
    private const string FinalPathPrefix = "GG_FINAL_PATH|";
    private const string MetadataTitlePrefix = "GG_METADATA_TITLE|";
    private const string MetadataExtractorPrefix = "GG_METADATA_EXTRACTOR|";
    private const string MetadataPublisherPrefix = "GG_METADATA_PUBLISHER|";
    private const string MetadataViewCountPrefix = "GG_METADATA_VIEW_COUNT|";
    private const string DesktopBrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly string[] GenericCookieFileNames =
    [
        "yt-dlp.cookies.txt",
        "cookies.txt"
    ];

    private static readonly string[] BilibiliCookieFileNames =
    [
        "bilibili.cookies.txt",
        "bilibili_cookies.txt",
        "yt-dlp.cookies.txt",
        "cookies.txt"
    ];

    private static readonly Regex PercentRegex = new(
        @"(?<value>\d+(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] KnownAudioExtensions =
    [
        ".aac",
        ".flac",
        ".m4a",
        ".mka",
        ".mp3",
        ".ogg",
        ".opus",
        ".wav",
        ".webm"
    ];

    private static readonly HttpClient MetadataHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly string? _ytDlpPath;
    private readonly string _baseDirectory;

    /// <summary>
    /// 创建 yt-dlp 外部下载器适配器。
    /// Create an external yt-dlp downloader adapter.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    /// <param name="ytDlpPath">显式指定的 yt-dlp.exe 路径，可为空。Explicit yt-dlp.exe path, optional.</param>
    /// <param name="baseDirectory">软件运行目录，用于查找 tools 子目录。Runtime base directory used to probe the tools folder.</param>
    public YtDlpAudioDownloadService(string? ytDlpPath = null, string? baseDirectory = null)
    {
        _ytDlpPath = ytDlpPath;
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;
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
            throw new ArgumentException("网页视频链接不是有效的绝对 URL。The web video URL is not a valid absolute URL.", nameof(request));
        }

        if (sourceUri.Scheme is not ("http" or "https"))
        {
            throw new NotSupportedException("网页视频音频提取仅支持 http/https 链接。Only http/https URLs are supported for web video audio extraction.");
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var tempDirectory = Path.Combine(outputDirectory, "_临时下载");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(tempDirectory);

        var executablePath = ResolveYtDlpPath();
        var ffmpegLocation = ResolveFfmpegLocation();
        if (string.IsNullOrWhiteSpace(ffmpegLocation))
        {
            throw new FileNotFoundException("网页视频转写音频准备需要 ffmpeg.exe。请将 ffmpeg.exe 放到软件目录的 tools 子文件夹中。Preparing transcription-ready web audio requires ffmpeg.exe under the software tools folder.");
        }

        var cookieFilePath = ResolveCookieFilePath(sourceUri);
        var anonymousBilibiliFingerprint = string.IsNullOrWhiteSpace(cookieFilePath) && IsBilibiliUrl(sourceUri)
            ? BuildAnonymousBilibiliFingerprint()
            : null;
        var state = new DownloadProcessState(DateTimeOffset.UtcNow, cookieFilePath, anonymousBilibiliFingerprint is not null);
        var startInfo = BuildProcessStartInfo(executablePath, request, outputDirectory, tempDirectory, ffmpegLocation, cookieFilePath, sourceUri, anonymousBilibiliFingerprint);

        progress?.Report(new DownloadProgress(null, "正在启动 yt-dlp 音频提取与转写 WAV 准备。Starting yt-dlp audio extraction and transcription WAV preparation."));

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("yt-dlp 进程启动失败。Failed to start the yt-dlp process.");
        }

        var outputTask = ReadProcessStreamAsync(
            process.StandardOutput,
            line => HandleProcessLine(line, state, progress),
            cancellationToken);

        var errorTask = ReadProcessStreamAsync(
            process.StandardError,
            line => HandleProcessLine(line, state, progress),
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        await Task.WhenAll(outputTask, errorTask);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                BuildYtDlpFailureMessage(process.ExitCode, state, request.SourceUrl));
        }

        var finalPath = ResolveFinalOutputPath(state, outputDirectory);
        if (string.IsNullOrWhiteSpace(finalPath))
        {
            throw new FileNotFoundException("yt-dlp 已结束，但未能定位下载后的音频文件。yt-dlp finished, but the final audio file could not be located.");
        }

        progress?.Report(new DownloadProgress(100, "网页视频音频已转换为转写 WAV。Web video audio converted to transcription-ready WAV."));

        var pageMetadata = await TryFetchPageMetadataAsync(sourceUri, cancellationToken);
        var title = SelectMetadataText(
            state.MetadataTitle,
            pageMetadata?.Title,
            Path.GetFileNameWithoutExtension(finalPath),
            "网页视频") ?? "网页视频";
        var publisherName = SelectMetadataText(
            state.MetadataPublisherName,
            pageMetadata?.PublisherName,
            null,
            null);
        var viewCount = state.MetadataViewCount ?? pageMetadata?.ViewCount;
        var sourceMetadata = new VideoSourceMetadata(
            NormalizeWebsiteName(state.MetadataExtractorKey, sourceUri),
            publisherName,
            viewCount,
            title);

        return new AudioDownloadResult(
            finalPath,
            request.SourceUrl,
            title,
            SourceMetadata: sourceMetadata);
    }

    /// <summary>
    /// 构建 yt-dlp 进程启动参数。
    /// Build process arguments for a reproducible yt-dlp audio-only task.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static ProcessStartInfo BuildProcessStartInfo(
        string executablePath,
        AudioDownloadRequest request,
        string outputDirectory,
        string tempDirectory,
        string? ffmpegLocation,
        string? cookieFilePath,
        Uri sourceUri,
        string? anonymousBilibiliFingerprint)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var outputTemplate = BuildOutputTemplate(request);

        // 关键设计：
        // 1. --progress-template 输出 GG_PROGRESS 前缀，避免解析普通控制台文本。
        // 2. --print after_move 输出最终文件路径，避免后处理后扩展名变化导致找不到文件。
        // 3. -f bestaudio/best + --extract-audio 优先下载音频流，再由 ffmpeg 统一转为识别友好的 WAV。
        // 4. B 站等平台当前可能要求合法站点 cookies；仅在 tools 目录存在本地 cookies 文件时显式传入，不自动读取浏览器登录态。
        // 5. B 站 2026-06 playurl 接口会因缺少 buvid_fp 返回 HTTP 412；无本地 cookies 时传入一次性匿名指纹，不涉及账号登录态。
        // 6. 输出统一为 16 kHz、单声道、PCM s16le WAV，便于 whisper.cpp 等转写引擎直接读取。
        // 7. --print after_move 同步输出标题、平台、发布人和浏览量，用于主界面任务信息卡片。
        // 8. Windows 管道下 Python 可能按本地代码页输出中文；显式 UTF-8 + JSON 元数据可避免发布人乱码。
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.ArgumentList.Add("--ignore-config");
        startInfo.ArgumentList.Add("--no-simulate");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--encoding");
        startInfo.ArgumentList.Add("utf-8");
        startInfo.ArgumentList.Add("--newline");
        startInfo.ArgumentList.Add("--progress");
        startInfo.ArgumentList.Add("--windows-filenames");
        startInfo.ArgumentList.Add("--trim-filenames");
        startInfo.ArgumentList.Add("140");
        startInfo.ArgumentList.Add("--extractor-retries");
        startInfo.ArgumentList.Add("5");
        startInfo.ArgumentList.Add("--retry-sleep");
        startInfo.ArgumentList.Add("extractor:linear=1:3:1");
        AddHttpHeader(startInfo, "User-Agent", DesktopBrowserUserAgent);
        AddHttpHeader(startInfo, "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        if (IsBilibiliUrl(sourceUri))
        {
            AddHttpHeader(startInfo, "Referer", BuildBilibiliReferer(sourceUri));
            AddBilibiliPlayUrlHeaders(startInfo, anonymousBilibiliFingerprint);
        }

        if (!string.IsNullOrWhiteSpace(cookieFilePath))
        {
            startInfo.ArgumentList.Add("--cookies");
            startInfo.ArgumentList.Add(cookieFilePath);
        }

        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(outputDirectory);
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add($"temp:{tempDirectory}");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputTemplate);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("bestaudio/best");
        startInfo.ArgumentList.Add("--extract-audio");
        startInfo.ArgumentList.Add("--audio-format");
        startInfo.ArgumentList.Add("wav");
        startInfo.ArgumentList.Add("--postprocessor-args");
        startInfo.ArgumentList.Add("ExtractAudio+ffmpeg_o:-ar 16000 -ac 1 -c:a pcm_s16le");
        if (!string.IsNullOrWhiteSpace(ffmpegLocation))
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(ffmpegLocation);
        }

        startInfo.ArgumentList.Add("--progress-template");
        startInfo.ArgumentList.Add($"download:{ProgressPrefix}%(progress._percent_str)s");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add($"after_move:{FinalPathPrefix}%(filepath)j");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add($"after_move:{MetadataTitlePrefix}%(title)j");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add($"after_move:{MetadataExtractorPrefix}%(extractor_key)j");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add($"after_move:{MetadataPublisherPrefix}%(uploader)j");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add($"after_move:{MetadataViewCountPrefix}%(view_count)j");
        startInfo.ArgumentList.Add(request.SourceUrl);

        return startInfo;
    }

    /// <summary>
    /// 向 yt-dlp 添加 HTTP 请求头。
    /// Add one HTTP request header passed through yt-dlp.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static void AddHttpHeader(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add("--add-headers");
        startInfo.ArgumentList.Add($"{name}:{value}");
    }

    /// <summary>
    /// 添加 B 站 playurl 接口当前需要的浏览器同源请求头。
    /// Add browser same-site headers currently required by BiliBili playurl requests.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static void AddBilibiliPlayUrlHeaders(ProcessStartInfo startInfo, string? anonymousBilibiliFingerprint)
    {
        AddHttpHeader(startInfo, "Accept", "application/json, text/plain, */*");
        AddHttpHeader(startInfo, "Origin", "https://www.bilibili.com");
        AddHttpHeader(startInfo, "Sec-Fetch-Mode", "cors");
        AddHttpHeader(startInfo, "Sec-Fetch-Dest", "empty");
        AddHttpHeader(startInfo, "Sec-Fetch-Site", "same-site");

        if (!string.IsNullOrWhiteSpace(anonymousBilibiliFingerprint))
        {
            AddHttpHeader(startInfo, "Cookie", $"buvid_fp={anonymousBilibiliFingerprint}");
        }
    }

    /// <summary>
    /// 构建下载文件名模板。
    /// Build the yt-dlp output template under the chosen output directory.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string BuildOutputTemplate(AudioDownloadRequest request)
    {
        var taskTime = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(request.PreferredFileName))
        {
            return $"{taskTime}_%(title)s.%(ext)s";
        }

        var preferredStem = Path.GetFileNameWithoutExtension(SanitizeFileName(request.PreferredFileName));
        return $"{taskTime}_{EscapeOutputTemplateLiteral(preferredStem)}.%(ext)s";
    }

    /// <summary>
    /// 处理 yt-dlp 标准输出和错误输出中的一行文本。
    /// Handle one line from yt-dlp stdout or stderr and convert it into UI progress.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static void HandleProcessLine(
        string line,
        DownloadProcessState state,
        IProgress<DownloadProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        state.Lines.Enqueue(line);

        if (line.StartsWith(FinalPathPrefix, StringComparison.Ordinal))
        {
            var payload = line[FinalPathPrefix.Length..].Trim();
            state.FinalFilePath = TryReadJsonString(payload) ?? payload;
            return;
        }

        if (TryHandleMetadataLine(line, state))
        {
            return;
        }

        if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
        {
            var percentText = line[ProgressPrefix.Length..];
            var percent = ParsePercent(percentText);
            var message = percent.HasValue
                ? $"正在下载并准备转写 WAV。Downloading and preparing transcription WAV. {percent.Value:0.0}%"
                : "正在下载并准备转写 WAV。Downloading and preparing transcription WAV.";

            progress?.Report(new DownloadProgress(percent, message));
            return;
        }

        if (line.StartsWith("[ExtractAudio]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("[Merger]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("[MoveFiles]", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new DownloadProgress(null, $"正在转换为转写 WAV。Converting to transcription WAV. {line}"));
        }
    }

    /// <summary>
    /// 解析 yt-dlp 输出的任务卡片元数据行。
    /// Parse task-card metadata lines emitted by yt-dlp.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool TryHandleMetadataLine(string line, DownloadProcessState state)
    {
        if (line.StartsWith(MetadataTitlePrefix, StringComparison.Ordinal))
        {
            state.MetadataTitle = NormalizeMetadataText(line[MetadataTitlePrefix.Length..]);
            return true;
        }

        if (line.StartsWith(MetadataExtractorPrefix, StringComparison.Ordinal))
        {
            state.MetadataExtractorKey = NormalizeMetadataText(line[MetadataExtractorPrefix.Length..]);
            return true;
        }

        if (line.StartsWith(MetadataPublisherPrefix, StringComparison.Ordinal))
        {
            state.MetadataPublisherName = NormalizeMetadataText(line[MetadataPublisherPrefix.Length..]);
            return true;
        }

        if (line.StartsWith(MetadataViewCountPrefix, StringComparison.Ordinal))
        {
            state.MetadataViewCount = ParseViewCount(line[MetadataViewCountPrefix.Length..]);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 清理 yt-dlp 元数据模板中的空值占位。
    /// Clean empty placeholders from yt-dlp metadata templates.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? NormalizeMetadataText(string value)
    {
        var text = TryReadJsonScalarText(value.Trim()) ?? value.Trim();
        text = WebUtility.HtmlDecode(text).Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.Equals("NA", StringComparison.OrdinalIgnoreCase)
            || text.Equals("None", StringComparison.OrdinalIgnoreCase)
            || text.Equals("null", StringComparison.OrdinalIgnoreCase)
            || text.Equals("\"\"", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TextEncodingRepair.RepairOrNull(text);
    }

    /// <summary>
    /// 读取 yt-dlp JSON 元数据标量，避免中文发布人被转义或带引号显示。
    /// Read one scalar value from yt-dlp JSON metadata so non-ASCII uploader names are restored.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? TryReadJsonScalarText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            return root.ValueKind switch
            {
                JsonValueKind.String => root.GetString(),
                JsonValueKind.Number => root.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                _ => root.GetRawText()
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 yt-dlp 返回的浏览量字段。
    /// Parse the view-count field emitted by yt-dlp.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static long? ParseViewCount(string value)
    {
        var text = NormalizeMetadataText(value)?.Replace(",", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var compactViewCount = ParseCompactViewCount(text);
        if (compactViewCount.HasValue)
        {
            return compactViewCount;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integerValue)
            && integerValue >= 0)
        {
            return integerValue;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue)
            && floatValue >= 0
                ? (long)Math.Round(floatValue, MidpointRounding.AwayFromZero)
                : null;
    }

    /// <summary>
    /// 选择可信的元数据文本，自动跳过无法修复的乱码候选。
    /// Select trustworthy metadata text and skip unrecoverable mojibake candidates.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? SelectMetadataText(
        string? primaryValue,
        string? secondaryValue,
        string? tertiaryValue,
        string? fallbackValue)
    {
        foreach (var candidate in new[] { primaryValue, secondaryValue, tertiaryValue, fallbackValue })
        {
            var repairedCandidate = TextEncodingRepair.RepairOrNull(candidate);
            if (!string.IsNullOrWhiteSpace(repairedCandidate))
            {
                return CleanMetadataDisplayText(repairedCandidate);
            }
        }

        return null;
    }

    /// <summary>
    /// 读取网页 HTML 中的基础视频元数据，作为 yt-dlp 元数据乱码时的回退来源。
    /// Read basic video metadata from page HTML as a fallback when yt-dlp metadata is mojibake.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static async Task<PageMetadata?> TryFetchPageMetadataAsync(
        Uri sourceUri,
        CancellationToken cancellationToken)
    {
        if (sourceUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            httpRequest.Headers.UserAgent.ParseAdd(DesktopBrowserUserAgent);
            httpRequest.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
            if (IsBilibiliUrl(sourceUri))
            {
                httpRequest.Headers.Referrer = new Uri(BuildBilibiliReferer(sourceUri));
            }

            using var response = await MetadataHttpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var encoding = ResolveResponseEncoding(response.Content.Headers.ContentType?.CharSet);
            var html = encoding.GetString(bytes);

            return ParsePageMetadata(html);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// 根据响应头字符集选择 HTML 解码方式，默认使用 UTF-8。
    /// Resolve the HTML response encoding from the content-type charset; default to UTF-8.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static Encoding ResolveResponseEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim().Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// 从网页 HTML 中解析标题、发布人和播放量。
    /// Parse title, publisher name, and view count from page HTML.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static PageMetadata ParsePageMetadata(string html)
    {
        var title = SelectMetadataText(
            ReadMetaContent(html, "name", "title"),
            ReadMetaContent(html, "property", "og:title"),
            ReadHtmlTitle(html),
            null);
        var publisherName = SelectMetadataText(
            ReadMetaContent(html, "name", "author"),
            ReadJsonLdAuthorName(html),
            null,
            null);
        var viewCount = ReadJsonLdWatchCount(html)
            ?? ParseViewCountFromDescription(ReadMetaContent(html, "name", "description"));

        return new PageMetadata(title, publisherName, viewCount);
    }

    /// <summary>
    /// 从 meta 标签读取 content 字段。
    /// Read the content field from a matching meta tag.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? ReadMetaContent(string html, string attributeName, string attributeValue)
    {
        foreach (Match match in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var tag = match.Value;
            if (!AttributeEquals(tag, attributeName, attributeValue)
                || !TryReadAttribute(tag, "content", out var content))
            {
                continue;
            }

            return WebUtility.HtmlDecode(content);
        }

        return null;
    }

    /// <summary>
    /// 从 HTML title 标签读取标题。
    /// Read the HTML title tag.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? ReadHtmlTitle(string html)
    {
        var match = Regex.Match(
            html,
            @"<title[^>]*>(?<title>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["title"].Value)
            : null;
    }

    /// <summary>
    /// 判断标签属性是否等于目标值。
    /// Check whether an HTML tag attribute equals the target value.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool AttributeEquals(string tag, string attributeName, string attributeValue)
    {
        return TryReadAttribute(tag, attributeName, out var value)
            && value.Equals(attributeValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从单个 HTML 标签读取属性值。
    /// Read one attribute value from an HTML tag.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool TryReadAttribute(string tag, string attributeName, out string value)
    {
        value = string.Empty;
        var match = Regex.Match(
            tag,
            $@"\b{Regex.Escape(attributeName)}\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        value = WebUtility.HtmlDecode(match.Groups["value"].Value);
        return true;
    }

    /// <summary>
    /// 从 JSON-LD 中读取作者名称。
    /// Read the author name from JSON-LD.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? ReadJsonLdAuthorName(string html)
    {
        foreach (var root in EnumerateJsonLdRoots(html))
        {
            if (!root.TryGetProperty("author", out var author))
            {
                continue;
            }

            if (author.ValueKind == JsonValueKind.Object
                && author.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }

            if (author.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in author.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("name", out var itemName)
                        && itemName.ValueKind == JsonValueKind.String)
                    {
                        return itemName.GetString();
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 从 JSON-LD 中读取观看次数。
    /// Read the watch count from JSON-LD.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static long? ReadJsonLdWatchCount(string html)
    {
        foreach (var root in EnumerateJsonLdRoots(html))
        {
            if (!root.TryGetProperty("interactionStatistic", out var statistics)
                || statistics.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var statistic in statistics.EnumerateArray())
            {
                var interactionTypeName = statistic.TryGetProperty("interactionType", out var interactionType)
                    && interactionType.ValueKind == JsonValueKind.Object
                    && interactionType.TryGetProperty("@type", out var type)
                        ? type.GetString()
                        : null;
                if (!string.Equals(interactionTypeName, "WatchAction", StringComparison.OrdinalIgnoreCase)
                    || !statistic.TryGetProperty("userInteractionCount", out var count))
                {
                    continue;
                }

                if (count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var numericCount))
                {
                    return numericCount;
                }

                if (count.ValueKind == JsonValueKind.String)
                {
                    return ParseViewCount(count.GetString() ?? string.Empty);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 枚举 HTML 中的 JSON-LD 根对象。
    /// Enumerate JSON-LD root objects embedded in the HTML.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IEnumerable<JsonElement> EnumerateJsonLdRoots(string html)
    {
        foreach (Match match in Regex.Matches(
                     html,
                     @"<script[^>]+type\s*=\s*[""']application/ld\+json[""'][^>]*>(?<json>.*?)</script>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            var json = WebUtility.HtmlDecode(match.Groups["json"].Value).Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                yield return document.RootElement.Clone();
            }
        }
    }

    /// <summary>
    /// 从页面描述中解析播放量文本。
    /// Parse view count text from a page description.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static long? ParseViewCountFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = Regex.Match(
            description,
            @"视频播放量\s*(?<value>[\d,.]+(?:\s*[万億亿])?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? ParseViewCount(match.Groups["value"].Value)
            : null;
    }

    /// <summary>
    /// 解析带中文单位的播放量。
    /// Parse compact view counts with Chinese units.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static long? ParseCompactViewCount(string text)
    {
        var normalizedText = text.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        var multiplier = 1.0;
        if (normalizedText.EndsWith("万", StringComparison.Ordinal))
        {
            multiplier = 10_000;
            normalizedText = normalizedText[..^1].Trim();
        }
        else if (normalizedText.EndsWith("亿", StringComparison.Ordinal) || normalizedText.EndsWith("億", StringComparison.Ordinal))
        {
            multiplier = 100_000_000;
            normalizedText = normalizedText[..^1].Trim();
        }
        else
        {
            return null;
        }

        return double.TryParse(normalizedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value >= 0
                ? (long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero)
                : null;
    }

    /// <summary>
    /// 清理元数据展示文本中的平台后缀。
    /// Clean platform suffixes from metadata display text.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string CleanMetadataDisplayText(string value)
    {
        var text = value.Trim();
        foreach (var suffix in new[] { "_哔哩哔哩_bilibili", " - YouTube" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^suffix.Length].Trim();
            }
        }

        return text;
    }

    /// <summary>
    /// 将 yt-dlp 平台标识和来源域名规范为用户可读的网站名称。
    /// Normalize yt-dlp extractor keys and source hosts into user-facing website names.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string NormalizeWebsiteName(string? extractorKey, Uri sourceUri)
    {
        var key = (extractorKey ?? string.Empty)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
        var host = sourceUri.Host.Trim().ToLowerInvariant();

        if (key.Contains("bilibili", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
        {
            return "B站";
        }

        if (key.Contains("douyin", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("douyin.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("iesdouyin.com", StringComparison.OrdinalIgnoreCase))
        {
            return "抖音";
        }

        if (key.Contains("youtube", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            return "YouTube";
        }

        if (key.Contains("tiktok", StringComparison.OrdinalIgnoreCase) || host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            return "TikTok";
        }

        if (key.Contains("kuaishou", StringComparison.OrdinalIgnoreCase) || host.EndsWith("kuaishou.com", StringComparison.OrdinalIgnoreCase))
        {
            return "快手";
        }

        if (key.Contains("ixigua", StringComparison.OrdinalIgnoreCase)
            || key.Contains("xigua", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("ixigua.com", StringComparison.OrdinalIgnoreCase))
        {
            return "西瓜视频";
        }

        if (key.Contains("weibo", StringComparison.OrdinalIgnoreCase) || host.EndsWith("weibo.com", StringComparison.OrdinalIgnoreCase))
        {
            return "微博";
        }

        if (key.Contains("twitter", StringComparison.OrdinalIgnoreCase) || host.Equals("x.com", StringComparison.OrdinalIgnoreCase))
        {
            return "X/Twitter";
        }

        if (key.Contains("instagram", StringComparison.OrdinalIgnoreCase) || host.EndsWith("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Instagram";
        }

        if (key.Contains("facebook", StringComparison.OrdinalIgnoreCase) || host.EndsWith("facebook.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Facebook";
        }

        if (key.Contains("vimeo", StringComparison.OrdinalIgnoreCase) || host.EndsWith("vimeo.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Vimeo";
        }

        if (key.Contains("twitch", StringComparison.OrdinalIgnoreCase) || host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            return "Twitch";
        }

        if (!string.IsNullOrWhiteSpace(extractorKey))
        {
            return extractorKey.Trim();
        }

        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : string.IsNullOrWhiteSpace(host) ? "网页视频" : host;
    }

    /// <summary>
    /// 读取进程输出流。
    /// Read process output stream line by line.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static async Task ReadProcessStreamAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            onLine(line);
        }
    }

    /// <summary>
    /// 解析 yt-dlp 输出的百分比文本。
    /// Parse the percent text emitted by yt-dlp progress-template.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static double? ParsePercent(string text)
    {
        var match = PercentRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var percent)
            ? Math.Clamp(percent, 0, 100)
            : null;
    }

    /// <summary>
    /// 读取 yt-dlp 的 JSON 字符串输出。
    /// Read a JSON string emitted by yt-dlp output templates.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string? TryReadJsonString(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 根据进程输出或输出目录定位最终文件。
    /// Resolve the final audio file from the after_move marker or by scanning recent audio files.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string? ResolveFinalOutputPath(DownloadProcessState state, string outputDirectory)
    {
        if (!string.IsNullOrWhiteSpace(state.FinalFilePath) && File.Exists(state.FinalFilePath))
        {
            return state.FinalFilePath;
        }

        return Directory
            .EnumerateFiles(outputDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => KnownAudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => File.GetLastWriteTimeUtc(path) >= state.StartedAtUtc.UtcDateTime.AddSeconds(-5))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// 查找 yt-dlp 可执行文件。
    /// Resolve yt-dlp from explicit path, software tools folder, software directory, or PATH.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private string ResolveYtDlpPath()
    {
        if (!string.IsNullOrWhiteSpace(_ytDlpPath) && File.Exists(_ytDlpPath))
        {
            return _ytDlpPath;
        }

        var candidatePaths = new[]
        {
            Path.Combine(_baseDirectory, "tools", "yt-dlp.exe"),
            Path.Combine(_baseDirectory, "yt-dlp.exe")
        };

        foreach (var candidatePath in candidatePaths)
        {
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        var pathExecutable = FindExecutableOnPath("yt-dlp.exe");
        if (!string.IsNullOrWhiteSpace(pathExecutable))
        {
            return pathExecutable;
        }

        throw new FileNotFoundException(
            "未找到 yt-dlp.exe。请将 yt-dlp.exe 放到软件目录的 tools 子文件夹中，或加入系统 PATH。yt-dlp.exe was not found. Put it under the software tools folder or add it to PATH.");
    }

    /// <summary>
    /// 查找随软件放置的 ffmpeg 工具目录。
    /// Resolve a bundled ffmpeg tools directory when ffmpeg.exe exists under the software tools folder.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private string? ResolveFfmpegLocation()
    {
        var toolsDirectory = Path.Combine(_baseDirectory, "tools");
        var ffmpegPath = Path.Combine(toolsDirectory, "ffmpeg.exe");

        return File.Exists(ffmpegPath)
            ? toolsDirectory
            : null;
    }

    /// <summary>
    /// 查找用户显式放置的 yt-dlp cookies 文件。
    /// Resolve a user-provided Netscape cookies file for yt-dlp without reading browser profiles automatically.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private string? ResolveCookieFilePath(Uri sourceUri)
    {
        var cookieFileNames = IsBilibiliUrl(sourceUri)
            ? BilibiliCookieFileNames
            : GenericCookieFileNames;

        var candidateDirectories = new[]
        {
            Path.Combine(_baseDirectory, "tools"),
            _baseDirectory
        };

        foreach (var candidateDirectory in candidateDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var cookieFileName in cookieFileNames)
            {
                var candidatePath = Path.Combine(candidateDirectory, cookieFileName);
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 构建 yt-dlp 失败说明，并对 B 站 HTTP 412 给出可执行处理建议。
    /// Build a failure message and add actionable guidance for BiliBili HTTP 412 errors.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private string BuildYtDlpFailureMessage(int exitCode, DownloadProcessState state, string sourceUrl)
    {
        var recentOutput = string.Join(Environment.NewLine, state.Lines.TakeLast(10));
        var messageBuilder = new StringBuilder()
            .AppendLine($"yt-dlp 音频提取失败，退出码：{exitCode}。");

        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri)
            && IsBilibiliUrl(sourceUri)
            && HasHttpPreconditionFailed(state))
        {
            messageBuilder.AppendLine("B 站 playurl 元数据接口返回 HTTP 412。当前公开视频也可能要求携带浏览器中的合法站点 cookies。");

            if (state.UsedAnonymousBilibiliFingerprint)
            {
                messageBuilder.AppendLine("本次已尝试自动生成匿名 buvid_fp 指纹；若仍失败，可能是 IP 临时风控、浏览器会话 cookies 过期，或该视频需要登录态。");
            }

            if (string.IsNullOrWhiteSpace(state.CookieFilePath))
            {
                var expectedCookiePath = Path.Combine(_baseDirectory, "tools", "bilibili.cookies.txt");
                messageBuilder.AppendLine($"请将从浏览器导出的 Netscape cookies 文件保存为：{expectedCookiePath}");
                messageBuilder.AppendLine("也可命名为 bilibili_cookies.txt、yt-dlp.cookies.txt 或 cookies.txt。程序只读取这些本地文件，不会自动读取浏览器登录态。");
            }
            else
            {
                messageBuilder.AppendLine($"本次已使用 cookies 文件：{state.CookieFilePath}");
                messageBuilder.AppendLine("请确认 cookies 未过期，并且来自可正常打开该视频的 bilibili.com 浏览器会话。");
            }
        }

        messageBuilder.Append(recentOutput);
        return messageBuilder.ToString();
    }

    /// <summary>
    /// 判断 yt-dlp 输出中是否包含 HTTP 412 预条件失败。
    /// Detect whether yt-dlp output contains the HTTP 412 precondition failure.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static bool HasHttpPreconditionFailed(DownloadProcessState state)
    {
        return state.Lines.Any(line =>
            line.Contains("HTTP Error 412", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Precondition Failed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断链接是否属于 B 站域名。
    /// Detect BiliBili and short BiliBili URLs.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static bool IsBilibiliUrl(Uri sourceUri)
    {
        return sourceUri.Host.Equals("bilibili.com", StringComparison.OrdinalIgnoreCase)
            || sourceUri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
            || sourceUri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构建 B 站请求 Referer，帮助 CDN 和接口确认来源页面。
    /// Build a BiliBili referer from the source URL.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string BuildBilibiliReferer(Uri sourceUri)
    {
        if (sourceUri.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(sourceUri.AbsolutePath))
        {
            return $"https://{sourceUri.Host}{sourceUri.AbsolutePath}";
        }

        return "https://www.bilibili.com/";
    }

    /// <summary>
    /// 生成一次性匿名 buvid_fp 指纹，用于满足 B 站公开视频 playurl 接口的基础指纹检查。
    /// Generate one anonymous buvid_fp value for BiliBili public-video playurl requests.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string BuildAnonymousBilibiliFingerprint()
    {
        var hash = MD5.HashData(Guid.NewGuid().ToByteArray());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 在 PATH 环境变量中查找可执行文件。
    /// Find an executable from the PATH environment variable.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string? FindExecutableOnPath(string executableName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        foreach (var rawPath in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawPath.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidatePath = Path.Combine(directory, executableName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    /// <summary>
    /// 清理 Windows 文件名非法字符。
    /// Sanitize invalid Windows file-name characters.
    /// 最近修改时间：2026-06-23；修改人：GG。
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
    /// 转义 yt-dlp 输出模板中的百分号。
    /// Escape percent literals used by yt-dlp output templates.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string EscapeOutputTemplateLiteral(string value)
    {
        return value.Replace("%", "%%", StringComparison.Ordinal);
    }

    /// <summary>
    /// 取消下载时结束外部进程树。
    /// Kill the external process tree when a download is cancelled.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// yt-dlp 进程输出状态。
    /// State collected from yt-dlp process output.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class DownloadProcessState(DateTimeOffset startedAtUtc, string? cookieFilePath, bool usedAnonymousBilibiliFingerprint)
    {
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;

        public string? CookieFilePath { get; } = cookieFilePath;

        public bool UsedAnonymousBilibiliFingerprint { get; } = usedAnonymousBilibiliFingerprint;

        public string? FinalFilePath { get; set; }

        public string? MetadataTitle { get; set; }

        public string? MetadataExtractorKey { get; set; }

        public string? MetadataPublisherName { get; set; }

        public long? MetadataViewCount { get; set; }

        public ConcurrentQueue<string> Lines { get; } = new();
    }

    private sealed record PageMetadata(string? Title, string? PublisherName, long? ViewCount);
}
