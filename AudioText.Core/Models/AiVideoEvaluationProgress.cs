namespace AudioText.Core.Models;

/// <summary>
/// AI 联网评价进度。
/// Progress message for AI web verification.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
/// <param name="Percent">百分比进度，可为空。Optional percent progress.</param>
/// <param name="Message">进度说明。Progress message.</param>
public sealed record AiVideoEvaluationProgress(
    double? Percent,
    string Message);
