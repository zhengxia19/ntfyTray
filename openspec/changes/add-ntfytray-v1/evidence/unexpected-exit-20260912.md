# 2026-09-12 进程消失诊断

范围：用户要求检查、分析不到 24 小时托盘消失的原因。本轮未修改应用代码、配置或系统设置，未重启应用。时间均为北京时间。

## 已确认的现场

- 12:44 首次检查没有 NtfyTray 进程，故不只是托盘图标隐藏。
- 最新会话于 09-11 22:55:02 启动，运行目录为 `artifacts/local-test/20260911-225351`；历史启动工具记录确认 PID 18024，由 Codex 经 PowerShell `Start-Process` 启动，没有 `--smoke-test`。
- `%LOCALAPPDATA%/NtfyTray/logs/ntfytray-20260912.log` 在 04:42、08:42、10:14 各记录一次传输失败，随后均成功重连。最后一条为 10:14:20.1739856 的 TopicConnected，当时已运行约 11 小时 19 分钟。
- 最新会话未记录 Stopping、Exiting、BackgroundFailure。默认 Information 等级不记录 Debug 心跳，所以最后日志不是退出时刻。只能将结束时间限定在最后日志之后、首次检查之前。
- 系统 LastBootUpTime 为 09-11 10:58:19；本次期间没有系统重启。10:14 后 System 日志未找到所查的启动、关机、异常掉电和睡眠事件（12/13/41/1074/6005/6006/6008/42/1）。
- 查询 Application 日志自启动以来的 1000/1001/1026 事件，原始 Properties 未匹配 NtfyTray。WER ReportArchive/ReportQueue 无名称匹配的报告，CrashDumps 未找到匹配转储。没有报告不能证明没有崩溃。
- Security 进程退出事件 4689 无读取权限，申请脱离沙箱后仍要求管理员权限；未更改审计设置。系统未提供 Sysmon 日志。
- 11:00:24–25 有会话断开/重连记录；不能据此认定应用被终止。当前 Codex 后端进程开始于 12:43:14；这及历史启动来源只构成进一步核对启动器生命周期的线索，不构成因果证据。
- 候选清单中 47 项源码/资源指纹全部一致；运行目录 EXE、DLL、runtimeconfig、deps 四项与候选一致。当前目录没有 Git 仓库，以清单为基线。

## 代码结论

- 普通运行没有 24 小时或其他运行时长上限。Program.cs 中唯一自动停止计时器属于 `--smoke-test`，1500ms 后执行通知测试并退出。
- 普通退出经过 Program.cs 的停止/清理回调，写入 Stopping 和 Exiting，之后 TrayApplicationContext 隐藏图标并结束消息循环。
- NtfySubscriber 的网络异常进入重连；内部异常转为故障等待，不主动结束 UI 消息循环。现场三次恢复成功也不支持将断网直接判为退出原因。
- 日志没有顶层未处理异常、进程退出状态或 Application.Run 返回的兜底记录；绕过正常 StopAsync 的结束可能不留下原因。这是诊断缺口，尚非本次根因。

## 判定与下一步

可以确认：本次未完成连续 24 小时运行，进程结束且缺少正常停止记录。结合下述补充证据，最可能是关闭/重启 Codex 时，其管理的进程随 Windows Job 被终止；尚无旧 PID 的退出审计或当时 Job 记录，不能将机制证据表述为历史因果已完全证实。

用户已确认该时段关闭或重启过 Codex。通过与昨晚同类的脱离沙箱命令环境，只读调用 IsProcessInJob、QueryInformationJobObject，结果为 InJob=True、LimitFlags=0x2800、KillOnJobClose=True、BreakawayAllowed=True、SilentBreakaway=False。Microsoft 的 [Job Objects 文档](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)说明：普通子进程默认继承 Job；设置 KILL_ON_JOB_CLOSE 时，最后一个 Job 句柄关闭会终止关联进程。当前环境允许显式脱离，但不会自动脱离。昨晚普通 Start-Process 命令没有显式脱离处理。

优先下一步：用户从资源管理器直接双击同一候选 EXE 启动，核对其独立运行，再关闭/重启 Codex 观察，并重新进行 24 小时验收。若独立运行仍异常结束，再在授权实施范围内补充脱敏异常/生命周期记录及退出状态或转储。强制终止可能绕过应用日志，不能仅靠异常捕获解决。未执行应用重现、构建或测试，本轮不声明修复或验收通过。

任务 8.5、9.4 保持未完成；未同步规格、未归档。
