# API 限制与注册网址

最近修改时间：2026-06-27  
维护者：GG  
信息来源：官方文档链接。外部服务价格、模型名、额度和限制可能变化，发布前应再次核实。

## 开源纯代码版原则

本仓库不内置任何 API Key。用户需要在程序设置页自行填写 Key；Key 只应保存在本地运行环境中，不应提交到 GitHub。

## DeepSeek API

用途：AI 断句、纠错、搜索规划和最终评分。

- API 文档：https://api-docs.deepseek.com/
- API Key 管理：https://platform.deepseek.com/api_keys
- 控制台：https://platform.deepseek.com/

默认配置：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| API 地址 | `https://api.deepseek.com` | OpenAI 兼容 `/chat/completions` 调用。 |
| 默认模型 | `deepseek-v4-flash` | 速度和成本优先。 |
| 可选模型 | `deepseek-v4-pro` | 设置页可切换，也可手动输入兼容模型名。 |
| API Key | 空 | 用户自行填写。 |

安全规则：

- 不在源码、日志、Markdown 报告或 JSON 报告中输出 Key。
- 请求时仅通过 `Authorization: Bearer <Key>` 发送给 DeepSeek API。
- 不要把手机号、邮箱、姓名、身份证号等隐私信息写入可能外发的用户标识字段。

## Bocha Web Search API

用途：作为联网证据搜索的可选主搜索源。未填写 Bocha Key 时，程序跳过 Bocha，使用 SearXNG / Google Search / Bing Search / Baidu Search / DuckDuckGo Lite 降级链路。

- 开放平台官网：https://open.bochaai.com/
- 控制台：https://open.bochaai.com/dashboard

默认配置：

| 配置项 | 默认值 | 说明 |
|---|---|---|
| API 地址 | `https://api.bochaai.com/v1/web-search` | Bocha Web Search API。 |
| 鉴权 | 空 | 用户自行填写 Key 后使用 `Authorization: Bearer <Key>`。 |
| 单查询结果数 | 8 | 程序侧每个搜索词最多读取 8 条。 |

注意事项：

- Bocha 价格、额度、速率限制和服务条款以官方控制台为准。
- Bocha Key 可留空，不影响本地转写，但联网证据质量可能下降。
- 不要把 Bocha Key 写入 issue、日志、截图或公开文档。

## SearXNG 可选端点

用途：用户自建或可用的 SearXNG JSON 搜索端点。

- 项目官网：https://github.com/searxng/searxng

若端点带私有 Token、反向代理账号或内网地址，不要提交到仓库。

## 内容合规限制

- 仅处理你有权访问、保存、转写和分析的音频或视频内容。
- 不绕过登录、付费、会员、地区、DRM 或平台风控限制。
- AI 核查结果只是辅助判断，不能替代专业法律、医疗、金融或安全审查。