namespace AudioText.Verification.Services;

/// <summary>
/// AI 查验力度等级，用于控制搜索查询数量和进入最终评分的参考证据数量。
/// AI verification intensity level controlling search query volume and evidence reference volume.
/// 最近修改时间：2026-06-27；修改人：GG。
/// </summary>
public enum AiVerificationIntensity
{
    /// <summary>
    /// 普通：仅搜索核心观点，搜索量 8 次以内。
    /// Normal: search only core claims, capped at 8 searches.
    /// </summary>
    Normal,

    /// <summary>
    /// 细节：搜索主要观点，搜索量 20 次以内。
    /// Detail: search main claims, capped at 20 searches.
    /// </summary>
    Detail,

    /// <summary>
    /// 苛刻：覆盖全部观点，不主动限制搜索量。
    /// Strict: cover all claims without a software-side search cap.
    /// </summary>
    Strict
}

/// <summary>
/// AI 联网评价配置。
/// Configuration for AI web verification.
/// 最近修改时间：2026-06-27；修改人：GG。
/// </summary>
public sealed class AiVerificationSettings
{
    /// <summary>
    /// DeepSeek OpenAI 兼容 API 默认地址。
    /// Default DeepSeek OpenAI-compatible API base URL.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>
    /// 默认模型名称。
    /// Default model name.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public const string DefaultModel = "deepseek-v4-flash";

    /// <summary>
    /// 界面下拉框内置模型名称。
    /// Built-in model names for the UI drop-down list.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedModels =
    [
        "deepseek-v4-flash",
        "deepseek-v4-pro"
    ];

    /// <summary>
    /// 默认查验力度；用“细节”保持当前默认核查质量，不把升级后的默认行为降得过轻。
    /// Default verification intensity; Detail preserves the existing verification quality.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public const AiVerificationIntensity DefaultVerificationIntensity = AiVerificationIntensity.Detail;

    /// <summary>
    /// 开源纯代码版不内置任何 DeepSeek API Key，用户需要在设置页自行填写。
    /// The pure source release does not bundle any DeepSeek API key; users must configure their own key in settings.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public const string DefaultApiKey = "";

    /// <summary>
    /// 开源纯代码版不内置任何 Bocha Web Search API Key；留空时走公开搜索降级链路。
    /// The pure source release does not bundle any Bocha Web Search API key; leaving it empty uses the public-search fallback chain.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public const string DefaultBochaWebSearchApiKey = "";

    /// <summary>
    /// 默认勾选的广告/数据源屏蔽平台。
    /// Default checked ad/data source platforms to block from evidence collection.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultBlockedEvidencePlatformNames =
    [
        "快手",
        "抖音",
        "小红书",
        "B站",
        "知乎"
    ];

