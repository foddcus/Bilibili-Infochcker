namespace AudioText.Core.Models;

/// <summary>
/// 实时音频切片。
/// Audio chunk produced by system loopback capture before transcription.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="PcmBytes">PCM 音频字节。PCM audio bytes.</param>
/// <param name="SampleRate">采样率，例如 16000 Hz。Sample rate such as 16000 Hz.</param>
/// <param name="ChannelCount">声道数。Channel count.</param>
/// <param name="StartTime">该切片相对任务开始的起点。Start time relative to task start.</param>
/// <param name="Duration">该切片持续时间。Chunk duration.</param>
/// <param name="FormatDescription">格式说明。Human-readable format description.</param>
public sealed record AudioChunk(
    byte[] PcmBytes,
    int SampleRate,
    int ChannelCount,
    TimeSpan StartTime,
    TimeSpan Duration,
    string FormatDescription);
