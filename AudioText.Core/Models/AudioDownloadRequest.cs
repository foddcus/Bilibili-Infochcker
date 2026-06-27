namespace AudioText.Core.Models;

/// <summary>
/// 音频下载请求。
/// Audio download request passed from the UI/task layer to a download service.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
/// <param name="SourceUrl">网页或音频链接。Web page or direct audio URL.</param>
/// <param name="OutputDirectory">下载输出目录。Output directory for downloaded audio.</param>
/// <param name="PreferredFileName">用户期望文件名，可为空。Optional preferred file name.</param>
/// <param name="SourceKind">音频来源类型。Audio source kind.</param>
public sealed record AudioDownloadRequest(
    string SourceUrl,
    string OutputDirectory,
    string? PreferredFileName = null,
    AudioSourceKind SourceKind = AudioSourceKind.DirectAudioUrl);
