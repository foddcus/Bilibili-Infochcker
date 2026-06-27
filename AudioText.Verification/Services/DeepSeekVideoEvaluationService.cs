using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;
using AudioText.Core.Utilities;

namespace AudioText.Verification.Services;

/// <summary>
/// DeepSeek 视频文字联网评价服务。
/// DeepSeek-based web verification service for video transcript text.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public sealed class DeepSeekVideoEvaluationService : IAiVideoEvaluationService
{
    private const int MaxTranscriptCharacters = 6000;
    private const int MaxEvidenceCharactersPerItem = 420;
    private const int MaxSearchResultsPerQuery = 8;
    private const int ScoreStep = 5;
    private const int NoEvidenceTruthfulnessCap = 40;
    private const int NoEvidenceTimelinessCap = 45;
    private const int NoEvidenceOverallCap = 45;
    private const string ClaimRatingObjectiveTrue = "客观属实";
    private const string ClaimRatingMostlyTrue = "基本属实";
    private const string ClaimRatingBiased = "有失偏颇";
    private const string ClaimRatingInflammatory = "煽风点火";
    private const string ClaimRatingNonsense = "胡言乱语";

    private static readonly IReadOnlyList<string> ClaimRatingLabels =
    [
        ClaimRatingObjectiveTrue,
        ClaimRatingMostlyTrue,
        ClaimRatingBiased,
        ClaimRatingInflammatory,
        ClaimRatingNonsense
    ];

    private static readonly IReadOnlyList<BlockedEvidencePlatform> BlockedEvidencePlatforms =
    [
        new("快手", ["kuaishou.com", "kwai.com", "gifshow.com", "kuaishouzt.com"]),
        new("抖音", ["douyin.com", "iesdouyin.com", "amemv.com", "douyinpic.com"]),
        new("小红书", ["xiaohongshu.com", "xhslink.com", "xhscdn.com"]),
        new("B站", ["bilibili.com", "b23.tv", "bili2233.cn"]),
        new("知乎", ["zhihu.com", "zhimg.com"])
    ];

    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadableJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly AiVerificationSettings _settings;
    private readonly IWebSearchService _webSearchService;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 创建 DeepSeek 视频文字评价服务。
    /// Create the DeepSeek video transcript evaluation service.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public DeepSeekVideoEvaluationService(
        AiVerificationSettings settings,
        IWebSearchService webSearchService,
        HttpClient? httpClient = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _webSearchService = webSearchService ?? throw new ArgumentNullException(nameof(webSearchService));
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <inheritdoc />
    public async Task<AiVideoEvaluationResult> EvaluateAsync(
        AiVideoEvaluationRequest request,
        IProgress<AiVideoEvaluationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TranscriptText);
        ArgumentException.ThrowIfNullOrWhiteSpace(_settings.ApiKey);

        request = request with { Title = TextEncodingRepair.RepairOrNull(request.Title) };
        var transcript = NormalizeTranscript(request.TranscriptText);
        var maxSearchQueries = NormalizeOptionalLimit(request.MaxSearchRounds);
        var maxEvidenceItems = _settings.MaxEvidenceItems;
        var warnings = new List<string>();

        progress?.Report(new AiVideoEvaluationProgress(10, "正在生成事实核查搜索词。"));
        var searchPlan = await BuildSearchPlanAsync(request, transcript, maxSearchQueries, cancellationToken);
        if (searchPlan.SearchQueries.Count == 0)
        {
            var fallbackQueries = BuildFallbackSearchQueries(request.Title, transcript, maxSearchQueries);
            searchPlan = searchPlan with { SearchQueries = fallbackQueries };
            warnings.Add("模型未返回有效搜索词，已根据标题和转写文本自动生成搜索词。");
        }

        var expandedSearchQueries = BuildExpandedSearchQueries(
            request.Title,
            request.SourceUrl,
            transcript,
            searchPlan.SearchQueries,
            maxSearchQueries);
        if (expandedSearchQueries.Count > searchPlan.SearchQueries.Count)
        {
            searchPlan = searchPlan with { SearchQueries = expandedSearchQueries };
            warnings.Add("已启用增强搜索：自动补充多语言、本地官方词、英文国际词、数据词和站点限定词。");
        }

        progress?.Report(new AiVideoEvaluationProgress(35, "正在执行联网搜索。"));
        var evidenceResult = await CollectEvidenceAsync(searchPlan.SearchQueries, maxEvidenceItems, progress, cancellationToken);
        warnings.AddRange(evidenceResult.Warnings);
        if (evidenceResult.Evidences.Count == 0)
        {
            warnings.Add("联网搜索未取得可用证据，真实性与时效性评分只能作为弱判断。");
        }

        progress?.Report(new AiVideoEvaluationProgress(75, "正在生成真实性、时效性、信息专业性与娱乐性评分。"));
        var result = await BuildFinalEvaluationAsync(
            request,
            transcript,
            searchPlan,
            evidenceResult.Evidences,
            warnings,
            cancellationToken);

        progress?.Report(new AiVideoEvaluationProgress(100, "AI 联网评价完成。"));
        return result;
    }

    /// <summary>
    /// 生成事实主张和搜索词。
    /// Generate factual claims and search queries.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task<SearchPlan> BuildSearchPlanAsync(
        AiVideoEvaluationRequest request,
        string transcript,
        int? maxSearchQueries,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessageDto>
        {
            CreateMessage(
                "system",
                """
                你是视频内容核查的搜索规划助手。
                任务：从视频转写文本中提取可核查的事实主张，并生成适合联网搜索的关键词。
                约束：
                1. 只输出 JSON 对象，不输出 Markdown。
                2. 不评价内容好坏，只规划搜索。
                3. 若文本主要是情绪宣泄、口号或广告，也要返回能验证主题背景的搜索词。
                4. 搜索词应简短、具体，优先包含事件、机构、人名、地点、时间、专业术语。
                5. 同一输入应输出同一组核心主张和搜索词；不要随机扩展同义搜索词。
                6. 如果视频讨论中国大陆以外的国家、地区、机构、人物、事件或数据，必须优先补充该国家/地区常用语言的搜索词，并同时给出英文桥接搜索词。
                7. 不要只输出中文搜索词；中文搜索词只能作为中文材料补充，本地语言和英文结果通常优先。
                """),
            CreateMessage(
                "user",
                BuildSearchPlanUserPrompt(request, transcript, maxSearchQueries))
        };

        var content = await CallChatCompletionAsync(
            messages,
            maxTokens: 1200,
            cancellationToken);

        return ParseSearchPlan(content, maxSearchQueries);
    }

    /// <summary>
    /// 执行搜索并收集网页证据。
    /// Execute searches and collect web evidence.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task<EvidenceCollectionResult> CollectEvidenceAsync(
        IReadOnlyList<string> searchQueries,
        int? maxEvidenceItems,
        IProgress<AiVideoEvaluationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var evidences = new List<AiEvaluationEvidence>();
        var warnings = new List<string>();
        var blockedPlatformNames = new HashSet<string>(StringComparer.Ordinal);
        var uniqueQueries = searchQueries
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(RepairSearchText)
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => WhitespaceRegex.Replace(query!.Trim(), " "))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var index = 0; index < uniqueQueries.Count; index++)
        {
            if (HasReachedOptionalLimit(evidences.Count, maxEvidenceItems))
            {
                break;
            }

            var query = uniqueQueries[index];
            var percent = 35 + ((index + 1) * 30.0 / Math.Max(1, uniqueQueries.Count));
            progress?.Report(new AiVideoEvaluationProgress(percent, $"正在搜索：{query}"));

            try
            {
                var results = await _webSearchService.SearchAsync(
                    new WebSearchRequest(query, MaxResults: MaxSearchResultsPerQuery),
                    cancellationToken);

                foreach (var result in results)
                {
                    if (HasReachedOptionalLimit(evidences.Count, maxEvidenceItems))
                    {
                        break;
                    }

                    if (TryGetBlockedEvidencePlatform(result.Url, out var blockedPlatformName))
                    {
                        blockedPlatformNames.Add(blockedPlatformName);
                        continue;
                    }

                    if (evidences.Any(item => item.Url.Equals(result.Url, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    evidences.Add(new AiEvaluationEvidence(
                        result.Title,
                        result.Url,
                        TruncateText(result.Snippet, MaxEvidenceCharactersPerItem),
                        query,
                        result.Source));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
            {
                warnings.Add($"搜索失败：{query}；原因：{ex.Message}");
            }
        }

        if (blockedPlatformNames.Count > 0)
        {
            warnings.Add(
                "已按用户要求屏蔽以下参考源平台："
                + string.Join("、", blockedPlatformNames.OrderBy(item => item, StringComparer.Ordinal))
                + "。");
        }

        return new EvidenceCollectionResult(evidences, warnings);
    }

    /// <summary>
    /// 基于转写文本和搜索证据生成最终评分。
    /// Build the final score from transcript text and web evidence.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private async Task<AiVideoEvaluationResult> BuildFinalEvaluationAsync(
        AiVideoEvaluationRequest request,
        string transcript,
        SearchPlan searchPlan,
        IReadOnlyList<AiEvaluationEvidence> evidences,
        IReadOnlyList<string> warnings,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessageDto>
        {
            CreateMessage(
                "system",
                """
                你是严谨、客观的视频内容评价助手。
                任务：根据视频转写文本和联网搜索证据，对视频内容的真实性、时效性、信息专业性、娱乐性和情绪引导嫌疑进行评分。
                必须遵守：
                1. 只使用用户给出的转写文本和搜索证据，不得编造来源。
                2. 搜索证据不足时要明确写入 warnings，不要把无法验证的内容当作真实。
                3. 评分范围为 0-100。真实性、时效性、信息专业性、娱乐性越高越好；情绪引导嫌疑越高越差。
                4. 所有分数必须是 5 的整数倍，例如 0、5、10、...、95、100；不要输出 73、84 这类细碎分数。
                5. 边界案例一律选较保守的低一档分数，避免同一内容多次评分大幅波动。
                6. overall_score 表示“是否值得观看/是否有信息价值”，高分代表更有意义。
                7. category 必须从以下中文类别中选择一个：有意义的信息视频、部分有价值但需谨慎、情绪引导嫌疑视频、低信息垃圾视频、证据不足无法判断。
                8. 按短视频批量筛选标准严格打分，不要把普通闲聊、复述新闻标题或弱证据内容默认打到 60 分以上；约四成普通视频应落入低分淘汰区。
                9. 只输出 JSON 对象，不输出 Markdown；不要输出摘要字段。
                10. verdict 必须保持事实判断客观公正，但表达方式可以诙谐、挖苦、抽象，像一个嘴毒但讲证据的评价员。
                11. verdict 允许适度使用粗口、脏话和网络抽象表达来吐槽内容质量，但只能针对文本、论证、证据和信息价值开火；不得编造、不得人身威胁、不得歧视攻击身份群体，不得用脏话替代事实依据。
                12. key_claims 和 warnings 仍保持清晰正式，不要把脏话写进事实主张或风险提示。
                13. claim_evaluations 必须逐条评价视频观点或事实主张，rating 只能从“客观属实、基本属实、有失偏颇、煽风点火、胡言乱语”五个等级中选择。
                14. 记忆数据库中的往期视频只用于识别同一发布人的历史主题、表述习惯和重复说法，不能替代本次联网证据，不能把往期结论直接套到本次视频。
                """),
            CreateMessage(
                "user",
                BuildFinalEvaluationUserPrompt(request, transcript, searchPlan, evidences, warnings))
        };

        var rawOutput = await CallChatCompletionAsync(
            messages,
            maxTokens: 2200,
            cancellationToken);

        return TryParseEvaluationResult(rawOutput, searchPlan, evidences, warnings)
            ?? BuildFallbackEvaluationResult(rawOutput, searchPlan, evidences, warnings);
    }

    /// <summary>
    /// 调用 DeepSeek Chat Completions API。
    /// Call the DeepSeek Chat Completions API.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private async Task<string> CallChatCompletionAsync(
        IReadOnlyList<ChatMessageDto> messages,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequestDto
        {
            Model = _settings.Model,
            Messages = messages,
            Thinking = new ThinkingDto { Type = "disabled" },
            Temperature = 0,
            MaxTokens = maxTokens,
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
                $"DeepSeek API 请求失败：HTTP {(int)response.StatusCode}。{TruncateText(responseText, 900)}");
        }

        var completion = JsonSerializer.Deserialize<ChatCompletionResponseDto>(responseText, ApiJsonOptions);
        var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("DeepSeek API 未返回有效文本内容。");
        }

        return content;
    }

    /// <summary>
    /// 构造搜索规划用户提示词。
    /// Build the user prompt for search planning.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string BuildSearchPlanUserPrompt(
        AiVideoEvaluationRequest request,
        string transcript,
        int? maxSearchQueries)
    {
        var searchLimitInstruction = maxSearchQueries.HasValue
            ? $"生成最多 {maxSearchQueries.Value} 个联网搜索词。"
            : "覆盖全部可核查事实主张，不主动设置搜索词数量上限，但必须去除重复搜索词。";

        var maxSearchRounds = maxSearchQueries.HasValue
            ? maxSearchQueries.Value.ToString()
            : "不主动限制";

        return $$"""
        查验力度约束：{{searchLimitInstruction}}
        请从以下视频转写文本中提取需要核查的事实主张，并生成最多 {{maxSearchRounds}} 个联网搜索词。
        若事实主张涉及中国大陆以外的国家/地区，请把搜索词组合成“该国语言优先 + 英文桥接 + 中文补充”，避免只用中文检索国外数据。

        输出 JSON 格式：
        {
          "key_claims": ["主张1", "主张2"],
          "search_queries": ["搜索词1", "搜索词2"]
        }

        视频标题：{{request.Title ?? "未知"}}
        来源链接：{{request.SourceUrl ?? "未知"}}
        当前时间：{{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}}

        视频转写文本：
        {{transcript}}
        """;
    }

    /// <summary>
    /// 构造最终评价用户提示词。
    /// Build the user prompt for final evaluation.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string BuildFinalEvaluationUserPrompt(
        AiVideoEvaluationRequest request,
        string transcript,
        SearchPlan searchPlan,
        IReadOnlyList<AiEvaluationEvidence> evidences,
        IReadOnlyList<string> warnings)
    {
        var evidenceText = evidences.Count == 0
            ? "无可用搜索证据。"
            : string.Join(
                Environment.NewLine + Environment.NewLine,
                evidences.Select((evidence, index) =>
                    $"[{index + 1}] 搜索词：{evidence.Query}{Environment.NewLine}"
                    + $"来源：{evidence.Source}{Environment.NewLine}"
                    + $"标题：{evidence.Title}{Environment.NewLine}"
                    + $"URL：{evidence.Url}"));

        var warningText = warnings.Count == 0
            ? "无。"
            : string.Join(Environment.NewLine, warnings.Select(item => "- " + item));
        var memoryReferenceText = FormatMemoryReferences(request.MemoryReferences);

        return $$"""
        请输出严格 JSON，字段和含义如下：
        {
          "category": "有意义的信息视频/部分有价值但需谨慎/情绪引导嫌疑视频/低信息垃圾视频/证据不足无法判断",
          "overall_score": 0,
          "truthfulness_score": 0,
          "timeliness_score": 0,
          "information_professionalism_score": 0,
          "entertainment_score": 0,
          "emotional_guidance_suspicion_score": 0,
          "verdict": "客观评价结论；保持证据和分数公正，但表达可以诙谐挖苦、抽象吐槽，允许适度粗口",
          "key_claims": ["主张1", "主张2"],
          "claim_evaluations": [
            { "claim": "主张1", "rating": "客观属实" },
            { "claim": "主张2", "rating": "有失偏颇" }
          ],
          "warnings": ["证据限制或风险提示"]
        }

        评分解释：
        - 所有分数只能取 0、5、10、...、95、100。
        - truthfulness_score：转写文本中的事实主张是否得到证据支持；没有联网证据时最高 40，证据很弱或只靠标题/二手材料时最高 50，证据互相冲突时最高 55。
        - timeliness_score：内容是否涉及近期变化、是否有时效参考价值；没有明确时间或外部证据时最高 45，只是复述旧闻或常识时最高 50。
        - information_professionalism_score：加权合并信息密度、内容重要性和观点新颖性。内部建议按 50% 信息密度、35% 重要性/影响范围、15% 观点新颖性判断；只有口号、常识重复、单一观点输出或缺少方法/数据时应压到 55 以下。
        - entertainment_score：用词是否诙谐、表达是否有节奏、视频文本是否不让人打瞌睡；不要把恐吓、愤怒或标题党当成娱乐性。
        - emotional_guidance_suspicion_score：是否明显依赖恐吓、愤怒、站队标签、阴谋化、标题党、重复口号或带节奏话术；越高代表嫌疑越高、越差。
        - overall_score：按固定公式估算：0.35 * truthfulness_score + 0.10 * timeliness_score + 0.40 * information_professionalism_score + 0.05 * entertainment_score + 0.10 * (100 - emotional_guidance_suspicion_score)，再四舍五入到最近的 5 分。

        逐条主张五级评价：
        - claim_evaluations 必须覆盖 key_claims 中的核心主张，claim 写原始主张或精简后的同义主张，rating 只能使用以下五个固定短语。
        - 客观属实：主张被可靠证据支持，表述没有明显偷换、夸大或遗漏关键条件。
        - 基本属实：核心事实大体成立，但存在轻微简化、时点不完整、细节不全或证据强度一般。
        - 有失偏颇：只选取部分事实、忽略重要上下文、把推测说得过满，或证据互相冲突但视频偏向单边结论。
        - 煽风点火：事实基础薄弱或选择性引用明显，主要靠恐吓、愤怒、站队标签、阴谋化或标题党推动情绪。
        - 胡言乱语：主张明显违背证据、张冠李戴、凭空编造，或逻辑混乱到无法形成可靠判断。
        - 不要把 rating 直接写进 key_claims；程序会在界面和报告中显示为“主张（评价）”。

        评价文字风格要求：
        - verdict 要像“讲证据的毒舌嘴替”：先基于证据说清楚为什么值得看或为什么不行，再用诙谐、挖苦、抽象的说法补刀。
        - 可以出现适度粗口和脏话，例如吐槽“这段论证太扯”“信息量低得离谱”“标题党味儿冲得要命”等，但不要为了脏而脏。
        - 挖苦对象只能是视频文本、论证方式、证据质量、信息密度和情绪引导套路；不要攻击创作者的身份、外貌、地域、性别、民族、疾病、职业群体等。
        - 分数和 category 必须先按上面的客观规则确定，不能因为语言好笑或骂得爽就抬分或压分。
        - warnings 必须保持正式，key_claims 必须保持事实化；只有 verdict 使用这种抽象吐槽风格。

        固定分档锚点：
        - 0-25：几乎没有有效信息、证据或判断基础。
        - 30-50：信息很弱，主要是情绪引导、口号、广告、重复、复述标题或未经证实说法，通常应被淘汰。
        - 55-65：有部分事实或线索，但证据不足、结构松散、专业性一般或实用价值有限。
        - 70-85：事实、证据、因果或方法较清楚，信息专业性有明确价值。
        - 90-100：证据充分、信息专业性强、表达有吸引力且情绪引导嫌疑低，明显值得观看；此档必须少给。

        类别判定：
        - 有意义的信息视频：overall_score >= 75 且 truthfulness_score >= 65 且 information_professionalism_score >= 70 且 emotional_guidance_suspicion_score <= 50。
        - 部分有价值但需谨慎：overall_score 在 55-75 之间，或证据/情绪引导嫌疑存在明显限制。
        - 情绪引导嫌疑视频：emotional_guidance_suspicion_score >= 70 且 information_professionalism_score < 55。
        - 低信息垃圾视频：overall_score <= 50，或 information_professionalism_score <= 40 且 entertainment_score <= 45。
        - 证据不足无法判断：事实主张需要外部证据但搜索证据为空或严重不足。

        视频标题：{{request.Title ?? "未知"}}
        来源链接：{{request.SourceUrl ?? "未知"}}
        当前时间：{{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}}

        记忆数据库中的同发布人往期视频参考：
        {{memoryReferenceText}}

        搜索阶段提取的关键主张：
        {{JsonSerializer.Serialize(searchPlan.KeyClaims, ReadableJsonOptions)}}

        实际搜索词：
        {{JsonSerializer.Serialize(searchPlan.SearchQueries, ReadableJsonOptions)}}

        搜索/系统限制：
        {{warningText}}

        联网搜索证据：
        {{evidenceText}}

        视频转写文本：
        {{transcript}}
        """;
    }

    /// <summary>
    /// 将同发布人的往期视频记忆整理成提示词背景，避免传入整段历史转写导致 token 浪费。
    /// Format same-publisher historical video memories as prompt background without sending full old transcripts.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string FormatMemoryReferences(IReadOnlyList<VideoMemoryReference>? memoryReferences)
    {
        if (memoryReferences is null || memoryReferences.Count == 0)
        {
            return "无可用往期视频记忆。";
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            memoryReferences.Select((reference, index) =>
                $"[{index + 1}] 标题：{reference.Title}{Environment.NewLine}"
                + $"平台：{reference.Website}{Environment.NewLine}"
                + $"发布人：{reference.PublisherName ?? "未知"}{Environment.NewLine}"
                + $"最近分析：{reference.LastAnalyzedAt:yyyy-MM-dd HH:mm:ss zzz}；任务次数：{reference.TaskCount}{Environment.NewLine}"
                + $"类别/综合分：{reference.Category ?? "未知"} / {FormatOptionalScore(reference.OverallScore)}{Environment.NewLine}"
                + $"关键主张：{FormatPromptBulletLine(reference.KeyClaims)}{Environment.NewLine}"
                + $"逐条评价：{FormatPromptClaimRatings(reference.ClaimEvaluations)}{Environment.NewLine}"
                + $"历史结论：{(string.IsNullOrWhiteSpace(reference.Verdict) ? "无" : TruncateText(reference.Verdict, 260))}{Environment.NewLine}"
                + $"历史限制：{FormatPromptBulletLine(reference.Warnings)}"));
    }

    /// <summary>
    /// 格式化可空分数，供提示词中的历史摘要使用。
    /// Format an optional score for historical prompt summaries.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string FormatOptionalScore(int? score)
    {
        return score.HasValue
            ? $"{score.Value}/100"
            : "未知";
    }

    /// <summary>
    /// 将字符串列表压缩为单行，避免历史摘要过长。
    /// Compress a string list into one prompt line to keep historical summaries compact.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string FormatPromptBulletLine(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return "无";
        }

        return string.Join("；", items.Select(item => TruncateText(item, 120)));
    }

    /// <summary>
    /// 将历史逐条评价压缩为单行，保留主张和五级标签。
    /// Compress historical claim ratings into one line while keeping claim and label.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string FormatPromptClaimRatings(IReadOnlyList<AiClaimEvaluation> claimEvaluations)
    {
        if (claimEvaluations.Count == 0)
        {
            return "无";
        }

        return string.Join(
            "；",
            claimEvaluations.Select(item =>
                $"{TruncateText(item.Claim, 100)}（{item.Rating}）"));
    }

    /// <summary>
    /// 解析搜索规划结果。
    /// Parse the search planning result.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static SearchPlan ParseSearchPlan(string rawOutput, int? maxSearchQueries)
    {
        var json = ExtractJsonObject(rawOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SearchPlan(Array.Empty<string>(), Array.Empty<string>());
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var keyClaims = ReadStringArray(root, "key_claims")
                .Concat(ReadStringArray(root, "claims"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var searchQueries = ReadStringArray(root, "search_queries")
                .Concat(ReadStringArray(root, "queries"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(RepairSearchText)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => TruncateText(WhitespaceRegex.Replace(item!.Trim(), " "), 120))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var limitedSearchQueries = TakeOptionalLimit(searchQueries, maxSearchQueries)
                .ToList();

            return new SearchPlan(keyClaims, limitedSearchQueries);
        }
        catch (JsonException)
        {
            return new SearchPlan(Array.Empty<string>(), Array.Empty<string>());
        }
    }

    /// <summary>
    /// 解析最终评价结果。
    /// Parse the final evaluation result.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static AiVideoEvaluationResult? TryParseEvaluationResult(
        string rawOutput,
        SearchPlan searchPlan,
        IReadOnlyList<AiEvaluationEvidence> evidences,
        IReadOnlyList<string> warnings)
    {
        var json = ExtractJsonObject(rawOutput);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<EvaluationDto>(json, ReadableJsonOptions);
            if (dto is null)
            {
                return null;
            }

            var mergedWarnings = (dto.Warnings ?? [])
                .Concat(warnings)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var modelClaimEvaluations = dto.ClaimEvaluations ?? [];
            var keyClaims = (dto.KeyClaims ?? [])
                .Concat(modelClaimEvaluations.Select(item => item.Claim ?? string.Empty))
                .Concat(searchPlan.KeyClaims)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var truthfulnessScore = NormalizeScoreOrDefault(dto.TruthfulnessScore, fallbackScore: 40);
            var timelinessScore = NormalizeScoreOrDefault(dto.TimelinessScore, fallbackScore: 40);
            var informationProfessionalismScore = NormalizeScoreOrDefault(
                dto.InformationProfessionalismScore,
                ComputeLegacyInformationProfessionalismScore(dto.ImportanceScore, dto.InformationValueScore));
            var entertainmentScore = NormalizeScoreOrDefault(dto.EntertainmentScore, fallbackScore: 40);
            var emotionalGuidanceSuspicionScore = NormalizeScoreOrDefault(
                dto.EmotionalGuidanceSuspicionScore,
                dto.EmotionalManipulationRiskScore ?? 60);

            ApplyEvidenceCaps(
                evidences,
                ref truthfulnessScore,
                ref timelinessScore);

            var overallScore = ComputeOverallScore(
                truthfulnessScore,
                timelinessScore,
                informationProfessionalismScore,
                entertainmentScore,
                emotionalGuidanceSuspicionScore,
                evidences);
            var category = NormalizeCategory(
                dto.Category,
                overallScore,
                truthfulnessScore,
                informationProfessionalismScore,
                entertainmentScore,
                emotionalGuidanceSuspicionScore,
                evidences);
            var claimEvaluations = BuildClaimEvaluations(
                modelClaimEvaluations,
                keyClaims,
                truthfulnessScore,
                emotionalGuidanceSuspicionScore);

            return new AiVideoEvaluationResult(
                category,
                overallScore,
                truthfulnessScore,
                timelinessScore,
                informationProfessionalismScore,
                entertainmentScore,
                emotionalGuidanceSuspicionScore,
                string.Empty,
                dto.Verdict ?? string.Empty,
                keyClaims,
                claimEvaluations,
                searchPlan.SearchQueries,
                evidences,
                mergedWarnings,
                rawOutput);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 构建格式解析失败时的保底评价。
    /// Build a fallback evaluation when the model output cannot be parsed.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static AiVideoEvaluationResult BuildFallbackEvaluationResult(
        string rawOutput,
        SearchPlan searchPlan,
        IReadOnlyList<AiEvaluationEvidence> evidences,
        IReadOnlyList<string> warnings)
    {
        var mergedWarnings = warnings
            .Append("模型返回内容未能解析为预期 JSON，已保留原始输出供排查。")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fallbackTruthfulnessScore = evidences.Count > 0 ? 50 : 35;
        const int fallbackTimelinessScore = 40;
        const int fallbackInformationProfessionalismScore = 35;
        const int fallbackEntertainmentScore = 35;
        const int fallbackEmotionalGuidanceSuspicionScore = 60;
        var claimEvaluations = BuildClaimEvaluations(
            null,
            searchPlan.KeyClaims,
            fallbackTruthfulnessScore,
            fallbackEmotionalGuidanceSuspicionScore);

        return new AiVideoEvaluationResult(
            evidences.Count > 0 ? "证据不足无法判断" : "低信息垃圾视频",
            OverallScore: evidences.Count > 0 ? 45 : 30,
            TruthfulnessScore: fallbackTruthfulnessScore,
            TimelinessScore: fallbackTimelinessScore,
            InformationProfessionalismScore: fallbackInformationProfessionalismScore,
            EntertainmentScore: fallbackEntertainmentScore,
            EmotionalGuidanceSuspicionScore: fallbackEmotionalGuidanceSuspicionScore,
            Summary: string.Empty,
            Verdict: TruncateText(rawOutput, 700),
            KeyClaims: searchPlan.KeyClaims,
            ClaimEvaluations: claimEvaluations,
            SearchQueries: searchPlan.SearchQueries,
            Evidences: evidences,
            Warnings: mergedWarnings,
            RawModelOutput: rawOutput);
    }

    /// <summary>
    /// 转写文本归一化并限制长度，控制 API 成本。
    /// Normalize and truncate transcript text to control API cost.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string NormalizeTranscript(string transcriptText)
    {
        var normalized = WhitespaceRegex.Replace(transcriptText.Trim(), " ");
        return normalized.Length <= MaxTranscriptCharacters
            ? normalized
            : normalized[..MaxTranscriptCharacters] + " ... [文本过长，已截断用于 AI 评价]";
    }

    /// <summary>
    /// 将旧请求中的搜索轮数字段转换为可空上限；int.MaxValue 表示不主动限制。
    /// Convert the legacy search-round field into a nullable cap; int.MaxValue means uncapped.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static int? NormalizeOptionalLimit(int limit)
    {
        if (limit >= int.MaxValue / 2)
        {
            return null;
        }

        return Math.Max(1, limit);
    }

    /// <summary>
    /// 判断当前数量是否达到可空上限；空上限表示不主动截断。
    /// Check whether a nullable cap has been reached; null means no active cap.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static bool HasReachedOptionalLimit(int count, int? limit)
    {
        return limit.HasValue && count >= limit.Value;
    }

    /// <summary>
    /// 对可空上限做乘法扩展；空上限继续保持不主动限制。
    /// Multiply a nullable cap; null remains uncapped.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static int? MultiplyOptionalLimit(int? limit, int multiplier)
    {
        if (!limit.HasValue)
        {
            return null;
        }

        return checked(limit.Value * multiplier);
    }

    /// <summary>
    /// 按可空上限截断序列；空上限返回原序列。
    /// Take up to a nullable cap; null returns the original sequence.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static IEnumerable<T> TakeOptionalLimit<T>(IEnumerable<T> source, int? limit)
    {
        return limit.HasValue
            ? source.Take(limit.Value)
            : source;
    }

    /// <summary>
    /// 根据标题和转写文本生成保底搜索词，并遵守查验力度映射出的搜索查询上限。
    /// Generate fallback search queries from title and transcript while respecting the intensity-derived query cap.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static IReadOnlyList<string> BuildFallbackSearchQueries(
        string? title,
        string transcript,
        int? maxSearchQueries)
    {
        var candidates = new List<string>();
        var repairedTitle = RepairSearchText(title);
        if (!string.IsNullOrWhiteSpace(repairedTitle))
        {
            candidates.Add(repairedTitle);
        }

        var sentences = Regex.Split(transcript, @"[。！？!?；;\r\n]+", RegexOptions.CultureInvariant)
            .Select(item => WhitespaceRegex.Replace(item.Trim(), " "))
            .Where(item => item.Length is >= 8 and <= 120)
            .Take(MultiplyOptionalLimit(maxSearchQueries, 2) ?? int.MaxValue);
        candidates.AddRange(sentences);

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(RepairSearchText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => TruncateText(item!, 100))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxSearchQueries ?? int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// 修复或剔除搜索文本，防止乱码标题进入联网搜索。
    /// Repair or drop search text so mojibake titles cannot enter web search.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? RepairSearchText(string? text)
    {
        return TextEncodingRepair.RepairOrNull(text);
    }

    /// <summary>
    /// 对 AI 规划搜索词做确定性多语言扩展，提升本地语言、国际来源、官方来源和数据类证据召回。
    /// Deterministically expand AI-planned queries for local-language, international, official, and data-oriented evidence.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static IReadOnlyList<string> BuildExpandedSearchQueries(
        string? title,
        string? sourceUrl,
        string transcript,
        IReadOnlyList<string> plannedQueries,
        int? maxSearchQueries)
    {
        var queryLimit = maxSearchQueries;
        var candidates = new List<string>();
        var localeProfiles = DetectSearchLocaleProfiles(title, sourceUrl, transcript, plannedQueries);

        var repairedTitle = RepairSearchText(title);
        if (!string.IsNullOrWhiteSpace(repairedTitle))
        {
            candidates.Add(repairedTitle);
        }

        candidates.AddRange(plannedQueries
            .Select(RepairSearchText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!));

        foreach (var query in plannedQueries)
        {
            var repairedQuery = RepairSearchText(query);
            if (string.IsNullOrWhiteSpace(repairedQuery))
            {
                continue;
            }

            var cleanQuery = WhitespaceRegex.Replace(repairedQuery.Trim(), " ");
            if (string.IsNullOrWhiteSpace(cleanQuery))
            {
                continue;
            }

            AddLocaleSpecificQueryExpansions(candidates, cleanQuery, localeProfiles);
        }

        if (!queryLimit.HasValue || candidates.Count < queryLimit.Value)
        {
            candidates.AddRange(BuildFallbackSearchQueries(title, transcript, MultiplyOptionalLimit(maxSearchQueries, 2)));
        }

        return candidates
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(RepairSearchText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => TruncateText(WhitespaceRegex.Replace(item!.Trim(), " "), 140))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(queryLimit ?? int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// 根据来源域名、标题、转写文本和基础搜索词选择搜索语言/地区扩展模板。
    /// Select search language/region profiles from the URL host, title, transcript, and base queries.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlyList<SearchLocaleProfile> DetectSearchLocaleProfiles(
        string? title,
        string? sourceUrl,
        string transcript,
        IReadOnlyList<string> plannedQueries)
    {
        var profiles = new List<SearchLocaleProfile>();
        var context = string.Join(
            " ",
            new[]
            {
                title ?? string.Empty,
                sourceUrl ?? string.Empty,
                transcript,
                string.Join(" ", plannedQueries)
            });
        var host = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri)
            ? sourceUri.Host
            : string.Empty;

        AddProfileIfMatched(
            profiles,
            BuildJapanSearchProfile(),
            HostEndsWith(host, ".jp")
            || ContainsJapaneseScript(context)
            || ContainsAny(context, "日本", "Japan", "Tokyo", "东京", "東京"));
        AddProfileIfMatched(
            profiles,
            BuildKoreaSearchProfile(),
            HostEndsWith(host, ".kr")
            || ContainsKoreanScript(context)
            || ContainsAny(context, "韩国", "韓国", "Korea", "Seoul", "首尔", "서울"));
        AddProfileIfMatched(
            profiles,
            BuildFranceSearchProfile(),
            HostEndsWith(host, ".fr")
            || ContainsAny(context, "法国", "France", "French", "Paris", "巴黎"));
        AddProfileIfMatched(
            profiles,
            BuildGermanySearchProfile(),
            HostEndsWith(host, ".de")
            || ContainsAny(context, "德国", "Germany", "German", "Berlin", "柏林"));
        AddProfileIfMatched(
            profiles,
            BuildRussiaSearchProfile(),
            HostEndsWith(host, ".ru")
            || ContainsCyrillicScript(context)
            || ContainsAny(context, "俄罗斯", "俄国", "Russia", "Moscow", "莫斯科"));
        AddProfileIfMatched(
            profiles,
            BuildSpanishSearchProfile(),
            HostEndsWith(host, ".es")
            || HostEndsWith(host, ".mx")
            || HostEndsWith(host, ".ar")
            || ContainsAny(context, "西班牙", "墨西哥", "阿根廷", "Spain", "Mexico", "Argentina", "Spanish"));
        AddProfileIfMatched(
            profiles,
            BuildBrazilSearchProfile(),
            HostEndsWith(host, ".br")
            || ContainsAny(context, "巴西", "Brazil", "Portuguese"));
        AddProfileIfMatched(
            profiles,
            BuildUnitedKingdomSearchProfile(),
            HostEndsWith(host, ".uk")
            || ContainsAny(context, "英国", "Britain", "United Kingdom", "UK", "London", "伦敦"));
        AddProfileIfMatched(
            profiles,
            BuildUnitedStatesSearchProfile(),
            HostEndsWith(host, ".us")
            || ContainsAny(context, "美国", "美國", "United States", "USA", "US ", "Washington"));
        AddProfileIfMatched(
            profiles,
            BuildIndiaSearchProfile(),
            HostEndsWith(host, ".in")
            || ContainsAny(context, "印度", "India", "Delhi", "New Delhi"));

        AddProfileIfMatched(profiles, BuildEnglishInternationalSearchProfile(), shouldAdd: true);
        AddProfileIfMatched(
            profiles,
            BuildChinaSearchProfile(),
            ContainsChineseScript(context) || HostEndsWith(host, ".cn"));

        return profiles;
    }

    /// <summary>
    /// 按语言/地区模板给单个搜索词追加修饰词和站点限定。
    /// Append modifier and site-filter variants for one query from language/region profiles.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static void AddLocaleSpecificQueryExpansions(
        List<string> candidates,
        string cleanQuery,
        IReadOnlyList<SearchLocaleProfile> localeProfiles)
    {
        foreach (var profile in localeProfiles)
        {
            foreach (var modifier in profile.Modifiers)
            {
                candidates.Add(cleanQuery + " " + modifier);
            }

            foreach (var siteFilter in profile.SiteFilters)
            {
                candidates.Add(cleanQuery + " site:" + siteFilter);
            }
        }
    }

    /// <summary>
    /// 在满足条件且未重复时加入搜索语言/地区模板。
    /// Add a search language/region profile when matched and not duplicated.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static void AddProfileIfMatched(
        List<SearchLocaleProfile> profiles,
        SearchLocaleProfile profile,
        bool shouldAdd)
    {
        if (!shouldAdd || profiles.Any(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        profiles.Add(profile);
    }

    /// <summary>
    /// 判断主机名是否匹配指定顶级域或域名后缀。
    /// Check whether a host matches one top-level-domain or host suffix.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool HostEndsWith(string host, string suffix)
    {
        return !string.IsNullOrWhiteSpace(host)
            && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断文本是否包含任意关键词。
    /// Check whether text contains any keyword.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool ContainsAny(string text, params string[] keywords)
    {
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 判断文本是否包含中文汉字。
    /// Check whether text contains Chinese characters.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool ContainsChineseScript(string text)
    {
        return ContainsCharacterInRange(text, 0x4E00, 0x9FFF);
    }

    /// <summary>
    /// 判断文本是否包含日文假名。
    /// Check whether text contains Japanese kana.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool ContainsJapaneseScript(string text)
    {
        return ContainsCharacterInRange(text, 0x3040, 0x30FF);
    }

    /// <summary>
    /// 判断文本是否包含韩文字符。
    /// Check whether text contains Korean Hangul characters.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool ContainsKoreanScript(string text)
    {
        return ContainsCharacterInRange(text, 0xAC00, 0xD7AF);
    }

    /// <summary>
    /// 判断文本是否包含西里尔字符。
    /// Check whether text contains Cyrillic characters.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool ContainsCyrillicScript(string text)
    {
        return ContainsCharacterInRange(text, 0x0400, 0x04FF);
    }

    /// <summary>
    /// 判断文本是否包含指定 Unicode 范围内的字符。
    /// Check whether text contains a character in one Unicode range.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool ContainsCharacterInRange(string text, int start, int end)
    {
        return text.Any(character => character >= start && character <= end);
    }

    /// <summary>
    /// 英文国际搜索模板。
    /// English international search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildEnglishInternationalSearchProfile()
    {
        return new SearchLocaleProfile(
            "EnglishInternational",
            ["latest", "official", "data", "statistics", "report", "source", "fact check"],
            ["gov", "edu", "who.int", "un.org", "worldbank.org"]);
    }

    /// <summary>
    /// 中国大陆中文搜索模板。
    /// Mainland-China Chinese search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildChinaSearchProfile()
    {
        return new SearchLocaleProfile(
            "China",
            ["最新", "官方", "数据", "统计", "通报"],
            ["gov.cn", "edu.cn"]);
    }

    /// <summary>
    /// 日本本地语言搜索模板。
    /// Japan local-language search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildJapanSearchProfile()
    {
        return new SearchLocaleProfile(
            "Japan",
            ["最新", "公式", "データ", "統計", "報告"],
            ["go.jp", "ac.jp"]);
    }

    /// <summary>
    /// 韩国本地语言搜索模板。
    /// Korea local-language search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildKoreaSearchProfile()
    {
        return new SearchLocaleProfile(
            "Korea",
            ["최신", "공식", "자료", "통계", "보고서"],
            ["go.kr", "ac.kr"]);
    }

    /// <summary>
    /// 法国本地语言搜索模板。
    /// France local-language search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildFranceSearchProfile()
    {
        return new SearchLocaleProfile(
            "France",
            ["actualité", "officiel", "données", "rapport"],
            ["gouv.fr"]);
    }

    /// <summary>
    /// 德国本地语言搜索模板。
    /// Germany local-language search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildGermanySearchProfile()
    {
        return new SearchLocaleProfile(
            "Germany",
            ["aktuell", "offiziell", "Daten", "Bericht"],
            ["bund.de"]);
    }

    /// <summary>
    /// 俄罗斯本地语言搜索模板。
    /// Russia local-language search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildRussiaSearchProfile()
    {
        return new SearchLocaleProfile(
            "Russia",
            ["последние", "официально", "данные", "статистика"],
            ["gov.ru"]);
    }

    /// <summary>
    /// 西班牙语国家搜索模板。
    /// Spanish-speaking country search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildSpanishSearchProfile()
    {
        return new SearchLocaleProfile(
            "Spanish",
            ["últimas noticias", "oficial", "datos", "informe"],
            ["gob.es", "gob.mx", "gob.ar", "gob.cl"]);
    }

    /// <summary>
    /// 巴西葡萄牙语搜索模板。
    /// Brazil Portuguese search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildBrazilSearchProfile()
    {
        return new SearchLocaleProfile(
            "Brazil",
            ["notícias", "oficial", "dados", "relatório"],
            ["gov.br"]);
    }

    /// <summary>
    /// 英国搜索模板。
    /// United Kingdom search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildUnitedKingdomSearchProfile()
    {
        return new SearchLocaleProfile(
            "UnitedKingdom",
            ["latest", "official", "data", "report"],
            ["gov.uk", "ac.uk"]);
    }

    /// <summary>
    /// 美国搜索模板。
    /// United States search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildUnitedStatesSearchProfile()
    {
        return new SearchLocaleProfile(
            "UnitedStates",
            ["latest", "official", "data", "statistics", "report"],
            ["gov", "edu"]);
    }

    /// <summary>
    /// 印度搜索模板。
    /// India search profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static SearchLocaleProfile BuildIndiaSearchProfile()
    {
        return new SearchLocaleProfile(
            "India",
            ["latest", "official", "data", "report"],
            ["gov.in", "nic.in", "ac.in"]);
    }

    /// <summary>
    /// 判断搜索结果是否属于用户要求屏蔽的短视频/社区平台参考源。
    /// Check whether a search result belongs to user-blocked short-video/community reference platforms.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private bool TryGetBlockedEvidencePlatform(string url, out string platformName)
    {
        platformName = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        foreach (var platform in BlockedEvidencePlatforms)
        {
            if (!_settings.IsEvidencePlatformBlocked(platform.Name))
            {
                continue;
            }

            if (platform.HostSuffixes.Any(suffix =>
                    host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)))
            {
                platformName = platform.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从模型输出中提取 JSON 对象。
    /// Extract a JSON object from model output.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        var first = text.IndexOf("{", StringComparison.Ordinal);
        var last = text.LastIndexOf("}", StringComparison.Ordinal);
        return first >= 0 && last > first
            ? text[first..(last + 1)]
            : null;
    }

    /// <summary>
    /// 从 JSON 对象中读取字符串数组。
    /// Read a string array from a JSON object.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IEnumerable<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item));
    }

    /// <summary>
    /// 将模型分数约束到 0-100，并归一为 5 分档，降低重复打分的微小随机差异。
    /// Clamp model scores to 0-100 and normalize them to 5-point buckets to reduce repeated-run jitter.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static int NormalizeScore(int score)
    {
        var clampedScore = Math.Clamp(score, 0, 100);
        return (int)(Math.Round(clampedScore / (double)ScoreStep, MidpointRounding.AwayFromZero) * ScoreStep);
    }

    /// <summary>
    /// 将可空模型分数约束到 0-100，并在缺失时使用保守默认值。
    /// Clamp an optional model score to 0-100 and use a conservative fallback when missing.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static int NormalizeScoreOrDefault(int? score, int fallbackScore)
    {
        return NormalizeScore(score ?? fallbackScore);
    }

    /// <summary>
    /// 兼容旧 JSON 字段：将原重要性和信息密度合成为新的信息专业性分。
    /// Convert legacy importance and information-density scores into the new information professionalism score.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static int ComputeLegacyInformationProfessionalismScore(int? importanceScore, int? informationValueScore)
    {
        if (!importanceScore.HasValue && !informationValueScore.HasValue)
        {
            return 40;
        }

        var normalizedImportanceScore = NormalizeScoreOrDefault(importanceScore, fallbackScore: 40);
        var normalizedInformationValueScore = NormalizeScoreOrDefault(informationValueScore, fallbackScore: 40);
        var weightedScore = 0.45 * normalizedInformationValueScore + 0.35 * normalizedImportanceScore + 0.20 * 40;
        return NormalizeScore((int)Math.Round(weightedScore, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// 当没有联网证据时，对真实性和时效性设置保守上限。
    /// Apply conservative truthfulness and timeliness caps when no web evidence is available.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static void ApplyEvidenceCaps(
        IReadOnlyList<AiEvaluationEvidence> evidences,
        ref int truthfulnessScore,
        ref int timelinessScore)
    {
        if (evidences.Count > 0)
        {
            return;
        }

        truthfulnessScore = Math.Min(truthfulnessScore, NoEvidenceTruthfulnessCap);
        timelinessScore = Math.Min(timelinessScore, NoEvidenceTimelinessCap);
    }

    /// <summary>
    /// 合并模型返回和搜索阶段提取的主张，并为缺失评价的主张补上保守五级标签。
    /// Merge model claim evaluations with planned claims and fill missing ratings conservatively.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static IReadOnlyList<AiClaimEvaluation> BuildClaimEvaluations(
        IReadOnlyList<ClaimEvaluationDto>? modelClaimEvaluations,
        IReadOnlyList<string> keyClaims,
        int truthfulnessScore,
        int emotionalGuidanceSuspicionScore)
    {
        var fallbackRating = InferFallbackClaimRating(truthfulnessScore, emotionalGuidanceSuspicionScore);
        var claimEvaluations = new List<AiClaimEvaluation>();
        var seenClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in modelClaimEvaluations ?? [])
        {
            var claim = NormalizeClaimText(item.Claim);
            if (string.IsNullOrWhiteSpace(claim) || !seenClaims.Add(claim))
            {
                continue;
            }

            var rating = NormalizeClaimRating(item.Rating) ?? fallbackRating;
            claimEvaluations.Add(new AiClaimEvaluation(claim, rating));
        }

        foreach (var keyClaim in keyClaims)
        {
            var claim = NormalizeClaimText(keyClaim);
            if (string.IsNullOrWhiteSpace(claim) || !seenClaims.Add(claim))
            {
                continue;
            }

            claimEvaluations.Add(new AiClaimEvaluation(claim, fallbackRating));
        }

        return claimEvaluations;
    }

    /// <summary>
    /// 归一化单条主张文本，避免超长主张撑爆界面和报告。
    /// Normalize one claim text to keep UI and reports compact.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string NormalizeClaimText(string? claim)
    {
        if (string.IsNullOrWhiteSpace(claim))
        {
            return string.Empty;
        }

        return TruncateText(WhitespaceRegex.Replace(claim.Trim(), " "), 220);
    }

    /// <summary>
    /// 将模型可能输出的同义标签收敛到用户指定的五个固定等级。
    /// Normalize model-provided claim labels into the five user-defined rating levels.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string? NormalizeClaimRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var compactRating = WhitespaceRegex.Replace(rating.Trim(), string.Empty);
        foreach (var label in ClaimRatingLabels)
        {
            if (compactRating.Equals(label, StringComparison.Ordinal)
                || compactRating.Contains(label, StringComparison.Ordinal))
            {
                return label;
            }
        }

        if (compactRating.Contains("胡言", StringComparison.Ordinal)
            || compactRating.Contains("乱语", StringComparison.Ordinal)
            || compactRating.Contains("胡说", StringComparison.Ordinal)
            || compactRating.Contains("瞎扯", StringComparison.Ordinal))
        {
            return ClaimRatingNonsense;
        }

        if (compactRating.Contains("煽动", StringComparison.Ordinal)
            || compactRating.Contains("煽风", StringComparison.Ordinal)
            || compactRating.Contains("带节奏", StringComparison.Ordinal)
            || compactRating.Contains("标题党", StringComparison.Ordinal))
        {
            return ClaimRatingInflammatory;
        }

        if (compactRating.Contains("偏颇", StringComparison.Ordinal)
            || compactRating.Contains("片面", StringComparison.Ordinal)
            || compactRating.Contains("夸大", StringComparison.Ordinal))
        {
            return ClaimRatingBiased;
        }

        if ((compactRating.Contains("基本", StringComparison.Ordinal)
                || compactRating.Contains("大体", StringComparison.Ordinal))
            && (compactRating.Contains("属实", StringComparison.Ordinal)
                || compactRating.Contains("真实", StringComparison.Ordinal)
                || compactRating.Contains("成立", StringComparison.Ordinal)))
        {
            return ClaimRatingMostlyTrue;
        }

        if (compactRating.Contains("客观", StringComparison.Ordinal)
            && (compactRating.Contains("属实", StringComparison.Ordinal)
                || compactRating.Contains("真实", StringComparison.Ordinal)
                || compactRating.Contains("成立", StringComparison.Ordinal)))
        {
            return ClaimRatingObjectiveTrue;
        }

        return null;
    }

    /// <summary>
    /// 缺失逐条主张标签时，根据整体真实性和情绪引导嫌疑给出保守兜底等级。
    /// Infer a conservative fallback claim rating from overall truthfulness and emotional-guidance suspicion.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private static string InferFallbackClaimRating(
        int truthfulnessScore,
        int emotionalGuidanceSuspicionScore)
    {
        if (truthfulnessScore >= 85 && emotionalGuidanceSuspicionScore <= 35)
        {
            return ClaimRatingObjectiveTrue;
        }

        if (truthfulnessScore >= 65)
        {
            return ClaimRatingMostlyTrue;
        }

        if (truthfulnessScore <= 25)
        {
            return ClaimRatingNonsense;
        }

        if (emotionalGuidanceSuspicionScore >= 70)
        {
            return ClaimRatingInflammatory;
        }

        return ClaimRatingBiased;
    }

    /// <summary>
    /// 按更严格的固定权重计算综合价值分，并对弱证据、低专业性和高引导嫌疑内容封顶。
    /// Compute the overall value score with stricter fixed weights and cap weak-evidence, low-professionalism, or high-guidance content.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static int ComputeOverallScore(
        int truthfulnessScore,
        int timelinessScore,
        int informationProfessionalismScore,
        int entertainmentScore,
        int emotionalGuidanceSuspicionScore,
        IReadOnlyList<AiEvaluationEvidence> evidences)
    {
        var weightedScore =
            0.35 * truthfulnessScore
            + 0.10 * timelinessScore
            + 0.40 * informationProfessionalismScore
            + 0.05 * entertainmentScore
            + 0.10 * (100 - emotionalGuidanceSuspicionScore);

        var normalizedScore = NormalizeScore((int)Math.Round(weightedScore, MidpointRounding.AwayFromZero));
        if (evidences.Count == 0)
        {
            normalizedScore = Math.Min(normalizedScore, NoEvidenceOverallCap);
        }

        if (informationProfessionalismScore <= 30 && emotionalGuidanceSuspicionScore >= 70)
        {
            normalizedScore = Math.Min(normalizedScore, 35);
        }

        if (truthfulnessScore <= 40)
        {
            normalizedScore = Math.Min(normalizedScore, 45);
        }

        if (truthfulnessScore <= 45
            && timelinessScore <= 50
            && informationProfessionalismScore <= 60)
        {
            normalizedScore = Math.Min(normalizedScore, 45);
        }

        if (informationProfessionalismScore <= 45 && entertainmentScore <= 45)
        {
            normalizedScore = Math.Min(normalizedScore, 40);
        }

        if (emotionalGuidanceSuspicionScore >= 65 && informationProfessionalismScore < 60)
        {
            normalizedScore = Math.Min(normalizedScore, 45);
        }

        return normalizedScore;
    }

    /// <summary>
    /// 根据固定分档校正类别，避免类别和分数互相矛盾。
    /// Normalize the category from fixed score buckets to avoid contradiction between category and scores.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static string NormalizeCategory(
        string? modelCategory,
        int overallScore,
        int truthfulnessScore,
        int informationProfessionalismScore,
        int entertainmentScore,
        int emotionalGuidanceSuspicionScore,
        IReadOnlyList<AiEvaluationEvidence> evidences)
    {
        if (evidences.Count == 0)
        {
            return "证据不足无法判断";
        }

        if (emotionalGuidanceSuspicionScore >= 70 && informationProfessionalismScore < 60)
        {
            return "情绪引导嫌疑视频";
        }

        if (overallScore <= 50
            || (informationProfessionalismScore <= 40 && entertainmentScore <= 45)
            || (truthfulnessScore <= 45 && informationProfessionalismScore <= 60))
        {
            return "低信息垃圾视频";
        }

        if (overallScore >= 75
            && truthfulnessScore >= 65
            && informationProfessionalismScore >= 70
            && emotionalGuidanceSuspicionScore <= 50)
        {
            return "有意义的信息视频";
        }

        if (!string.IsNullOrWhiteSpace(modelCategory)
            && IsKnownCategory(modelCategory)
            && !modelCategory.Equals("有意义的信息视频", StringComparison.Ordinal))
        {
            return modelCategory;
        }

        return "部分有价值但需谨慎";
    }

    /// <summary>
    /// 判断模型返回类别是否属于允许集合。
    /// Check whether the model category belongs to the allowed set.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static bool IsKnownCategory(string category)
    {
        return category is
            "有意义的信息视频"
            or "部分有价值但需谨慎"
            or "情绪引导嫌疑视频"
            or "低信息垃圾视频"
            or "证据不足无法判断";
    }

    /// <summary>
    /// 截断长文本，避免 UI 和请求体过大。
    /// Truncate long text for UI and request safety.
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
    /// 搜索语言/地区扩展模板。
    /// Search language/region expansion profile.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed record SearchLocaleProfile(
        string Name,
        IReadOnlyList<string> Modifiers,
        IReadOnlyList<string> SiteFilters);

    /// <summary>
    /// 用户要求屏蔽的参考源平台域名规则。
    /// User-requested blocked reference-source platform host rules.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed record BlockedEvidencePlatform(
        string Name,
        IReadOnlyList<string> HostSuffixes);

    /// <summary>
    /// 搜索规划结果。
    /// Search planning result.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed record SearchPlan(
        IReadOnlyList<string> KeyClaims,
        IReadOnlyList<string> SearchQueries);

    /// <summary>
    /// 联网证据收集结果。
    /// Web evidence collection result.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed record EvidenceCollectionResult(
        IReadOnlyList<AiEvaluationEvidence> Evidences,
        IReadOnlyList<string> Warnings);

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
    /// V4 thinking 模式 DTO；联网核查需要稳定 JSON，因此显式使用非 thinking 模式。
    /// V4 thinking-mode DTO; web verification uses non-thinking mode to keep JSON output stable.
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
    /// 最终评价 JSON DTO。
    /// Final evaluation JSON DTO.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private sealed class EvaluationDto
    {
        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("overall_score")]
        public int? OverallScore { get; init; }

        [JsonPropertyName("truthfulness_score")]
        public int? TruthfulnessScore { get; init; }

        [JsonPropertyName("timeliness_score")]
        public int? TimelinessScore { get; init; }

        [JsonPropertyName("information_professionalism_score")]
        public int? InformationProfessionalismScore { get; init; }

        [JsonPropertyName("entertainment_score")]
        public int? EntertainmentScore { get; init; }

        [JsonPropertyName("emotional_guidance_suspicion_score")]
        public int? EmotionalGuidanceSuspicionScore { get; init; }

        [JsonPropertyName("importance_score")]
        public int? ImportanceScore { get; init; }

        [JsonPropertyName("information_value_score")]
        public int? InformationValueScore { get; init; }

        [JsonPropertyName("emotional_manipulation_risk_score")]
        public int? EmotionalManipulationRiskScore { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("verdict")]
        public string? Verdict { get; init; }

        [JsonPropertyName("key_claims")]
        public List<string>? KeyClaims { get; init; }

        [JsonPropertyName("claim_evaluations")]
        public List<ClaimEvaluationDto>? ClaimEvaluations { get; init; }

        [JsonPropertyName("warnings")]
        public List<string>? Warnings { get; init; }
    }

    /// <summary>
    /// 单条主张五级评价 JSON DTO。
    /// JSON DTO for one claim's five-level evaluation.
    /// 最近修改时间：2026-06-25；修改人：GG。
    /// </summary>
    private sealed class ClaimEvaluationDto
    {
        [JsonPropertyName("claim")]
        public string? Claim { get; init; }

        [JsonPropertyName("rating")]
        public string? Rating { get; init; }
    }
}
