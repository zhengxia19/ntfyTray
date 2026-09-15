## Purpose

定义客户端启动配置与本地诊断的输入、默认值和失败边界，保证配置完整校验后才连接服务，并让用户能够排查故障而不在默认日志中泄露凭据或消息内容。

## ADDED Requirements

### Requirement: 配置位置和启动校验
系统 SHALL 从可执行文件目录读取 config.yaml，不依赖工作目录；缺失、无效 YAML、重复键、未知字段、错误类型或越界值 SHALL 使程序进入托盘错误状态且不创建任何订阅。诊断包含字段和原因，可定位时包含行号，不输出凭据值。

#### Scenario: 从其他目录启动
- **WHEN** 工作目录与可执行文件目录不同
- **THEN** 仍读取可执行文件旁的 config.yaml

#### Scenario: 配置无效
- **WHEN** 配置缺失或任一字段校验失败
- **THEN** 托盘显示错误，记录安全诊断，不发起网络连接

### Requirement: 配置字段契约
系统 SHALL 要求 version 为 1、单个 server.url 和 1–20 个 subscriptions；Topic 不得为空、重复或包含逗号、斜杠和查询字符串。服务器必须是无 Topic、查询参数、片段和账户密码的 HTTPS 根地址，允许末尾斜杠；HTTP 仅允许通过本机自动化测试注入回环地址，不提供用户协议或 TLS 校验开关。空 token 不发送认证头，非空使用 Bearer 头，不支持 transport 选项。

#### Scenario: 无凭据订阅
- **WHEN** 合法配置未设置 token 或其值为空
- **THEN** 请求不包含 Authorization 头

#### Scenario: 非法端点或订阅
- **WHEN** URL 含账户密码、Topic 或查询，或订阅数量为 0/21，或 Topic 重复
- **THEN** 配置被拒绝且无部分启动

### Requirement: 默认参数和约束
系统 SHALL 要求 version、server.url、subscriptions 必填，对省略的可选配置使用下表默认值；连接超时、初始退避、日志大小和文件数量须为正值，空闲超时大于连接超时，最大退避不小于初始退避，扰动比例在闭区间 [0,1]。数值须有限且能安全转换为对应时长或大小，文件数量为正整数；logging.max_file_size_mb 按 MiB 解释，logging.level 必须为受支持的 Serilog 等级。

| 字段 | 默认值 |
| --- | --- |
| notification.enabled | true |
| notification.default_title | NtfyTray |
| notification.include_topic | true |
| connection.connect_timeout_seconds | 15 |
| connection.idle_timeout_seconds | 150 |
| reconnect.initial_delay_seconds | 1 |
| reconnect.max_delay_seconds | 30 |
| reconnect.jitter_ratio | 0.2 |
| logging.level | Information |
| logging.max_file_size_mb | 5 |
| logging.retained_file_count | 14 |

#### Scenario: 省略可选配置
- **WHEN** 配置仅包含合法版本、服务器和订阅
- **THEN** 应用采用表中默认值

#### Scenario: 不一致超时
- **WHEN** 空闲超时不大于连接超时或最大退避小于初始退避
- **THEN** 报对应字段错误并停止连接初始化

### Requirement: 滚动与脱敏诊断
系统 SHALL 在读取配置前建立基础日志，写入 %LOCALAPPDATA%/NtfyTray/logs；默认按天且单文件 5 MiB 滚动、最多保留 14 个文件。日志 SHALL 记录版本、配置路径、校验结果、Topic 状态、HTTP 错误、重试信息、消息 ID、处理结果及退出，不记录 Token、标题或正文；正常心跳不写 Information。

#### Scenario: 日志轮转
- **WHEN** 跨日或文件达到大小限制
- **THEN** 轮转文件并按文件数量保留，14 个文件不解释为 14 天

#### Scenario: 敏感内容和写入失败
- **WHEN** 消息包含敏感正文或日志磁盘写入失败
- **THEN** 默认日志不泄露消息或凭据，写入失败体现在托盘，且不向失败日志递归报告错误

### Requirement: 日志故障恢复边界
系统 SHALL 在检测到日志写入失败后保留本进程的日志故障，并提示检查目录或磁盘后重启。其他组件恢复或后续偶然写入成功不得清除此故障；仅在新进程基础日志初始化及首次写入成功后视为恢复。不新增日志探测定时器、自动重建日志组件或备用日志系统。

#### Scenario: 修复目录或磁盘后恢复
- **WHEN** 日志写入失败后用户修复原因并重启
- **THEN** 新进程初始化及首次基础日志写入成功时不保留旧故障；若仍失败则继续显示日志故障，不误报恢复
