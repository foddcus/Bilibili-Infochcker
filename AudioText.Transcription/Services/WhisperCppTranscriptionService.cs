using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;
using AudioText.Core.Utilities;

namespace AudioText.Transcription.Services;

/// <summary>
/// whisper.cpp 本地语音识别服务。
/// Local whisper.cpp transcription service.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class WhisperCppTranscriptionService : ITranscriptionService
{
    private const string PreferredModelFileName = "ggml-large-v3-turbo.bin";
    private const string LegacyFallbackModelFileName = "ggml-tiny.bin";
    private const int ChunkedTranscriptionThresholdMilliseconds = 5 * 60 * 1000;
    private const int ChunkDurationMilliseconds = 3 * 60 * 1000;
    private const int ChunkOverlapMilliseconds = 2500;
    private const int MinimumChunkDurationMilliseconds = 10 * 1000;

    private static readonly Regex WhisperProgressRegex = new(
        @"whisper_print_progress_callback:\s*progress\s*=\s*(?<percent>[0-9]+(?:\.[0-9]+)?)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ModelFileNames =
    [
        PreferredModelFileName,
        "ggml-large-v3-turbo-q8_0.bin",
        "ggml-large-v3-turbo-q5_0.bin",
        "ggml-large-v3.bin",
        "ggml-large-v3-q8_0.bin",
        "ggml-large-v3-q5_0.bin",
        "ggml-medium.bin",
        "ggml-medium-q8_0.bin",
        "ggml-medium-q5_0.bin",
        "ggml-small.bin",
        "ggml-small-q8_0.bin",
        "ggml-small-q5_0.bin",
        "ggml-base.bin",
        LegacyFallbackModelFileName
    ];

    private static readonly (string FileNameContains, string DisplayName)[] GpuBackendFileSignatures =
    [
        ("ggml-cuda", "NVIDIA CUDA"),
        ("cublas64", "NVIDIA CUDA"),
        ("ggml-vulkan", "Vulkan GPU"),
        ("vulkan-1", "Vulkan runtime"),
        ("ggml-hip", "AMD HIP/ROCm"),
        ("hipblas", "AMD HIP/ROCm"),
        ("ggml-opencl", "OpenCL GPU"),
        ("ggml-kompute", "Kompute/Vulkan GPU"),
        ("ggml-sycl", "Intel/oneAPI SYCL"),
        ("openvino", "OpenVINO GPU/NPU")
    ];

    private readonly string _baseDirectory;
    private readonly string _transcriptDirectory;

    /// <summary>
    /// 初始化 whisper.cpp 适配器，并记录工具与模型查找基准目录。
    /// Initialize the whisper.cpp adapter and remember probing roots for tools and models.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    public WhisperCppTranscriptionService(string baseDirectory, string? transcriptDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        _baseDirectory = Path.GetFullPath(baseDirectory);
        _transcriptDirectory = string.IsNullOrWhiteSpace(transcriptDirectory)
            ? Path.Combine(_baseDirectory, "输出文本")
            : Path.GetFullPath(transcriptDirectory);
    }

    /// <summary>
    /// 调用 whisper.cpp 完成本地语音转文字，并在进程运行期间回传识别进度。
    /// Run local whisper.cpp transcription and report recognition progress while the process is running.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputAudioPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(request.InputAudioPath))
        {
            throw new FileNotFoundException($"待转写音频文件不存在：{request.InputAudioPath}", request.InputAudioPath);
        }

        progress?.Report(new TranscriptionProgress(5, "正在准备 whisper.cpp 本地语音识别。"));

        var whisperCliPath = ResolveWhisperCliPath();
        progress?.Report(new TranscriptionProgress(10, BuildAccelerationMessage(whisperCliPath)));

        var modelPath = ResolveModelPath(request.ModelPath);
        var commandPaths = BuildCommandPaths(modelPath);

        Directory.CreateDirectory(_transcriptDirectory);

        var outputPrefix = Path.Combine(_transcriptDirectory, BuildOutputFileStem(request));
        var rawOutputPath = outputPrefix + ".txt";

        progress?.Report(new TranscriptionProgress(
            15,
            $"正在调用 whisper.cpp 转写：{Path.GetFileName(request.InputAudioPath)}"));

        var audioDurationMilliseconds = await TryProbeAudioDurationMillisecondsAsync(
            request.InputAudioPath,
            cancellationToken);
        var chunkPlan = BuildTranscriptionChunkPlan(audioDurationMilliseconds);
        var transcriptText = chunkPlan.Count > 1
            ? await RunChunkedWhisperAndMaterializeAsync(
                whisperCliPath,
                commandPaths,
                request.InputAudioPath,
                request.Language,
                chunkPlan,
                rawOutputPath,
                progress,
                cancellationToken)
            : await RunSingleWhisperAndMaterializeAsync(
                whisperCliPath,
                commandPaths,
                request.InputAudioPath,
                request.Language,
                rawOutputPath,
                progress,
                cancellationToken);
        var simplifiedTranscriptText = ChineseTextNormalizer.ToSimplifiedChinese(transcriptText);
        if (!string.Equals(transcriptText, simplifiedTranscriptText, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(rawOutputPath, simplifiedTranscriptText, Encoding.UTF8, cancellationToken);
        }

        var normalizedText = NormalizeTranscriptText(simplifiedTranscriptText);

        var document = new TranscriptDocument(
            Title: BuildDocumentTitle(request),
            Source: request.SourceUrl ?? request.InputAudioPath,
            CreatedAt: DateTimeOffset.Now,
            Segments: BuildSegments(normalizedText));

        progress?.Report(new TranscriptionProgress(
            100,
            $"转写完成：{rawOutputPath}",
            document.PlainText));

        return new TranscriptionResult(document, rawOutputPath);
    }

    /// <summary>
    /// 对短音频保留单次 whisper.cpp 推理路径，减少不必要的外部进程开销。
    /// Keep the single-pass whisper.cpp path for short audio to avoid unnecessary process overhead.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static async Task<string> RunSingleWhisperAndMaterializeAsync(
        string whisperCliPath,
        WhisperCommandPaths commandPaths,
        string inputAudioPath,
        string? language,
        string rawOutputPath,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var processResult = await RunWhisperAsync(
            whisperCliPath,
            commandPaths.WorkingDirectory,
            commandPaths.ModelArgument,
            inputAudioPath,
            language,
            commandPaths.TemporaryOutputPrefixArgument,
            progressLabel: "本地语音转文字",
            uiPercentStart: 15,
            uiPercentSpan: 70,
            offsetMilliseconds: 0,
            durationMilliseconds: 0,
            progress: progress,
            cancellationToken: cancellationToken);

        if (processResult.ExitCode != 0)
        {
            throw new InvalidOperationException(BuildWhisperFailureMessage(processResult));
        }

        progress?.Report(new TranscriptionProgress(90, "正在读取 whisper.cpp 转写文本。"));

        return await MaterializeTranscriptTextAsync(
            commandPaths.TemporaryRawOutputPath,
            rawOutputPath,
            processResult.StandardOutput,
            cancellationToken);
    }

    /// <summary>
    /// 对长音频分段调用 whisper.cpp，避免低置信片段或长上下文导致后半段重复、漏写。
    /// Run whisper.cpp in time chunks for long audio so one low-confidence span cannot collapse the rest of the transcript.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static async Task<string> RunChunkedWhisperAndMaterializeAsync(
        string whisperCliPath,
        WhisperCommandPaths commandPaths,
        string inputAudioPath,
        string? language,
        IReadOnlyList<WhisperAudioChunk> chunkPlan,
        string rawOutputPath,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new TranscriptionProgress(
            14,
            $"检测到长音频，启用分段转写：{chunkPlan.Count} 段，每段约 {ChunkDurationMilliseconds / 1000 / 60} 分钟。"));

        var chunkTexts = new List<string>(chunkPlan.Count);
        var standardOutputBuilder = new StringBuilder();
        var standardErrorBuilder = new StringBuilder();

        for (var index = 0; index < chunkPlan.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunkPlan[index];
            var suffix = $"_part{index + 1:000}";
            var temporaryOutputPrefix = commandPaths.TemporaryOutputPrefixArgument + suffix;
            var temporaryRawOutputPath = BuildChunkTemporaryRawOutputPath(commandPaths, suffix);
            var chunkPercentStart = 15 + (index * 70.0 / chunkPlan.Count);
            var chunkPercentSpan = 70.0 / chunkPlan.Count;

            progress?.Report(new TranscriptionProgress(
                chunkPercentStart,
                $"正在转写第 {index + 1}/{chunkPlan.Count} 段，起点 {chunk.OffsetMilliseconds / 1000.0:0.0} 秒。"));

            var processResult = await RunWhisperAsync(
                whisperCliPath,
                commandPaths.WorkingDirectory,
                commandPaths.ModelArgument,
                inputAudioPath,
                language,
                temporaryOutputPrefix,
                progressLabel: $"本地语音转文字 第 {index + 1}/{chunkPlan.Count} 段",
                uiPercentStart: chunkPercentStart,
                uiPercentSpan: chunkPercentSpan,
                offsetMilliseconds: chunk.OffsetMilliseconds,
                durationMilliseconds: chunk.DurationMilliseconds,
                progress: progress,
                cancellationToken: cancellationToken);

            standardOutputBuilder.AppendLine(processResult.StandardOutput);
            standardErrorBuilder.AppendLine(processResult.StandardError);
            if (processResult.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildWhisperFailureMessage(processResult));
            }

            var chunkText = await ReadTemporaryTranscriptTextAsync(
                temporaryRawOutputPath,
                processResult.StandardOutput,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(chunkText))
            {
                chunkTexts.Add(chunkText);
            }

            TryDeleteFile(temporaryRawOutputPath);
        }

        progress?.Report(new TranscriptionProgress(90, "正在合并分段转写文本。"));
        var mergedText = MergeTranscriptChunks(chunkTexts);
        if (string.IsNullOrWhiteSpace(mergedText))
        {
            var aggregateResult = new WhisperProcessResult(
                ExitCode: 0,
                StandardOutput: standardOutputBuilder.ToString(),
                StandardError: standardErrorBuilder.ToString());
            throw new InvalidOperationException(BuildWhisperFailureMessage(aggregateResult));
        }

        await File.WriteAllTextAsync(rawOutputPath, mergedText, Encoding.UTF8, cancellationToken);
        return mergedText;
    }

    /// <summary>
    /// 调用 whisper-cli.exe 生成纯文本输出。
    /// Run whisper-cli.exe to generate plain text output.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static async Task<WhisperProcessResult> RunWhisperAsync(
        string whisperCliPath,
        string workingDirectory,
        string modelArgument,
        string inputAudioPath,
        string? language,
        string outputPrefixArgument,
        string progressLabel,
        double uiPercentStart,
        double uiPercentSpan,
        int offsetMilliseconds,
        int durationMilliseconds,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = whisperCliPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(modelArgument);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(inputAudioPath);
        if (offsetMilliseconds > 0)
        {
            startInfo.ArgumentList.Add("-ot");
            startInfo.ArgumentList.Add(offsetMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        if (durationMilliseconds > 0)
        {
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(durationMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(language);
        }

        // GPU 约定：不传 --no-gpu / -ng。若当前 whisper.cpp 二进制带 CUDA/Vulkan/OpenCL 等后端，会由 whisper.cpp 自动优先使用可用 GPU。
        // GPU rule: never pass --no-gpu / -ng. A GPU-enabled whisper.cpp build can automatically use its available backend.
        // -otxt 输出纯文本文件；-nt 去除时间戳；-np 减少控制台噪声，避免 UI 日志被模型内部信息淹没。
        // -pp 输出 whisper.cpp 推理进度，供 WPF 进度条实时显示。
        // -mc 0 清空跨窗口文本上下文；-nf 关闭温度回退；-sns 抑制非语音标记，降低长视频低置信片段循环复读的概率。
        // -mc 0 resets cross-window context; -nf disables temperature fallback; -sns suppresses non-speech tokens to reduce long-video repetition loops.
        // -otxt writes a plain text file; -nt removes timestamps; -np reduces console noise; -pp prints progress for the WPF progress bar.
        startInfo.ArgumentList.Add("-otxt");
        startInfo.ArgumentList.Add("-nt");
        startInfo.ArgumentList.Add("-np");
        startInfo.ArgumentList.Add("-pp");
        startInfo.ArgumentList.Add("-mc");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-nf");
        startInfo.ArgumentList.Add("-sns");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add(outputPrefixArgument);

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"无法启动 whisper.cpp：{ex.Message}", ex);
        }

        var standardOutputBuilder = new StringBuilder();
        var standardErrorBuilder = new StringBuilder();
        var standardOutputTask = ReadWhisperOutputAsync(
            process.StandardOutput,
            standardOutputBuilder,
            progressLabel,
            uiPercentStart,
            uiPercentSpan,
            progress,
            cancellationToken);
        var standardErrorTask = ReadWhisperOutputAsync(
            process.StandardError,
            standardErrorBuilder,
            progressLabel,
            uiPercentStart,
            uiPercentSpan,
            progress,
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        return new WhisperProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    /// <summary>
    /// 构建本地推理加速提示，说明当前 whisper.cpp 工具包是否带常见 GPU 后端。
    /// Build an acceleration message that explains whether common GPU backends are present.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string BuildAccelerationMessage(string whisperCliPath)
    {
        var backendName = DetectWhisperGpuBackendName(whisperCliPath);
        return string.IsNullOrWhiteSpace(backendName)
            ? "未检测到常见 whisper.cpp GPU 后端 DLL，当前工具包预计使用 CPU；替换为 CUDA/Vulkan/OpenCL/OpenVINO 版后程序不会禁用 GPU。"
            : $"检测到 whisper.cpp GPU 后端：{backendName}；程序未传入 --no-gpu，将优先让 whisper.cpp 使用可用 GPU。";
    }

    /// <summary>
    /// 根据 whisper.cpp 工具目录中的后端 DLL 判断是否存在 GPU 推理能力。
    /// Detect likely GPU inference support from backend DLLs near whisper-cli.exe.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? DetectWhisperGpuBackendName(string whisperCliPath)
    {
        var toolDirectory = Path.GetDirectoryName(whisperCliPath);
        if (string.IsNullOrWhiteSpace(toolDirectory) || !Directory.Exists(toolDirectory))
        {
            return null;
        }

        foreach (var dllPath in Directory.EnumerateFiles(toolDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(dllPath);
            foreach (var signature in GpuBackendFileSignatures)
            {
                if (fileName.Contains(signature.FileNameContains, StringComparison.OrdinalIgnoreCase))
                {
                    return signature.DisplayName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 实时读取 whisper.cpp 输出流，并从进度回调行中提取识别百分比。
    /// Read one whisper.cpp output stream live and extract recognition percentage from progress callback lines.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static async Task<string> ReadWhisperOutputAsync(
        StreamReader reader,
        StringBuilder outputBuilder,
        string progressLabel,
        double uiPercentStart,
        double uiPercentSpan,
        IProgress<TranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            outputBuilder.AppendLine(line);
            ReportWhisperProgressIfAvailable(line, progressLabel, uiPercentStart, uiPercentSpan, progress);
        }

        return outputBuilder.ToString();
    }

    /// <summary>
    /// 将 whisper.cpp 原始推理进度映射到语音转文字阶段的界面进度。
    /// Map raw whisper.cpp inference progress to the UI transcription-stage progress range.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static void ReportWhisperProgressIfAvailable(
        string outputLine,
        string progressLabel,
        double uiPercentStart,
        double uiPercentSpan,
        IProgress<TranscriptionProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        var match = WhisperProgressRegex.Match(outputLine);
        if (!match.Success)
        {
            return;
        }

        if (!double.TryParse(
                match.Groups["percent"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var enginePercent))
        {
            return;
        }

        var clampedEnginePercent = Math.Clamp(enginePercent, 0, 100);
        var uiPercent = uiPercentStart + clampedEnginePercent * uiPercentSpan / 100.0;
        progress.Report(new TranscriptionProgress(
            uiPercent,
            $"{progressLabel}：{clampedEnginePercent:0.0}%"));
    }

    /// <summary>
    /// 使用 ffprobe 读取音频时长；读取失败时返回空值，并回退到单次转写路径。
    /// Probe audio duration with ffprobe; return null on failure so transcription can fall back to the single-pass path.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private async Task<double?> TryProbeAudioDurationMillisecondsAsync(
        string inputAudioPath,
        CancellationToken cancellationToken)
    {
        var ffprobePath = ResolveFfprobePath();
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
        startInfo.ArgumentList.Add(inputAudioPath);

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var standardOutput = (await standardOutputTask).Trim();
        _ = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            return null;
        }

        return double.TryParse(
            standardOutput,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var durationSeconds) && durationSeconds > 0
            ? durationSeconds * 1000.0
            : null;
    }

    /// <summary>
    /// 构建长音频分段计划；短音频或未知时长返回空计划。
    /// Build a chunk plan for long audio; short or unknown-duration audio returns an empty plan.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlyList<WhisperAudioChunk> BuildTranscriptionChunkPlan(double? audioDurationMilliseconds)
    {
        if (!audioDurationMilliseconds.HasValue
            || audioDurationMilliseconds.Value <= ChunkedTranscriptionThresholdMilliseconds)
        {
            return Array.Empty<WhisperAudioChunk>();
        }

        var chunks = new List<WhisperAudioChunk>();
        var offsetMilliseconds = 0;
        var totalMilliseconds = (int)Math.Ceiling(audioDurationMilliseconds.Value);
        while (offsetMilliseconds < totalMilliseconds)
        {
            var remainingMilliseconds = totalMilliseconds - offsetMilliseconds;
            if (remainingMilliseconds < MinimumChunkDurationMilliseconds && chunks.Count > 0)
            {
                break;
            }

            var durationMilliseconds = Math.Min(ChunkDurationMilliseconds, remainingMilliseconds);
            chunks.Add(new WhisperAudioChunk(offsetMilliseconds, durationMilliseconds));
            if (offsetMilliseconds + durationMilliseconds >= totalMilliseconds)
            {
                break;
            }

            offsetMilliseconds += ChunkDurationMilliseconds - ChunkOverlapMilliseconds;
        }

        return chunks;
    }

    /// <summary>
    /// 生成某个分段对应的 whisper.cpp 临时输出路径。
    /// Build the whisper.cpp temporary output path for one chunk.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string BuildChunkTemporaryRawOutputPath(WhisperCommandPaths commandPaths, string suffix)
    {
        var directory = Path.GetDirectoryName(commandPaths.TemporaryRawOutputPath) ?? commandPaths.WorkingDirectory;
        var stem = Path.GetFileNameWithoutExtension(commandPaths.TemporaryRawOutputPath);
        return Path.Combine(directory, stem + suffix + ".txt");
    }

    /// <summary>
    /// 读取某个分段的临时转写文本；空分段允许返回空字符串，供最终合并阶段统一判断。
    /// Read one chunk's temporary transcript; empty chunks may return an empty string and are handled during final merge.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static async Task<string> ReadTemporaryTranscriptTextAsync(
        string temporaryRawOutputPath,
        string standardOutput,
        CancellationToken cancellationToken)
    {
        if (File.Exists(temporaryRawOutputPath))
        {
            var temporaryText = await File.ReadAllTextAsync(temporaryRawOutputPath, Encoding.UTF8, cancellationToken);
            if (!string.IsNullOrWhiteSpace(temporaryText))
            {
                return temporaryText;
            }
        }

        return string.IsNullOrWhiteSpace(standardOutput)
            ? string.Empty
            : standardOutput;
    }

    /// <summary>
    /// 合并分段转写结果，并清理重叠窗口带来的边界重复。
    /// Merge chunk transcripts and remove boundary duplication caused by chunk overlap.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string MergeTranscriptChunks(IReadOnlyList<string> chunkTexts)
    {
        var builder = new StringBuilder();
        foreach (var chunkText in chunkTexts)
        {
            var normalizedChunk = NormalizeTranscriptText(chunkText);
            if (string.IsNullOrWhiteSpace(normalizedChunk))
            {
                continue;
            }

            var textToAppend = RemoveLeadingBoundaryOverlap(builder.ToString(), normalizedChunk);
            if (string.IsNullOrWhiteSpace(textToAppend))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(textToAppend);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 删除后一段开头与前一段末尾的短重复，避免分段重叠造成同一句出现两次。
    /// Remove short prefix/suffix duplication between adjacent chunks caused by the overlap window.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string RemoveLeadingBoundaryOverlap(string previousText, string nextText)
    {
        if (string.IsNullOrWhiteSpace(previousText))
        {
            return nextText.Trim();
        }

        var overlapLength = DetectNormalizedBoundaryOverlap(previousText, nextText, maxOverlapLength: 80, minOverlapLength: 8);
        return overlapLength <= 0
            ? nextText.Trim()
            : RemoveLeadingNonWhitespaceCharacters(nextText, overlapLength).Trim();
    }

    /// <summary>
    /// 在去空白后的文本上检测前后两段边界重叠长度。
    /// Detect boundary overlap length after whitespace-insensitive normalization.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static int DetectNormalizedBoundaryOverlap(
        string previousText,
        string nextText,
        int maxOverlapLength,
        int minOverlapLength)
    {
        var normalizedPrevious = NormalizeForBoundaryOverlap(previousText);
        var normalizedNext = NormalizeForBoundaryOverlap(nextText);
        var maxLength = Math.Min(maxOverlapLength, Math.Min(normalizedPrevious.Length, normalizedNext.Length));

        for (var length = maxLength; length >= minOverlapLength; length--)
        {
            if (normalizedPrevious.EndsWith(normalizedNext[..length], StringComparison.Ordinal))
            {
                return length;
            }
        }

        return 0;
    }

    /// <summary>
    /// 从原始文本开头移除指定数量的非空白字符，同时保留后续正文。
    /// Remove a number of non-whitespace characters from the start while preserving the remaining text.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string RemoveLeadingNonWhitespaceCharacters(string text, int characterCount)
    {
        var removedCharacters = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsWhiteSpace(text[index]))
            {
                removedCharacters++;
            }

            if (removedCharacters >= characterCount)
            {
                return text[(index + 1)..];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 生成边界重叠检测用文本，忽略空白差异但不改变具体汉字或英文字符。
    /// Build a boundary-overlap comparison string, ignoring whitespace but preserving actual characters.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string NormalizeForBoundaryOverlap(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 生成 whisper.cpp 命令行路径参数，规避 Windows 版本对中文绝对模型/输出路径不稳定的问题。
    /// Build whisper.cpp command paths, avoiding unstable Chinese absolute model/output paths on Windows builds.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private WhisperCommandPaths BuildCommandPaths(string modelPath)
    {
        var workingDirectory = ResolveCommandWorkingDirectory(modelPath);
        var modelArgument = Path.GetRelativePath(workingDirectory, modelPath);
        var temporaryOutputDirectory = Path.Combine(workingDirectory, ".whisper-output");
        Directory.CreateDirectory(temporaryOutputDirectory);

        var temporaryOutputStem = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
            + "_"
            + Guid.NewGuid().ToString("N");
        var temporaryOutputPrefixArgument = Path.Combine(".whisper-output", temporaryOutputStem);
        var temporaryRawOutputPath = Path.Combine(temporaryOutputDirectory, temporaryOutputStem + ".txt");

        return new WhisperCommandPaths(
            workingDirectory,
            modelArgument,
            temporaryOutputPrefixArgument,
            temporaryRawOutputPath);
    }

    /// <summary>
    /// 选择命令工作目录，使模型路径可以用相对路径传递给 whisper.cpp。
    /// Choose a command working directory so the model path can be passed to whisper.cpp as a relative path.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private string ResolveCommandWorkingDirectory(string modelPath)
    {
        foreach (var baseDirectory in EnumerateCandidateBaseDirectories())
        {
            if (IsPathUnderDirectory(modelPath, Path.Combine(baseDirectory, "models"))
                || IsPathUnderDirectory(modelPath, Path.Combine(baseDirectory, "tools", "whisper", "models")))
            {
                return baseDirectory;
            }
        }

        return Path.GetDirectoryName(modelPath) ?? _baseDirectory;
    }

    /// <summary>
    /// 判断文件路径是否位于指定目录下。
    /// Determine whether a file path is located under a target directory.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static bool IsPathUnderDirectory(string filePath, string directoryPath)
    {
        var fullFilePath = Path.GetFullPath(filePath);
        var fullDirectoryPath = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return fullFilePath.StartsWith(fullDirectoryPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 查找 whisper-cli.exe，优先使用软件目录或源码根目录下的 tools/whisper/Release。
    /// Resolve whisper-cli.exe, preferring tools/whisper/Release under runtime or source roots.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private string ResolveWhisperCliPath()
    {
        var candidates = new List<string>();

        foreach (var baseDirectory in EnumerateCandidateBaseDirectories())
        {
            candidates.Add(Path.Combine(baseDirectory, "tools", "whisper", "Release", "whisper-cli.exe"));
            candidates.Add(Path.Combine(baseDirectory, "tools", "whisper", "whisper-cli.exe"));
            candidates.Add(Path.Combine(baseDirectory, "tools", "whisper-cli.exe"));
            candidates.Add(Path.Combine(baseDirectory, "whisper-cli.exe"));
        }

        candidates.AddRange(ResolveExecutableFromPath("whisper-cli.exe"));

        var resolvedPath = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);

        if (resolvedPath is null)
        {
            throw new FileNotFoundException(
                "未找到 whisper-cli.exe。请将 whisper.cpp Windows x64 Release 解压到 tools/whisper/Release，" +
                "或将 whisper-cli.exe 所在目录加入 PATH。");
        }

        return resolvedPath;
    }

    /// <summary>
    /// 查找 ffprobe.exe，用于读取音频时长并决定是否启用分段转写。
    /// Resolve ffprobe.exe so audio duration can decide whether chunked transcription is needed.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private string? ResolveFfprobePath()
    {
        var candidates = new List<string>();

        foreach (var baseDirectory in EnumerateCandidateBaseDirectories())
        {
            candidates.Add(Path.Combine(baseDirectory, "tools", "ffprobe.exe"));
            candidates.Add(Path.Combine(baseDirectory, "ffprobe.exe"));
        }

        candidates.AddRange(ResolveExecutableFromPath("ffprobe.exe"));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// 查找 whisper.cpp 模型文件，优先使用 large-v3-turbo / large-v3 等高精度模型，再退回 tiny。
    /// Resolve the whisper.cpp model file, preferring large-v3-turbo / large-v3 before falling back to tiny.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private string ResolveModelPath(string? requestedModelPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedModelPath))
        {
            var fullRequestedPath = Path.GetFullPath(requestedModelPath);
            if (File.Exists(fullRequestedPath))
            {
                return fullRequestedPath;
            }

            throw new FileNotFoundException($"指定的 whisper.cpp 模型文件不存在：{requestedModelPath}", requestedModelPath);
        }

        var candidates = new List<string>();

        foreach (var baseDirectory in EnumerateCandidateBaseDirectories())
        {
            foreach (var modelFileName in ModelFileNames)
            {
                candidates.Add(Path.Combine(baseDirectory, "models", modelFileName));
                candidates.Add(Path.Combine(baseDirectory, "tools", "whisper", "models", modelFileName));
            }
        }

        var resolvedPath = candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);

        if (resolvedPath is null)
        {
            throw new FileNotFoundException(
                $"未找到 whisper.cpp 模型文件。建议将 {PreferredModelFileName} 放入 models 子文件夹；临时轻量模式可放入 {LegacyFallbackModelFileName}，" +
                "或在后续配置界面中指定模型路径。");
        }

        return resolvedPath;
    }

    /// <summary>
    /// 生成可能的查找基准目录，允许调试运行目录向上回溯到源码根目录。
    /// Build probing roots, allowing debug runtime directories to walk back to the source root.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private IEnumerable<string> EnumerateCandidateBaseDirectories()
    {
        var seeds = new[]
        {
            _baseDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds.Where(seed => !string.IsNullOrWhiteSpace(seed)))
        {
            var directoryInfo = new DirectoryInfo(Path.GetFullPath(seed));

            while (directoryInfo is not null)
            {
                if (visited.Add(directoryInfo.FullName))
                {
                    yield return directoryInfo.FullName;
                }

                directoryInfo = directoryInfo.Parent;
            }
        }
    }

    /// <summary>
    /// 从 PATH 中查找可执行文件。
    /// Resolve an executable from PATH.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static IEnumerable<string> ResolveExecutableFromPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = pathEntry.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return Path.Combine(directory, executableName);
            }
        }
    }

    /// <summary>
    /// 将 whisper.cpp 的 ASCII 临时输出复制到软件输出目录，必要时回退到标准输出。
    /// Copy whisper.cpp ASCII temporary output to the application output directory, falling back to stdout when necessary.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static async Task<string> MaterializeTranscriptTextAsync(
        string temporaryRawOutputPath,
        string rawOutputPath,
        string standardOutput,
        CancellationToken cancellationToken)
    {
        var rawOutputDirectory = Path.GetDirectoryName(rawOutputPath);
        if (!string.IsNullOrWhiteSpace(rawOutputDirectory))
        {
            Directory.CreateDirectory(rawOutputDirectory);
        }

        if (File.Exists(temporaryRawOutputPath))
        {
            var temporaryText = await File.ReadAllTextAsync(temporaryRawOutputPath, Encoding.UTF8, cancellationToken);
            if (!string.IsNullOrWhiteSpace(temporaryText))
            {
                await File.WriteAllTextAsync(rawOutputPath, temporaryText, Encoding.UTF8, cancellationToken);
                TryDeleteFile(temporaryRawOutputPath);
                return temporaryText;
            }
        }

        if (File.Exists(rawOutputPath))
        {
            var fileText = await File.ReadAllTextAsync(rawOutputPath, Encoding.UTF8, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fileText))
            {
                return fileText;
            }
        }

        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            await File.WriteAllTextAsync(rawOutputPath, standardOutput, Encoding.UTF8, cancellationToken);
            return standardOutput;
        }

        throw new InvalidOperationException("whisper.cpp 已结束，但没有生成可读取的转写文本。");
    }

    /// <summary>
    /// 尝试删除 whisper.cpp 临时输出文件。
    /// Try to delete a whisper.cpp temporary output file.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 将文本整理为界面预览适合的多行形式。
    /// Normalize text into a multi-line form suitable for preview.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string NormalizeTranscriptText(string transcriptText)
    {
        var lines = transcriptText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 将纯文本拆为片段；当前 whisper 纯文本不保留时间戳，因此片段时间统一置零。
    /// Split plain text into segments; timestamps are zero because the plain text output omits them.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static IReadOnlyList<TranscriptSegment> BuildSegments(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return
            [
                new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, "未识别到有效语音文本。")
            ];
        }

        return normalizedText
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new TranscriptSegment(TimeSpan.Zero, TimeSpan.Zero, line))
            .ToArray();
    }

    /// <summary>
    /// 构建输出文件名前缀，避免平台标题中的非法字符破坏保存路径。
    /// Build an output file stem and remove invalid file-name characters from platform titles.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string BuildOutputFileStem(TranscriptionRequest request)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var title = BuildDocumentTitle(request);
        return $"{timestamp}_{MakeSafeFileName(title)}";
    }

    /// <summary>
    /// 构建文档标题，优先使用下载器返回的媒体标题。
    /// Build the document title, preferring the media title returned by the downloader.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string BuildDocumentTitle(TranscriptionRequest request)
    {
        var repairedRequestTitle = TextEncodingRepair.RepairOrNull(request.Title);
        if (!string.IsNullOrWhiteSpace(repairedRequestTitle))
        {
            return repairedRequestTitle;
        }

        return TextEncodingRepair.RepairOrNull(Path.GetFileNameWithoutExtension(request.InputAudioPath))
            ?? "视频转写文本";
    }

    /// <summary>
    /// 清理文件名并限制长度，防止 Windows 路径过长。
    /// Sanitize and shorten file names to avoid invalid characters and long Windows paths.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string MakeSafeFileName(string rawName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(rawName.Length);

        foreach (var character in rawName)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        var safeName = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "transcript";
        }

        return safeName.Length <= 80 ? safeName : safeName[..80];
    }

    /// <summary>
    /// 生成 whisper.cpp 失败详情，保留 stderr/stdout 尾部，便于定位模型或音频格式问题。
    /// Build whisper.cpp failure details, retaining stderr/stdout tails for troubleshooting.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string BuildWhisperFailureMessage(WhisperProcessResult processResult)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"whisper.cpp 音频转文字失败，退出码：{processResult.ExitCode}。");

        var standardErrorTail = TakeTail(processResult.StandardError);
        if (!string.IsNullOrWhiteSpace(standardErrorTail))
        {
            builder.AppendLine();
            builder.AppendLine(standardErrorTail);
        }

        var standardOutputTail = TakeTail(processResult.StandardOutput);
        if (!string.IsNullOrWhiteSpace(standardOutputTail))
        {
            builder.AppendLine();
            builder.AppendLine(standardOutputTail);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 截取长日志尾部，避免异常消息过大。
    /// Take the tail of long process logs to keep exception messages readable.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static string TakeTail(string value, int maxLength = 4000)
    {
        var trimmedValue = value.Trim();
        return trimmedValue.Length <= maxLength
            ? trimmedValue
            : trimmedValue[^maxLength..];
    }

    /// <summary>
    /// 尝试终止取消中的 whisper.cpp 子进程。
    /// Try to terminate a canceled whisper.cpp child process.
    /// 最近修改时间：2026-06-23；修改人：GG。
    /// </summary>
    private static void TryKillProcess(Process process)
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

    private sealed record WhisperProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record WhisperCommandPaths(
        string WorkingDirectory,
        string ModelArgument,
        string TemporaryOutputPrefixArgument,
        string TemporaryRawOutputPath);

    private sealed record WhisperAudioChunk(
        int OffsetMilliseconds,
        int DurationMilliseconds);
}
