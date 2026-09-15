# 后续实施与平台验证

## T0 平台证据

- 用户明确确认已经收到本地系统通知，随后确认按“测试通知 → 托盘退出 → 点击保留通知”的顺序恢复红叉托盘。
- 热点击：probe.log 记录 pid 15200 注册 Enabled、提交 ID=823、NotificationInvoked 无业务操作、正常退出；重复启动 pid 19756 自动退出。
- 冷激活：pid 13828 于 22:20:26 提交本地通知，22:20:34 stopped；用户点击后 22:20:38 新进程 pid 18756 tray_started，读进程仅一个、MainWindowHandle=0。用户随后通过菜单退出。
- 单实例采用当前用户 SID + Windows SessionId 的 Local 命名互斥体。主实例先获锁，再绑定通知回调并注册 COM 激活；普通重复启动直接结束。冷激活开始新会话，不解析或执行消息参数。当前冷启动没有记录 NotificationInvoked 回调本身，证据为用户操作、新主进程及无窗口状态，不冒充回调已观测。
- 最小自包含验证目录 `artifacts/t0-probe/`：命令 `dotnet publish src/NtfyTray/NtfyTray.csproj -c Release -r win-x64 --self-contained true -p:OutputPath=bin/T0Publish/ -o artifacts/t0-probe`。包含 coreclr.dll；从 TEMP 工作目录执行 --notification-probe，pid 16168 注册 Enabled、提交 ID=829、正常退出。该目录是 T0 验证产物，不是候选发行包，也不代表干净环境通过。
- 运行库检查沿用前轮实际记录：Windows 10 19045 x64、WindowsAppRuntime.2 2.4.0.0；普通用户正常执行可用，受限沙箱的启动失败不作为兼容性通过或失败证据。

## T1/T2 与独立逻辑

新增正式双状态托盘、固定菜单、UiDispatcher、AsyncLifetime、SingleInstanceGuard、WindowsNotificationService；根 PNG 保留，使用 tools/New-TrayIcons.ps1 生成多尺寸 ICO 并嵌入程序集。用户在真实托盘使用测试/退出菜单并确认红叉；[16/20/32/40 像素图标检查](tray-icons.png) 覆盖小尺寸与 125% 对应尺寸，未切换整台机器 DPI 设置。

加入 SessionRecovery、NotificationProcessor、TrayStatus 的纯逻辑及回归测试，加入安全滚动日志组件。独立输出整体验证：`dotnet test tests/NtfyTray.Tests/NtfyTray.Tests.csproj -c Release --no-restore -p:OutputPath=bin/IntegrationVerification/ --logger "trx;LogFileName=tests.trx" --results-directory openspec/changes/add-ntfytray-v1/evidence/verification-2`，160/160 通过，详见 [TRX](verification-2/tests.trx)。后续资源清理修复仍须重跑受影响测试。

配置/日志启动烟雾测试：从 TEMP 工作目录运行当前构建 --smoke-test，日志显示 executable 目录 config.yaml 缺失、ConfigurationRejected 安全字段与原因；未创建任何订阅，本地测试通知仍 Submitted。pid 7264 stopping=22:24:24.759799、stopped=22:24:24.772680，退出约 13ms；没有把该无网络测试冒充网络读取退出验收。

专项审查发现并处理：注册失败时解除通知事件、通知注销异常仍关闭日志、Dispatcher 释放终止排队调用、重复 StopAsync 返回同一停止任务。后两项正在补回归证据，不能用之前160项结果替代修复后验证。

## 外部环境

用户指定测试服务 https://ntfy.553368.xyz、Topic codex，未提供 Token。后续按无 Authorization 请求；没有擅自搜索凭据、修改服务器或发送外部消息。真实链路、干净候选部署和24小时测试尚未开始。
