## Context

动机及产品范围见 [proposal.md](proposal.md)，根目录《ntfyTray设计方案.md》保留为来源参考。各能力规格维护行为、默认值和验收要求，本文仅维护实现决策；实施依赖、进度及验证记录统一见 [tasks.md](tasks.md)。

V1 采用本地无窗口托盘进程，不要求改变已有 ntfy 或 Tunnel 服务。SDK 精确版本及目标系统的通知路线由 T0 最小通知与发布验证确定，最终候选包交由用户部署验收，代理不执行干净系统测试，未验证的兼容性不作为既成事实。

## Goals / Non-Goals

**Goals:** 用一个主项目和一个测试项目实现职责清晰的托盘接收器；把网络状态、通知结果和 Windows 最终呈现分开；使超时、退避、去重及取消可自动测试，优先验证通知及最小发布路线，同时允许独立纯逻辑推进。

**Non-Goals:** 不引入 MVVM、消息总线、复杂依赖注入、通用插件或额外服务。其余产品排除项见 proposal；不建立第二套任务计划，不在本阶段将增量规格同步成现行规格。

## Decisions

### 1. 构建基线与通知优先验证

采用 C#、.NET 10 LTS、WinForms，目标 `net10.0-windows10.0.19041.0`，主要验收平台 Windows 10 22H2 x64。项目属性为 `OutputType=WinExe`、`UseWindowsForms=true`、`WindowsPackageType=None`、`RuntimeIdentifier=win-x64`，清单 `asInvoker`；依赖框架发布（SelfContained=false、WindowsAppSDKSelfContained=false），不随包提供 .NET/Windows App Runtime；仅引用通知所需 SDK 组件，排除未使用的 AI、WinUI、语言卫星资源和调试符号，关闭单文件和裁剪。

通知采用 Windows App SDK 的 `AppNotificationManager` 和 `AppNotificationBuilder`。不选择旧 Toolkit 路线或自定义弹窗，以保持方案选定的原生通知行为。T0 优先在目标 Win10 验证稳定 SDK 的未打包初始化、注册、点击激活和最小发布；最终部署交由用户验收，不再要求代理先完成干净系统测试。等待环境或通知验证期间，固定契约后的配置、解析、退避和格式化等纯逻辑可独立推进，通知集成和候选发布仍须通过必要的 T0 验证。若技术路线不成立，记录具体证据并调整本 change，不用替代通知方式掩盖失败。

依赖锁定 Microsoft.WindowsAppSDK.Foundation 2.3.9、Microsoft.WindowsAppSDK.InteractiveExperiences 2.1.6 与 Microsoft.WindowsAppSDK.Runtime 2.4.0（来自原 2.4.0 总包的同版本组件），不引用未使用的 WinUI、AI、ML、Widgets、Search 和 DWrite 组件。应用使用系统运行库，保留通知所需的投影与 bootstrap 支持库；不以手工删除必需 DLL 来压缩包。所有依赖使用明确稳定版本和锁文件。SSE 采用 `System.Net.ServerSentEvents.SseParser`，JSON 采用 `System.Text.Json`，YAML 采用 YamlDotNet，日志采用 Serilog + Serilog.Sinks.File，测试采用 xUnit。

### 2. 模块及线程边界

| 位置 | 职责及接口边界 |
| --- | --- |
| `src/NtfyTray/Program.cs` | 单实例、基础日志、对象组装、STA 消息循环，不写网络业务 |
| `TrayApplicationContext.cs` | 托盘、菜单、状态及退出编排，不解析数据 |
| `Configuration/` | AppConfig、ConfigLoader、ConfigValidator；输出完整验证后的不可变配置 |
| `Ntfy/` | NtfyEvent、NtfySubscriber、SubscriptionManager、SubscriptionState、ReconnectPolicy |
| `Notifications/` | INotificationService、WindowsNotificationService、NotificationFormatter；输入格式化通知及取消令牌，返回处理结果 |
| `Infrastructure/` | SingleInstanceGuard、UiDispatcher、RecentMessageIds、LoggingSetup |
| `Assets/`、`app.manifest` | 固定应用图标、由根目录 connected.png/disconnected.png 转换的双状态 ICO 资源及普通用户清单 |
| `tests/NtfyTray.Tests/` | 配置、解析、恢复、通知格式、取消及隔离测试 |