    /// <summary>
    /// 创建 AI 联网评价配置。
    /// Create AI web verification settings.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public AiVerificationSettings(
        string apiKey,
        string model,
        string baseUrl,
        string? bochaWebSearchApiKey,
        string? searxngEndpoint,
        AiVerificationIntensity verificationIntensity,
        IReadOnlyCollection<string>? blockedEvidencePlatformNames = null)
    {
        ApiKey = apiKey;
        Model = model;
        BaseUrl = baseUrl;
        BochaWebSearchApiKey = bochaWebSearchApiKey;
        SearxngEndpoint = searxngEndpoint;
        VerificationIntensity = verificationIntensity;
        BlockedEvidencePlatformNames = NormalizeBlockedEvidencePlatformNames(blockedEvidencePlatformNames);
    }

    /// <summary>
    /// DeepSeek API Key，禁止写入日志或报告。
    /// DeepSeek API key, never write it to logs or reports.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public string ApiKey { get; }

    /// <summary>
    /// DeepSeek 模型名称。
    /// DeepSeek model name.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public string Model { get; }

    /// <summary>
    /// DeepSeek API 根地址。
    /// DeepSeek API base URL.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Bocha Web Search API Key，只用于搜索适配器，不写入日志或报告。
    /// Bocha Web Search API key used only by the search adapter and never written to logs or reports.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public string? BochaWebSearchApiKey { get; }

    /// <summary>
    /// 可选 SearXNG 搜索端点。
    /// Optional SearXNG search endpoint.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public string? SearxngEndpoint { get; }

    /// <summary>
    /// 当前查验力度，用于同时控制搜索查询量和参考证据量。
    /// Current verification intensity controlling both search-query volume and evidence-reference volume.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public AiVerificationIntensity VerificationIntensity { get; }

    /// <summary>
    /// 最多搜索查询数；苛刻模式返回空，表示不主动限制由程序生成出的搜索词数量。
    /// Maximum search query count; null in Strict mode means no software-side cap on generated queries.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public int? MaxSearchQueryCount => VerificationIntensity switch
    {
        AiVerificationIntensity.Normal => 8,
        AiVerificationIntensity.Detail => 20,
        AiVerificationIntensity.Strict => null,
        _ => 20
    };

    /// <summary>
    /// 最多参考证据数量；苛刻模式返回空，表示尽量把搜索得到的去重证据都交给最终评分。
    /// Maximum evidence reference count; null in Strict mode keeps all de-duplicated evidence found.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public int? MaxEvidenceItems => VerificationIntensity switch
    {
        AiVerificationIntensity.Normal => 8,
        AiVerificationIntensity.Detail => 20,
        AiVerificationIntensity.Strict => null,
        _ => 20
    };

    /// <summary>
    /// 兼容旧请求字段的搜索轮数；苛刻模式用 int.MaxValue 标记“不主动限制”。
    /// Search-round compatibility value for older request fields; int.MaxValue marks Strict as uncapped.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public int MaxSearchRounds => MaxSearchQueryCount ?? int.MaxValue;

    /// <summary>
    /// 查验力度显示名称。
    /// Verification intensity display label.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public string VerificationIntensityLabel => GetVerificationIntensityLabel(VerificationIntensity);

    /// <summary>
    /// 查验力度说明文字。
    /// Verification intensity description.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public string VerificationIntensityDescription => GetVerificationIntensityDescription(VerificationIntensity);

    /// <summary>
    /// 当前勾选的广告/数据源屏蔽平台名称。
    /// Currently checked ad/data source platform names to block from evidence collection.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public IReadOnlySet<string> BlockedEvidencePlatformNames { get; }

    /// <summary>
    /// 判断指定平台是否需要从证据来源中屏蔽。
    /// Check whether a platform should be blocked from evidence collection.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public bool IsEvidencePlatformBlocked(string platformName)
    {
        return BlockedEvidencePlatformNames.Contains(platformName);
    }

    /// <summary>
    /// 创建默认配置。
    /// Create default settings.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public static AiVerificationSettings CreateDefault()
    {
        return new AiVerificationSettings(
            DefaultApiKey,
            DefaultModel,
            DefaultBaseUrl,
            string.IsNullOrWhiteSpace(DefaultBochaWebSearchApiKey)
                ? null
                : DefaultBochaWebSearchApiKey,
            searxngEndpoint: null,
            DefaultVerificationIntensity,
            DefaultBlockedEvidencePlatformNames);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{BaseUrl.TrimEnd('/')}, model={Model}, intensity={VerificationIntensityLabel}, maxSearchQueries={FormatOptionalLimit(MaxSearchQueryCount)}";
    }

    /// <summary>
    /// 获取查验力度中文显示名称。
    /// Get the Chinese display label for one verification intensity.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public static string GetVerificationIntensityLabel(AiVerificationIntensity verificationIntensity)
    {
        return verificationIntensity switch
        {
            AiVerificationIntensity.Normal => "普通",
            AiVerificationIntensity.Detail => "细节",
            AiVerificationIntensity.Strict => "苛刻",
            _ => "细节"
        };
    }

    /// <summary>
    /// 获取查验力度说明，用于设置页、日志和主界面摘要。
    /// Get the verification intensity description for settings UI, logs, and summary text.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public static string GetVerificationIntensityDescription(AiVerificationIntensity verificationIntensity)
    {
        return verificationIntensity switch
        {
            AiVerificationIntensity.Normal => "仅搜索核心观点（8次以内搜索量）",
            AiVerificationIntensity.Detail => "主要观点（20次以内搜索量）",
            AiVerificationIntensity.Strict => "全部观点（无限次搜索量）",
            _ => "主要观点（20次以内搜索量）"
        };
    }

    /// <summary>
    /// 格式化可为空的数量上限；空值代表不主动限制。
    /// Format a nullable volume limit; null means no active cap.
    /// 最近修改时间：2026-06-27；修改人：GG。
    /// </summary>
    public static string FormatOptionalLimit(int? limit)
    {
        return limit.HasValue
            ? limit.Value.ToString()
            : "无限";
    }

    /// <summary>
    /// 标准化屏蔽源平台名称，避免空项和重复项进入运行时配置。
    /// Normalize blocked platform names so empty and duplicated items do not enter runtime settings.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IReadOnlySet<string> NormalizeBlockedEvidencePlatformNames(
        IReadOnlyCollection<string>? blockedEvidencePlatformNames)
    {
        var sourceNames = blockedEvidencePlatformNames ?? DefaultBlockedEvidencePlatformNames;
        return sourceNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
