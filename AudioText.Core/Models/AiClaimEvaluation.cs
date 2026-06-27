namespace AudioText.Core.Models;

/// <summary>
/// 单条视频观点或事实主张的五级评价。
/// Five-level evaluation for one video claim or viewpoint.
/// 最近修改时间：2026-06-25；修改人：GG。
/// </summary>
/// <param name="Claim">视频中的观点或事实主张。Claim or viewpoint from the transcript.</param>
/// <param name="Rating">五级评价，只允许：客观属实、基本属实、有失偏颇、煽风点火、胡言乱语。Five-level rating label.</param>
public sealed record AiClaimEvaluation(
    string Claim,
    string Rating);