主 STA 线程使用 `Application.Run(new TrayApplicationContext(...))`，不创建再隐藏 Form。内部消息句柄用于调度是允许的。所有托盘与必要通知调用经 UiDispatcher 切回主线程，后台网络不能直接碰控件。主线程业务路径不调用 Wait、Result、Thread.Sleep。

SubscriptionManager 持有每 Topic 唯一长期任务和取消资源；订阅顺序处理消息，不按消息创建 Task.Run，也不设无界队列。通知服务不发 ntfy 请求。与通用消息总线和多层类库相比，这种边界便于取消、测试和观察异常而不扩大结构。

### 2.1 双状态托盘图标

使用项目根目录现有 `connected.png`（绿色勾）和 `disconnected.png`（红色叉），保留原始图片。实施时按比例生成 `Assets/connected.ico` 和 `Assets/disconnected.ico` 并打包，不重新绘制、不增加连接中/警告等第三种托盘图案。原始 PNG 不作为可由工作目录改变的运行时路径；程序加载随包资源，释放图标句柄。EXE 和通知注册的 app.ico 使用同一 connected.png 派生图标；通知身份不随连接状态改变。

图标映射、文字状态及故障清除条件以 [tray-lifecycle](specs/tray-lifecycle/spec.md) 为准。保留各 Topic 的细分状态及组件各自的故障值，由托盘层统一汇总；每个来源只更新自身状态，避免用一个全局故障布尔值被最后一次成功操作覆盖。不增加健康监控服务或探测定时器。

T1 验证图标切换与资源释放，并在目标机器当前显示设置及一个代表性缩放比例下检查小尺寸图案可辨识，无需穷举 DPI 与主题组合；具体状态与连接数量由悬浮提示和只读菜单补足。

### 3. YAML 契约与日志启动顺序

配置字段、默认值、校验和日志恢复契约统一见 [configuration-logging](specs/configuration-logging/spec.md)。T2 据此生成 config.example.yaml，本文不维护第二份完整配置示例或默认值表。

以 AppContext.BaseDirectory 定位 config.yaml。先建立 LocalAppData 基础日志，再解析配置，完整验证成功才组装订阅。使用 YamlDotNet 支持严格键检查，校验层输出字段路径和安全原因，不输出原始 YAML。Topic 校验后作为单路径段编码，测试回环 HTTP 仅通过测试注入。真实 config.yaml 在实施时加入 .gitignore，示例仅使用占位 Token。

日志字段使用 timestamp、level、event、topic、message_id、http_status、retry_attempt、retry_delay_ms、duration_ms。默认不记录原始异常响应、认证头、标题和正文，日志故障通过独立状态通道上报，避免递归写失败日志。

日志写入失败在本进程保持故障，用户修复目录或磁盘后重启，由基础日志初始化及首次写入结果重新判定。采用这一恢复边界，避免为了故障提示再实现日志探测、自动重建或备用输出系统。

### 4. 连接状态、超时和恢复

共享长生命周期 HttpClient，禁止自动重定向；每次尝试独占 request、response、stream 和连接 CTS。使用 ResponseHeadersRead，请求头预算与事件空闲监控分离，成功取头后销毁请求阶段计时；应用停止令牌覆盖所有阶段。

状态为 Stopped → Connecting → Connected，网络/EOF/超时进入 Reconnecting，再进入 Connecting；配置/权限进入 Faulted。错误分类、等待时间和优先级以 [ntfy-subscription](specs/ntfy-subscription/spec.md) 为准。证书错误保留 TLS 检查，可按普通退避再次尝试但持续显示证书故障。订阅重新进入 Connected 时仅清除该 Topic 的连接故障。错误状态优先于普通连接汇总，提示中仍保留 X/N。

只有合法 ntfy open、keepalive 或校验通过的 message 刷新有效事件时间。未知事件和坏 JSON 不保活。稳定计时从首个有效事件进入 Connected 起算，满足规格中的稳定时长才重置退避；基准封顶后再扰动，最大基准不作为实际等待的硬上限。429 支持 Retry-After 秒数及 HTTP 日期，过期日期按零等待解释，非法值回退普通退避；计算和等待可取消且避免数值溢出。

