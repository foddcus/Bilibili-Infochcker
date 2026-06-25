# Third-Party Notices

最近修改时间：2026-06-24  
维护者：GG

本项目源码本身不复制 `yt-dlp`、`ffmpeg`、`whisper.cpp` 或模型文件源码；运行包可能为了开箱试用而附带外部可执行文件和模型权重。第三方组件遵循其各自许可证，本项目许可证不覆盖第三方组件。

| 组件 | 用途 | 许可证/来源提示 |
|---|---|---|
| yt-dlp | 网页视频/音频提取 | 项目仓库：https://github.com/yt-dlp/yt-dlp；其 LICENSE 当前为 Unlicense。 |
| FFmpeg / FFprobe | 音频转码、时长探测 | 项目官网：https://ffmpeg.org/；不同构建可能是 LGPL 或 GPL，当前测试包来自 GPL 构建，分发时需保留相应许可说明并遵守 FFmpeg legal 指引。 |
| whisper.cpp | 本地语音转写命令行工具 | 项目仓库：https://github.com/ggml-org/whisper.cpp；其 LICENSE 当前为 MIT。 |
| Whisper 模型权重 | 本地 ASR 模型 | 模型权重来源和授权取决于具体下载位置；发布包默认只放 tiny 模型用于快速试运行，推荐用户自行确认模型许可后替换为更高精度模型。 |

若你重新打包 release，请记录每个第三方二进制的版本号、下载来源、许可证和校验值。
