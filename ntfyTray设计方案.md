> 文档定位：本文保留 V1 的设计来源与选型说明。当前变更范围以 [proposal](openspec/changes/add-ntfytray-v1/proposal.md) 为准，行为与默认值以该变更的五项 specs 为准，实现决策见 [design](openspec/changes/add-ntfytray-v1/design.md)，任务依赖、进度和验证记录仅维护在 [tasks](openspec/changes/add-ntfytray-v1/tasks.md)。这些仍是变更规划，不代表应用已实现；归档后现行约定以 openspec/specs 为准。

## 一、最终设计确认

**确定采用：C#＋无窗口 WinForms 托盘程序＋YAML 配置＋ntfy 长连接订阅＋Windows 原生通知。**

程序不创建主窗口，不实现自定义通知弹窗。WinForms 只负责消息循环、系统托盘图标和右键菜单；通知由 Windows 显示。`ApplicationContext` 和 `NotifyIcon` 可以承担这种程序的生命周期与托盘交互。([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.applicationcontext?view=windowsdesktop-10.0 "https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.applicationcontext?view=windowsdesktop-10.0"))

**有一处需要根据你此前的部署环境调整：第一版将连接协议明确为 HTTPS＋SSE，而不是同时实现 JSON Stream、SSE、WebSocket。** 原因是你计划使用 Cloudflare Tunnel，Cloudflare 文档明确提示普通流式响应可能被缓冲，而 `Content-Type: text/event-stream` 用于告知 Tunnel 实时转发。ntfy 的 `/topic/sse` 正好提供这种格式，事件中的消息内容仍然是 JSON。([Cloudflare Docs](https://developers.cloudflare.com/cloudflare-one/troubleshooting/tunnel/ "https://developers.cloudflare.com/cloudflare-one/troubleshooting/tunnel/"))

最终架构为：

```text
NtfyTray.exe
    │
    ├─ 不显示主窗口、控制台窗口或任务栏窗口按钮
    ├─ 在系统托盘显示图标及右键菜单
    ├─ 启动时读取 config.yaml
    ├─ 通过 HTTPS + SSE 持续订阅 ntfy Topic
    ├─ 连接异常后自动恢复
    │
    └─ 收到消息
         ↓
       解析 JSON
         ↓
       调用 Windows 通知 API
         ↓
       Windows 显示原生通知
```

下文说明 V1 的设计来源；实施时按 OpenSpec 中维护的规格和设计执行，不将本文作为第二份持续维护的行为契约或任务计划。

---

## 二、V1 的目标与边界

### 2.1 产品目标

> 开发一个面向 Windows 10 的轻量 ntfy 通知接收器。程序由用户启动，在当前用户会话中常驻系统托盘，根据 YAML 配置订阅 ntfy 消息，并将消息转换成 Windows 原生通知。

本客户端只负责**接收和展示**。任务完成、任务失败、等待人工操作等事件，由发送端转换成 ntfy 消息，不在客户端中识别或判断。

### 2.2 功能范围

| 功能         | V1 设计                        |
| ---------- | ---------------------------- |
| 无窗口运行      | 不创建业务窗口，不显示控制台               |
| 托盘图标       | 显示运行状态，提供必要右键菜单              |
| YAML 配置    | 启动时读取，修改后重启生效                |
| 服务器        | 支持一个 ntfy 服务地址               |
| Topic      | 支持一个或多个 Topic                |
| 身份认证       | 支持 Bearer Token，也允许不配置 Token |
| 连接协议       | 只实现 HTTPS＋SSE                |
| 实时接收       | 保持长连接，不定时轮询                  |
| 自动重连       | 指数退避、随机扰动、心跳超时检测             |
| 断线补收       | 在同一次程序运行期间，利用服务端缓存尝试补收       |
| 重复消息       | 在内存中按消息 ID 去重                |
| Windows 通知 | 标题、正文、来源 Topic               |
| 日志         | 本地滚动日志，默认不记录消息正文和凭据          |
| 退出         | 可取消连接、重试等待和后台任务，清理托盘图标       |

### 2.3 明确不做的功能

V1 不做配置窗口、消息历史窗口、数据库、多服务器、消息发送、附件下载、Markdown 渲染、消息操作按钮、复杂规则、自动更新和开机自启动管理。

**不持久化消息游标。** 因而需要明确区分：

| 场景            | V1 行为                 |
| ------------- | --------------------- |
| 程序运行，网络短暂断开   | 自动重连，尝试补收断线期间的缓存消息    |
| 程序运行，服务器重启    | 自动重连；能否补收取决于服务器是否保留缓存 |
| 用户退出程序，然后重新启动 | 视为新会话，不主动补收退出期间的历史消息  |
| 电脑睡眠后恢复，原进程仍在 | 重建连接，按运行期间断线处理        |
| 电脑重启          | 用户重新启动程序，视为新会话        |

这是对第一版范围的控制，不是以后无法扩展。后续需要“程序重启后继续补收”时，再增加一个小型游标文件即可，仍不必先引入数据库。

---

## 三、技术选型

### 3.1 技术栈

| 层次       | 选型                                         | 职责             |
| -------- | ------------------------------------------ | -------------- |
| 开发语言及运行时 | C#、.NET 10 LTS                             | 程序主体           |
| 桌面基础     | WinForms                                   | 消息循环、托盘、右键菜单   |
| 应用生命周期   | `ApplicationContext`                       | 无主窗口情况下管理启动和退出 |
| 托盘       | `NotifyIcon`                               | 图标、状态、菜单       |
| HTTP 客户端 | `HttpClient`                               | SSE 连接         |
| SSE 解析   | `System.Net.ServerSentEvents.SseParser`    | 解析 SSE 事件      |
| JSON 解析  | `System.Text.Json`                         | 解析 ntfy 消息     |
| YAML 解析  | `YamlDotNet`                               | 读取配置           |
| 系统通知     | Windows App SDK 的 `AppNotificationManager` | 注册及发送原生通知      |
| 日志       | `Serilog`＋`Serilog.Sinks.File`             | 本地日志及滚动保留      |
| 自动化测试    | xUnit                                      | 配置、消息处理、重连等测试  |

.NET 10 是当前长期支持版本；.NET 提供了 SSE 解析器和可取消的异步枚举 API，没有必要为这个项目自行实现一套完整 SSE 协议解析器。([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support "https://learn.microsoft.com/en-us/dotnet/core/releases-and-support"))

YAML 和日志分别使用已有的 YamlDotNet、Serilog 文件输出组件，不自行编写解析器或日志轮转逻辑。([GitHub](https://github.com/aaubry/yamldotnet "https://github.com/aaubry/yamldotnet"))

### 3.2 修正此前的通知组件建议

此前提到的 `Microsoft.Toolkit.Uwp.Notifications` 属于旧教程常见路线。**本次新项目采用微软当前推荐的 Windows App SDK 通知 API：**

```csharp
Microsoft.Windows.AppNotifications
Microsoft.Windows.AppNotifications.Builder
```

微软当前将 `AppNotificationManager` 列为 WinForms、WPF 和未打包 Win32 应用的推荐通知 API。使用它不意味着要把程序改成 WinUI，也不要求你开发 WinUI 窗口。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/ "https://learn.microsoft.com/en-us/windows/apps/develop/notifications/"))

### 3.3 系统与构建基线

建议将 V1 验收目标明确为：

```text
主要平台：Windows 10 x64
主要测试版本：Windows 10 22H2
.NET：10 LTS
目标框架：net10.0-windows10.0.19041.0
运行权限：普通用户
输出类型：WinExe
```

这里的 Windows 10 是**本项目兼容性测试目标**，不能据此宣称所有 Windows 10 版本仍处于微软支持周期内；微软当前的 .NET 支持矩阵对 Windows 10 版本和版本类型有所限制。([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/install/windows "https://learn.microsoft.com/en-us/dotnet/core/install/windows"))

所有第三方依赖使用稳定版本，并将实际版本锁定到项目文件和依赖锁文件中，不使用浮动版本，不自动采用 Preview 或 Experimental 包。

---

## 四、程序结构

### 4.1 项目组织

采用**一个主程序项目＋一个测试项目**即可，不拆成多个服务或大量类库。

```text
NtfyTray/
├─ src/
│  └─ NtfyTray/
│     ├─ Program.cs
│     ├─ TrayApplicationContext.cs
│     │
│     ├─ Configuration/
│     │  ├─ AppConfig.cs
│     │  ├─ ConfigLoader.cs
│     │  └─ ConfigValidator.cs
│     │
│     ├─ Ntfy/
│     │  ├─ NtfyEvent.cs
│     │  ├─ NtfySubscriber.cs
│     │  ├─ SubscriptionManager.cs
│     │  ├─ SubscriptionState.cs
│     │  └─ ReconnectPolicy.cs
│     │
│     ├─ Notifications/
│     │  ├─ INotificationService.cs
│     │  ├─ WindowsNotificationService.cs
│     │  └─ NotificationFormatter.cs
│     │
│     ├─ Infrastructure/
│     │  ├─ SingleInstanceGuard.cs
│     │  ├─ UiDispatcher.cs
│     │  ├─ RecentMessageIds.cs
│     │  └─ LoggingSetup.cs
│     │
│     ├─ Assets/
│     │  ├─ app.ico
│     │  ├─ connected.ico       ← 从根目录 connected.png 转换
│     │  └─ disconnected.ico    ← 从根目录 disconnected.png 转换
│     │
│     ├─ app.manifest
│     └─ NtfyTray.csproj
│
├─ tests/
│  └─ NtfyTray.Tests/
│
├─ config.example.yaml
├─ README.md
└─ .gitignore
```

不引入 MVVM、消息总线、通用插件系统，也不为了这几个对象引入复杂依赖注入框架。由启动入口完成对象组装即可。

### 4.2 模块职责

| 模块                           | 负责                    | 不负责          |
| ---------------------------- | --------------------- | ------------ |
| `Program`                    | 启动准备、单实例、基础日志、进入消息循环  | 网络业务         |
| `TrayApplicationContext`     | 托盘、菜单、状态显示、生命周期       | JSON/YAML 解析 |
| `ConfigLoader/Validator`     | 配置读取、默认值、校验           | 修改服务器配置      |
| `SubscriptionManager`        | 创建和停止各 Topic 的订阅，汇总状态 | 通知内容排版       |
| `NtfySubscriber`             | 连接、SSE 读取、事件解析、重连     | 操作托盘控件       |
| `ReconnectPolicy`            | 重连等待时间、错误分类           | 实际建立连接       |
| `NotificationFormatter`      | 标题和正文转换               | 调用网络         |
| `WindowsNotificationService` | 通知注册、提交、错误反馈          | 判断任务是否完成     |
| `RecentMessageIds`           | 有界内存去重                | 保存完整历史消息     |

**网络模块不能直接修改托盘控件；通知模块不能自行建立 ntfy 连接。**

---

## 五、无窗口托盘与生命周期

### 5.1 无窗口的具体含义

入口通过如下方式维持 WinForms 消息循环：

```csharp
Application.Run(new TrayApplicationContext(...));
```

不创建 `Form1`，不采用“先创建窗口再隐藏”的方式。

需要说明的是，**没有用户可见窗口，不等于进程内部不存在任何 Windows 消息句柄**。托盘和线程调度可以使用内部消息句柄，但不得出现可见主窗口、空白窗口或任务栏窗口按钮。

### 5.2 托盘菜单

V1 固定提供以下菜单，不增加配置表单：

```text
NtfyTray
─────────────────────
状态：已连接 2/2       ← 只读
─────────────────────
测试系统通知
打开配置文件
打开日志目录
─────────────────────
退出
```

“打开配置文件”交给系统默认编辑器，“打开日志目录”交给文件资源管理器。它们属于用户主动发起的操作，不是程序自身的配置 UI。

不提供配置热加载。配置修改后，退出并重新启动程序。

### 5.3 状态显示

托盘只使用当前项目根目录中的两张图片作为图标来源：`connected.png` 表示就绪，`disconnected.png` 表示故障。启动等尚未就绪的过渡阶段也使用 disconnected，以文字区分过渡与实际故障，不增加第三种图标。

| 状态 | 托盘图标 | 文字提示 |
| --- | --- | --- |
| 启动或连接中 | disconnected | 连接中 |
| 所有 Topic 正常且无当前故障 | connected | 就绪，已连接 N/N |
| 部分 Topic 断线 | disconnected | 部分连接 X/N |
| 全部断线但正在重试 | disconnected | 正在重连 |
| 配置、认证、通知或日志故障 | disconnected | 故障原因，提示查看日志 |
| 正在退出 | disconnected，随后移除 | 正在退出，并禁用重复退出操作 |

故障优先于连接数量：即使全部 Topic 已连接，只要仍有当前故障，也不显示 connected。故障解除且所有 Topic 恢复后才切回 connected；各 Topic、通知和日志仅清除自身故障，连接成功或测试通知成功不能清除其他来源的问题。用户主动配置 `notification.enabled: false` 不视为故障；系统禁止通知或通知组件异常仍须显示故障及原因。具体恢复触发以 [托盘规格](openspec/changes/add-ntfytray-v1/specs/tray-lifecycle/spec.md) 及通知、日志规格为准，不增加健康监控服务或探测定时器。

实现时保留两张原始 PNG，不重新绘制或更换图案；从它们生成供 NotifyIcon 使用的 ICO 资源并随应用发布，保持比例和勾/叉标识，检查小尺寸及不同 DPI 下的辨识度。图标仅分两种，连接中、部分连接、重连和具体故障继续通过悬浮提示及只读菜单文字区分。应用身份/通知注册图标与托盘状态图标分别管理。

Windows 可能把图标收纳进托盘溢出区域。验收要求是图标已正确注册并可访问，而不是强制要求图标始终显示在任务栏外侧。

### 5.4 单实例

同一用户、同一 Windows 会话中，只允许一个主接收实例。

普通情况下重复启动时，第二个进程直接结束，不新增托盘图标、不重复订阅、不重复弹通知。

普通启动可用命名互斥体实现；通知点击产生的激活路径需要按通知 SDK 的生命周期规则单独处理，不能把所有激活都粗暴地当成普通重复启动。微软文档也要求在通知注册前绑定激活事件处理器。([Microsoft Learn](https://learn.microsoft.com/ro-ro/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing "https://learn.microsoft.com/ro-ro/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing"))

---

## 六、YAML 配置设计

### 6.1 配置来源

完整字段、默认值与校验约束统一维护在 [configuration-logging](openspec/changes/add-ntfytray-v1/specs/configuration-logging/spec.md)。T2 实施时据此生成 config.example.yaml，真实配置由用户建立；本文不再维护重复 YAML 示例。V1 仅支持 SSE，不增加协议选项。

### 6.2 文件位置

采用两个固定位置：

```text
配置：
<NtfyTray.exe 所在目录>\config.yaml

日志：
%LOCALAPPDATA%\NtfyTray\logs\
```

读取配置必须基于 `AppContext.BaseDirectory`，不能依赖当前工作目录，否则从快捷方式或其他目录启动时容易找错文件。

日志不默认写在程序目录，避免程序放入只读目录后不能写日志。

源码仓库只保存 `config.example.yaml`，真实 `config.yaml` 加入 `.gitignore`。

### 6.3 配置字段语义

必填项、可选项省略语义、Token、Topic、通知、连接、退避及日志字段以 [配置字段契约与默认参数](openspec/changes/add-ntfytray-v1/specs/configuration-logging/spec.md) 为准。日志文件数量不代表保留天数；HTTP 回环仅供自动化测试注入，不提供忽略证书校验的用户开关。

### 6.4 校验与失败处理

配置加载应严格校验：拒绝未知字段、重复键、字段类型错误和越界值。配置必须一次完整验证通过后，才能创建订阅任务。

字段之间的大小关系及数值范围以配置规格为准，校验层统一报告字段路径和安全原因。

配置缺失或无效时，程序进入**托盘错误状态**，不发起网络连接。日志记录字段名和原因；能够定位时附带行号，但不把 Token 值输出到日志。

---

## 七、ntfy 连接与消息接收

### 7.1 连接方式

每个 Topic 建立一个独立 SSE 请求：

```http
GET /codex/sse HTTP/1.1
Host: ntfy.example.com
Accept: text/event-stream
Authorization: Bearer tk_xxxxxxxxx
```

Bearer Token 是 ntfy 支持的认证方式，应放在请求头，不拼接进 URL。([ntfy](https://docs.ntfy.sh/publish/ "https://docs.ntfy.sh/publish/"))

例如两个 Topic：

```text
SubscriptionManager
    ├─ codex        → /codex/sse
    └─ server-alert → /server-alert/sse
```

ntfy 也支持一次订阅多个 Topic，但 V1 选择每 Topic 一条连接，便于独立维护状态、恢复位置和权限错误。这个选择面向少量 Topic，不针对大量订阅优化。([ntfy](https://docs.ntfy.sh/subscribe/api/ "https://docs.ntfy.sh/subscribe/api/"))

### 7.2 HttpClient 的生命周期

整个程序复用一个长生命周期 `HttpClient`，每条订阅拥有独立的请求、响应、读取流和取消令牌。

重连时释放旧的请求与响应，不要每次都销毁并重建整个 `HttpClient`。微软也建议复用客户端和连接池。([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/httpclient-guidelines "https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/httpclient-guidelines"))

发送请求时必须使用：

```csharp
HttpCompletionOption.ResponseHeadersRead
```

不能使用读取完整响应正文后才返回的方式，因为 SSE 响应本来就可能持续很长时间。

**响应头超时和响应正文读取超时必须分开实现。** 微软明确说明，`ResponseHeadersRead` 模式下，`HttpClient.Timeout` 不会继续约束后面的响应流读取。([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-9.0 "https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-9.0"))

### 7.3 超时策略

本项目采用：

```text
连接及响应头超时：15 秒
正常长连接总时长：不设置固定上限
有效事件空闲超时：150 秒
```

实现时需要两个不同的取消范围：

```text
程序退出 CancellationToken
    └─ 当前连接 CancellationToken
         ├─ 请求阶段超时
         └─ 运行阶段心跳监控
```

取得响应头后，应解除请求阶段的超时计时，避免一个原本用于“连接超时”的计时器，在连接成功后继续运行并误杀长连接。

### 7.4 SSE 与 JSON 解析

使用 `SseParser` 取得完整事件，再解析事件的 `Data`。不要把网络读取的一块字节直接当作一条 JSON，也不要简单删除每行的 `data:` 就认为完成了 SSE 解析。

关键调用形态为：

```csharp
await foreach (
    var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
{
    // item.Data 是一个完整 SSE 事件的数据。
    // 在这里再解析 ntfy JSON。
}
```

该解析器及其可取消异步枚举由 .NET 提供。([Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/system.net.serversentevents.sseparser.create?view=net-10.0 "https://learn.microsoft.com/en-us/dotnet/api/system.net.serversentevents.sseparser.create?view=net-10.0"))

解析后按 JSON 内的 `event` 字段处理：

| ntfy 事件     | V1 行为                |
| ----------- | -------------------- |
| `open`      | 确认订阅建立，更新连接状态        |
| `keepalive` | 更新连接活跃时间，不弹通知        |
| `message`   | 校验、去重、格式转换、提交通知      |
| 其他事件        | 忽略业务内容，必要时记 Debug 日志 |

ntfy 的连接、保活和消息事件都可能包含 ID，因此**不能看到 `id` 就认为收到了一条需要展示的通知**。([ntfy](https://docs.ntfy.sh/subscribe/api/ "https://docs.ntfy.sh/subscribe/api/"))

### 7.5 消息处理顺序

```text
收到完整 SSE 事件
    ↓
解析 JSON
    ↓
判断 event 是否为 message
    ↓
校验消息 ID、Topic 和内容
    ↓
按消息 ID 去重
    ↓
生成通知标题和正文
    ↓
提交 Windows 通知
    ↓
记录处理结果
    ↓
更新内存恢复位置
```

同一个 Topic 内按接收顺序处理。不同 Topic 不承诺全局顺序。

V1 不设置无界消息队列，也不为每条消息创建一个 `Task.Run`。每个订阅顺序处理；对 Windows 通知和托盘的访问，通过统一调度入口完成。

---

## 八、心跳、自动重连与断线补收

### 8.1 连接状态机

每个 Topic 单独维护：

```text
Stopped
   ↓
Connecting
   ↓
Connected
   │
   ├─ 网络断开 / EOF / 心跳超时
   │       ↓
   │    Reconnecting
   │       ↓
   │    Connecting
   │
   └─ 配置或权限错误
           ↓
         Faulted
```

收到 HTTP 200 还不能马上判定业务连接正常。应检查响应类型，并收到有效 ntfy 事件后再进入 `Connected`。

### 8.2 指数退避

普通网络错误采用：

```text
1 秒 → 2 秒 → 4 秒 → 8 秒 → 16 秒 → 30 秒
```

再加入约 ±20% 的随机扰动，避免多个订阅同时断线后同步重连。

建议计算方式：

```text
基准等待 = min(最大等待, 初始等待 × 2^失败次数)
实际等待 = 带随机扰动的基准等待
```

不要在每次收到 HTTP 200 时立即清零失败次数。建议连接稳定运行满 60 秒后再重置，避免“连上即断”的情况下不断进行高频重连。

### 8.3 心跳失效检测

默认连续 150 秒未收到有效事件，主动取消当前连接并进入重连。

ntfy 默认发送保活事件的间隔为 45 秒，因此该默认值留有余量；服务器改变保活间隔时，客户端空闲超时也需要相应调整。([ntfy](https://docs.ntfy.sh/config/ "https://docs.ntfy.sh/config/"))

检测的是**连接是否仍在传递有效事件**，不是“是否有业务消息”。正常情况下即使几小时没有任务通知，也不应该因为没有 `message` 而反复重连。

### 8.4 错误分类

| 错误                    | V1 处理                             |
| --------------------- | --------------------------------- |
| DNS 失败、连接超时、网络断开、流结束  | 指数退避重连                            |
| HTTP 500、502、503、504  | 自动重连                              |
| HTTP 429              | 优先遵守 `Retry-After`；允许超过普通的 30 秒上限 |
| HTTP 401、403          | 显示认证/权限错误，低频重试，例如每 300 秒一次        |
| HTTP 404 或明显错误的服务地址   | 显示配置错误，等待用户修改后重启                  |
| HTTPS 证书校验失败          | 记录具体原因，不绕过证书验证                    |
| HTTP 200 但返回 HTML 登录页 | 判定为代理或认证配置错误，不尝试按 SSE 解析          |
| 单条 JSON 无效            | 记录异常，跳过该事件，不让整个程序崩溃               |
| 用户主动退出产生的取消异常         | 正常停止，不记录为网络故障                     |

断线和恢复默认只更新托盘与日志，不每次都弹系统通知，避免网络抖动产生大量无用提醒。

### 8.5 睡眠恢复和网络切换

收到系统恢复或网络变化信号时，通知现有订阅循环结束当前连接并重新连接。

**不能另起一套订阅循环。** 必须先取消并等待旧连接结束，再建立新连接，确保每个 Topic 始终最多只有一个活动订阅任务。恢复信号只是请求，不绕过普通退避、429 限流、401/403 权限等待，也不能激活等待修改配置后重启的订阅；不重新起算等待，睡眠已过去的时间计入原截止时间。退出优先并可取消全部等待，具体交叉场景见 [订阅规格](openspec/changes/add-ntfytray-v1/specs/ntfy-subscription/spec.md)。

### 8.6 断线补收

每个 Topic 在内存中保存：

```text
最后处理完成的消息 ID
必要的服务器时间信息
最近处理过的消息 ID 集合
```

首次启动建立连接，不主动读取历史缓存；运行中重连时，使用类似：

```text
/codex/sse?since=<最后处理完成的消息ID>
```

ntfy 的 `since` 参数支持消息 ID、时间戳等恢复方式，但补收依赖服务端缓存。缓存关闭、缓存过期或服务器缓存丢失时，客户端无法恢复已经不存在的消息。([ntfy](https://docs.ntfy.sh/subscribe/api/ "https://docs.ntfy.sh/subscribe/api/"))

从未处理过业务消息但此前已经成功连接时，可以使用首次有效连接事件提供的服务器时间作为恢复参考，避免依赖客户端本地时钟；**不能把 `open` 或 `keepalive` 的 ID 当作业务消息游标。**

对于实际部署版本，记录可观测的过期游标及截断行为；难以安全、稳定触发的诊断分支，可使用真实脱敏响应或明确协议依据构造可信夹具，并注明实服未验证范围，不根据猜测载荷分类。仅在服务端响应明确拒绝恢复位置时执行有界回退，不能凭普通 400、断线或没有新消息认定游标失效。成功 SSE 响应携带 `X-Messages-Truncated: 1` 时只记录“可能有消息未恢复”，继续处理该响应中的缓存消息和实时事件，不取消有效连接、不重置 since、不触发回退。截断是有效响应中的缺失提示，与恢复请求被拒绝不同；参见 [ntfy Replay limits](https://docs.ntfy.sh/subscribe/api/#replay-limits)。未报告该头不代表保证缓存完整；真实主链仍须实测，验收分工以 [发布规格](openspec/changes/add-ntfytray-v1/specs/windows-distribution/spec.md) 为准。

### 8.7 去重与可靠性边界

V1 每个 Topic 保留最近 2,048 个已处理消息 ID，使用有界集合，超过容量淘汰最旧项。

不要用“标题＋正文”去重：两个内容相同但 ID 不同的任务通知，仍可能是两条不同事件。

恢复位置只能在消息得到明确处理结果后推进：

```text
Submitted：已提交 Windows
Suppressed：因配置或系统通知设置而跳过
Dropped：处理失败，已明确记录
```

不能把“刚接收到消息”直接当成“已经提交通知”。

**V1 是尽力送达的桌面通知工具，不承诺严格一次送达，也不承诺用户一定看到。** 正常运行、正常网络和有效缓存范围内，应尽量避免漏收和重复；异常退出、缓存淘汰和 Windows 抑制通知需要如实记录。

---

## 九、Windows 原生通知设计

### 9.1 通知类型

使用 Windows App SDK 的本地应用通知：

```text
ntfy 负责网络传输
    ↓
NtfyTray 接收消息
    ↓
本地调用 AppNotificationManager
    ↓
Windows 显示通知
```

这里不使用 Windows Push Notification Services，不需要为本功能建立 Azure 推送服务。微软将本地应用通知与 WNS 推送明确区分为不同投递方式。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/ "https://learn.microsoft.com/en-us/windows/apps/develop/notifications/"))

### 9.2 初始化

通知服务初始化时应：

1. 检查通知 API 是否可用。

2. 绑定 `NotificationInvoked`。

3. 注册固定应用名称 `NtfyTray` 和本地图标。

4. 检查应用通知设置状态。

未打包应用可以通过注册 API 指定显示名称和图标；普通退出时使用 `Unregister` 清理运行资源，不应每次退出都调用用于清除长期注册的 `UnregisterAll`。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.register?view=windows-app-sdk-2.0 "https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.register?view=windows-app-sdk-2.0"))

### 9.3 消息映射

| 输入            | V1 映射                           |
| ------------- | ------------------------------- |
| `title` 非空    | 使用消息标题                          |
| `title` 缺失或空白 | 使用 `notification.default_title` |
| `message`     | 作为正文，按纯文本处理                     |
| `topic`       | 按配置附加为来源说明                      |
| `priority`    | 可解析和记录，V1 不映射成闹钟或持续响铃           |
| `tags`        | V1 不做复杂转换                       |
| `click`       | 忽略，不自动打开链接                      |
| `actions`     | 忽略，不执行 HTTP 请求或系统命令             |
| `attachment`  | 不下载、不打开、不渲染                     |

例如：

```text
应用：NtfyTray

标题：Codex 任务完成
正文：项目 A 的修改和测试已完成。
      来源：codex
```

标题和正文应设置项目级长度限制，例如标题 128 个字符、正文 1,024 个字符，超出截断并加省略标记。这是本工具的显示策略，不是宣称 Windows 的通知上限就是这两个数。

字符串必须正确处理中文、换行和特殊字符；通过通知构建器添加文本，不直接把服务器内容拼接进 XML。

### 9.4 通知调用

关键调用形态为：

```csharp
var notification = new AppNotificationBuilder()
    .AddText(title)
    .AddText(body)
    .BuildNotification();

AppNotificationManager.Default.Show(notification);
```

这些是系统通知 API，不是 WinForms 自定义弹窗，也不采用 `MessageBox` 或 `NotifyIcon.ShowBalloonTip` 代替本项目选定的通知路径。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart "https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart"))

### 9.5 点击通知

V1 不提供任何消息业务操作。

点击通知不执行命令，不下载附件，不打开消息携带的 URL，也不弹出历史窗口。SDK 必需的通知激活处理仍需保留，并保证不会产生第二套订阅。

用户在程序退出后点击旧通知，Windows 可能激活应用。允许程序因此恢复到托盘运行；这与“程序退出后还能自动接收新的 ntfy 消息”不是一回事。通知交互激活属于 Windows 的应用生命周期机制。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart "https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart"))

### 9.6 通知提交失败和系统抑制

API 提交异常采用 [通知规格](openspec/changes/add-ntfytray-v1/specs/native-notifications/spec.md) 规定的有限、可取消尝试；仍失败则记录结果并显示托盘异常，不让单条消息无限阻塞整个订阅。初始化、消息处理及手动测试时复查可检测通知设置；设置允许只清除设置故障，后续 Submitted 只清除提交故障，Suppressed 不作为提交恢复证据。手动测试遵守同一配置；通知初始化失败需重启并初始化成功才清除，不增加设置轮询或后台初始化重试。

关闭应用通知、关闭横幅、专注助手以及声音设置，都可能改变通知实际呈现。程序可以读取应用通知设置，但**提交成功不等于横幅一定出现，也不等于用户已经阅读**。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.setting?view=windows-app-sdk-1.8 "https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.appnotifications.appnotificationmanager.setting?view=windows-app-sdk-1.8"))

程序必须以普通用户权限运行。微软当前明确说明，Windows App SDK 应用通知不支持提升权限运行的应用。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart "https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart"))

---

## 十、线程、取消和退出

### 10.1 线程模型

```text
主 STA 线程
    ├─ WinForms 消息循环
    ├─ NotifyIcon
    ├─ 右键菜单
    └─ 必要的通知及状态调度

异步任务
    ├─ Topic A 订阅循环
    ├─ Topic B 订阅循环
    └─ 心跳监控
```

所有托盘控件访问通过 `UiDispatcher` 切回主线程。网络 I/O 使用异步调用，不在主线程上同步等待。

禁止以下写法出现在主线程业务路径中：

```csharp
task.Wait();
task.Result;
Thread.Sleep(...);
```

### 10.2 CancellationToken 的覆盖范围

程序退出令牌必须传递到请求发送、SSE 枚举、重连等待和通知本地重试。

不能仅给 `SendAsync` 传令牌，却遗漏后面的流读取和 `Task.Delay`。

所有后台任务都必须被保存并观察异常，不能创建后不再管理。

### 10.3 退出流程

```text
用户点击退出
    ↓
标记 Stopping，防止重复退出
    ↓
停止接收新工作
    ↓
取消所有订阅和重连等待
    ↓
释放当前 HTTP 响应与读取流
    ↓
异步等待后台任务结束
    ↓
注销通知运行资源
    ↓
移除并释放 NotifyIcon
    ↓
刷新并关闭日志
    ↓
结束 WinForms 消息循环
```

正常退出目标可设为 3 秒以内。该数字是验收目标，不应通过一开始就强制杀进程来实现。

---

## 十一、日志和安全设计

### 11.1 日志内容

建议记录：

```text
应用启动及版本
配置文件位置
配置校验结果
Topic 连接状态变化
HTTP 状态码和错误类型
重连次数及下次等待时间
消息 ID 和 Topic
通知提交、抑制或失败结果
程序退出
```

采用结构化字段：

```text
timestamp
level
event
topic
message_id
http_status
retry_attempt
retry_delay_ms
duration_ms
```

正常 `keepalive` 不写 Information 日志，避免长期运行产生大量无用内容。

### 11.2 日志滚动

默认按天滚动，同时限制单文件大小为 5 MiB，保留最近 14 个文件。Serilog 文件输出组件支持按时间、大小滚动和文件数量保留。([GitHub](https://github.com/serilog/serilog-sinks-file "https://github.com/serilog/serilog-sinks-file"))

应用启动时先建立最基础的日志能力，再读取配置。这样 YAML 解析失败也有诊断记录。

磁盘写入失败应反映在托盘状态中；不能递归尝试向同一个已经失败的日志文件写入错误，形成异常循环。该故障在本进程保持，提示用户检查目录或磁盘后重启；仅新进程基础日志初始化及首次写入成功才视为恢复，其他组件恢复或后续偶然写入成功不能清除它。不增加日志探测、自动重建或备用日志系统。

### 11.3 安全约束

Token 只放认证请求头，不写日志、不写通知、不拼 URL。默认日志也不保存标题和正文。

YAML 中保存明文 Token 时，应使用当前用户受控目录；生产环境给客户端配置专门的只读用户及其 Token，而不是服务器管理员凭据。

客户端只信任配置文件指定的服务器。禁止跟随消息里的地址发起额外网络请求，禁止执行消息中的命令或操作按钮。

TLS 使用正常证书校验。对重定向采取严格策略，避免误连到登录页或把凭据传给不预期的目标。

---

## 十二、Cloudflare Tunnel 对接要求

你的预期链路可以保留：

```text
NtfyTray
   │
   │ HTTPS + SSE
   ▼
Cloudflare 自定义域名
   ▼
已配置的 Cloudflare Tunnel
   ▼
ntfy 服务
```

客户端不需要安装 `cloudflared`。

部署时需要验证三个条件：

**第一，使用正式配置的 Tunnel 和固定域名。** 不使用随机 `trycloudflare.com` 临时地址；Cloudflare 的 Quick Tunnel 不支持 SSE。([Cloudflare Docs](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/ "https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/"))

**第二，订阅端点必须实时转发。** 客户端应收到 `text/event-stream`，消息不能等到连接关闭或积累一批后才出现。不能认为“设置了不缓存”就已经等同于“不会缓冲流式响应”。([developers.cloudflare.com](https://developers.cloudflare.com/cloudflare-one/troubleshooting/tunnel/?utm_source=chatgpt.com "Tunnel · Cloudflare One docs"))

**第三，不让订阅端点依赖浏览器交互登录。** 本 V1 只实现 ntfy 的 Token 认证，不实现 Cloudflare Access 的浏览器登录流程或服务令牌配置。遇到 HTML 登录页、挑战页或非预期重定向时，应明确报代理/认证配置错误。

验收必须走实际域名测试，不能只测试直接访问服务器的情况。

---

## 十三、发布与运行环境

### 13.1 发布形态

第一版采用：

> **Windows x64 文件夹发布，压缩成 ZIP 分发。**

不要把“无窗口小工具”进一步误解成“必须只有一个 EXE”。

发行目录可以是：

```text
NtfyTray/
├─ NtfyTray.exe
├─ 程序及运行时依赖文件...
├─ Assets/
├─ config.example.yaml
├─ config.yaml          ← 用户自行配置，不随更新覆盖
└─ README.md
```

项目关键属性：

| 属性                   | 设计          |
| -------------------- | ----------- |
| `OutputType`         | `WinExe`    |
| `UseWindowsForms`    | `true`      |
| `WindowsPackageType` | `None`      |
| `RuntimeIdentifier`  | `win-x64`   |
| .NET 发布              | 自包含         |
| 单文件发布                | V1 不启用      |
| 裁剪                   | V1 不启用      |
| 应用权限                 | `asInvoker` |

### 13.2 必须处理的运行库依赖

**.NET 自包含，不代表 Windows App SDK 的通知依赖也自动解决。**

微软文档指出，未打包应用需要正确部署和初始化 Windows App SDK 运行库；通知 API 还涉及 Singleton 等附加包。即使采用 Windows App SDK 自包含方式，也不能直接认定所有通知能力都已无依赖。([Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-in-existing-project "https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-in-existing-project"))

因此本方案的 V1 发布基线是：

```text
应用及 .NET 运行时：随 ZIP 提供
Windows App SDK：明确列出匹配的运行库先决条件
```

发行说明必须写清楚安装哪个稳定版本的运行库、如何检查以及如何验证通知；这些说明须在候选包生成时随包提供。

最终候选包必须在**没有 Visual Studio、没有开发工具的干净 Windows 10 环境**中测试，不能以“开发机上可以运行”代替发布验收。T0 最小通知与发布路线可先在目标开发机验证，干净环境测试集中到最终候选包，不要求两个阶段重复执行。

### 13.3 启动失败的边界

配置错误发生在应用正常启动之后，可以通过托盘和日志提示。

但某些底层运行库缺失，可能让程序在进入正常启动逻辑之前就失败，不能承诺此时仍一定显示托盘错误图标。发行包需要附带运行环境检查说明，必要时提供独立检查脚本。

因此通知与最小发布路线应优先验证；环境等待期间可推进契约已明确的独立纯逻辑，通知路线未验证时不能完成相关集成和候选发布。

---

## 十四、开发任务与顺序

### 14.1 任务与进度来源

T0–T8 的任务编号、依赖、完成条件及进度统一维护在 [tasks.md](openspec/changes/add-ntfytray-v1/tasks.md)。本文不再复制任务表或依赖图。

### 14.2 开发与验收顺序

保留通知优先原则：T0 在目标 Win10 验证无窗口托盘、本地原生通知、激活和最小发布；等待环境期间可提前实现配置、解析、退避、格式化等纯逻辑，集成完成仍须满足实际依赖。完整自动化通过后准备含说明的候选 ZIP，集中完成干净部署、更新回退、真实主链和必要实机验收，首次正式交付前保留一次 24 小时稳定性验证。性能采样可与联调或稳定性运行合并，会退出/重启客户端的测试放在计时之外。候选变化按发布规格分析影响与复用证据，最终直接交付满足验收的候选包。具体依赖以 tasks 为准，T0 最小包验证不能代替最终干净环境验收。

---

## 十五、测试与验收标准

### 15.1 自动化测试

| 测试类别    | 必测内容                                               |
| ------- | -------------------------------------------------- |
| 配置      | 正常配置、缺失配置、错误缩进、未知字段、重复键、空 Topic、错误 URL             |
| 消息解析    | `open`、`keepalive`、`message`、未知事件、缺失字段、额外字段、坏 JSON |
| SSE     | 一个事件拆成多次网络读取，多事件一次读取，中文跨字节分块，正常结束及异常断开             |
| 格式转换    | 空标题、长正文、中文、换行、XML 特殊字符                             |
| 去重      | 同一 ID 只处理一次；同正文不同 ID 正常处理；容量有上限                    |
| 重连      | 退避增长、上限、随机扰动、稳定后重置、`Retry-After`                   |
| 取消      | 正在连接、等待事件、重连等待期间均可取消                               |
| 多 Topic | 一个 Topic 403 不影响其他 Topic                           |
| 日志      | Token、标题和正文不意外写入默认日志                               |

重连测试应使用可替换的时钟或延迟机制，不让自动化测试真的等待数分钟。

每个行为需要合适的验证依据，不要求每个场景同时具备单元、集成和实机测试。同类边界使用参数化用例；日志等第三方能力主要验证配置映射和必要接入行为，不全面复测库的内部实现。各阶段取消、重连等待与恢复信号组合优先用自动化验证，实机只选代表性路径。不设覆盖率数字或穷举组合门槛，已有且仍适用的通过记录可以复用。

### 15.2 集成与实机验收

| 场景                   | 验收要求                     |
| -------------------- | ------------------------ |
| 正常启动                 | 无主窗口、无控制台，只有一个托盘图标       |
| 重复启动                 | 不出现第二个接收实例               |
| 本地测试通知               | 显示 Windows 原生通知          |
| 发送 ntfy 消息           | 标题、正文、Topic 映射正确         |
| 收到心跳                 | 不弹通知                     |
| Chrome 完全退出          | 不影响客户端接收                 |
| 断网后恢复                | 自动重连，无需重启客户端             |
| 连接假死                 | 超过空闲阈值后主动恢复，可复用自动化验证 |
| ntfy 重启              | 自动恢复订阅                   |
| Cloudflare Tunnel 重启 | 自动恢复，无重复订阅               |
| 断线期间发送消息             | 缓存有效时按设计补收，正常恢复不重复弹同一 ID |
| 程序退出期间发送消息           | 重新启动后不主动补历史，符合 V1 边界     |
| Token 错误             | 明确显示认证异常，不高频重试           |
| 关闭 Windows 通知        | 不崩溃，不误记为用户已经看到           |
| 系统睡眠后恢复              | 重建连接，不增加第二套订阅            |
| 网络重连等待时点击退出          | 能及时退出，不残留进程或托盘图标         |
| 干净 Win10 环境          | 按发行说明准备运行环境后可运行          |
| 首次正式交付前连续运行 24 小时 | 无异常退出，内存、线程、句柄没有持续异常增长；候选变更后按影响范围决定重跑或复用 |

性能验收目标保持为：

> 在正常负载下，从客户端取得完整 ntfy 消息到调用 Windows 通知 API，处理延迟的第 95 百分位不超过 1 秒。

这个指标不包含互联网传输延迟，也不包含 Windows 最终展示横幅的调度时间。Windows 是否把每条消息都立即显示为横幅，应与客户端是否处理和提交了消息分开验证。

性能验证采用一台目标 Win10、一次正常负载联调中的一组有限样本，记录发送速率、样本数及 P95 算法，不以单条消息代表统计；无需独立性能平台、多机或多轮压测。实机表中的连接假死等确定性分支可复用自动化证据，真实通知、激活、服务主链、系统睡眠与代表性退出仍须实测，具体分工以 tasks 和发布规格为准。

---

## 十六、最终交付标准

V1 完成时应交付：完整源码、锁定版本的依赖、示例 YAML、Windows x64 发布包、运行库准备说明、使用与排错文档，以及实际 Windows 10＋ntfy＋Cloudflare Tunnel 的验收记录。

候选 ZIP SHA-256、解压清单及运行文件指纹、源码基线、锁文件和发布命令集中记录一次，脱敏配置及环境也集中维护，各任务核对并引用。最终候选满足必要验收后直接交付，不重新构建。候选变化按 [windows-distribution](openspec/changes/add-ntfytray-v1/specs/windows-distribution/spec.md) 补测：影响连接循环、并发、资源生命周期、长期占用或相关依赖/发布配置，或影响不明时，重跑完整 24 小时；无此影响且有可追溯依据时，复用既有通过记录并补做受影响检查。首次未通过或失效记录不得复用，也不宣称新包已实际运行 24 小时。一秒 P95、三秒正常退出、首次 24 小时及最终候选干净 Win10 验收保留。

**最终方案可以归纳为：**

```text
形态：无窗口 WinForms 托盘程序
配置：启动时读取 YAML，修改后重启
网络：HTTPS + SSE，每 Topic 一个受控异步订阅
恢复：心跳检测 + 指数退避 + 运行期间缓存补收
展示：Windows App SDK 原生通知
存储：无消息数据库，只有配置和滚动日志
退出：全链路 CancellationToken，清理连接和托盘资源
发布：文件夹 ZIP，明确处理 Windows App SDK 运行库依赖
```

* 优先在目标 Windows 10 验证通知注册、点击激活和最小发布；干净环境发布验收集中到最终候选包，独立纯逻辑可在等待期间推进。
