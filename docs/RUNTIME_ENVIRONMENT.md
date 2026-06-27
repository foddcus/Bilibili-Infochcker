# 运行环境与发布说明

最近修改时间：2026-06-27  
维护者：GG

## 当前仓库形态

当前仓库是 GitHub 纯代码版，只包含源码和文档。它不包含：

- 生成的软件版本或压缩包。
- `yt-dlp.exe`、`ffmpeg.exe`、`ffprobe.exe`、`whisper-cli.exe` 等第三方二进制文件。
- `ggml-*.bin` 等 whisper 模型权重。
- API Key、cookies、本地日志、AI 报告和记忆数据库。

## 开发环境

- Windows 10/11 x64。
- .NET SDK 10。
- Visual Studio 2026 或支持 .NET 10 / WPF 的 IDE。

构建命令：

```powershell
dotnet restore AudioText.App\AudioText.App.csproj --ignore-failed-sources
dotnet build AudioText.App\AudioText.App.csproj -c Release
```

## 运行环境

运行编译后的程序需要：

- .NET 10 Desktop Runtime。
- 用户自行下载并放置的外部工具和模型，详见根目录 `README_ENVIRONMENT_AND_REFERENCES.md`。
- 可选 API Key：DeepSeek 用于 AI 断句和核查，Bocha 用于联网搜索。

## 发布包说明

本仓库不再保存 release zip 或 publish 输出。如果需要生成可运行发布包，请在本地执行：

```powershell
dotnet publish AudioText.App\AudioText.App.csproj -c Release -r win-x64 --self-contained false -o publish\视频嚼真机-V1.1-win-x64
```

发布包可按需要复制 `tools/` 和 `models/`，但不要把这些目录提交到 GitHub。若生成 self-contained 包，需要本机具备对应 `.NET 10 win-x64` runtime pack。