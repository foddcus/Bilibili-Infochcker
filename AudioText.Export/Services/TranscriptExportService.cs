using System.Globalization;
using System.Text;
using AudioText.Core.Interfaces;
using AudioText.Core.Models;

namespace AudioText.Export.Services;

/// <summary>
/// 转写文本导出服务。
/// Transcript export service for TXT and SRT files.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public sealed class TranscriptExportService : ITranscriptExportService
{
    /// <inheritdoc />
    public async Task ExportTextAsync(TranscriptDocument document, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        EnsureParentDirectory(outputPath);

        var builder = new StringBuilder();
        builder.AppendLine(document.Title);
        builder.AppendLine(document.Source);
        builder.AppendLine(document.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine(document.PlainText);

        await File.WriteAllTextAsync(outputPath, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ExportSrtAsync(TranscriptDocument document, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        EnsureParentDirectory(outputPath);

        var builder = new StringBuilder();

        for (var index = 0; index < document.Segments.Count; index++)
        {
            var segment = document.Segments[index];
            builder.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine($"{FormatSrtTime(segment.StartTime)} --> {FormatSrtTime(segment.EndTime)}");
            builder.AppendLine(segment.Text);
            builder.AppendLine();
        }

        await File.WriteAllTextAsync(outputPath, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    /// <summary>
    /// 确保导出文件的父目录存在。
    /// Ensure the parent directory of an output file exists.
    /// </summary>
    private static void EnsureParentDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>
    /// 格式化 SRT 时间码。
    /// Format SRT timestamp as HH:mm:ss,fff.
    /// </summary>
    private static string FormatSrtTime(TimeSpan value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}");
    }
}
