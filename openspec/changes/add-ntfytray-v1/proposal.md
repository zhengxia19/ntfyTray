## Why

依据根目录《ntfyTray设计方案.md》，将 Windows 10 上独立于浏览器运行的轻量 ntfy 接收器整理为可实施、可验证的 V1 变更。当前项目没有应用代码和现行能力规格，需要先明确功能边界、模块设计、实施依赖和验收条件。

## What Changes

- 新增 C# / .NET 10 无窗口 WinForms 托盘程序，提供状态、测试通知、打开配置、打开日志和退出菜单，以及单实例和可取消生命周期。
- 托盘仅使用项目根目录的 `connected.png`（就绪）和 `disconnected.png`（故障及尚未就绪）作为图标来源；细分状态保留文字说明。
- 新增启动时严格校验的 YAML 配置与脱敏滚动日志。
- 新增单服务器、1–20 个 Topic 的独立 HTTPS + SSE 订阅，支持 Bearer Token、心跳、分类重连、运行期间缓存补收和有界去重。
- 新增 Windows App SDK 原生通知，明确提交、抑制、丢弃结果以及安全的通知激活行为。
- 新增 Windows x64 依赖系统运行库的精简文件夹 ZIP 发布及必要自动化、实机、Tunnel 和首次正式交付前的 24 小时验收要求；后续按影响范围补测，验收证据集中记录并复用。
- V1 不包含配置/历史窗口、数据库、游标持久化、多服务器、发送、附件、Markdown、消息动作、复杂规则、自动更新、自启动管理或配置热加载。

本文定义变更范围；各能力规格维护行为与默认值，design 维护实现决策，tasks 统一维护依赖、进度与验证记录。根目录《ntfyTray设计方案.md》保留为来源参考，不独立维护另一份规格或任务计划。规划产物齐全不代表应用已实现或通过实机验证。

## Capabilities

### New Capabilities

- `tray-lifecycle`: 无窗口、单实例、状态汇总、菜单、线程调度和正常退出。
- `configuration-logging`: YAML 契约、路径、校验、滚动日志和凭据保护。
- `ntfy-subscription`: SSE 接收、事件过滤、独立连接、错误恢复、会话内游标和去重。
- `native-notifications`: 原生通知注册、文本映射、结果处理、有限重试和激活边界。
- `windows-distribution`: 构建与依赖基线、ZIP 发布、运行说明及完整验收。

### Modified Capabilities

无。`openspec/specs/` 当前没有现行能力规格。

## Impact

拟新增 `src/NtfyTray/`、`tests/NtfyTray.Tests/`、示例配置、使用说明及发布产物。依赖 WinForms、System.Net.ServerSentEvents、System.Text.Json、YamlDotNet、Windows App SDK、Serilog 文件输出和 xUnit；稳定版本将在实施时锁定。

外部依赖为已有 ntfy 服务、正式 Cloudflare Tunnel 固定域名和目标 Windows 通知运行环境。本变更不部署或修改这些服务。优先验证目标 Windows 10 上的通知注册、激活及最小发布，等待环境期间允许独立纯逻辑开发；最终部署由用户验收，按用户要求不执行代理侧干净系统测试，不阻塞本轮开发交付。未验证的 SDK 兼容性不视为既成事实。真实服务主链必须实测，难以安全、稳定触发的罕见诊断分支可用可信夹具并注明限制，不要求每个场景重复执行多层测试。


