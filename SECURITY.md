# Security Policy

最近修改时间：2026-06-24  
维护者：GG

## 敏感信息规则

- 不要把 DeepSeek API Key、Bocha Web Search API Key、SearXNG 私有端点凭据、cookies 文件或平台登录态提交到仓库。
- 不要把 `tools/bilibili.cookies.txt`、`yt-dlp.cookies.txt`、浏览器导出的 Netscape cookies、任务日志和 AI 报告里的私人 URL 发布到 issue。
- 若 Key 曾经写入公开仓库或聊天记录，应立即在对应平台删除/重置该 Key。

## 本项目默认行为

- 开源分发版不内置任何 API Key。
- Key 只从设置窗口输入，运行时通过 HTTP `Authorization: Bearer` 头发送给对应 API 服务。
- 程序不把 Key 写入日志、Markdown 报告或 JSON 报告。
- `.gitignore` 已排除 cookies、模型权重、外部工具二进制和构建输出。

## 报告漏洞

请在公开 issue 中只描述复现步骤和影响范围，不粘贴真实 Key、cookies、付费内容 URL 或个人隐私数据。
