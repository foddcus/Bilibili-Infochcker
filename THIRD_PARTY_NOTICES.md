# Third-Party Notices

最近修改时间：2026-06-27  
维护者：GG

本仓库是纯代码版，不复制 `yt-dlp`、`FFmpeg`、`whisper.cpp` 或任何模型权重。第三方组件需要用户自行下载，且遵守各自许可证。本项目许可证不覆盖第三方组件。

| 组件 | 用途 | 来源与许可证提示 |
|---|---|---|
| yt-dlp | 网页视频/音频提取 | https://github.com/yt-dlp/yt-dlp，许可证以其仓库为准。 |
| FFmpeg / FFprobe | 音频转码和媒体信息读取 | https://ffmpeg.org/，不同构建可能采用 LGPL 或 GPL，分发时需保留对应许可证说明。 |
| whisper.cpp | 本地语音转写命令行工具 | https://github.com/ggml-org/whisper.cpp，许可证以其仓库为准。 |
| Whisper 模型权重 | 本地 ASR 模型 | 模型来源、许可证和再分发条件取决于具体下载位置；本仓库不包含模型权重。 |
| DeepSeek API | AI 断句、纠错和核查评分 | https://api-docs.deepseek.com/，调用需用户自行配置 API Key。 |
| Bocha Web Search API | 联网证据搜索 | https://open.bochaai.com/，调用需用户自行配置 API Key。 |
| SearXNG | 可选搜索端点 | https://github.com/searxng/searxng，可由用户自建。 |

重新打包 release 时，请记录每个第三方二进制的版本号、下载来源、许可证和校验值，并确保未包含 cookies、API Key、任务日志或用户数据。