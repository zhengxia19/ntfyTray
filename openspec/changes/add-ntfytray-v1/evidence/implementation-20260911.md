# 实施记录（2026-09-11）

## 范围与状态

完成 T0 工程基线；原生通知探针已实现。按 tasks 第 10 节允许的提前开发边界，实现配置校验、事件解析、退避、去重容器及通知格式化。尚未连接这些模块，也未实现正式订阅客户端；当前 Program 启动的是 T0 探针，不是 V1 成品。

1.1 已完成。1.2 仅取得 API 和进程证据，显示尚待核实；1.3 点击激活尚未验证。后续依赖 T0 的集成任务不提前勾选。未生成候选发行包、未同步规格、未归档。

## 环境与依赖

- 实际系统：Windows 10 Pro，10.0.19045，x64；.NET SDK 10.0.106，运行时 10.0.6。
- Windows PowerShell 5.1 `Get-AppxPackage *WindowsAppRuntime*` 确认 `Microsoft.WindowsAppRuntime.2` 版本 2.4.0.0 已安装。PowerShell 7 的 Appx 模块不兼容，不能据此判断运行库缺失。
- 主项目：net10.0-windows10.0.19041.0、WinExe、WinForms、win-x64、WindowsPackageType=None、asInvoker、非单文件、非裁剪。
- 包版本：Windows App SDK 2.4.0、YamlDotNet 18.1.0、Serilog 4.4.0、Serilog.Sinks.File 7.0.0；测试依赖见项目和 packages.lock.json。版本从 NuGet 实际查询并还原，不使用预览或浮动版本。
- System.Net.ServerSentEvents 使用 .NET 10 自带实现。首次显式引用包产生 NU1510，移除冗余引用后构建无警告；SseParser 分块测试仍通过。

## T0 探针

入口：`src/NtfyTray/NotificationProbeContext.cs`。绑定 NotificationInvoked 后 Register 固定名称 NtfyTray 和 app.ico，读取 Setting，以 AppNotificationBuilder 创建本地固定文本，Show 后记录 ID。点击处理仅记录无业务操作事件。退出调用 Unregister，不调用 UnregisterAll。

`--notification-probe` 在消息循环开始后发送一次本地通知，随后自动正常退出；不创建网络请求或业务窗口。无该参数时保留托盘，提供测试和退出菜单。正式固定菜单、双状态图标及单实例尚属后续任务。

首次运行日志见 [t0-probe.log](t0-probe.log)：pid 18860，注册后 Setting=Enabled，提交 ID=820，随后 stopped。运行时进程 Responding=True、MainWindowHandle=0，自动退出后不再存在。该证据仅证明注册、设置读取、API 提交和探针正常退出；不证明实际横幅显示、阅读、点击激活或单实例。

界面验证使用 Computer Use 的 @oai/sky：应用枚举成功，选择返回的 Explorer 窗口后，激活操作返回 `GetCursorPos failed: 拒绝访问。 (0x80070005)`。未能继续观察通知中心或执行点击。该错误不能独自确定是锁屏、桌面权限还是其他会话状态；需要可交互桌面后再验证。未改变桌面权限或系统设置。

## 协议依据与限制

- SDK 通知 API：锁定包内 `Microsoft.Windows.AppNotifications.Projection.xml` 核对 IsSupported、Register(displayName, iconUri)、Setting、Show 和 Unregister；[Microsoft 官方通知文档](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart)。
- ntfy 字段：[Message format](https://docs.ntfy.sh/subscribe/api/#message-format)，open/keepalive ID 不作为业务游标。
- 错误分类：[ntfy server/errors.go](https://raw.githubusercontent.com/binwiederhier/ntfy/main/server/errors.go) 的 HTTP 400 / code 40008 表示 invalid since parameter。仅实现明确参数拒绝识别，普通 400 不视为游标失效；没有目标服务器版本或实服过期游标测试证据。有界回退和截断流处理仍待网络循环实施。

## 最终合并验证

- `dotnet restore --locked-mode`：通过，见 [restore.log](restore.log)。
- `dotnet build -c Release --no-restore`：通过，0 警告、0 错误，见 [build.log](build.log)。
- `dotnet test -c Release --no-build --logger "trx;LogFileName=tests.trx" --results-directory openspec/changes/add-ntfytray-v1/evidence/test-results`：112/112 通过、0 跳过；配置 70、协议逻辑 30、格式化 12，见 [test.log](test.log) 和 [TRX](test-results/tests.trx)。
- `openspec validate add-ntfytray-v1 --strict`：通过。
- [源码指纹](source-fingerprints.json) 记录本轮最终源码、依赖锁、工程、示例及原始 PNG；这不是候选包清单，不将首次探针二进制冒充最终候选。

修复并验证 YAML 显式标签类型绕过、URL `/topic/..` 规范化绕过、可配置退避扰动、大数 Retry-After 溢出及长前导零。真实网络响应生命周期、通知重试、结果后推进游标、隔离、日志与系统恢复尚未集成，112 项通过不代表 8.1 或 V1 完成。

## 下一步

2026-09-11 20:06 续跑：Computer Use 只读读取 Explorer 成功；激活曾返回 app approval timed out，随后按键仍返回 GetCursorPos 0x80070005，因此没有执行通知点击。受限沙箱启动 pid 7788 在托管入口前出现 “This application could not be started”，没有新增探针日志；只清理该次失败启动的进程。经正常用户执行环境重试同一程序，pid 19220 成功注册（Enabled）、提交通知 ID=821，并于 20:06:17 正常退出。详见更新的 t0-probe.log。此次环境差异不能归为应用回归；源码未改，复用上一轮 112 项测试。仍需人工确认本次本地通知是否实际可见，或恢复 Computer Use 输入访问后验证；1.2/1.3 保持未完成。

恢复桌面访问后验证 1.2 显示与 1.3 运行中/退出后点击，固定 SDK 激活和单实例协作，再按 tasks 依赖推进 T1、集成及发布。干净 Win10、真实 ntfy/Tunnel 和首次 24 小时仍待后续环境与候选包准备，均未标通过。
