namespace AudioText.Core.Models;

/// <summary>
/// 单条转写文本片段。
/// One transcript segment with time range.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="StartTime">片段开始时间。Segment start time.</param>
/// <param name="EndTime">片段结束时间。Segment end time.</param>
/// <param name="Text">识别文本。Recognized text.</param>
/// <param name="Confidence">置信度，识别引擎不提供时为空。Confidence score, null when unavailable.</param>
public sealed record TranscriptSegment(
    TimeSpan StartTime,
    TimeSpan EndTime,
    string Text,
    double? Confidence = null);
