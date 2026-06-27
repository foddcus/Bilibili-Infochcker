using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using IOPath = System.IO.Path;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;
using AudioText.Core.Utilities;
using AudioText.Download.Services;
using AudioText.Infrastructure.Services;
using AudioText.Transcription.Services;
using AudioText.Verification.Services;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;

namespace AudioText.App;

/// <summary>
/// 主窗口交互逻辑。
/// Main window interaction logic.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions AiReportJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly string[] DirectAudioExtensions =
    [
        ".aac",
        ".flac",
        ".m4a",
        ".mp3",
        ".ogg",
        ".opus",
        ".wav"
    ];

    private const double AiEvaluationDisplayFontSize = 15.0;
    private const double AiEvaluationDisplayLineHeight = 22.5;
    private const double AiEvaluationTitleFontSize = 22.5;
    private const string AiEvaluationAttributionText = "基于视频嚼真机开源项目：https://github.com/foddcus/Bilibili-Infochcker";

    private readonly AppDirectories _directories;
    private readonly IAudioDownloadService _directAudioDownloadService;
    private readonly IAudioDownloadService _ytDlpAudioDownloadService;
    private readonly ITranscriptionService _transcriptionService;
    private readonly VideoAnalysisMemoryStore _memoryStore;
    private AudioDownloadResult? _lastDownloadResult;
    private TranscriptionResult? _lastTranscriptionResult;
    private AiVerificationSettings _aiSettings = AiVerificationSettings.CreateDefault();

    /// <summary>
    /// 初始化基础界面，并创建默认输出目录。
    /// Initialize the base UI and create default output directories.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        _directories = AppDirectoryService.EnsureDefaultDirectories(AppContext.BaseDirectory);
        _directAudioDownloadService = new DirectAudioLinkDownloadService();
        _ytDlpAudioDownloadService = new YtDlpAudioDownloadService(baseDirectory: _directories.BaseDirectory);
        _transcriptionService = new WhisperCppTranscriptionService(
            _directories.BaseDirectory,
            _directories.TranscriptDirectory);
        _memoryStore = new VideoAnalysisMemoryStore(_directories.MemoryDatabaseDirectory);

        OutputTextBox.Text = BuildStartupMessage();
        TranscriptPreviewTextBox.Text = "尚未开始转写。";
        SetAiEvaluationPlainText("尚未进行 AI 联网核查；转写完成后将自动执行。");
        ResetAiEvaluationVisuals();
        UpdateAiSettingsSummary();
        StatusTextBlock.Text = "框架初始化完成。";
    }

    /// <summary>
    /// 创建网页音频任务按钮事件。
    /// Handle creating a web/direct-audio transcription task.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private async void OnCreateTaskClick(object sender, RoutedEventArgs e)
    {
        var sourceUrl = UrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            AppendLine("下载链接为空，请先输入网页视频或直接音频链接。");
            StatusTextBlock.Text = "等待有效下载链接。";
            return;
        }

        SetDownloadBusyState(isBusy: true);
        ResetDownloadProgress();
        TranscriptPreviewTextBox.Text = "等待下载完成后自动转写。";
        SetAiEvaluationPlainText("等待转写完成后，将自动执行 AI 联网核查。");
        ResetAiEvaluationVisuals();
        _lastDownloadResult = null;
        _lastTranscriptionResult = null;
        UpdateVideoInfoCard(InferVideoSourceMetadata(sourceUrl), isLoading: true);

        try
        {
            var cachedEntry = await TryFindCachedTranscriptAsync(sourceUrl);
            if (cachedEntry is not null)
            {
                await UseCachedTranscriptAsync(sourceUrl, cachedEntry);
                return;
            }

            var isDirectAudioUrl = LooksLikeDirectAudioUrl(sourceUrl);
            var sourceKind = isDirectAudioUrl
                ? AudioSourceKind.DirectAudioUrl
                : AudioSourceKind.WebPageUrl;

            var downloadService = isDirectAudioUrl
                ? _directAudioDownloadService
                : _ytDlpAudioDownloadService;

            var request = new AudioDownloadRequest(
                sourceUrl,
                _directories.DownloadedAudioDirectory,
                SourceKind: sourceKind);

            AppendLine($"开始下载：{sourceUrl}");
            AppendLine($"保存目录：{_directories.DownloadedAudioDirectory}");
            AppendLine(isDirectAudioUrl
                ? "检测到直接音频链接，使用内置下载器。"
                : "检测到网页视频链接，使用 yt-dlp 外部适配器提取音频。");

            var progress = new Progress<DownloadProgress>(UpdateDownloadProgress);
            var result = await downloadService.DownloadAsync(request, progress, CancellationToken.None);

            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            DownloadProgressTextBlock.Text = "100.0%";
            StatusTextBlock.Text = "下载完成。";

            AppendLine($"下载完成：{result.LocalFilePath}");
            _lastDownloadResult = result;
            UpdateVideoInfoCard(EnsureVideoSourceMetadata(result), isLoading: false);
            await TranscribeDownloadedAudioAsync(result);
        }
        catch (Exception ex)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            DownloadProgressTextBlock.Text = "下载失败";
            StatusTextBlock.Text = "下载失败。";
            TranscriptPreviewTextBox.Text = "下载失败，未开始转写。";
            UpdateVideoInfoCard(InferVideoSourceMetadata(sourceUrl), isLoading: false);
            AppendLine($"下载失败：{ex.Message}");
        }
        finally
        {
            SetDownloadBusyState(isBusy: false);
        }
    }

    /// <summary>
    /// 打开 AI 设置页面，并把保存后的模型、API 与搜索设置同步到主窗口运行态。
    /// Open the AI settings page and sync saved model, API and search settings back to the main-window runtime state.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new AiSettingsWindow(_aiSettings)
        {
            Owner = this
        };

        if (settingsWindow.ShowDialog() != true)
        {
            return;
        }

        _aiSettings = settingsWindow.Settings;
        UpdateAiSettingsSummary();
        AppendLine($"AI settings updated: {_aiSettings}");
        StatusTextBlock.Text = "AI settings updated.";
    }

    /// <summary>
    /// AI 联网评价按钮事件，用于按当前设置重新核查已生成的转写文本。
    /// Handle AI web verification action for rechecking the current transcript with current settings.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private async void OnEvaluateTranscriptClick(object sender, RoutedEventArgs e)
    {
        await RunAiEvaluationForCurrentTranscriptAsync(isAutomatic: false);
    }

    /// <summary>
    /// 对当前文字预览执行 AI 联网核查，可由转写流程自动触发，也可由按钮手动重跑。
    /// Run AI web verification for the current transcript preview, either automatically after transcription or manually from the button.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task RunAiEvaluationForCurrentTranscriptAsync(bool isAutomatic)
    {
        var transcriptText = TranscriptPreviewTextBox.Text.Trim();
        if (!HasEvaluableTranscript(transcriptText))
        {
            if (isAutomatic)
            {
                AppendLine("自动 AI 核查已跳过：当前没有有效的视频转写文本。");
            }
            else
            {
                AppendLine("AI 核查需要先获得有效的视频转写文本。");
            }

            StatusTextBlock.Text = "等待有效转写文本。";
            return;
        }

        if (string.IsNullOrWhiteSpace(_aiSettings.ApiKey))
        {
            var message = "AI 联网核查已跳过：未配置 DeepSeek API Key，请在“设置”页面填写自己的 Key 后再重试。";
            SetAiEvaluationPlainText(message);
            ResetAiEvaluationVisuals("未配置");
            AppendLine(message);
            StatusTextBlock.Text = "等待配置 DeepSeek API Key。";
            return;
        }

        SetEvaluationBusyState(isBusy: true);
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;
        DownloadProgressTextBlock.Text = isAutomatic ? "自动核查" : "AI核查";
        SetAiEvaluationPlainText(
            isAutomatic
                ? "转写完成，正在自动进行 AI 联网核查，请等待..."
                : "正在重新进行 AI 联网核查，请等待...",
            includeAttribution: true);
        ResetAiEvaluationVisuals("评分中");

        try
        {
            var settings = BuildAiVerificationSettings();
            var webSearchService = WebSearchServiceFactory.Create(settings);
            IAiVideoEvaluationService evaluationService = new DeepSeekVideoEvaluationService(
                settings,
                webSearchService);
            var evaluationTitle = BuildSafeTaskTitle(
                _lastTranscriptionResult?.Document.Title,
                _lastDownloadResult?.Title,
                "视频转写文本");
            var sourceUrl = _lastDownloadResult?.SourceUrl ?? UrlTextBox.Text.Trim();
            var currentMetadata = BuildCurrentVideoSourceMetadata();
            var memoryReferences = await LoadMemoryReferencesAsync(currentMetadata, sourceUrl);

            var request = new AiVideoEvaluationRequest(
                TranscriptText: transcriptText,
                Title: evaluationTitle,
                SourceUrl: sourceUrl,
                CreatedAt: DateTimeOffset.Now,
                MaxSearchRounds: settings.MaxSearchRounds,
                MemoryReferences: memoryReferences);

            AppendLine(isAutomatic
                ? $"转写完成，自动开始 AI 联网核查：模型 {settings.Model}"
                : $"开始重新 AI 联网核查：模型 {settings.Model}");
            AppendLine(string.IsNullOrWhiteSpace(settings.SearxngEndpoint)
                ? "搜索源：优先使用 Google Search，失败时退回 Bing Search / Baidu Search / DuckDuckGo Lite。"
                : "搜索源：优先使用 SearXNG 国际引擎组合，失败时退回 Google Search / Bing Search / Baidu Search / DuckDuckGo Lite。");
            AppendLine($"查验力度：{settings.VerificationIntensityLabel} - {settings.VerificationIntensityDescription}；参考证据上限：{AiVerificationSettings.FormatOptionalLimit(settings.MaxEvidenceItems)}。");
            AppendLine(BuildBlockedEvidenceSourceLogLine(settings));

            if (!string.IsNullOrWhiteSpace(settings.BochaWebSearchApiKey))
            {
                AppendLine("Search source: Bocha Web Search API is enabled before the existing fallback chain.");
            }

            var progress = new Progress<AiVideoEvaluationProgress>(UpdateAiEvaluationProgress);
            var result = await evaluationService.EvaluateAsync(request, progress, CancellationToken.None);
            var reportPath = await SaveAiEvaluationReportAsync(result, request.Title);
            await RecordAiEvaluationMemoryAsync(result, reportPath, request.Title, sourceUrl, currentMetadata);

            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            DownloadProgressTextBlock.Text = "核查完成";
            StatusTextBlock.Text = "AI 联网核查完成。";
            UpdateAiEvaluationVisuals(result);
            SetAiEvaluationResultDocument(result, reportPath);
            AppendLine($"AI 联网核查完成：{reportPath}");
        }
        catch (Exception ex)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            DownloadProgressTextBlock.Text = "核查失败";
            StatusTextBlock.Text = "AI 联网核查失败。";
            SetAiEvaluationPlainText($"AI 联网核查失败：{ex.Message}", includeAttribution: true);
            ResetAiEvaluationVisuals("核查失败");
            AppendLine($"AI 联网核查失败：{ex.Message}");
        }
        finally
        {
            SetEvaluationBusyState(isBusy: false);
        }
    }

    /// <summary>
    /// 绯荤粺澹伴煶鐩戝惉鎸夐挳浜嬩欢銆?
    /// Handle system loopback capture action.
    /// </summary>
    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        AppendLine("系统声音监听接口已保留，后续将接入 WASAPI Loopback。");
        StatusTextBlock.Text = "系统声音采集接口占位完成。";
    }

    /// <summary>
    /// 鏄剧ず杈撳嚭鐩綍鎸夐挳浜嬩欢銆?
    /// Handle showing runtime output directories.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void OnShowDirectoriesClick(object sender, RoutedEventArgs e)
    {
        AppendLine($"运行目录：{_directories.BaseDirectory}");
        AppendLine($"下载音频：{_directories.DownloadedAudioDirectory}");
        AppendLine($"输出文本：{_directories.TranscriptDirectory}");
        AppendLine($"AI评价：{IOPath.Combine(_directories.TranscriptDirectory, "AI评价")}");
        AppendLine($"记忆数据库：{_directories.MemoryDatabaseDirectory}");
        AppendLine($"日志：{_directories.LogDirectory}");
        AppendLine($"临时文件：{_directories.TempDirectory}");
        AppendLine($"外部工具：{_directories.ToolsDirectory}");
        StatusTextBlock.Text = "已输出目录信息。";
    }

    /// <summary>
    /// 鏋勫缓鍚姩璇存槑銆?
    /// Build startup message.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string BuildStartupMessage()
    {
        return string.Join(
            Environment.NewLine,
            "视频嚼真机 V1.1 已启动。",
            "designed by foddcus 快怿",
            "",
            "已完成：",
            "1. AudioText.sln 传统解决方案入口。",
            "2. App/Core/Capture/Download/Transcription/Export/Infrastructure 模块拆分。",
            "3. 下载、采集、识别、导出核心接口。",
            "4. 默认输出目录创建。",
            "5. 直接音频链接下载和网页视频 yt-dlp 音频提取。",
            "6. 下载完成后自动调用 whisper.cpp 本地语音转文字，并显示简体中文文本和转写进度。",
            "7. 本地转写完成后先用独立 AI 会话断句并保守修正明显错别字，再把断句/纠错稿写回预览框。",
            "8. 断句/纠错完成后自动调用 DeepSeek API 进行 AI 联网核查，输出真实性、时效性、信息专业性、娱乐性、情绪引导嫌疑评分，并为关键主张标注客观属实/基本属实/有失偏颇/煽风点火/胡言乱语五级评价。",
            "9. 创建网页视频任务后显示当前视频网站、发布人和浏览量信息卡片；平台未返回字段时显示未知。",
            "10. 记忆数据库会保存每次任务的转写和 AI 结果；重复视频会直接复用已转写文本，同发布人往期视频会作为 AI 核查背景参考。",
            "11. 分析流程结束后自动删除下载音频和临时视频文件，降低软件目录空间占用。",
            "",
            "提示：网页视频提取需要在软件目录 tools 子文件夹中放置 yt-dlp.exe，必要时同目录配置 ffmpeg。",
            "网页视频音频会统一输出为 16 kHz、单声道、PCM WAV，便于后续语音转文字模块读取。",
            "语音转文字需要 tools/whisper/Release/whisper-cli.exe；当前已推荐使用 models/ggml-large-v3-turbo-q8_0.bin 强模型；若 whisper.cpp 工具包带 GPU 后端，会优先让其使用 GPU。",
            "B 站公开视频会自动补充匿名 buvid_fp 指纹；若仍遇到 HTTP 412，可将浏览器导出的 Netscape cookies 文件保存为 tools/bilibili.cookies.txt 后重试。",
            "AI 联网核查默认使用 DeepSeek API 和 deepseek-v4-flash；当前私有目录已恢复内置默认 DeepSeek Key 和 Bocha Web Search Key，也可在设置页临时覆盖；结构化 JSON 请求会关闭 V4 默认 thinking 模式；SearXNG 站点可选，未填写时使用 Google Search -> Bing Search -> Baidu Search -> DuckDuckGo Lite 国际搜索退路；设置页可选择普通/细节/苛刻查验力度并勾选屏蔽快手、抖音、小红书、B 站和知乎参考源；“重新AI核查”按钮用于失败后或修改设置后重跑。");
    }

    /// <summary>
    /// 涓嬭浇瀹屾垚鍚庤嚜鍔ㄨ皟鐢ㄦ湰鍦拌闊宠瘑鍒紝骞跺皢缁撴灉鏄剧ず鍒版柇鍙?绾犻敊绋块瑙堝尯銆?
    /// Automatically run local transcription after download and show the result in the punctuated/corrected transcript preview area.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private async Task TranscribeDownloadedAudioAsync(AudioDownloadResult downloadResult)
    {
        ArgumentNullException.ThrowIfNull(downloadResult);

        try
        {
            AppendLine("开始语音转文字。");
            TranscriptPreviewTextBox.Text = "正在进行本地语音转文字，请等待...";
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressTextBlock.Text = "转写中";
            StatusTextBlock.Text = "下载完成，正在转写。";

            var request = new TranscriptionRequest(
                InputAudioPath: downloadResult.LocalFilePath,
                ModelPath: string.Empty,
                Title: downloadResult.Title,
                Language: "zh",
                SourceUrl: downloadResult.SourceUrl);

            var transcriptionProgress = new Progress<TranscriptionProgress>(UpdateTranscriptionProgress);
            var transcriptionResult = await _transcriptionService.TranscribeAsync(
                request,
                transcriptionProgress,
                CancellationToken.None);
            _lastTranscriptionResult = transcriptionResult;

            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            DownloadProgressTextBlock.Text = "转写完成";
            StatusTextBlock.Text = "下载与转写完成。";

            TranscriptPreviewTextBox.Text = string.IsNullOrWhiteSpace(transcriptionResult.Document.PlainText)
                ? "未识别到有效语音文本。"
                : transcriptionResult.Document.PlainText;

            AppendLine($"Transcription completed: {transcriptionResult.RawOutputPath ?? "raw transcript file not generated"}");
            transcriptionResult = await PunctuateTranscriptAsync(transcriptionResult, downloadResult);
            _lastTranscriptionResult = transcriptionResult;
            await RecordTranscriptMemoryAsync(downloadResult, transcriptionResult);
            SetAiEvaluationPlainText("断句/纠错完成，正在准备自动 AI 联网核查。");
            await RunAiEvaluationForCurrentTranscriptAsync(isAutomatic: true);
        }
        catch (Exception ex)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            DownloadProgressTextBlock.Text = "转写失败";
            StatusTextBlock.Text = "下载完成，但转写失败。";
            TranscriptPreviewTextBox.Text = $"转写失败：{ex.Message}";
            AppendLine($"转写失败：{ex.Message}");
        }
        finally
        {
            CleanupDownloadedMediaFiles(downloadResult);
        }
    }

    /// <summary>
    /// 璋冪敤鐙珛 AI 浼氳瘽涓哄師濮嬭浆鍐欐枃鏈柇鍙ュ苟淇濆畧绾犻敊锛屽皢鏂彞/绾犻敊绋垮啓鍥為瑙堟銆?
    /// Use a separate AI session to punctuate and conservatively correct the raw transcript, then write it back to the preview box.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private async Task<TranscriptionResult> PunctuateTranscriptAsync(
        TranscriptionResult transcriptionResult,
        AudioDownloadResult downloadResult)
    {
        var rawText = transcriptionResult.Document.PlainText.Trim();
        if (!HasEvaluableTranscript(rawText))
        {
            AppendLine("AI 断句/纠错已跳过：当前没有有效转写文本。");
            return transcriptionResult;
        }

        if (string.IsNullOrWhiteSpace(_aiSettings.ApiKey))
        {
            AppendLine("AI 断句/纠错已跳过：未配置 DeepSeek API Key，继续使用原始转写文本。");
            return transcriptionResult;
        }

        try
        {
            AppendLine("开始 AI 断句/纠错：本次断句与后续评分使用独立会话。");
            TranscriptPreviewTextBox.Text = "正在进行 AI 断句/纠错，请等待...";
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            DownloadProgressTextBlock.Text = "AI断句纠错";
            StatusTextBlock.Text = "转写完成，正在 AI 断句/纠错。";

            var settings = BuildAiVerificationSettings();
            ITranscriptPunctuationService punctuationService = new DeepSeekTranscriptPunctuationService(settings);
            var progress = new Progress<TranscriptPunctuationProgress>(UpdateTranscriptPunctuationProgress);
            var punctuationResult = await punctuationService.PunctuateAsync(
                new TranscriptPunctuationRequest(
                    rawText,
                    transcriptionResult.Document.Title,
                    downloadResult.SourceUrl),
                progress,
                CancellationToken.None);

            var punctuatedText = punctuationResult.PunctuatedText.Trim();
            if (string.IsNullOrWhiteSpace(punctuatedText))
            {
                AppendLine("AI 断句/纠错没有返回有效文本，继续使用原始转写文本。");
                TranscriptPreviewTextBox.Text = rawText;
                return transcriptionResult;
            }

            var punctuatedPath = await SavePunctuatedTranscriptAsync(
                transcriptionResult.RawOutputPath,
                punctuatedText);
            var punctuatedDocument = new TranscriptDocument(
                transcriptionResult.Document.Title,
                transcriptionResult.Document.Source,
                transcriptionResult.Document.CreatedAt,
                BuildPreviewSegments(punctuatedText));
            var punctuatedResult = new TranscriptionResult(
                punctuatedDocument,
                punctuatedPath ?? transcriptionResult.RawOutputPath);

            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 100;
            DownloadProgressTextBlock.Text = "断句纠错完成";
            StatusTextBlock.Text = "AI 断句/纠错完成。";
            TranscriptPreviewTextBox.Text = punctuatedText;
            AppendLine($"AI punctuation completed: {punctuatedPath ?? "punctuated file not generated"}");

            return punctuatedResult;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressTextBlock.Text = "断句纠错失败";
            StatusTextBlock.Text = "AI 断句/纠错失败，继续使用原始转写文本。";
            TranscriptPreviewTextBox.Text = rawText;
            AppendLine($"AI 断句/纠错失败，继续使用原始转写文本：{ex.Message}");
            return transcriptionResult;
        }
    }

    /// <summary>
    /// 淇濆瓨 AI 鏂彞/绾犻敊绋匡紝閬垮厤瑕嗙洊 whisper.cpp 鍘熷杈撳嚭銆?
    /// Save the AI-punctuated/corrected transcript without overwriting the raw whisper.cpp output.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private async Task<string?> SavePunctuatedTranscriptAsync(
        string? rawOutputPath,
        string punctuatedText)
    {
        if (string.IsNullOrWhiteSpace(rawOutputPath))
        {
            return null;
        }

        var outputDirectory = IOPath.GetDirectoryName(rawOutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return null;
        }

        Directory.CreateDirectory(outputDirectory);
        var outputStem = IOPath.GetFileNameWithoutExtension(rawOutputPath);
        var punctuatedPath = IOPath.Combine(outputDirectory, outputStem + "_断句.txt");
        await File.WriteAllTextAsync(punctuatedPath, punctuatedText, Encoding.UTF8);
        return punctuatedPath;
    }

    /// <summary>
    /// 从记忆数据库查找同一来源链接的可复用转写文本；数据库异常只写日志，不阻断新任务。
    /// Find reusable transcript text for the same source URL; database failures are logged without blocking new tasks.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task<VideoAnalysisMemoryEntry?> TryFindCachedTranscriptAsync(string sourceUrl)
    {
        try
        {
            var cachedEntry = await _memoryStore.FindBySourceUrlAsync(sourceUrl, CancellationToken.None);
            return cachedEntry is not null && HasEvaluableTranscript(cachedEntry.TranscriptText)
                ? cachedEntry
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            AppendLine($"读取记忆数据库失败，继续执行全新下载/转写：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 使用记忆数据库中已有转写文本跳过下载和 whisper，并重新执行 AI 核查。
    /// Reuse an existing transcript from the memory database, skip download/whisper, and rerun AI verification.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task UseCachedTranscriptAsync(string sourceUrl, VideoAnalysisMemoryEntry cachedEntry)
    {
        var metadata = BuildVideoSourceMetadataFromMemory(cachedEntry, InferVideoSourceMetadata(sourceUrl));
        var title = BuildSafeTaskTitle(cachedEntry.Title, metadata.Title, "视频转写文本");
        var transcriptText = cachedEntry.TranscriptText.Trim();
        var downloadResult = new AudioDownloadResult(
            LocalFilePath: string.Empty,
            SourceUrl: cachedEntry.SourceUrl,
            Title: title,
            SourceMetadata: metadata);
        var transcriptionResult = new TranscriptionResult(
            new TranscriptDocument(
                title,
                cachedEntry.SourceUrl,
                cachedEntry.LastTranscribedAt ?? cachedEntry.CreatedAt,
                BuildPreviewSegments(transcriptText)),
            cachedEntry.TranscriptPath);

        _lastDownloadResult = downloadResult;
        _lastTranscriptionResult = transcriptionResult;
        UpdateVideoInfoCard(metadata, isLoading: false);
        TranscriptPreviewTextBox.Text = transcriptText;
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 100;
        DownloadProgressTextBlock.Text = "已复用转写";
        StatusTextBlock.Text = "已从记忆数据库复用转写文本。";
        SetAiEvaluationPlainText("检测到重复视频，已复用记忆数据库中的转写文本，正在重新 AI 联网核查。");
        AppendLine($"检测到重复视频，跳过下载和 whisper，复用记忆数据库转写：{_memoryStore.DatabasePath}");

        try
        {
            await _memoryStore.TouchReuseAsync(sourceUrl, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            AppendLine($"更新记忆数据库复用次数失败：{ex.Message}");
        }

        await RunAiEvaluationForCurrentTranscriptAsync(isAutomatic: true);
    }

    /// <summary>
    /// 将本次断句/纠错后的转写稿写入记忆数据库，供重复视频直接复用。
    /// Write the punctuated/corrected transcript into the memory database for repeated-video reuse.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task RecordTranscriptMemoryAsync(
        AudioDownloadResult downloadResult,
        TranscriptionResult transcriptionResult)
    {
        var transcriptText = transcriptionResult.Document.PlainText.Trim();
        if (!HasEvaluableTranscript(transcriptText))
        {
            AppendLine("记忆数据库写入已跳过：当前转写文本无有效内容。");
            return;
        }

        try
        {
            var metadata = EnsureVideoSourceMetadata(downloadResult);
            var title = BuildSafeTaskTitle(
                transcriptionResult.Document.Title,
                downloadResult.Title,
                metadata.Title,
                "视频转写文本");
            await _memoryStore.RecordTranscriptAsync(
                downloadResult.SourceUrl,
                metadata,
                title,
                transcriptText,
                transcriptionResult.RawOutputPath,
                CancellationToken.None);
            AppendLine($"记忆数据库已写入转写文本：{_memoryStore.DatabasePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            AppendLine($"写入记忆数据库转写文本失败：{ex.Message}");
        }
    }

    /// <summary>
    /// AI 核查前读取同一发布人的历史视频摘要，作为本次评分背景参考。
    /// Load same-publisher historical video summaries before AI verification as background references.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task<IReadOnlyList<VideoMemoryReference>> LoadMemoryReferencesAsync(
        VideoSourceMetadata metadata,
        string? sourceUrl)
    {
        try
        {
            var references = await _memoryStore.FindPublisherReferencesAsync(
                metadata,
                sourceUrl,
                maxCount: 5,
                CancellationToken.None);
            if (references.Count > 0)
            {
                AppendLine($"记忆数据库已加载同发布人往期视频参考：{references.Count} 条。");
            }

            return references;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            AppendLine($"读取同发布人记忆参考失败，本次 AI 核查将不使用历史参考：{ex.Message}");
            return Array.Empty<VideoMemoryReference>();
        }
    }

    /// <summary>
    /// 将本次 AI 核查结果回写记忆数据库，使后续同发布人视频可以参考往期结论。
    /// Write the latest AI evaluation result back to the memory database for future same-publisher references.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task RecordAiEvaluationMemoryAsync(
        AiVideoEvaluationResult result,
        string reportPath,
        string? title,
        string? sourceUrl,
        VideoSourceMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            AppendLine("记忆数据库 AI 结果写入已跳过：来源链接为空。");
            return;
        }

        var transcriptText = (_lastTranscriptionResult?.Document.PlainText ?? TranscriptPreviewTextBox.Text).Trim();
        if (!HasEvaluableTranscript(transcriptText))
        {
            AppendLine("记忆数据库 AI 结果写入已跳过：当前没有有效转写文本。");
            return;
        }

        try
        {
            await _memoryStore.RecordEvaluationAsync(
                sourceUrl,
                metadata,
                BuildSafeTaskTitle(title, metadata.Title, "视频转写文本"),
                transcriptText,
                _lastTranscriptionResult?.RawOutputPath,
                result,
                reportPath,
                CancellationToken.None);
            AppendLine($"记忆数据库已写入 AI 核查结果：{_memoryStore.DatabasePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            AppendLine($"写入记忆数据库 AI 核查结果失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 获取当前任务的视频元数据；没有下载结果时退回 URL 域名推断。
    /// Get current task metadata; fall back to URL host inference when no download result exists.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private VideoSourceMetadata BuildCurrentVideoSourceMetadata()
    {
        return _lastDownloadResult is null
            ? InferVideoSourceMetadata(UrlTextBox.Text.Trim())
            : EnsureVideoSourceMetadata(_lastDownloadResult);
    }

    /// <summary>
    /// 将记忆条目还原为界面任务卡片可显示的视频元数据。
    /// Restore a memory entry to video metadata that can be shown in the task card.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static VideoSourceMetadata BuildVideoSourceMetadataFromMemory(
        VideoAnalysisMemoryEntry entry,
        VideoSourceMetadata fallbackMetadata)
    {
        return new VideoSourceMetadata(
            string.IsNullOrWhiteSpace(entry.Website) ? fallbackMetadata.Website : entry.Website,
            string.IsNullOrWhiteSpace(entry.PublisherName) ? fallbackMetadata.PublisherName : entry.PublisherName,
            entry.ViewCount ?? fallbackMetadata.ViewCount,
            string.IsNullOrWhiteSpace(entry.Title) ? fallbackMetadata.Title : entry.Title);
    }

    /// <summary>
    /// 分析流程结束后删除软件托管的下载音频和临时视频文件，保留转写文本、AI 报告和记忆数据库。
    /// Delete app-managed downloaded audio and temporary video files after analysis while keeping transcripts, AI reports, and memory database.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private void CleanupDownloadedMediaFiles(AudioDownloadResult downloadResult)
    {
        if (TryDeleteManagedFile(downloadResult.LocalFilePath, _directories.DownloadedAudioDirectory))
        {
            AppendLine($"已自动删除下载音频/视频中间文件：{downloadResult.LocalFilePath}");
        }

        TryDeleteManagedDirectory(
            IOPath.Combine(_directories.DownloadedAudioDirectory, "_临时下载"),
            _directories.DownloadedAudioDirectory);
    }

    /// <summary>
    /// 删除托管目录内的单个文件；路径不在托管目录内时拒绝删除。
    /// Delete one file under a managed directory; refuse paths outside the managed directory.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private bool TryDeleteManagedFile(string? filePath, string managedDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            var fullFilePath = IOPath.GetFullPath(filePath);
            if (!IsPathUnderDirectory(fullFilePath, managedDirectory))
            {
                AppendLine($"自动清理已跳过：文件不在软件托管下载目录内，{fullFilePath}");
                return false;
            }

            if (!File.Exists(fullFilePath))
            {
                return false;
            }

            File.Delete(fullFilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppendLine($"自动删除下载中间文件失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 删除托管目录内的临时子目录；只允许删除托管根目录下的子路径。
    /// Delete a temporary subdirectory under a managed root only.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private void TryDeleteManagedDirectory(string directoryPath, string managedDirectory)
    {
        try
        {
            var fullDirectoryPath = IOPath.GetFullPath(directoryPath);
            if (!Directory.Exists(fullDirectoryPath))
            {
                return;
            }

            if (!IsPathUnderDirectory(fullDirectoryPath, managedDirectory))
            {
                AppendLine($"自动清理已跳过：目录不在软件托管下载目录内，{fullDirectoryPath}");
                return;
            }

            Directory.Delete(fullDirectoryPath, recursive: true);
            AppendLine($"已自动删除下载临时目录：{fullDirectoryPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppendLine($"自动删除下载临时目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 判断候选路径是否位于指定目录内部，避免自动清理越界。
    /// Check whether a candidate path is inside the given directory to prevent cleanup escaping the app folder.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static bool IsPathUnderDirectory(string candidatePath, string directoryPath)
    {
        var fullCandidatePath = IOPath.GetFullPath(candidatePath)
            .TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
        var fullDirectoryPath = IOPath.GetFullPath(directoryPath)
            .TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar)
            + IOPath.DirectorySeparatorChar;

        return fullCandidatePath.StartsWith(fullDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 向折叠消息栏追加一行系统消息，便于需要排查时展开查看。
    /// Append one system message line to the collapsed message bar for on-demand diagnostics.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private void AppendLine(string message)
    {
        OutputTextBox.AppendText(Environment.NewLine + message);
        OutputTextBox.ScrollToEnd();
    }

    /// <summary>
    /// 鏇存柊涓嬭浇杩涘害鏉″拰鐘舵€佹枃鏈€?
    /// Update download progress bar and status text from download services.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-23锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void UpdateDownloadProgress(DownloadProgress progress)
    {
        if (progress.Percent.HasValue)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = Math.Clamp(progress.Percent.Value, 0, 100);
            DownloadProgressTextBlock.Text = $"{Math.Clamp(progress.Percent.Value, 0, 100):0.0}%";
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressTextBlock.Text = progress.ReceivedBytes > 0
                ? $"{FormatByteSize(progress.ReceivedBytes)} / 未知"
                : "处理中";
        }

        StatusTextBlock.Text = progress.Message;
    }

    /// <summary>
    /// 鏇存柊璇煶璇嗗埆杩涘害锛屽苟鍦ㄨ瘑鍒畬鎴愭椂鍒锋柊鏂彞/绾犻敊绋块瑙堝尯銆?
    /// Update transcription progress and refresh the transcript preview area when text is available.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void UpdateTranscriptionProgress(TranscriptionProgress progress)
    {
        if (progress.Percent.HasValue)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = Math.Clamp(progress.Percent.Value, 0, 100);
            DownloadProgressTextBlock.Text = $"{Math.Clamp(progress.Percent.Value, 0, 100):0.0}%";
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressTextBlock.Text = "转写中";
        }

        StatusTextBlock.Text = progress.Message;

        if (!string.IsNullOrWhiteSpace(progress.CurrentSegmentText))
        {
            TranscriptPreviewTextBox.Text = progress.CurrentSegmentText;
        }
    }

    /// <summary>
    /// 鏇存柊 AI 鏂彞/绾犻敊杩涘害銆?
    /// Update AI punctuation and typo-correction progress.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void UpdateTranscriptPunctuationProgress(TranscriptPunctuationProgress progress)
    {
        if (progress.Percent.HasValue)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = Math.Clamp(progress.Percent.Value, 0, 100);
            DownloadProgressTextBlock.Text = $"{Math.Clamp(progress.Percent.Value, 0, 100):0.0}%";
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressTextBlock.Text = "AI断句纠错";
        }

        StatusTextBlock.Text = progress.Message;
        SetAiEvaluationPlainText(progress.Message);
    }

    /// <summary>
    /// 鏇存柊 AI 鑱旂綉璇勪环杩涘害銆?
    /// Update AI web verification progress.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private void UpdateAiEvaluationProgress(AiVideoEvaluationProgress progress)
    {
        if (progress.Percent.HasValue)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = Math.Clamp(progress.Percent.Value, 0, 100);
            DownloadProgressTextBlock.Text = $"{Math.Clamp(progress.Percent.Value, 0, 100):0.0}%";
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressTextBlock.Text = "AI评价";
        }

        StatusTextBlock.Text = progress.Message;
        SetAiEvaluationPlainText(progress.Message, includeAttribution: true);
    }

    /// <summary>
    /// 閲嶇疆涓嬭浇杩涘害鏉°€?
    /// Reset the download progress bar before a new task.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-23锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void ResetDownloadProgress()
    {
        DownloadProgressBar.IsIndeterminate = false;
        DownloadProgressBar.Value = 0;
        DownloadProgressTextBlock.Text = "0.0%";
    }

    /// <summary>
    /// 璁剧疆涓嬭浇鏈熼棿鐨勭晫闈㈠彲鐢ㄧ姸鎬併€?
    /// Toggle UI controls while a download task is running.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void SetDownloadBusyState(bool isBusy)
    {
        CreateTaskButton.IsEnabled = !isBusy;
        UrlTextBox.IsEnabled = !isBusy;
        EvaluateTranscriptButton.IsEnabled = !isBusy;
        SettingsButton.IsEnabled = !isBusy;
    }

    /// <summary>
    /// 璁剧疆 AI 璇勪环鏈熼棿鐨勭晫闈㈠彲鐢ㄧ姸鎬併€?
    /// Toggle AI verification controls while an evaluation task is running.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void SetEvaluationBusyState(bool isBusy)
    {
        EvaluateTranscriptButton.IsEnabled = !isBusy;
        CreateTaskButton.IsEnabled = !isBusy;
        SettingsButton.IsEnabled = !isBusy;
    }

    /// <summary>
    /// 鏇存柊褰撳墠瑙嗛淇℃伅鍗＄墖锛屼笅杞藉墠鏄剧ず鏉ユ簮浼拌鍊硷紝涓嬭浇鍚庢樉绀?yt-dlp 鍏冩暟鎹€?
    /// Update the current-video card with inferred data before download and yt-dlp metadata after download.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void UpdateVideoInfoCard(VideoSourceMetadata metadata, bool isLoading)
    {
        VideoInfoCard.Visibility = Visibility.Visible;
        VideoTitleTextBlock.Text = string.IsNullOrWhiteSpace(metadata.Title)
            ? (isLoading ? "读取中" : "未知标题")
            : metadata.Title;
        VideoWebsiteTextBlock.Text = string.IsNullOrWhiteSpace(metadata.Website)
            ? "未知网站"
            : metadata.Website;
        VideoPublisherTextBlock.Text = string.IsNullOrWhiteSpace(metadata.PublisherName)
            ? (isLoading ? "Loading" : "Unknown")
            : metadata.PublisherName;
        VideoViewCountTextBlock.Text = metadata.ViewCount.HasValue
            ? FormatViewCount(metadata.ViewCount.Value)
            : (isLoading ? "Loading" : "Unknown");
    }

    /// <summary>
    /// 鍚堝苟涓嬭浇鏈嶅姟杩斿洖鐨勭湡瀹炲厓鏁版嵁鍜?URL 鎺ㄦ柇鍏冩暟鎹紝閬垮厤骞冲彴鏈繑鍥炲瓧娈垫椂鍗＄墖涓虹┖銆?
    /// Merge downloader metadata with URL-inferred fallback data so the task card stays readable.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static VideoSourceMetadata EnsureVideoSourceMetadata(AudioDownloadResult result)
    {
        var fallbackMetadata = InferVideoSourceMetadata(result.SourceUrl);
        if (result.SourceMetadata is null)
        {
            return fallbackMetadata with
            {
                Title = BuildSafeTaskTitle(result.Title)
            };
        }

        return result.SourceMetadata with
        {
            Title = string.IsNullOrWhiteSpace(result.SourceMetadata.Title)
                ? BuildSafeTaskTitle(result.Title)
                : result.SourceMetadata.Title,
            Website = string.IsNullOrWhiteSpace(result.SourceMetadata.Website)
                ? fallbackMetadata.Website
                : result.SourceMetadata.Website
        };
    }

    /// <summary>
    /// 浠?URL 鍩熷悕鎺ㄦ柇褰撳墠瑙嗛缃戠珯锛岀敤浜庝笅杞藉厓鏁版嵁杩斿洖鍓嶇殑鍗虫椂鍗＄墖鏄剧ず銆?
    /// Infer the video website from the URL host for immediate card display before metadata is available.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static VideoSourceMetadata InferVideoSourceMetadata(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri))
        {
            return new VideoSourceMetadata("未知网站");
        }

        return new VideoSourceMetadata(InferWebsiteName(sourceUri));
    }

    /// <summary>
    /// 灏嗗父瑙佽棰戝钩鍙板煙鍚嶆槧灏勪负鐢ㄦ埛鍙鍚嶇О銆?
    /// Map common video-platform hosts to user-facing names.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static string InferWebsiteName(Uri sourceUri)
    {
        var host = sourceUri.Host.Trim().ToLowerInvariant();
        if (host.Length == 0)
        {
            return "未知网站";
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

        if (host.EndsWith("kuaishou.com", StringComparison.OrdinalIgnoreCase))
        {
            return "快手";
        }

        if (host.EndsWith("ixigua.com", StringComparison.OrdinalIgnoreCase))
        {
            return "西瓜视频";
        }

        if (host.EndsWith("weibo.com", StringComparison.OrdinalIgnoreCase))
        {
            return "微博";
        }

        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host;
    }

    /// <summary>
    /// 灏嗘祻瑙堥噺鏍煎紡鍖栦负閫傚悎涓枃鐣岄潰蹇€熸壂鎻忕殑鏂囨湰銆?
    /// Format view counts for quick scanning in the Chinese UI.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static string FormatViewCount(long viewCount)
    {
        if (viewCount >= 100_000_000)
        {
            return $"{viewCount / 100_000_000.0:0.##}亿";
        }

        if (viewCount >= 10_000)
        {
            return $"{viewCount / 10_000.0:0.##}万";
        }

        return viewCount.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 閫夋嫨瀹夊叏浠诲姟鏍囬锛岄伩鍏嶄贡鐮佹爣棰樿繘鍏?AI 鎼滅储璇嶅拰鎶ュ憡鏂囦欢鍚嶃€?
    /// Select a safe task title so mojibake does not enter AI queries or report filenames.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static string BuildSafeTaskTitle(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var repairedCandidate = TextEncodingRepair.RepairOrNull(candidate);
            if (!string.IsNullOrWhiteSpace(repairedCandidate))
            {
                return repairedCandidate;
            }
        }

        return "视频转写文本";
    }

    /// <summary>
    /// 鍒锋柊涓荤晫闈㈢殑 AI 璁剧疆鎽樿锛屽彧灞曠ず闈炴晱鎰熶俊鎭紝閬垮厤 API Key 娉勯湶鍒扮晫闈㈡棩蹇椼€?    /// Refresh the main-window AI settings summary with non-sensitive information only.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void UpdateAiSettingsSummary()
    {
        var searchText = string.IsNullOrWhiteSpace(_aiSettings.SearxngEndpoint)
            ? "公开搜索降级链路"
            : "SearXNG 优先";

        if (!string.IsNullOrWhiteSpace(_aiSettings.BochaWebSearchApiKey))
        {
            searchText = $"Bocha Web Search primary -> {searchText}";
        }

        var blockedSourceText = FormatBlockedEvidencePlatformNames(_aiSettings.BlockedEvidencePlatformNames);
        AiSettingsSummaryTextBlock.Text =
            $"模型：{_aiSettings.Model}；API：{_aiSettings.BaseUrl.TrimEnd('/')}；Key：{(string.IsNullOrWhiteSpace(_aiSettings.ApiKey) ? "未配置" : "已配置")}；查验力度：{_aiSettings.VerificationIntensityLabel}；搜索：{searchText}；屏蔽源：{blockedSourceText}";
    }

    /// <summary>
    /// 构建当前广告数据源屏蔽设置的日志说明。
    /// Build the log line for current ad/data source block settings.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string BuildBlockedEvidenceSourceLogLine(AiVerificationSettings settings)
    {
        var blockedSourceText = FormatBlockedEvidencePlatformNames(settings.BlockedEvidencePlatformNames);
        return blockedSourceText == "无"
            ? "参考源过滤：当前未屏蔽广告数据源平台结果。"
            : $"参考源过滤：已屏蔽 {blockedSourceText} 平台结果。";
    }

    /// <summary>
    /// 格式化广告数据源屏蔽平台名称，便于界面摘要和日志显示。
    /// Format blocked ad/data source platform names for UI summary and logs.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string FormatBlockedEvidencePlatformNames(IReadOnlyCollection<string> blockedPlatformNames)
    {
        var defaultOrderedNames = AiVerificationSettings.DefaultBlockedEvidencePlatformNames;
        var formattedPlatformNames = defaultOrderedNames
            .Where(defaultName => blockedPlatformNames.Contains(defaultName, StringComparer.OrdinalIgnoreCase))
            .Concat(blockedPlatformNames
                .Where(name => !defaultOrderedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal))
            .ToArray();

        return blockedPlatformNames.Count == 0
            ? "无"
            : string.Join("、", formattedPlatformNames);
    }

    /// <summary>
    /// 浠庤缃〉闈繚瀛樼殑杩愯鎬佸璞¤鍙?AI 鑱旂綉璇勪环璁剧疆銆?    /// Read AI web verification settings from the runtime object saved by the settings page.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?    /// </summary>
    private AiVerificationSettings BuildAiVerificationSettings()
    {
        return _aiSettings;
    }

    /// <summary>
    /// 鍒ゆ柇褰撳墠鏂囧瓧棰勮鏄惁鍙互杩涜 AI 璇勪环銆?
    /// Check whether the current preview text is evaluable.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static bool HasEvaluableTranscript(string transcriptText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText) || transcriptText.Length < 8)
        {
            return false;
        }

        var blockedPrefixes = new[]
        {
            "尚未开始",
            "等待下载",
            "正在进行",
            "下载失败",
            "转写失败",
            "未识别到"
        };

        return !blockedPrefixes.Any(prefix => transcriptText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 淇濆瓨 AI 璇勪环 Markdown 鍜?JSON 鎶ュ憡銆?
    /// Save AI evaluation reports as Markdown and JSON.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private async Task<string> SaveAiEvaluationReportAsync(AiVideoEvaluationResult result, string? title)
    {
        var reportDirectory = IOPath.Combine(_directories.TranscriptDirectory, "AI评价");
        Directory.CreateDirectory(reportDirectory);

        var reportStem = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss") + "_" + SanitizeFileName(BuildSafeTaskTitle(title, "AI评价"));
        var markdownPath = IOPath.Combine(reportDirectory, reportStem + ".md");
        var jsonPath = IOPath.Combine(reportDirectory, reportStem + ".json");

        await File.WriteAllTextAsync(markdownPath, BuildAiEvaluationMarkdown(result), Encoding.UTF8);
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(result, AiReportJsonOptions),
            Encoding.UTF8);

        return markdownPath;
    }

    /// <summary>
    /// 鏋勫缓 AI 璇勪环鐣岄潰鏄剧ず鏂囨湰銆?
    /// Build the AI evaluation text shown in the UI.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string BuildAiEvaluationDisplayText(AiVideoEvaluationResult result, string reportPath)
    {
        var scoreStamp = BuildScoreStamp(result.OverallScore);

        return string.Join(
            Environment.NewLine,
            AiEvaluationAttributionText,
            "",
            $"盖章评级：{scoreStamp.Label}",
            $"类别：{result.Category}",
            $"综合价值：{result.OverallScore}/100",
            $"真实性：{result.TruthfulnessScore}/100",
            $"时效性：{result.TimelinessScore}/100",
            $"信息专业性：{result.InformationProfessionalismScore}/100",
            $"娱乐性：{result.EntertainmentScore}/100",
            $"情绪引导嫌疑：{result.EmotionalGuidanceSuspicionScore}/100",
            "",
            "结论：",
            result.Verdict,
            "",
            "关键主张：",
            FormatClaimEvaluationList(result),
            "",
            "证据来源：",
            FormatEvidenceList(result.Evidences),
            "",
            "限制与风险：",
            FormatBulletList(result.Warnings),
            "",
            $"报告：{reportPath}");
    }

    /// <summary>
    /// 鍦?AI 璇勪环瀵屾枃鏈涓樉绀烘櫘閫氱姸鎬佹枃鏈€?
    /// Show plain status text in the AI evaluation rich text box.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private void SetAiEvaluationPlainText(string text, bool includeAttribution = false)
    {
        var document = CreateAiEvaluationFlowDocument();
        if (includeAttribution)
        {
            AddAiEvaluationAttributionBlock(document);
        }

        document.Blocks.Add(new Paragraph(new Run(text))
        {
            Margin = new Thickness(0, 0, 0, 6)
        });
        AiEvaluationRichTextBox.Document = document;
        AiEvaluationRichTextBox.ScrollToHome();
    }

    /// <summary>
    /// 浣跨敤瀵屾枃鏈粨鏋勫睍绀?AI 鑱旂綉鏍告煡缁撴灉锛屼繚鐣欒瘉鎹爣棰樸€佹潵婧愩€佹悳绱㈣瘝鍜?URL銆?
    /// Render AI web verification as rich text while preserving evidence title, source, query, and URL.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private void SetAiEvaluationResultDocument(AiVideoEvaluationResult result, string reportPath)
    {
        var document = CreateAiEvaluationFlowDocument();
        AddAiEvaluationAttributionBlock(document);

        var stamp = BuildScoreStamp(result.OverallScore);
        var stampBrush = CreateBrush(stamp.BrushHex);

        var titleParagraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        titleParagraph.Inlines.Add(new Bold(new Run($"盖章评级：{stamp.Label}"))
        {
            Foreground = stampBrush,
            FontSize = AiEvaluationTitleFontSize
        });
        titleParagraph.Inlines.Add(new Run($"    类别：{result.Category}"));
        document.Blocks.Add(titleParagraph);

        document.Blocks.Add(BuildScoreParagraph(result));
        AddRichSection(document, "结论", result.Verdict);
        AddRichClaimEvaluationSection(document, result);
        AddRichEvidenceSection(document, result.Evidences);
        AddRichBulletSection(document, "限制与风险", result.Warnings);
        AddRichSection(document, "报告", reportPath);

        AiEvaluationRichTextBox.Document = document;
        AiEvaluationRichTextBox.ScrollToHome();
    }

    /// <summary>
    /// 创建 AI 评价富文本基础文档；字号较原 12 提升 25%，便于长评语阅读。
    /// Create the base AI evaluation document; font size is 25% larger than the previous 12-point view for readability.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static FlowDocument CreateAiEvaluationFlowDocument()
    {
        return new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = AiEvaluationDisplayFontSize,
            LineHeight = AiEvaluationDisplayLineHeight
        };
    }

    /// <summary>
    /// 在 AI 核查结果前插入固定开源项目来源声明，该内容由程序直接输出，不交给 AI 生成。
    /// Insert the fixed open-source attribution before AI verification content; this text is emitted by the app, not generated by AI.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static void AddAiEvaluationAttributionBlock(FlowDocument document)
    {
        document.Blocks.Add(new Paragraph(new Run(AiEvaluationAttributionText))
        {
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = CreateBrush("#4B5563")
        });
    }

    /// <summary>
    /// 鏋勫缓璇勫垎鎽樿娈佃惤锛岀獊鍑哄叧閿垎鏁颁究浜庡揩閫熸壂鎻忋€?
    /// Build the score summary paragraph and emphasize key scores for quick scanning.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static Paragraph BuildScoreParagraph(AiVideoEvaluationResult result)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 10)
        };

        AddScoreInline(paragraph, "综合", result.OverallScore);
        AddScoreInline(paragraph, "真实性", result.TruthfulnessScore);
        AddScoreInline(paragraph, "时效性", result.TimelinessScore);
        AddScoreInline(paragraph, "专业性", result.InformationProfessionalismScore);
        AddScoreInline(paragraph, "娱乐性", result.EntertainmentScore);
        AddScoreInline(paragraph, "情绪引导嫌疑", result.EmotionalGuidanceSuspicionScore);
        return paragraph;
    }

    /// <summary>
    /// 鍚戣瘎鍒嗘钀藉姞鍏ヤ竴涓€滄寚鏍囷細鍒嗘暟鈥濈墖娈点€?
    /// Add one "metric: score" inline item to the score paragraph.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static void AddScoreInline(Paragraph paragraph, string label, int score)
    {
        if (paragraph.Inlines.Count > 0)
        {
            paragraph.Inlines.Add(new Run("    "));
        }

        paragraph.Inlines.Add(new Bold(new Run($"{label}: ")));
        paragraph.Inlines.Add(new Run($"{score}/100"));
    }

    /// <summary>
    /// 娣诲姞甯︽爣棰樼殑鏅€氭枃鏈珷鑺傘€?
    /// Add a titled plain-text section.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static void AddRichSection(FlowDocument document, string title, string text)
    {
        document.Blocks.Add(BuildRichHeading(title));
        document.Blocks.Add(new Paragraph(new Run(string.IsNullOrWhiteSpace(text) ? "无" : text))
        {
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    /// <summary>
    /// 娣诲姞椤圭洰绗﹀彿鍒楄〃绔犺妭銆?
    /// Add a bullet-list section.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static void AddRichBulletSection(FlowDocument document, string title, IReadOnlyList<string> items)
    {
        document.Blocks.Add(BuildRichHeading(title));
        if (items.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("无"))
            {
                Margin = new Thickness(0, 0, 0, 8)
            });
            return;
        }

        var list = new System.Windows.Documents.List
        {
            MarkerStyle = TextMarkerStyle.Disc,
            Margin = new Thickness(18, 0, 0, 8),
            Padding = new Thickness(0)
        };
        foreach (var item in items)
        {
            list.ListItems.Add(new ListItem(new Paragraph(new Run(item))
            {
                Margin = new Thickness(0, 0, 0, 3)
            }));
        }

        document.Blocks.Add(list);
    }

    /// <summary>
    /// 添加带颜色五级标签的关键主张章节；旧结果没有结构化标签时退回普通列表。
    /// Add the key-claim section with colored five-level labels; fall back to a plain list for legacy results.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static void AddRichClaimEvaluationSection(FlowDocument document, AiVideoEvaluationResult result)
    {
        if (result.ClaimEvaluations.Count == 0)
        {
            AddRichBulletSection(document, "关键主张", result.KeyClaims);
            return;
        }

        document.Blocks.Add(BuildRichHeading("关键主张"));
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = TextMarkerStyle.Disc,
            Margin = new Thickness(18, 0, 0, 8),
            Padding = new Thickness(0)
        };

        foreach (var evaluation in result.ClaimEvaluations)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 3)
            };
            paragraph.Inlines.Add(new Run(string.IsNullOrWhiteSpace(evaluation.Claim)
                ? "未提取到有效主张"
                : evaluation.Claim.Trim()));
            paragraph.Inlines.Add(new Run("（"));
            paragraph.Inlines.Add(new Bold(new Run(string.IsNullOrWhiteSpace(evaluation.Rating)
                ? "有失偏颇"
                : evaluation.Rating.Trim()))
            {
                Foreground = BuildClaimRatingBrush(evaluation.Rating)
            });
            paragraph.Inlines.Add(new Run("）"));
            list.ListItems.Add(new ListItem(paragraph));
        }

        document.Blocks.Add(list);
    }

    /// <summary>
    /// 为用户定义的五级主张评价选择固定颜色，便于一眼区分可信和高风险内容。
    /// Choose fixed colors for the five user-defined claim ratings so trustworthy and risky content scan differently.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static SolidColorBrush BuildClaimRatingBrush(string? rating)
    {
        return rating?.Trim() switch
        {
            "客观属实" => CreateBrush("#15803D"),
            "基本属实" => CreateBrush("#65A30D"),
            "有失偏颇" => CreateBrush("#CA8A04"),
            "煽风点火" => CreateBrush("#EA580C"),
            "胡言乱语" => CreateBrush("#DC2626"),
            _ => CreateBrush("#475569")
        };
    }

    /// <summary>
    /// 娣诲姞鑱旂綉璇佹嵁绔犺妭锛岄€愭潯鏄剧ず鎼滅储鏉ユ簮鍜屽彲鐐瑰嚮 URL銆?
    /// Add the web evidence section, showing search provider and clickable URL for every evidence item.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void AddRichEvidenceSection(FlowDocument document, IReadOnlyList<AiEvaluationEvidence> evidences)
    {
        document.Blocks.Add(BuildRichHeading("证据来源"));
        if (evidences.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("无"))
            {
                Margin = new Thickness(0, 0, 0, 8)
            });
            return;
        }

        for (var index = 0; index < evidences.Count; index++)
        {
            var evidence = evidences[index];
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            paragraph.Inlines.Add(new Bold(new Run($"{index + 1}. {evidence.Title}")));
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run($"来源：{evidence.Source}"));
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run($"搜索词：{evidence.Query}"));
            paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run("URL: "));
            paragraph.Inlines.Add(BuildEvidenceHyperlink(evidence.Url));
            document.Blocks.Add(paragraph);
        }
    }

    /// <summary>
    /// 鍒涘缓绔犺妭鏍囬娈佃惤銆?
    /// Create a section heading paragraph.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static Paragraph BuildRichHeading(string title)
    {
        return new Paragraph(new Bold(new Run(title)))
        {
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = CreateBrush("#1F2937")
        };
    }

    /// <summary>
    /// 鍒涘缓璇佹嵁 URL 瓒呴摼鎺ワ紱鏃犳硶瑙ｆ瀽涓虹粷瀵?URL 鏃堕€€鍥炴櫘閫氭枃鏈€?
    /// Create a hyperlink for evidence URL; fall back to plain text when it is not an absolute URL.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private Hyperlink BuildEvidenceHyperlink(string url)
    {
        var hyperlink = new Hyperlink(new Run(url));
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            hyperlink.NavigateUri = uri;
            hyperlink.RequestNavigate += OnExternalLinkRequestNavigate;
        }

        return hyperlink;
    }

    /// <summary>
    /// 浣跨敤绯荤粺榛樿娴忚鍣ㄦ墦寮€澶栭儴閾炬帴銆?
    /// Open external links with the system default browser.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-25锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void OnExternalLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppendLine($"打开外部链接失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 鏋勫缓 AI 璇勪环 Markdown 鎶ュ憡銆?
    /// Build the AI evaluation Markdown report.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string BuildAiEvaluationMarkdown(AiVideoEvaluationResult result)
    {
        var builder = new StringBuilder()
            .AppendLine("# AI 联网评价报告")
            .AppendLine()
            .AppendLine(AiEvaluationAttributionText)
            .AppendLine()
            .AppendLine($"最近修改时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine("维护者：GG")
            .AppendLine()
            .AppendLine("## 评分结果")
            .AppendLine()
            .AppendLine($"盖章评级：{BuildScoreStamp(result.OverallScore).Label}")
            .AppendLine()
            .AppendLine("| 指标 | 分数 | 解释 |")
            .AppendLine("|---|---:|---|")
            .AppendLine($"| 综合价值 | {result.OverallScore}/100 | 越高表示越值得观看、信息价值越高 |")
            .AppendLine($"| 真实性 | {result.TruthfulnessScore}/100 | 越高表示越能得到外部证据支持 |")
            .AppendLine($"| 时效性 | {result.TimelinessScore}/100 | 越高表示越新、越有时效参考价值 |")
            .AppendLine($"| 专业性 | {result.InformationProfessionalismScore}/100 | 综合信息密度、重要性和观点新颖性 |")
            .AppendLine($"| 娱乐性 | {result.EntertainmentScore}/100 | 越高表示用词越诙谐、文本越不让人打瞌睡 |")
            .AppendLine($"| 情绪引导嫌疑 | {result.EmotionalGuidanceSuspicionScore}/100 | 越高表示越像带节奏、煽动、恐吓或标题党 |")
            .AppendLine()
            .AppendLine($"类别：{result.Category}")
            .AppendLine()
            .AppendLine("## 结论")
            .AppendLine()
            .AppendLine(result.Verdict)
            .AppendLine()
            .AppendLine("## 关键主张")
            .AppendLine()
            .AppendLine(FormatClaimEvaluationList(result))
            .AppendLine()
            .AppendLine("## 搜索词")
            .AppendLine()
            .AppendLine(FormatBulletList(result.SearchQueries))
            .AppendLine()
            .AppendLine("## 外部证据")
            .AppendLine()
            .AppendLine(FormatEvidenceList(result.Evidences))
            .AppendLine()
            .AppendLine("## 限制与风险")
            .AppendLine()
            .AppendLine(FormatBulletList(result.Warnings));

        return builder.ToString();
    }

    /// <summary>
    /// 娓呯┖ AI 璇勪环闆疯揪鍥惧拰鐩栫珷璇勭骇锛岀瓑寰呬笅涓€娆℃湁鏁堣瘎鍒嗐€?
    /// Clear AI evaluation radar chart and stamp rating before the next valid result.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void ResetAiEvaluationVisuals(string stampText = "待评级")
    {
        DrawRadarChart(
        [
            new RadarMetric("真实性", 0),
            new RadarMetric("时效性", 0),
            new RadarMetric("专业性", 0),
            new RadarMetric("娱乐性", 0),
            new RadarMetric("低引导嫌疑", 0)
        ]);

        var neutralBrush = CreateBrush("#94A3B8");
        AiEvaluationStampTextBlock.Text = stampText;
        AiEvaluationStampTextBlock.Foreground = neutralBrush;
        AiEvaluationStampBorder.BorderBrush = neutralBrush;
        AiEvaluationScoreSummaryTextBlock.Text = "等待评分生成雷达图和盖章评级。";
    }

    /// <summary>
    /// 鏍规嵁 AI 璇勫垎鏇存柊鍙充晶闆疯揪鍥俱€佺洊绔犺瘎绾у拰鎽樿銆?
    /// Update the right-side radar chart, stamp rating, and score summary from AI scores.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void UpdateAiEvaluationVisuals(AiVideoEvaluationResult result)
    {
        var lowGuidanceSuspicionScore = 100 - result.EmotionalGuidanceSuspicionScore;
        DrawRadarChart(
        [
            new RadarMetric("真实性", result.TruthfulnessScore),
            new RadarMetric("时效性", result.TimelinessScore),
            new RadarMetric("专业性", result.InformationProfessionalismScore),
            new RadarMetric("娱乐性", result.EntertainmentScore),
            new RadarMetric("低引导嫌疑", lowGuidanceSuspicionScore)
        ]);

        var stamp = BuildScoreStamp(result.OverallScore);
        var stampBrush = CreateBrush(stamp.BrushHex);
        AiEvaluationStampTextBlock.Text = stamp.Label;
        AiEvaluationStampTextBlock.Foreground = stampBrush;
        AiEvaluationStampBorder.BorderBrush = stampBrush;
        AiEvaluationScoreSummaryTextBlock.Text =
            $"综合 {result.OverallScore}/100，盖章 {stamp.Label}，情绪引导嫌疑 {result.EmotionalGuidanceSuspicionScore}/100（雷达按低嫌疑绘制）";
    }

    /// <summary>
    /// 缁樺埗浜旂淮璇勪环闆疯揪鍥撅紱鍚勮酱浣跨敤 0-100 鍒嗭紝瓒婇潬澶栬〃绀鸿杞磋秺寮恒€?
    /// Draw a five-axis evaluation radar chart where every axis uses a 0-100 score.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private void DrawRadarChart(IReadOnlyList<RadarMetric> metrics)
    {
        AiEvaluationRadarCanvas.Children.Clear();

        if (metrics.Count < 3)
        {
            return;
        }

        const double centerX = 103;
        const double centerY = 98;
        const double radius = 56;
        const double labelRadius = 82;
        var center = new Point(centerX, centerY);
        var gridBrush = CreateBrush("#D5DCE8");
        var axisBrush = CreateBrush("#CBD5E1");

        for (var level = 1; level <= 5; level++)
        {
            var levelRadius = radius * level / 5.0;
            AiEvaluationRadarCanvas.Children.Add(new Polygon
            {
                Points = BuildRadarPointCollection(metrics.Count, center, levelRadius),
                Stroke = gridBrush,
                StrokeThickness = 0.8,
                Fill = Brushes.Transparent
            });
        }

        for (var index = 0; index < metrics.Count; index++)
        {
            var angle = BuildRadarAngle(index, metrics.Count);
            var endPoint = BuildRadarPoint(center, angle, radius);
            AiEvaluationRadarCanvas.Children.Add(new Line
            {
                X1 = center.X,
                Y1 = center.Y,
                X2 = endPoint.X,
                Y2 = endPoint.Y,
                Stroke = axisBrush,
                StrokeThickness = 0.8
            });

            var labelPoint = BuildRadarPoint(center, angle, labelRadius);
            var label = new TextBlock
            {
                Width = 70,
                Text = metrics[index].Label,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Foreground = CreateBrush("#334155")
            };
            Canvas.SetLeft(label, labelPoint.X - 35);
            Canvas.SetTop(label, labelPoint.Y - 9);
            AiEvaluationRadarCanvas.Children.Add(label);
        }

        var scorePoints = new PointCollection(
            metrics.Select((metric, index) =>
            {
                var scoreRadius = radius * Math.Clamp(metric.Score, 0, 100) / 100.0;
                return BuildRadarPoint(center, BuildRadarAngle(index, metrics.Count), scoreRadius);
            }));
        AiEvaluationRadarCanvas.Children.Add(new Polygon
        {
            Points = scorePoints,
            Stroke = CreateBrush("#1D4ED8"),
            StrokeThickness = 2,
            Fill = CreateBrush("#331D4ED8")
        });

        foreach (var point in scorePoints)
        {
            var marker = new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = CreateBrush("#1D4ED8")
            };
            Canvas.SetLeft(marker, point.X - 3);
            Canvas.SetTop(marker, point.Y - 3);
            AiEvaluationRadarCanvas.Children.Add(marker);
        }
    }

    /// <summary>
    /// 鎸夌敤鎴锋寚瀹氬垎妗ｇ敓鎴愮洊绔犺瘎绾с€?
    /// Build the stamp rating from the user-defined score buckets.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static ScoreStamp BuildScoreStamp(int overallScore)
    {
        return overallScore switch
        {
            <= 50 => new ScoreStamp("拉完了", "#B91C1C"),
            <= 62 => new ScoreStamp("NPC", "#64748B"),
            <= 75 => new ScoreStamp("人上人", "#C2410C"),
            <= 88 => new ScoreStamp("顶级", "#7C3AED"),
            _ => new ScoreStamp("夯", "#2563EB")
        };
    }

    /// <summary>
    /// 鐢熸垚闆疯揪鍥惧崟灞傚杈瑰舰鍧愭爣銆?
    /// Build one polygon point collection for a radar-chart ring.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static PointCollection BuildRadarPointCollection(int metricCount, Point center, double radius)
    {
        return new PointCollection(
            Enumerable.Range(0, metricCount)
                .Select(index => BuildRadarPoint(center, BuildRadarAngle(index, metricCount), radius)));
    }

    /// <summary>
    /// 璁＄畻闆疯揪鍥炬寚瀹氳酱鐨勮搴︼紝绗竴杞翠粠姝ｄ笂鏂瑰紑濮嬨€?
    /// Compute the angle for one radar-chart axis, starting from the top axis.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static double BuildRadarAngle(int index, int metricCount)
    {
        return -Math.PI / 2.0 + (2.0 * Math.PI * index / metricCount);
    }

    /// <summary>
    /// 鏍规嵁涓績鐐广€佽搴﹀拰鍗婂緞璁＄畻闆疯揪鍥惧潗鏍囥€?
    /// Compute one radar-chart point from center, angle, and radius.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static Point BuildRadarPoint(Point center, double angle, double radius)
    {
        return new Point(
            center.X + Math.Cos(angle) * radius,
            center.Y + Math.Sin(angle) * radius);
    }

    /// <summary>
    /// 浠庡崄鍏繘鍒堕鑹叉枃鏈垱寤?WPF 鐢诲埛銆?
    /// Create a WPF brush from a hex color string.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static SolidColorBrush CreateBrush(string colorText)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText)!);
    }

    /// <summary>
    /// 鏍煎紡鍖栭」鐩鍙峰垪琛ㄣ€?
    /// Format a bullet list.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static string FormatBulletList(IReadOnlyList<string> items)
    {
        return items.Count == 0
            ? "- 无"
            : string.Join(Environment.NewLine, items.Select(item => "- " + item));
    }

    /// <summary>
    /// 格式化带五级评价的关键主张列表。
    /// Format key claims with five-level evaluation labels.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string FormatClaimEvaluationList(AiVideoEvaluationResult result)
    {
        return FormatBulletList(BuildClaimEvaluationDisplayItems(result));
    }

    /// <summary>
    /// 生成界面和报告共用的“主张（评价）”文本；旧结果没有逐条评价时回退到原主张。
    /// Build shared "claim (rating)" text for UI and reports; fall back to raw claims for legacy results.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static IReadOnlyList<string> BuildClaimEvaluationDisplayItems(AiVideoEvaluationResult result)
    {
        if (result.ClaimEvaluations.Count == 0)
        {
            return result.KeyClaims;
        }

        return result.ClaimEvaluations
            .Select(FormatClaimEvaluationItem)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    /// <summary>
    /// 将单条结构化主张评价拼成用户要求的短标签形式。
    /// Format one structured claim evaluation with the short user-facing label appended.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string FormatClaimEvaluationItem(AiClaimEvaluation evaluation)
    {
        var claim = string.IsNullOrWhiteSpace(evaluation.Claim)
            ? "未提取到有效主张"
            : evaluation.Claim.Trim();
        var rating = string.IsNullOrWhiteSpace(evaluation.Rating)
            ? "有失偏颇"
            : evaluation.Rating.Trim();

        return $"{claim}（{rating}）";
    }

    /// <summary>
    /// 鏍煎紡鍖栬瘉鎹垪琛ㄣ€?
    /// Format evidence list.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static string FormatEvidenceList(IReadOnlyList<AiEvaluationEvidence> evidences)
    {
        if (evidences.Count == 0)
        {
            return "- 无";
        }

        return string.Join(
            Environment.NewLine,
            evidences.Select((evidence, index) =>
                $"{index + 1}. {evidence.Title}{Environment.NewLine}"
                + $"   来源：{evidence.Source}{Environment.NewLine}"
                + $"   URL：{evidence.Url}{Environment.NewLine}"
                + $"   搜索词：{evidence.Query}"));
    }

    /// <summary>
    /// 灏嗘柇鍙?绾犻敊绋挎媶鎴愰瑙堢墖娈碉紱褰撳墠 AI 鏂彞/绾犻敊涓嶇敓鎴愭椂闂磋酱锛屽洜姝ゆ椂闂寸粺涓€缃浂銆?
    /// Split punctuated/corrected text into preview segments; AI punctuation/correction does not create timestamps, so time ranges are zero.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static IReadOnlyList<TranscriptSegment> BuildPreviewSegments(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length == 0
            ? [new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, "未识别到有效语音文本。")]
            : lines.Select(line => new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, line)).ToArray();
    }

    /// <summary>
    /// 闆疯揪鍥惧崟杞存暟鎹€?
    /// One radar-chart axis metric.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private sealed class RadarMetric
    {
        /// <summary>
        /// 鍒涘缓闆疯揪鍥惧崟杞存暟鎹€?
        /// Create one radar-chart axis metric.
        /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
        /// </summary>
        public RadarMetric(string label, int score)
        {
            Label = label;
            Score = score;
        }

        public string Label { get; }

        public int Score { get; }
    }

    /// <summary>
    /// 鐩栫珷璇勭骇鏄剧ず鏁版嵁銆?
    /// Stamp rating display data.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private sealed class ScoreStamp
    {
        /// <summary>
        /// 鍒涘缓鐩栫珷璇勭骇鏄剧ず鏁版嵁銆?
        /// Create stamp rating display data.
        /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
        /// </summary>
        public ScoreStamp(string label, string brushHex)
        {
            Label = label;
            BrushHex = brushHex;
        }

        public string Label { get; }

        public string BrushHex { get; }
    }

    /// <summary>
    /// 鍒ゆ柇閾炬帴鏄惁鐪嬭捣鏉ュ儚鐩存帴闊抽鏂囦欢銆?
    /// Detect whether the URL appears to point directly to an audio file.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-23锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static bool LooksLikeDirectAudioUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri))
        {
            return false;
        }

        var extension = IOPath.GetExtension(sourceUri.LocalPath);
        return DirectAudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 鏍煎紡鍖栧瓧鑺傛暟锛屼究浜庢樉绀烘湭鐭ユ€诲ぇ灏忕殑涓嬭浇杩涘害銆?
    /// Format bytes for progress display when the total size is unknown.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-23锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    /// <summary>
    /// 娓呯悊 Windows 鏂囦欢鍚嶉潪娉曞瓧绗︼紝骞堕檺鍒舵姤鍛婃枃浠跺悕闀垮害銆?
    /// Sanitize invalid Windows file-name characters and limit report name length.
    /// 鏈€杩戜慨鏀规椂闂达細2026-06-24锛涗慨鏀逛汉锛欸G銆?
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = IOPath.GetInvalidFileNameChars();
        var cleanChars = fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        var cleanName = new string(cleanChars).Trim();

        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return "AI评价";
        }

        return cleanName.Length <= 80
            ? cleanName
            : cleanName[..80];
    }

}

