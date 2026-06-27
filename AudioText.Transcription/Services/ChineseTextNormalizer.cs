using System.Runtime.InteropServices;

namespace AudioText.Transcription.Services;

/// <summary>
/// 中文转写文本规范化工具。
/// Chinese transcription text normalization helper.
/// 最近修改时间：2026-06-24；修改人：GG。
/// </summary>
public static class ChineseTextNormalizer
{
    private const uint LocaleMapSimplifiedChinese = 0x02000000;
    private const string SimplifiedChineseLocaleName = "zh-CN";

    /// <summary>
    /// 将 whisper.cpp 输出中的繁体中文字符转换为简体中文，失败时保留原文本。
    /// Convert Traditional Chinese characters from whisper.cpp output to Simplified Chinese, preserving the original text if conversion fails.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    public static string ToSimplifiedChinese(string text)
    {
        if (string.IsNullOrEmpty(text) || !OperatingSystem.IsWindows())
        {
            return text;
        }

        try
        {
            var requiredLength = LCMapStringEx(
                SimplifiedChineseLocaleName,
                LocaleMapSimplifiedChinese,
                text,
                text.Length,
                null,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (requiredLength <= 0)
            {
                return text;
            }

            var buffer = new char[requiredLength];
            var writtenLength = LCMapStringEx(
                SimplifiedChineseLocaleName,
                LocaleMapSimplifiedChinese,
                text,
                text.Length,
                buffer,
                buffer.Length,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            return writtenLength > 0
                ? new string(buffer, 0, writtenLength)
                : text;
        }
        catch (DllNotFoundException)
        {
            return text;
        }
        catch (EntryPointNotFoundException)
        {
            return text;
        }
    }

    /// <summary>
    /// 调用 Windows NLS 映射函数执行本地繁简转换。
    /// Call the Windows NLS mapping function for local Traditional/Simplified Chinese conversion.
    /// 最近修改时间：2026-06-24；修改人：GG。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[]? lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);
}
