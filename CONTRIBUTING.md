# Contributing

最近修改时间：2026-06-24  
维护者：GG

## 贡献原则

- 保持模块边界：App、Core、Download、Transcription、Verification、Export、Infrastructure 分工不要混在一起。
- 新增下载器、转写器或搜索器时，优先实现现有接口，而不是在 WPF 主窗口里直接堆逻辑。
- 不提交 API Key、cookies、模型文件、大型二进制、构建输出和个人测试结果。
- 修改用户可见流程时，同步更新 README 或 `docs/` 下对应说明。

## 本地检查

```powershell
dotnet build AudioText.App\AudioText.App.csproj --no-restore -o build_verify\AudioText.App
```

提交前建议额外检查：

```powershell
rg -n "sk-[A-Za-z0-9]|api[_-]?key|secret|token|password|cookies" .
```

命中 API 字段名不一定是问题，但不能出现真实密钥、真实 cookies 或个人账号信息。
