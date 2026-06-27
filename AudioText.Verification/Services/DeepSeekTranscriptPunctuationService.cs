using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Verification.Services;

/// <summary>
/// DeepSeek 转写文本断句与错别字修正服务。
/// DeepSeek-based transcript punctuation and typo-correction service.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class DeepSeekTranscriptPunctuationService : ITranscriptPunctuationService
{
    private const int MaxChunkCharacters = 3200;

    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadableJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AiVerificationSettings _settings;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 创建 DeepSeek 转写文本断句与错别字修正服务。
    /// Create a DeepSeek transcript punctuation and typo-correction service.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public DeepSeekTranscriptPunctuationService(
        AiVerificationSettings settings,
        HttpClient? httpClient = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// 使用独立 AI 会话为 whisper.cpp 原始转写文本补充断句、基础标点并保守修正明显错别字。
    /// Use an independent AI session to add sentence boundaries, punctuation, and conservative typo corrections to raw whisper.cpp text.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public async Task<TranscriptPunctuationResult> PunctuateAsync(
        TranscriptPunctuationRequest request,
        IProgress<TranscriptPunctuationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PlainText);
        ArgumentException.ThrowIfNullOrWhiteSpace(_settings.ApiKey);

        var normalizedText = NormalizeTranscriptText(request.PlainText);
        var chunks = SplitIntoChunks(normalizedText, MaxChunkCharacters).ToList();
        var punctuatedChunks = new List<string>(chunks.Count);
        var rawOutputs = new List<string>(chunks.Count);

        progress?.Report(new TranscriptPunctuationProgress(5, "正在准备 AI 断句/纠错。"));

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var percent = 10 + (index * 80.0 / Math.Max(1, chunks.Count));
            progress?.Report(new TranscriptPunctuationProgress(
                percent,
                $"正在 AI 断句/纠错：第 {index + 1}/{chunks.Count} 段。"));

            var rawOutput = await PunctuateChunkAsync(
                request,
                chunks[index],
                index + 1,
                chunks.Count,
                cancellationToken);
            rawOutputs.Add(rawOutput);

            var punctuatedText = ExtractPunctuatedText(rawOutput);
            if (string.IsNullOrWhiteSpace(punctuatedText))
            {
                punctuatedText = chunks[index];
            }

            punctuatedChunks.Add(punctuatedText.Trim());
        }

        var mergedText = string.Join(Environment.NewLine + Environment.NewLine, punctuatedChunks)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        progress?.Report(new TranscriptPunctuationProgress(100, "AI 断句/纠错完成。"));
        return new TranscriptPunctuationResult(
            mergedText,
            string.Join(Environment.NewLine + Environment.NewLine, rawOutputs));
    }

    /// <summary>
    /// 对单个文本块执行断句/纠错，避免长视频一次请求过大。
    /// Punctuate and correct one transcript chunk to avoid oversized single requests for long videos.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private async Task<string> PunctuateChunkAsync(
        TranscriptPunctuationRequest request,
        string chunkText,
        int chunkIndex,
        int chunkCount,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessageDto>
        {
            CreateMessage(
                "system",
                """
                你是语音转写文本断句与错别字修正助手。
                任务：为无标点或少标点的中文转写文本补充断句、逗号、句号、问号、顿号、必要换行，并保守修正明显错别字。
                必须遵守：
                1. 不总结，不改写，不增删事实，不扩写内容。
                2. 尽量保留原词、原顺序和原语气，只修正明显的连续口语断句和同音、近音、口音导致的错别字。
                3. 输出简体中文。
                4. 只输出 JSON 对象，不输出 Markdown。
                """),
            CreateMessage(
                "user",
                BuildUserPrompt(request, chunkText, chunkIndex, chunkCount))
        };

        return await CallChatCompletionAsync(messages, cancellationToken);
    }

    /// <summary>
    /// 调用 DeepSeek Chat Completions API 执行断句/纠错。
    /// Call the DeepSeek Chat Completions API for punctuation and typo correction.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private async Task<string> CallChatCompletionAsync(
        IReadOnlyList<ChatMessageDto> messages,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequestDto
        {
            Model = _settings.Model,
            Messages = messages,
            Thinking = new ThinkingDto { Type = "disabled" },
            Temperature = 0,
            MaxTokens = 4096,
            ResponseFormat = new ResponseFormatDto { Type = "json_object" }
        };

        var requestJson = JsonSerializer.Serialize(request, ApiJsonOptions);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{_settings.BaseUrl.TrimEnd('/')}/chat/completions"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"DeepSeek AI 断句/纠错请求失败：HTTP {(int)response.StatusCode}。{TruncateText(responseText, 900)}");
        }

        var completion = JsonSerializer.Deserialize<ChatCompletionResponseDto>(responseText, ApiJsonOptions);
        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("DeepSeek AI 断句/纠错未返回有效文本内容。");
        }

        return content;
    }

    /// <summary>
    /// 构造断句用户提示词。
    /// Build the user prompt for transcript punctuation.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string BuildUserPrompt(
        TranscriptPunctuationRequest request,
        string chunkText,
        int chunkIndex,
        int chunkCount)
    {
        return $$"""
        请为下面的语音转写文本补充中文断句和标点，并保守修正明显错别字。

        输出 JSON 格式：
        {
          "punctuated_text": "补充断句并修正明显错别字后的文本"
        }

        注意：
        - 不要增加原文没有的信息。
        - 不要删除原文中的关键名词、数字、时间、地点、人名、机构名。
        - 只修正根据上下文可以明确判断的错别字，例如同音、近音、口音或发音不准导致的错误。
        - 专有名词、数字、时间、地点、人名、机构名不确定时保留原文，不要强行替换。
        - 不要润色、扩写或改成书面表达；仅做断句、标点和保守错别字修正。
        - 段落之间可用换行分隔，但不要输出解释。

        视频标题：{{request.Title ?? "未知"}}
        来源链接：{{request.SourceUrl ?? "未知"}}
        文本块：{{chunkIndex}} / {{chunkCount}}

        原始转写文本：
        {{chunkText}}
        """;
    }

    /// <summary>
    /// 从模型 JSON 输出中读取断句文本。
    /// Read punctuated text from the model JSON output.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string ExtractPunctuatedText(string rawOutput)
    {
        var first = rawOutput.IndexOf("{", StringComparison.Ordinal);
        var last = rawOutput.LastIndexOf("}", StringComparison.Ordinal);
        if (first < 0 || last <= first)
        {
            return rawOutput.Trim();
        }

        try
        {
            var json = rawOutput[first..(last + 1)];
            var dto = JsonSerializer.Deserialize<PunctuationDto>(json, ReadableJsonOptions);
            return dto?.PunctuatedText?.Trim() ?? string.Empty;
        }
        catch (JsonException)
        {
            return rawOutput.Trim();
        }
    }

    /// <summary>
    /// 归一化原始转写文本，减少无意义空白对断句模型的干扰。
    /// Normalize raw transcript text to reduce whitespace noise before punctuation.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string NormalizeTranscriptText(string text)
    {
        return WhitespaceRegex.Replace(text.Trim(), " ");
    }

    /// <summary>
    /// 将长文本按固定长度切块，优先在空白处切分。
    /// Split long text into fixed-size chunks, preferring whitespace boundaries.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IEnumerable<string> SplitIntoChunks(string text, int maxChunkCharacters)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var remainingLength = text.Length - offset;
            if (remainingLength <= maxChunkCharacters)
            {
                yield return text[offset..].Trim();
                yield break;
            }

            var preferredLength = maxChunkCharacters;
            var boundary = text.LastIndexOf(' ', offset + preferredLength - 1, preferredLength);
            if (boundary <= offset + maxChunkCharacters / 2)
            {
                boundary = offset + preferredLength;
            }

            yield return text[offset..boundary].Trim();
            offset = boundary;
        }
    }

    /// <summary>
    /// 截断长文本，避免异常消息过大。
    /// Truncate long text to keep exception messages readable.
    /// 最近修改时间：2026-06-24；修改人：GG。
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
    /// 创建聊天消息。
    /// Create one chat message DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static ChatMessageDto CreateMessage(string role, string content)
    {
        return new ChatMessageDto
        {
            Role = role,
            Content = content
        };
    }

    /// <summary>
    /// DeepSeek Chat Completion 请求 DTO。
    /// DeepSeek chat completion request DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class ChatCompletionRequestDto
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("messages")]
        public IReadOnlyList<ChatMessageDto> Messages { get; init; } = Array.Empty<ChatMessageDto>();

        [JsonPropertyName("thinking")]
        public ThinkingDto? Thinking { get; init; }

        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; }

        [JsonPropertyName("response_format")]
        public ResponseFormatDto? ResponseFormat { get; init; }
    }

    /// <summary>
    /// 聊天消息 DTO。
    /// Chat message DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class ChatMessageDto
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; init; } = string.Empty;
    }

    /// <summary>
    /// V4 thinking 模式 DTO；结构化 JSON 任务显式关闭 thinking，避免推理内容消耗输出预算。
    /// V4 thinking-mode DTO; structured JSON tasks disable thinking to avoid reasoning text consuming output budget.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class ThinkingDto
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;
    }

    /// <summary>
    /// JSON 输出格式 DTO。
    /// JSON response-format DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class ResponseFormatDto
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;
    }

    /// <summary>
    /// DeepSeek Chat Completion 响应 DTO。
    /// DeepSeek chat completion response DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class ChatCompletionResponseDto
    {
        [JsonPropertyName("choices")]
        public List<ChatChoiceDto>? Choices { get; init; }
    }

    /// <summary>
    /// DeepSeek 选项 DTO。
    /// DeepSeek choice DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class ChatChoiceDto
    {
        [JsonPropertyName("message")]
        public ChatChoiceMessageDto? Message { get; init; }
    }

    /// <summary>
    /// DeepSeek 响应消息 DTO。
    /// DeepSeek response message DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class ChatChoiceMessageDto
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    /// <summary>
    /// 断句结果 DTO。
    /// Punctuation result DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class PunctuationDto
    {
        [JsonPropertyName("punctuated_text")]
        public string? PunctuatedText { get; init; }
    }
}
