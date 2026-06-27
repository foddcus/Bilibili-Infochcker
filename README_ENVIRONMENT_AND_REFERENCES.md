# 环境与引用 README

最近修改时间：2026-06-27  
维护者：GG

## 说明

本文件面向 GitHub 纯代码版仓库。仓库只保存本项目源码和文档，不保存第三方二进制文件、模型权重、生成的软件包、cookies、API Key 或本地运行数据。

## 必需环境

| 类别 | 要求 | 说明 |
|---|---|---|
| 操作系统 | Windows 10/11 x64 | WPF 桌面程序目标环境。 |
| SDK | .NET SDK 10 | 用于编译源码。 |
| 运行时 | .NET 10 Desktop Runtime | 只运行已编译程序时需要。 |
| 网络 | 可选 | 视频下载、AI API 和联网搜索需要网络；本地转写本身可离线运行。 |

## 外部工具放置位置

以下文件需要用户自行从官方项目下载，并放到对应路径。不要把这些文件提交到 GitHub。

| 工具 | 建议路径 | 用途 | 官方来源 |
|---|---|---|---|
| yt-dlp | `tools/yt-dlp.exe` | 网页视频/音频提取 | https://github.com/yt-dlp/yt-dlp |
| FFmpeg | `tools/ffmpeg.exe` | 音频转码 | https://ffmpeg.org/ |
| FFprobe | `tools/ffprobe.exe` | 音频信息读取 | https://ffmpeg.org/ |
| whisper.cpp | `tools/whisper/Release/whisper-cli.exe` | 本地语音转写 | https://github.com/ggml-org/whisper.cpp |
| Whisper 模型 | `models/ggml-*.bin` | ASR 模型权重 | 请按 whisper.cpp 文档选择并下载 |

## 模型建议

- 快速测试：`ggml-tiny.bin` 体积较小，但中文长音频准确率有限。
- 常规中文视频：建议使用 `ggml-large-v3-turbo-q8_0.bin` 或同级别模型。
- GitHub 仓库不保存任何 `ggml-*.bin`，因为模型权重通常体积较大且许可证需要单独确认。

## API 配置

| 服务 | 是否内置 Key | 用途 | 管理入口 |
|---|---|---|---|
| DeepSeek | 否 | AI 断句、纠错、核查评分 | https://platform.deepseek.com/api_keys |
| Bocha Web Search | 否 | 联网证据搜索，可留空 | https://open.bochaai.com/dashboard |
| SearXNG | 否 | 可选自建搜索端点 | https://github.com/searxng/searxng |

注意：不要把 API Key 写入源码、README、issue、日志、截图或提交记录。

## 本地运行数据

程序运行时可能创建以下目录，均不应提交：

- `下载音频/`
- `输出文本/`
- `记忆数据库/`
- `临时文件/`
- `日志/`

## GitHub 上传前检查

```powershell
rg -n -P "(^|[^A-Za-z0-9])(sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}|AIza[0-9A-Za-z_-]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|hf_[A-Za-z0-9]{20,})" .
Get-ChildItem -Recurse -File | Where-Object { $_.Length -gt 50MB } | Select-Object FullName,Length
```

第一条命令应无真实密钥命中；第二条命令若命中第三方二进制、模型或生成包，应先删除或改为从说明文档下载。