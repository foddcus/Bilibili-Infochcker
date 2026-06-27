# Open Source Release Checklist

最近修改时间：2026-06-27  
维护者：GG

## 发布前检查

- [ ] `README.md`、`README_ENVIRONMENT_AND_REFERENCES.md`、`LICENSE.md`、`SECURITY.md`、`THIRD_PARTY_NOTICES.md` 已存在。
- [ ] `docs/API_AND_REGISTRATION.md` 已记录 DeepSeek、Bocha 和 SearXNG 的注册入口与限制。
- [ ] 源码中没有真实 API Key、cookies、账号、Token、测试 URL 或个人隐私路径。
- [ ] 仓库中没有 `tools/`、`models/`、`release/`、`publish/`、`.codex-tmp/`、`.whisper-output/`、`build_verify/`、`bin/`、`obj/`。
- [ ] 仓库中没有 `ggml-*.bin`、`*.zip` 发布包、`*.exe` 第三方工具、cookies 或本地日志。
- [ ] `.gitignore` 已排除模型、第三方二进制、生成包、构建产物、本地运行数据和 agent/IDE 缓存。
- [ ] 构建命令已通过，并在验证后再次清理构建产物。

## 建议命令

```powershell
rg -n -P "(^|[^A-Za-z0-9])(sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{20,}|AIza[0-9A-Za-z_-]{20,}|gh[pousr]_[A-Za-z0-9_]{20,}|hf_[A-Za-z0-9]{20,})" .
Get-ChildItem -Recurse -File | Where-Object { $_.Length -gt 50MB } | Select-Object FullName,Length
dotnet restore AudioText.App\AudioText.App.csproj --ignore-failed-sources
dotnet build AudioText.App\AudioText.App.csproj -c Release
```

若验证构建产生 `bin/`、`obj/` 或 `publish/`，上传 GitHub 前需要再次删除或确认被 `.gitignore` 排除。