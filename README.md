# 视频嚼真机

最近修改时间：2026-06-27  
维护者：GG  
版本：V1.1  
仓库形态：GitHub 纯代码版

## 项目简介

视频嚼真机是一个 Windows 桌面工具，用于从网页视频或直接音频链接提取音频，调用本地 whisper.cpp 生成转写文本，并可在用户自行配置 API Key 后进行 AI 断句、联网取证和结构化核查。

本仓库是纯代码版本：不包含生成的软件发布包、不包含 whisper 模型权重、不包含 FFmpeg/yt-dlp/whisper.cpp 二进制文件、不包含 cookies、不包含 API Key、不包含本地任务日志或记忆数据库。运行所需环境和第三方引用见 [README_ENVIRONMENT_AND_REFERENCES.md](README_ENVIRONMENT_AND_REFERENCES.md)。

## 核心功能

- 网页视频音频提取：通过用户自行放置的 `yt-dlp` 外部工具处理普通网页视频链接。
- 直接音频下载：支持 `.mp3`、`.m4a`、`.wav`、`.aac` 等直接音频链接。
- 本地语音转写：通过用户自行放置的 `whisper.cpp` 命令行工具和模型权重生成文本。
- AI 断句与核查：用户自行配置 DeepSeek API Key 后，可进行断句、纠错、联网搜索取证和结构化评分。
- 记忆数据库：运行时可把转写文本和 AI 结果写入本地 `记忆数据库/`，该目录不属于仓库内容。
- 空间清理：分析结束后清理软件托管的下载音频和临时视频文件，保留文本、报告和本地记忆数据。

## GitHub 版本边界

本仓库只保留源码、项目文件和文档。以下内容已从仓库目录移除，并由 `.gitignore` 禁止提交：

- `tools/`：yt-dlp、FFmpeg、FFprobe、whisper.cpp 等第三方二进制工具。
- `models/`：`ggml-*.bin` 等 whisper 模型权重。
- `release/`、`publish/`、`.codex-tmp/`：生成的软件版本和临时打包目录。
- `bin/`、`obj/`、`build_verify/`：本机构建产物。
- `.vs/`、`.codex/`、`.agents/`：本地 IDE 或 agent 配置。
- `记忆数据库/`、`输出文本/`、`下载音频/`、`临时文件/`、`日志/`：运行时数据。

## 源码构建

要求：Windows 10/11 x64，.NET SDK 10。

```powershell
dotnet restore AudioText.App\AudioText.App.csproj --ignore-failed-sources
dotnet build AudioText.App\AudioText.App.csproj -c Release
```

运行前请按 [README_ENVIRONMENT_AND_REFERENCES.md](README_ENVIRONMENT_AND_REFERENCES.md) 放置外部工具和模型文件。仓库本身不提供这些二进制文件。

## API 设置

开源纯代码版不内置任何 API Key。首次运行后，请在设置页自行填写 DeepSeek API Key；Bocha Web Search API Key 可留空，留空时使用公开搜索降级链路。详情见 [docs/API_AND_REGISTRATION.md](docs/API_AND_REGISTRATION.md)。

## 许可

本项目采用 [PolyForm Noncommercial License 1.0.0](LICENSE.md) 做非商业使用授权。该授权不是 OSI 认证的开源许可证；商业使用、商用集成、销售、SaaS 转售或内部商业生产用途需另行获得授权。

第三方工具和模型遵循其各自许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。


##直接下载
视频嚼真机-V1.0-win-x64.zip（~240M）
链接: https://pan.baidu.com/s/11LMiKUWlX2iNFB0GF8D_sg?pwd=31zd 提取码: 31zd 