睡眠/网络变化仅向原循环发送合并请求，不直接连接。循环按“退出 → 等待重启的配置故障 → 尚未到期的重试等待 → 重建连接”处理。恢复信号既不缩短也不重新起算等待；睡眠期间已过去的时间计入原等待，截止时间已到才可重试。等待与连接使用不同取消范围：退出可取消全部工作，恢复信号只结束当前连接，不能当成重试等待完成。取消并等待旧连接释放后再建立新连接，以保持每 Topic 单活动连接。

### 5. 游标、去重和处理结果

每 Topic 持有最后完成消息 ID、服务器时间参考和容量 2048 的集合加 FIFO 队列。业务消息要求非空 ID、匹配订阅的 Topic 以及字符串 message；允许空字符串正文，缺失或错误类型视为无效。额外 JSON 字段忽略。同一 Topic 顺序执行校验 → 去重 → 格式转换 → 通知处理 → 记录结果 → 更新集合与游标。

结果定义为 Submitted（API 已提交）、Suppressed（配置或可检测系统设置禁止）、Dropped（失败已记录）。尚无结果不得提前推进；去重命中不再次提交通知，也不把游标倒退到旧 ID。默认无持久化，新进程不带 since。重连优先用最后处理 ID；没有业务 ID 时才用首次有效连接的服务器时间。

对已明确识别为无效游标的服务端响应，本提案采用最多一次服务器时间回退，再一次不带 since 的实时连接，记录可能未恢复；通用 400 不能未经分类就当游标失效。最终实时请求仍失败时走错误分类，不重复同一无效游标。具体 ntfy 版本错误载荷由 T4/T7 的真实脱敏响应或明确协议依据确认；难以在目标实服触发的分支使用可信夹具并说明范围，不根据猜测载荷实现错误分类，不得把普通断线或无新消息误判为缓存丢失。

