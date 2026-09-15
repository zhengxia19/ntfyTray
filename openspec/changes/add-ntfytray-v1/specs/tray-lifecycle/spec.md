## Purpose

定义 NtfyTray 在当前 Windows 用户会话中的托盘交互和运行生命周期，使用户无需业务窗口即可观察连接状态、执行必要操作并可靠停止全部后台工作。

## ADDED Requirements

### Requirement: 无窗口和单实例运行
系统 SHALL 以普通用户权限运行，不显示主窗口、控制台、空白窗口或任务栏窗口按钮；同一用户同一 Windows 会话仅允许一个主接收实例。

#### Scenario: 正常与重复启动
- **WHEN** 用户启动应用并再次普通启动
- **THEN** 仅有一个可访问托盘图标和一套订阅，第二个普通启动进程退出；图标位于溢出区域也视为可访问

### Requirement: 固定托盘操作
系统 SHALL 提供只读状态、测试系统通知、打开配置文件、打开日志目录及退出菜单；文件和目录交由系统关联程序打开，配置变更仅在重启后生效。

#### Scenario: 用户修改配置
- **WHEN** 用户从菜单打开并保存配置
- **THEN** 当前订阅保持原配置，重新启动后加载新配置，不显示自定义配置窗口

### Requirement: 可辨识的汇总状态
系统 SHALL 仅使用项目根目录 `connected.png` 和 `disconnected.png` 派生的两种托盘图标；发布前重新读取当前 PNG 生成 ICO，EXE 和通知注册图标统一由 connected.png 派生。全部 Topic 已连接且无当前故障时使用 connected 表示就绪；配置、认证、通知、日志故障或任一 Topic 断线时使用 disconnected。启动、连接中及退出等尚未就绪阶段也使用 disconnected，不增加第三种图标；通过悬浮提示和只读状态菜单区分连接中、已连接 N/N、部分连接 X/N、正在重连、具体故障及正在退出。用户主动关闭 notification.enabled 不作为故障；系统禁止通知或通知组件异常仍为故障。错误不得被成功连接状态遮蔽。

#### Scenario: 就绪及故障恢复
- **WHEN** 所有 Topic 已进入 Connected 且配置、认证、通知和日志均无当前故障
- **THEN** 显示 connected 和就绪、已连接 N/N 文字；从故障恢复时切回同一 connected 图标

#### Scenario: 过渡阶段
- **WHEN** 程序正在启动、连接或退出
- **THEN** 使用 disconnected 并显示对应过渡文字，退出后移除图标，不使用第三种图案

#### Scenario: 部分订阅故障
- **WHEN** 两个 Topic 中一个网络断线
- **THEN** 使用 disconnected，显示部分连接 1/2，正常 Topic 继续接收

#### Scenario: 认证故障
- **WHEN** 任一 Topic 发生权限错误
- **THEN** 使用 disconnected，显示错误提示和可诊断原因，同时保留连接数量信息

#### Scenario: 连接正常但组件故障
- **WHEN** 所有 Topic 已连接但通知或日志仍有当前故障
- **THEN** 继续显示 disconnected 及故障原因，不误显示就绪

#### Scenario: 主动关闭通知
- **WHEN** 用户配置 notification.enabled 为 false，全部 Topic 正常且无其他故障
- **THEN** 显示 connected，同时用文字说明通知已按配置关闭

### Requirement: 故障按来源独立清除
系统 SHALL 分别保留配置、各 Topic、通知初始化、通知设置、通知提交和日志的当前故障。各 Topic 在重新收到有效 ntfy 事件并进入 Connected 后仅清除自身连接故障；通知故障按 native-notifications 的恢复规则清除，日志故障按 configuration-logging 的重启验证规则清除。配置修改须在重启后完整校验通过。任何单一来源的恢复不得清除其他来源的故障，不增加通用健康监控服务或周期性故障探测任务。

#### Scenario: 一个来源恢复而其他来源仍故障
- **WHEN** 所有 Topic 恢复连接但日志仍有故障，或本地测试通知提交成功但某个 Topic 仍有权限故障
- **THEN** 仅清除已恢复来源的故障，托盘继续显示 disconnected 和剩余故障原因

### Requirement: 响应式退出
系统 SHALL 保持菜单响应，退出时停止新工作、取消所有网络读取和重试、等待后台任务、释放通知及托盘资源并关闭日志；正常退出验收目标为三秒以内，不依赖强制杀进程。

#### Scenario: 在等待中退出
- **WHEN** 用户在请求建立、事件读取、重连等待或通知重试期间点击退出
- **THEN** 等待可被取消，重复退出操作被禁用，三秒内正常结束且不残留进程或托盘图标

