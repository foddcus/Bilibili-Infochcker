namespace AudioText.Core.Models;

/// <summary>
/// 音频来源类型。
/// Audio source kind used by task routing.
/// 最近修改时间：2026-06-23；修改人：GG。
/// </summary>
public enum AudioSourceKind
{
    /// <summary>
    /// 本地音频文件。
    /// Local audio file selected by the user.
    /// </summary>
    LocalFile = 0,

    /// <summary>
    /// 直接音频链接，例如 .mp3、.m4a、.wav。
    /// Direct audio URL such as .mp3, .m4a, or .wav.
    /// </summary>
    DirectAudioUrl = 1,

    /// <summary>
    /// 普通网页链接，需要解析后才能获得音频。
    /// Web page URL that needs a parser or external downloader.
    /// </summary>
    WebPageUrl = 2,

    /// <summary>
    /// Windows 系统声音回环采集。
    /// Windows system loopback capture.
    /// </summary>
    SystemLoopback = 3
}