缓存截断与游标拒绝分别处理：成功 SSE 响应中的 X-Messages-Truncated: 1 只触发安全诊断，仍消费该响应中的消息和后续实时流，不触发上述回退或清空恢复位置。这与 ntfy 的 [Replay limits](https://docs.ntfy.sh/subscribe/api/#replay-limits) 契约一致；客户端分支由可信夹具验证，目标服务是否提供提示按实际观测记录，无法触发时注明未实测，不推断未报告的缓存缺失。

不选持久数据库或游标文件，保留方案的会话边界。去重容量外的重放、异常退出和缓存清理不保证严格一次送达。

### 6. 原生通知和激活

先绑定 NotificationInvoked，再注册名称和图标、读取设置。使用构建器添加纯文本，避免拼接服务器字符串到 XML。文本长度及结果契约见 [native-notifications](specs/native-notifications/spec.md)；格式化按 Unicode 文本元素计数，省略号和来源占用限制，优先预留来源位置，不破坏字符。

本地重试遵守规格中的总尝试次数，使用短可取消等待并可测试；正常路径延迟指标不含故障重试。通知菜单测试使用同一服务与配置，不要求网络可用。设置复查由初始化、消息处理或手动测试触发，恢复后只更新设置状态；后续 Submitted 只清除提交故障。初始化故障通过重启重新验证，不增加后台初始化循环或设置轮询。无法准确检测的横幅/专注设置不推断为阅读或可见。

普通重复启动与 SDK 通知激活路径分开处理，激活事件不执行消息业务动作，不创建第二个主订阅。T0 验证实际 SDK 激活协作再固定具体转发机制。普通退出使用 Unregister，不使用 UnregisterAll 清除长期注册。

### 7. 取消与退出顺序

Stopping 幂等标志 → 停止接收工作 → 取消订阅/重试 → 释放当前响应与流 → 异步观察全部任务结束 → 注销通知运行资源 → 移除释放 NotifyIcon → 刷新关闭日志 → 结束消息循环。系统恢复事件处理器同时解除注册。三秒是正常退出验收，不作为立即杀进程策略。

### 8. 验证分层与原方案映射

| 原方案章节 | 能力规格 | 实施任务 |
| --- | --- | --- |
| 3、13 | windows-distribution | T0、T8 |
| 4、5、10 | tray-lifecycle | T1、T5 |
| 6、11 | configuration-logging | T2 |
| 7、8 | ntfy-subscription | T3、T4、T5 |
| 9 | native-notifications | T0、T6 |
| 12、15、16 | windows-distribution 及各能力场景 | T7、T8 |
| 1、2、14 | proposal 范围、tasks 依赖 | 全部 |

自动测试注入可替换时钟、延迟、随机源、HTTP 响应流和通知服务，覆盖协议分块、退避、游标、去重、故障隔离及各阶段取消，不等待真实数分钟。同类边界采用参数化用例；第三方库检查配置映射与必要接入行为，不全面复测其内部实现。每个规格场景需要适当依据，不要求同时建立单元、集成和实机测试，也不设覆盖率数字或穷举组合门槛。

真实通知、SDK 激活、Windows 10 运行库、Tunnel 主链及系统恢复仍通过实机验收；罕见诊断分支按 windows-distribution 使用可信夹具并注明限制。实机只验证代表性退出阶段，恢复信号与等待类型的组合复用自动化。性能在一台目标机器的一次正常负载联调中采集一组有限样本，记录发送速率、样本数及 P95 算法，不以单条消息作统计，不另建性能平台或要求多机多轮压测。

完整自动化通过后准备带说明的候选包，最终部署由用户验收，其他实机检查仍使用同一候选包；首次正式交付前完成一次 24 小时稳定性验证。性能采样可合并到联调或稳定性运行，会退出/重启客户端的操作安排在稳定性计时之外。具体依赖仅维护在 tasks。

源码基线、依赖、命令、ZIP SHA-256、解压清单及运行文件指纹集中于候选清单，配置和环境记录一次，各任务核对并引用；无 Git 时以源码文件指纹替代提交号。候选变化按 windows-distribution 分析影响：连接、并发、资源生命周期及相关依赖/发布配置变化须重跑长期验证，无此影响且有依据时复用已通过记录并补测受影响部分。影响不明或没有有效通过记录时不能复用。最终候选满足必要验收后直接交付，不重复构建或默用旧包通过结论。

## Risks / Trade-offs

- [Windows SDK 初始化、附加包或激活与目标系统不兼容] → T0 在目标 Win10 优先验证并锁版本，失败阻断依赖通知路线的集成与发布；独立纯逻辑可继续。开发机结果不表述为干净系统实测，最终部署由用户验收。
- [缓存淘汰及异常退出丢消息] → 有界回退、安全日志和尽力送达说明，不承诺进程退出期间补收。
- [系统通知受限但 API 调用成功] → 分开记录处理结果和实际展示，实机分别验证通知设置与横幅。
- [多 Topic 顺序处理与故障重试增加延迟] → 有界资源、有限重试，在正常负载联调中采样 P95，不增加无界队列。
- [Token 明文保存或服务返回敏感错误] → 说明当前用户受控目录及只读 Token，认证头专用、日志脱敏、拒绝重定向。
- [Tunnel 返回 HTML 或缓冲事件] → 通过实际固定域名验证健康响应的媒体类型和实时性；HTML 异常诊断分支可按发布规格使用可信夹具并注明限制。客户端不负责部署 Tunnel 或实现 Access 登录。

## Migration Plan

V1 不涉及旧数据迁移。T0 优先，独立纯逻辑按 tasks 的提前开发边界推进，其余集成遵循依赖；候选包附带 README，完成必要验收及有依据的证据复用后直接发行。README 指明 .NET 10 Desktop Runtime x64 与 Windows App Runtime 2.4 x64 先决条件，两者均由系统提供。

首次发行保留示例配置，用户自行建立真实配置。后续更新先退出旧进程，保存旧发行目录和用户配置，在新目录部署并按说明验证；失败时退出新版并恢复旧目录及配置。通知长期注册清理不是普通退出或回退的默认步骤。

本变更实现并验证后才能判定 V1 完成；规格同步及归档按项目约定和授权执行，实际状态统一记录在 tasks。

## Open Questions

- T0：实际锁定的稳定 SDK/包版本、运行库检查命令和通知激活协作细节，须以目标系统证据填写。
- T4/T7：实际部署 ntfy 版本对于过期游标的响应形态，以及是否提供 X-Messages-Truncated 提示；分别验证游标拒绝回退和有效截断响应继续消费。无法实服触发的分支按发布规格使用可信夹具并注明范围，缺少提示不等同于保证缓存完整。
- T7：验收机器、服务版本、正常负载和资源采样参数，在验收开始时记录；保留一秒 P95、三秒退出及首次 24 小时要求，候选变化后的复测范围遵循发布规格。




