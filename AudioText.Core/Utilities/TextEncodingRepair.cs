using System.Text;

namespace AudioText.Core.Utilities;

/// <summary>
/// 文本编码异常修复工具。
/// Text encoding repair helpers used before titles and search queries enter the workflow.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public static class TextEncodingRepair
{
    /// <summary>
    /// 修复常见 mojibake 文本；无法可靠修复时返回空，避免乱码继续污染文件名和搜索词。
    /// Repair common mojibake text; return null when the text cannot be safely repaired.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public static string? RepairOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmedValue = value.Trim();
        var repairedCandidates = EnumerateRepairCandidates(trimmedValue)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ComputeMojibakeScore)
            .ToList();

        var bestCandidate = repairedCandidates.FirstOrDefault();
        return bestCandidate is not null && !LooksCorrupted(bestCandidate)
            ? bestCandidate.Trim()
            : null;
    }

    /// <summary>
    /// 判断文本是否明显已经乱码。
    /// Detect whether text is clearly corrupted.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public static bool LooksCorrupted(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return ComputeMojibakeScore(value) >= 100;
    }

    /// <summary>
    /// 枚举常见编码误读的候选修复结果。
    /// Enumerate candidate repairs for common encoding misreads.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static IEnumerable<string> EnumerateRepairCandidates(string value)
    {
        yield return value;

        foreach (var encodingName in new[] { "windows-1252", "iso-8859-1" })
        {
            Encoding sourceEncoding;
            try
            {
                sourceEncoding = Encoding.GetEncoding(encodingName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var bytes = sourceEncoding.GetBytes(value);
            yield return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// 计算乱码风险分数，分数越高越不可信。
    /// Compute a mojibake risk score; higher means less trustworthy.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    private static int ComputeMojibakeScore(string value)
    {
        var score = 0;

        foreach (var character in value)
        {
            score += character switch
            {
                '\uFFFD' => 120,
                '?' => 15,
                '\u02B9' => 40,
                '\u02BA' => 40,
                '\u02BC' => 40,
                '\u02C8' => 40,
                >= '\u0080' and <= '\u00FF' => 8,
                >= '\u02B0' and <= '\u02FF' => 8,
                _ => 0
            };
        }

        if (value.Contains("Ã", StringComparison.Ordinal)
            || value.Contains("Â", StringComparison.Ordinal)
            || value.Contains("å", StringComparison.Ordinal)
            || value.Contains("æ", StringComparison.Ordinal)
            || value.Contains("ç", StringComparison.Ordinal))
        {
            score += 40;
        }

        return score;
    }
}
