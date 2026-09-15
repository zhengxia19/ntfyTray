# NtfyTray

Windows 10 上的无窗口 ntfy 接收器。每个 Topic 独立建立 HTTPS SSE 连接，将消息显示为 Windows 原生通知。

当前为待验收候选版本。开发机通知和自动化已验证，最终部署由用户验收，按用户安排不执行代理侧干净系统测试。完整真实链路及首次连续 24 小时验证尚待完成；不应将候选包视为已通过全部 V1 验收。

## 运行环境

- Windows 10 22H2 x64，普通用户。
- Windows App SDK **2.4.0 x64 Runtime**。从 [Microsoft 下载页](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)选择稳定版 2.4.0 的 x64 Installer，完成安装后重试。运行库安装器包含 Framework、Main、Singleton 等组件。
- 需要预装 **.NET 10 Desktop Runtime x64**（包含 Microsoft.WindowsDesktop.App 10.0.x），可用 `dotnet --list-runtimes` 检查。无需安装 .NET SDK 或 Visual Studio。精简 ZIP 不包含 .NET 和 Windows App Runtime，也不带语言包；底层运行库缺失可能在托盘和日志建立之前阻止启动。

在当前用户的 Windows PowerShell 5.1 中检查运行库（PowerShell 7 的 Appx 模块可能无法加载）：

```powershell
powershell.exe -NoProfile -Command 'Get-AppxPackage *WindowsAppRuntime* | Select-Object Name,Version,Architecture'
```

本机验证记录为 `Microsoft.WindowsAppRuntime.2`、`2.4.0.0`、x64；还需通过下面的本地通知测试确认实际运行链路。

## 启动与配置

1. 将 ZIP 完整解压到当前用户可写、其他非授权用户不可访问的目录；保留整个目录，不能只复制 EXE。
2. 将 `config.example.yaml` 复制为同目录 `config.yaml`，填写 HTTPS 根地址、Topic 和必要的只读 Token。
3. 双击 `NtfyTray.exe`，右键托盘选择“测试系统通知”。图标可能在系统托盘溢出区域。
4. 配置修改仅在退出并重新启动后生效。

最小配置：

```yaml
version: 1
server:
  url: https://ntfy.example.com
  token: ""
subscriptions:
  - my-topic
```

Token 留空则不发送 Authorization 头；非空仅用 Bearer 认证头发送。Token 在本地 YAML 中为明文，应使用仅能读取指定测试 Topic 的凭据并妥善保管；不要提交真实配置到版本库或放进发布 ZIP。

服务器必须是无账户密码、Topic 路径、查询参数和片段的 HTTPS 根地址。支持 1–20 个不重复 Topic；不接受空值、逗号、斜杠或查询字符串。配置缺失、重复键、未知字段、错误类型或越界值都会阻止全部订阅，托盘保留配置错误提示，本地通知测试仍可使用。程序始终在 EXE 目录寻找配置，不受启动工作目录影响。

完整可选字段见 `config.example.yaml`：

| 参数 | 默认值 |
| --- | --- |
| notification.enabled / default_title / include_topic | true / NtfyTray / true |
| connection.connect_timeout_seconds / idle_timeout_seconds | 15 / 150 |
| reconnect.initial_delay_seconds / max_delay_seconds / jitter_ratio | 1 / 30 / 0.2 |
| logging.level / max_file_size_mb / retained_file_count | Information / 5 / 14 |

时长及大小须为正且可安全转换，空闲超时须大于连接超时，最大退避不小于初始退避，扰动在 0–1 之间。日志大小单位为 MiB，保留数量是文件数；日志等级支持 Verbose、Debug、Information、Warning、Error、Fatal。

## 托盘与通知

菜单提供只读状态、测试系统通知、打开配置文件、打开日志目录和退出。绿勾表示所有 Topic 已连接且没有组件故障；红叉表示尚未就绪或有故障。文字显示连接数和具体原因。主动配置关闭通知不会单独导致红叉，系统禁止通知或提交故障仍会显示异常。

同一用户会话仅运行一个主实例。点击通知不打开 URL、附件或业务窗口，不执行消息动作；退出后点击旧通知可启动新的托盘会话。

