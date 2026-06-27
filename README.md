# 视频嚼真机

最近修改时间：2026-06-27  
维护者：GG  
版本：V1.1  
产品署名：designed by foddcus 快怿（https://github.com/foddcus）

## 项目简介

视频嚼真机是一个 Windows 轻量化桌面工具，用于从网页视频或直接音频链接提取音频，调用本地 whisper.cpp 生成转写文本，并可选调用 AI API 和联网搜索服务做视频内容核查。

本项目只面向用户有权访问、下载、转写和分析的内容，不提供登录绕过、付费/会员绕过、地区限制绕过、DRM 绕过或批量抓取能力。

## 核心功能

- 网页视频音频提取：通过 `yt-dlp` 外部工具处理普通网页视频链接。
- 直接音频下载：支持 `.mp3`、`.m4a`、`.wav`、`.aac` 等直接音频链接。
- 本地语音转写：通过 `whisper.cpp` 命令行工具生成文本。
- AI 断句与核查：用户自行配置 DeepSeek API Key 后，可进行断句/纠错、联网搜索取证、结构化评分，并为关键主张追加“客观属实 / 基本属实 / 有失偏颇 / 煽风点火 / 胡言乱语”五级评价。
- 记忆数据库：每次任务会把转写文本和 AI 结果写入 `记忆数据库/video-analysis-memory.json`；重复分析同一视频时直接复用已转写文本，同一发布人的往期视频会作为后续 AI 核查背景参考。
- 空间清理：分析流程结束后自动删除软件托管的下载音频和临时视频文件，保留转写文本、AI 报告和记忆数据库。
- 彩色评价：AI 评语栏会把“客观属实 / 基本属实 / 有失偏颇 / 煽风点火 / 胡言乱语”等主张标签以不同颜色显示。
- 结果保存：保存原始转写、断句稿、AI Markdown 报告和 JSON 报告。

## 发布配置边界

- 当前发布包不内置 DeepSeek API Key 或 Bocha Web Search API Key；用户需要在设置页自行填写。
- 源码和发布包不包含 cookies、任务日志、AI 报告或个人运行数据。
- `tools/*cookies*.txt`、`models/*.bin`、`tools/whisper/`、`.whisper-output/` 已在 `.gitignore` 中排除。
- 用户在设置页填写的 Key 仅用于当前运行态，不写入任务日志或 AI 报告。

## 快速运行

推荐直接使用 `历代软件压缩包/视频嚼真机-V1.1-win-x64.zip`。解压后先阅读压缩包内的 `README-运行环境.md`，再运行 `AudioText.App.exe`。

当前压缩包是自包含 Windows x64 版本，不要求目标机器预装 `.NET 10 Desktop Runtime`。本地语音转写随包放入 `tools/whisper/Release/whisper-cli.exe`、FFmpeg/yt-dlp 和 `models/ggml-large-v3-turbo-q8_0.bin`，并保留 `models/ggml-tiny.bin` 用于轻量备用。

## 源码构建

当前项目无第三方 NuGet 依赖，`NuGet.Config` 清空包源以便离线构建。

```powershell
dotnet build AudioText.App\AudioText.App.csproj --no-restore -o build_verify\AudioText.App
```

若需要发布自包含包，需要本机具备对应的 `.NET 10 win-x64` runtime pack，或临时恢复 NuGet 源后执行 restore/publish。

## API 设置

当前发布包不内置 DeepSeek 或 Bocha API Key。首次运行后，请在设置页自行填写 DeepSeek API Key；Bocha Web Search API Key 可留空，留空时使用公开搜索降级链路。详见 [docs/API_AND_REGISTRATION.md](docs/API_AND_REGISTRATION.md)。

## 许可证

本项目采用 [PolyForm Noncommercial License 1.0.0](LICENSE.md) 做非商业使用授权。该授权不是 OSI 认证的开源许可证；商业使用、商用集成、销售、SaaS 转售或内部商业生产用途需另行获得授权。

第三方工具和模型遵循其各自许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 下载
直接下载（AI和web Search的api需要自己买）：
视频嚼真机-V1.0-win-x64.zip（~240M，需要自己下模型和接口）
链接: https://pan.baidu.com/s/1frG3Us_crU8_r09yjSadmQ?pwd=56w4 提取码: 56w4 

视频嚼真机-V1.1-win-x64.zip（~1.66G，内含模型和调用接口）
链接: https://pan.baidu.com/s/1zqvuAWZXIKr8XEKwV6_4aw?pwd=jjgx 提取码: jjgx 