标题和正文仅按纯文本处理，按 Unicode 文本元素安全限制为 128/1024，包含来源和省略号。通知关闭或可检测系统设置禁止时记录 Suppressed；API 提交成功记录 Submitted；最多三次提交失败后记录 Dropped。Submitted 不代表横幅已显示或用户已阅读，Windows 专注设置等可能影响最终呈现。

## 恢复与日志

HTTP 响应头阶段和有效事件空闲时间分别计时。有效心跳会保持连接；一般网络错误指数退避，默认基准最大 30 秒后再加扰动。429 遵守 Retry-After；401/403 每 300 秒重试；404、重定向及 HTML 代理响应等待修正配置后重启。不会绕过 TLS 验证，也不把 Token 转发到重定向目标。

运行期间重连尝试使用内存中的消息 ID/服务器时间恢复，最近 2048 个已完成 ID 去重。消息处理得到明确结果后才推进游标。新进程不主动补收退出期间历史；缓存淘汰、容量外重放、异常退出不保证严格一次送达。缓存截断提示只记录可能遗漏，仍继续消费有效流。

日志位于 `%LOCALAPPDATA%\NtfyTray\logs`，按天和大小滚动，默认每文件 5 MiB，保留 14 个文件。记录状态、HTTP 码、重试、消息 ID 和处理结果，不记录 Token、标题、正文或原始异常响应。

| 问题 | 处理 |
| --- | --- |
| 程序未出现托盘 | 检查托盘溢出区、完整解压及匹配的 Windows App SDK 运行库；底层启动失败可能尚无日志。 |
| 配置错误 | 按日志的字段/原因修正 EXE 旁的 config.yaml，退出后重启。 |
| 认证或权限错误 | 检查 Token、Topic 和服务器只读权限；修改配置后重启。 |
| 代理配置错误 | 使用固定域名直达 SSE，避免交互式登录页面或重定向；客户端不部署 Tunnel。 |
| 日志写入故障 | 检查目录权限和磁盘空间，修复后重启；当前进程故障不会因其他成功操作自动清除。 |
| 通知初始化故障 | 修复运行库/环境后重启；不在后台重复初始化。 |
| 系统通知受限 | 重新允许应用通知后，下一条消息或手动测试会复查；没有周期轮询。 |
| 通知提交故障 | 后续业务或手动测试成功 Submitted 才清除此故障；Suppressed 不作为恢复。 |

各 Topic、通知和日志故障分别保留；一个来源恢复不会清除其他故障。

## 更新及回退

退出旧程序，保留旧发行目录与真实 `config.yaml`。将新 ZIP 解压到新目录，把配置复制到新目录，再启动并检查测试通知与连接状态。发行包只带示例，不覆盖真实配置。失败时退出新版，恢复旧目录与配置，再启动旧版。普通退出和回退不执行通知长期注册清理。

## 开发与复现

使用 `global.json` 指定的 .NET SDK 10.0.106 及兼容 Windows 构建工具。依赖固定版本并提交 packages.lock.json；首次新增依赖时才生成锁文件，后续使用锁定还原。

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet publish src/NtfyTray/NtfyTray.csproj -c Release -r win-x64 --self-contained false --no-restore -o artifacts/publish
```

项目明确关闭自包含、单文件和裁剪，仅引用 Windows App SDK Foundation、其所需的 InteractiveExperiences 与 Runtime 组件，不打包框架、AI/WinUI/WebView2、语言卫星资源或调试符号。EXE 和通知注册图标来自根目录 `connected.png`，托盘状态图标分别来自当前 `connected.png` 和 `disconnected.png`。`tools/Publish-Candidate.ps1` 每次重新生成图标，并生成唯一候选目录、ZIP、源码/运行文件指纹和候选清单；真实配置不参与打包。运行验收应引用对应清单，不能用开发目录测试替代候选证据。最终部署交由用户验收；真实 ntfy/Tunnel、睡眠恢复、延迟 P95≤1秒和连续24小时验证未通过时，不宣称全部 V1 验收完成。

`--smoke-test` 仅用于开发验收：按正常启动流程加载配置、发送一次本地测试通知并自动退出；配置合法时也会短暂启动订阅。它不向 ntfy 发布消息。



