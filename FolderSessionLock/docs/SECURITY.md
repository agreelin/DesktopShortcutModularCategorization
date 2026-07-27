# Folder Session Lock 安全边界

状态：阶段 3 与阶段 4 CP1–CP9 基线已完成；CP10 工具实现最近验证为 799/799、0 failed、0 skipped、Release 0 warning/0 error。D-031 将当前范围修订为本地单用户管理员和显式 unsigned 本地发布；同账户 UAC、SCM、LocalSystem、ProgramData/ProgramFiles ACL、真实 service SID ACL、恢复、重启/注销与 D-026 schema v2 安全证据仍未完成，阶段 4 不得完成。

## 1. 安全定位

本产品是当前交互登录会话的用户态自我约束工具。它不构成对管理员、SYSTEM、TrustedInstaller、内核组件、离线访问或已控制同一账户会话的恶意进程的强安全边界。

## 2. ACL 能保证的内容

- ACE 生效后，新发起访问使用包含目标 Logon SID 的令牌，且请求权限命中 Deny 掩码时，Windows DACL 访问检查拒绝该请求。
- 当前登录会话内普通进程及保留相同 Logon SID 的提升令牌受同一 Deny ACE 影响。
- 目录与对象继承标志允许规则传播到继承开启的子对象；阶段 3 必须用临时目录证明实际传播。
- 同一账户的另一个登录会话具有不同 Logon SID，不匹配当前任务 ACE。

## 3. ACL 不能保证的内容

- 不撤销 ACE 添加前已授予的打开句柄、目录枚举句柄或内存映射。
- 不阻止 SYSTEM、TrustedInstaller、其他账户、其他登录会话。
- 不阻止管理员取得所有权、修改 DACL、启用备份/恢复特权或离线修改文件。
- 不阻止内核驱动、Windows 恢复环境、离线挂载磁盘、使用备份或恢复特权的程序或存储设备底层直接访问。
- 不提供加密、内容保密、数据防泄漏或 minifilter 等价保证。
- 不保证目标不能借助父目录 `DeleteChild` 权限被重命名或删除；本应用禁止修改父目录 ACL。
- 不保证关闭继承的既有子对象自动接受父目录 ACE；阶段 3 必须记录覆盖结果，不得扩大声明。
- 不保证网络服务器、非 NTFS、可移动卷或 reparse target 具有相同语义；v1 全部拒绝。
- Logon SID 在操作系统重启后回收；因此自动启动 Windows 服务必须在交互登录前以 LocalSystem 身份运行 Broker 恢复模式，根据机器范围恢复记录验证并清理遗留 ACE。清理失败时保留记录并保持恢复阻断状态。

## 4. Account SID 与 Logon SID

- Account SID 是账户稳定身份；用它创建 Deny ACE 会影响当前和以后登录会话，违反会话级目标。
- Logon SID 是访问令牌组中带登录标志的 `S-1-5-5-X-Y`。
- 锁定主体只使用 Broker 当前令牌中精确提取并验证的 Logon SID。
- Broker 提升令牌必须包含 UI 发起会话的同一 Logon SID；同时验证 Account SID 和 Windows Session ID。
- v1 只支持同一账户、同一交互会话的 consent elevation。其他管理员账户凭据、跨账户 elevation、远程管理员控制和服务账户代执行均必须拒绝，并显示“不支持跨账户提升”。

参考：

- [Microsoft：Well-Known SID Structures](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-dtyp/81d92bba-d22b-4a8c-908a-554ab29148ab)

## 5. Deny 权限矩阵

v1 拒绝：

- `ListDirectory` / 文件上的 `ReadData`
- `CreateFiles` / `WriteData`
- `CreateDirectories` / `AppendData`
- `ReadExtendedAttributes`
- `WriteExtendedAttributes`
- `Traverse` / `ExecuteFile`
- `DeleteSubdirectoriesAndFiles`
- `ReadAttributes`
- `WriteAttributes`
- `Delete`

明确不拒绝：

- `FullControl`
- `ReadPermissions`
- `ChangePermissions`
- `TakeOwnership`
- `Synchronize`

该矩阵覆盖当前交互用户新发起的枚举、读取、创建、写入、删除、重命名、移动和修改请求。阶段 3 已从 Windows/.NET 官方 `FileSystemRights` 常量构造并逐项测试；确认掩码为 `0x000101FF`。该掩码与 `ReadPermissions`、`ChangePermissions`、`TakeOwnership`、`Synchronize` 零交集。

恢复进程只申请读取 DACL、精确移除应用 ACE 和验证结果所需权限。不得默认使用 `Deny FullControl`。

## 6. ACE 添加与所有权证明

ACE 属性固定为：任务 Logon SID、显式 `Deny`、上述掩码、目录继承、对象继承、无额外传播限制、仅目标目录。

NTFS ACE 不含应用私有标签。外部主体删除应用 ACE 后重建完全相同 ACE 时，SID、掩码、类型、标志和顺序均不能证明来源。用户已接受有限 DACL 稳定性信任假设：任务期间不存在不可检测的外部同元组重建。

自动移除必须结合：规范化路径、Logon SID、Allow/Deny 类型、权限掩码、继承标志、传播标志、任务 ID 对应恢复记录和必要 ACL 校验信息。不得只按 SID 和权限掩码删除。

安全算法：

1. 打开并持续持有稳定目录句柄，取得目录身份。
2. 通过同一持续句柄读取 UInt64 `FILE_ID_INFO.VolumeSerialNumber`、完整 16-byte FILE_ID_128、DACL、继承状态和 ACE 多重集合；volume 编码为 16 位小写 hex，FILE_ID 两个 8-byte little-endian half 编码为 UInt64 十进制 string。
3. 锁定前存在完全相同显式 ACE 时拒绝任务。
4. 按 D-022 `FSLDACL` v1 wrapper 对原始 ACE 顺序和有效 bytes 计算 baseline digest，写入并原子验证 `Prepared` 恢复事务。
5. 通过同一持续句柄添加一条 ACE；不整体替换 DACL；不关闭继承。无法基于句柄写入时拒绝。
6. 通过同一持续句柄后置读取，证明目标 ACE 恰好一条，原 ACE 多重集合和继承状态未变化；对唯一写后 ACE 计算 `FSLACE` fingerprint，对重读 DACL 计算 `FSLDACL` postApply digest。
7. 失败时只回滚新增 ACE。
8. 解锁时通过同一持续句柄读取；匹配数为 0，按幂等成功处理；为 1且信任假设成立，通过该句柄移除；大于 1或来源不明，禁止猜测删除。
9. 不用原 DACL 快照整体覆盖当前 DACL；快照只用于证明和诊断。

外部进程并发修改 DACL 时，只要恢复记录、ACL 校验或目录身份不一致，立即停止自动删除并进入人工恢复状态。禁止通过重建整个 DACL 解决不一致。完全相同 ACE 被外部替换时无法从 NTFS 结构检测；这是已接受的已知限制，不得称为绝对所有权证明。

## 7. 恢复边界

严格零持久化不能支持可靠崩溃恢复：ACL 在磁盘持久化，而进程内任务状态会丢失。

架构采用最小受保护恢复记录。记录只服务于精确回滚和清理，不保存普通历史。它必须包含足以证明任务、会话、目录身份和 ACE 元组的数据，使用原子状态转换和完整性保护；成功解锁后删除。

不同登录会话绝不续跑旧任务，只清理旧 ACE。断电、记录损坏、UAC 拒绝或目录身份变化均可导致需要人工恢复；产品必须明确报告，禁止沉默删除记录或宣称恢复完成。

Broker 是唯一真实 ACL 写入主体。注销、系统重启和新登录会话后的遗留清理由同一受信 Broker 的恢复专用模式执行；该模式由自动启动 Windows 服务以 LocalSystem 身份在交互登录前托管。

恢复模式只处理机器范围恢复记录可验证的旧 Logon SID ACE，不创建限制、不恢复旧任务或剩余时间；清理成功后尽快删除记录；失败时保留记录、保持恢复阻断状态并给出诊断；禁止删除普通 ACL 或整体覆盖 DACL。

恢复记录固定存放于 `%ProgramData%\FolderSessionLock\Recovery\Records`。容器头只包含 `FSLR`、版本、flags 和受保护 payload 长度；路径、SID、ACE 和错误细节全部位于 DPAPI `DataProtectionScope.LocalMachine` 保护的 payload 内。purpose entropy 固定为 UTF-8 `FolderSessionLock.RecoveryRecord.v1` 的 SHA-256，不作为秘密密钥。

恢复目录所有者固定为 `NT AUTHORITY\SYSTEM`，使用受保护显式 DACL，只允许：

- `NT AUTHORITY\SYSTEM`：`FullControl` / `ThisFolderSubfoldersAndFiles`。
- `BUILTIN\Administrators`：`FullControl` / `ThisFolderSubfoldersAndFiles`。
- `NT SERVICE\FolderSessionLockRecovery`：`FullControl` / `ThisFolderSubfoldersAndFiles`。

不得向 `Users`、`Authenticated Users`、`Everyone`、当前交互用户或普通 UI 授权。安装程序创建并验证 DACL；服务启动时复核 owner 和 DACL；异常时安全失败，不用 Deny ACE 修补错误 Allow ACL。普通 UI 只能经受限 IPC 查询脱敏状态。

`.fslr` 记录必须在 ACL 写入前原子提交 `Prepared`，写后验证成功再更新为 `Applied`。v1 header 固定 version=1、`Flags = 0`、blob length 1..262144、总长严格匹配且无尾随；明文 <=131072、UTF-8 without BOM、精确 25 字段。任何 magic/version/Flags/length/DPAPI/JSON/schema/state 校验失败均不得修改 ACL、删除或覆盖记录。临时文件、替换、flush-to-disk、回读和 DPAPI 验证流程以 `D-022` 为准。

Prepared 必须 postApply/error=null、count=0；Applied 必须实际 postApply 非 null且 error=null；CleanupPending 必须清除旧 error并 count>=1；CleanupFailed 必须同时保存脱敏稳定 error code/message 且保留记录。所有字段始终存在，不得省略 null 字段、映射未知 enum 默认值、屏蔽未知 flags 或自动修正状态矩阵。

目录身份和摘要合同以 D-022.1–D-022.5 为准。恢复字段 `volumeSerialNumber` 必须是 UInt64 的 16 位小写 hex；禁止使用 32-bit volume serial、`BY_HANDLE_FILE_INFORMATION`、路径重开句柄、SDDL、整个 SECURITY_DESCRIPTOR、owner/group/SACL、排序 ACE、运行时对象序列化或 ACL 未使用尾部字节生成恢复证据。三个摘要不能单独授权恢复，必须同时验证完整目录身份、主体、ACE 元组、记录状态和调用模式。

## 8. Broker 权限边界

- UI 普通权限运行。
- Broker 按需 UAC 提升，独占真实 ACL 操作。
- Broker 只接受 `ValidatePath`、`CreateLock`、`RemoveLock`、`GetStatus`。
- 禁止任意命令、脚本、shell、任意文件写入和任意 ACL 描述符。
- Broker 对所有输入重新验证；UI 结果不是安全依据。
- Broker 与 UI 的 Account SID、Logon SID 或 Session ID 不一致时拒绝；跨账户 elevation 不属于 D-031 支持范围，只用合成身份单元测试证明 fail closed。
- 身份不一致时用户可见错误固定为“不支持跨账户提升”。
- bootstrap 跨账户拒绝稳定错误码固定为 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`；必须在 Pipe、Replay、恢复记录、路径或 ACL 操作前拒绝。已连接 Pipe 的 `FSL_E_ACCOUNT_SID_MISMATCH` 保持握手层诊断，仅在 UI elevation 边界转换；其他 identity 错误不得转换。
- 服务名固定为 `FolderSessionLockRecovery`；账户 `LocalSystem`；启动类型 `Automatic`；`DelayedAutoStart = false`；唯一服务 SID `NT SERVICE\FolderSessionLockRecovery`。
- 服务只允许 `--mode recovery-service`；隔离 VM 单次诊断只允许 `--mode recovery-once`；交互 Broker 只允许 `--mode consent-broker --pipe-name FolderSessionLock.Broker.v1 --session-id <UInt32> --request-id <lowercase Guid D> --client-process-id <UInt32> --client-process-creation-filetime <UInt64 decimal>`。SID、用户名、角色、管理员标志或 Pipe SDDL 不得进入命令行。
- 未知参数、自定义恢复路径、任意 Pipe 名、任意 service name/binPath、任意 ACL 描述符或脚本参数必须安全失败。

## 9. Named Pipe 安全

- 只允许本机连接。
- 不使用默认 Pipe ACL；默认 ACL 会给 Everyone 和匿名账户读取权限。
- Pipe DACL 只允许发起会话 Logon SID 和 Broker 自身。
- Broker 通过命名管道客户端模拟或等价的令牌读取验证 Account SID、Logon SID 和 Session ID。
- 额外验证连接进程身份、一次性高熵握手值和防重放状态。
- 同一请求 ID 不得重复执行 ACL 变更。
- Pipe 名必须精确为 `FolderSessionLock.Broker.v1`；不得接受调用方提供的其他名称。
- 完整本地路径语义为 `\\.\pipe\FolderSessionLock.Broker.v1`；一个连接只处理一个请求/响应后关闭。
- byte mode 消息使用 4-byte little-endian `UInt32` 长度前缀，正文为严格 UTF-8 without BOM JSON，最大 65536 bytes。零长度、超限、不完整、额外字节、多 JSON、BOM 和非法 UTF-8 全部拒绝。
- JSON schema 大小写敏感；重复属性在反序列化业务对象前拒绝；多余、缺失、不允许 null、宽松数字/日期/Guid/enum、注释和尾逗号全部拒绝。
- 请求身份字段不构成信任依据。服务端必须从 OS 取得客户端进程、Account SID、Logon SID 和 Session ID；JSON `clientSessionId` 仅用于一致性比较。
- requestId 必须为非空小写 Guid D；最近 10 分钟 replay 返回 `FSL_E_REPLAY_DETECTED`。sentAtUtc 必须是 7 位小数 UTC `Z` 格式且偏差不超过 120 秒。
- 客户端不得发送 SID、Logon SID、ACL mask、SDDL、ACE、恢复路径、安装路径、服务/Pipe 名、shell、PowerShell、cmd、脚本、任意 executable、`LockRemovalIntent` 或清理模式；出现即 `FSL_E_FORBIDDEN_INPUT`。
- 协议公开错误必须脱敏，最多 256 Unicode 字符；不含 stack、内部类名、恢复记录、SID、凭据、原始 SDDL、未脱敏系统路径或 Win32 调试缓冲。`FSL_E_INTERNAL` message 固定为 `The operation could not be completed.`。
- 普通 UI 只允许 ValidatePath、CreateLock、GetStatus；RemoveLock 固定拒绝为 `FSL_E_UNAUTHORIZED_CALLER`。
- RemoveLock 权限由 OS 身份和 Broker 启动模式决定，客户端不能声明角色或 intent。仅内部 scheduler→Expiration、recovery-service/recovery-once→Recovery、隔离 VM 测试清理→TestCleanup。
- GetStatus 只返回调用方本账户和交互 Session 的内存任务；其他身份或不存在 task ID 统一 `FSL_E_TASK_NOT_FOUND`，不得泄露其他任务是否存在。
- 全部 envelope、字段、类型、错误码、命令 payload/result 和权限矩阵以 `D-027` 为精确合同；不得静默更改。
- consent-broker 只接受 `ClientHello -> ServerHello -> CommandRequest -> CommandResponse -> close`；握手前命令、重复握手、第二命令、响应后数据均拒绝并关闭。ClientHello 接收上限 5 seconds，握手有效 30 seconds。
- clientNonce/serverNonce 均为密码学安全随机 Base64URL without padding 32 bytes；nonce、SID、PID 和 token 不进入普通日志。bindingProof 按 D-027 固定 canonical string 计算 SHA-256，恒定时间比较，成功后握手立即消费。
- OS 身份验证必须取得实际 Pipe 客户端 PID、进程存活/启动时间、进程 Session、模拟令牌 Account SID/Logon SID/TokenSessionId，并与 Broker Account SID/Logon SID/Session 和 CLI/session 绑定比较。模拟必须 finally 恢复；失败不得回退客户端声明或用户名。
- Replay Registry 固定 `%ProgramData%\FolderSessionLock\Replay\v1`，ACL 与恢复目录同级保护；普通 Users、Everyone 无访问，不为 Authenticated Users 额外授权。Registry mutex 固定 `Global\FolderSessionLock.ReplayRegistry.v1` 且使用受保护 DACL。
- Replay 必须原子 CreateNew、跨 Broker 进程唯一所有者。未过期进行中返回 `FSL_E_REQUEST_IN_PROGRESS`；终态保留期内或 RecoveryRequired 返回 `FSL_E_REPLAY_DETECTED`。业务 taskId 幂等不允许传输 requestId 重放。
- Replay CreateNew 只允许在完整 OS Pipe 客户端 PID/进程/令牌身份、Broker Account/Logon/Session 比较和命令权限验证全部通过后执行。任何 malformed/schema/version/binding/time/PID/identity/session/unauthorized 失败均不得产生 Replay 文件；禁止保留身份前登记兼容分支。
- ServerHello 成功前错误只使用 ServerHello failure；成功后错误只使用 CommandResponse failure。失败后 Flush/close/清 nonce；对端断开不更换 frame。ServerHello 只回显合法输入 requestId/允许 command，CommandResponse 只回显服务端已接受绑定值。
- HANDSHAKE_REQUIRED、HANDSHAKE_VERSION_UNSUPPORTED、HANDSHAKE_EXPIRED、PROTOCOL_SEQUENCE_INVALID、REQUEST_IN_PROGRESS、REPLAY_DETECTED 的唯一场景、retryable、field、frame 与 Replay 终态严格按 D-027.13；不得因实现顺序返回另一错误。
- lease/TTL 固定为 60-second lease、20-second renewal、5-minute execution limit、10-minute terminal retention；RecoveryRequired 无自动过期。owner 崩溃后只有证明无副作用才能 Abandoned；存在恢复记录或未知副作用必须 RecoveryRequired。
- D-028 固定 cleanup first-task error 优先、scheduler error 仅内部记录。CP6 生命周期 Cleanup 不得被 scheduler error 阻止。Cleanup 必须按稳定任务顺序完整遍历；第一个 Cleanup task error 是唯一对外主错误，后续 Cleanup task errors 与 scheduler error 仅进入受保护内部诊断。scheduler error 与 Cleanup 全部成功并存时，对外结果仍为 Cleanup success count。

### consent elevation 安全合同

- UI 在 UAC 前只从自身当前进程 token 读取 TokenUser、唯一 Logon SID 与 TokenSessionId，并取得 PID 与 creation FILETIME。SID 只留在 UI 内存；Broker bootstrap 通过 PID+creation time 重开 UI process/token并重新读取身份。禁止用户名、环境变量、`WindowsIdentity.Name`、Account SID fallback 或 CLI SID。
- bootstrap identity/process 失败分别使用 exit 21/22；Account SID 不同 exit 20。只有 UI/Broker Account 和 Session 绑定全部成功后才创建 protected Pipe DACL，精确主体为可信 UI Logon SID 与 Broker Account SID的 ReadWrite + Synchronize，继续拒绝远程客户端。
- production Broker 路径仅为 known-folder Program Files 下固定安装文件，必须通过 D-023 install directory、file non-reparse、final path 和 identity 验证。禁止 cwd、环境变量、PATH、相对路径、仓库/bin、用户配置或 CLI 路径。CP9 路径验证不替代 CP10 Authenticode。
- UAC 固定使用 `ShellExecuteExW(runas)` 与非空 process handle；flags 为 `SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI | SEE_MASK_UNICODE`。`SEE_MASK_FLAG_NO_UI` 只禁止 Shell 普通错误弹窗，不得隐藏 UAC 安全提示。禁止 Process.Start/ProcessStartInfo.Verb、shell、token/logon API、Task Scheduler 或临时服务替代。
- UAC 提示没有应用级超时；应用取消不得强制关闭系统 UAC。`ERROR_CANCELLED` 映射 `FSL_E_ELEVATION_CANCELLED`，其他 launch failure/空 handle 映射 `FSL_E_ELEVATION_LAUNCH_FAILED`。
- UI 连接前等待 Pipe/process exit 20 seconds；Broker server 等待 client 15 seconds。连接前 timeout 允许 launcher `TerminateProcess(..., 29)` 并在 5 seconds 内证明退出；清理无法证明时主错误 `FSL_E_BROKER_PROCESS_CLEANUP_FAILED`。Pipe 一旦连接，UI 永远不得终止 Broker；响应、disconnect、Cleanup 与 RecoveryRequired 必须由 Broker 自行收敛。
- consent-broker exit code 只允许 0、2、20–29 的 D-029 关闭集合。exit 2 的公开对象固定为 `FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / false / null；禁止泄露 CLI、参数、路径、命令行、Win32 或异常细节。应用失败响应可以 exit 0；Cleanup failure优先 27，response write failure且 Cleanup成功为26。UI 已验证合法 CommandResponse 后，后续 process exit不得改写结果。
- 每个 consent-broker 只允许一个 listener、连接、四帧握手与应用请求。CreateLock 成功后 UI 可关闭 Pipe/process handle，但 Broker 保持运行到 scheduler 到期 Cleanup；UI 断开不得提前解除确定 Active lock，未知副作用进入 RecoveryRequired。
- production composition 禁止 AllowAll/fake identity/fake readiness/in-memory recovery/test cleanup hook/test path/debug Broker path，以及empty repository classifier、always-not-sync classifier、Console/Debug/Null/test logger或user-writable path provider。缺少任一 D-029/D-030 安全依赖 fail closed exit 28。
- 新公开错误固定为 `FSL_E_BROKER_PATH_UNTRUSTED`、`FSL_E_ELEVATION_CANCELLED`、`FSL_E_ELEVATION_LAUNCH_FAILED`、`FSL_E_BROKER_LAUNCH_CONTRACT_INVALID`、`FSL_E_PIPE_INITIALIZATION_FAILED`、`FSL_E_BROKER_CONNECT_TIMEOUT`、`FSL_E_BROKER_EXITED_EARLY`、`FSL_E_BROKER_PROCESS_CLEANUP_FAILED`；field 全为 null，仅 elevation cancelled 与 connect timeout retryable=true。
- protected 日志只允许D-030固定十四字段schema、编译期event/message目录与脱敏值。可记录requestId、taskId、PID、稳定error code、mode、event ID、状态、计数和布尔；路径只能记录`SHA-256("FSL-PATH-LOG-V1\n" + normalizedPath)`。禁止SID、SDDL/ACL、nonce、bindingProof、完整path、DPAPI、凭据/token/private key、UAC输入、command line、stack、Exception或Win32 FormatMessage。

### 恢复执行 fail-closed 规则

- `recovery-once` 在参数、D-023、完整枚举、4096/1024 上限完成前不得读取记录或修改 ACL。退出码只允许 0、2、10、11、12、13、14、15；任何 Win32/HRESULT/NTSTATUS 只进入受保护诊断。
- Records 只做顶层枚举，禁止递归与 reparse。未知文件、子目录、孤立 `.bak`/`.tmp-*` 不自动删除、重命名、提交或推断 ACL；保留构件并设置 recovery blocking。
- 单记录损坏、身份变化、ACL 漂移、移除失败或未知副作用不得停止其他规范记录的安全检查，但必须保留本记录和恢复责任。稳定排序中的首个非成功记录错误是公开主错误。
- `CleanupPending` 后为 ACL 临界区。取消、SCM stop 或 wait hint 不得强制中断；记录必须达到删除成功、`CleanupFailed` 或 `RecoveryRequired`。
- `AlreadyClean` 必须证明目录身份、baseline/current DACL 与 ACE 状态一致并成功删除记录；未找到 ACE 不足以授权删除。

### 恢复记录文件级安全

- `.fslr`、`.tmp-*`、`.bak` 唯一 owner 为 SYSTEM `S-1-5-18`。文件 DACL 必须 present、non-null、`SE_DACL_PROTECTED`、ACL revision 2，精确三个显式 Allow ACE：SYSTEM、Administrators、`NT SERVICE\FolderSessionLockRecovery`，均 File FullControl `0x001F01FF`、AceFlags 0、按该顺序；禁止 inherited、Deny、object、callback、conditional、unknown 或额外 ACE。
- `IRecoveryRecordFileSecurity` 只接收已打开 SafeFileHandle。writer 只在新 tempHandle 上设置和验证安全；reader 不修复 canonical/bak。service SID 不可解析必须 `FSL_E_RECOVERY_FILE_SERVICE_SID_UNAVAILABLE`。
- consent writer 不依赖默认 owner/父目录继承。需要改 owner 时仅临时启用 `SeRestorePrivilege`，finally 恢复；禁止 SeTakeOwnershipPrivilege。privilege 启用/恢复失败分别使用固定错误，revert failure 后停止新恢复写入和 CreateLock。
- 所有 writer 持有受保护 `Global\FolderSessionLock.RecoveryStore.v1` mutex。Records 目录、temp、old canonical 句柄贯穿 identity/security/content 验证和 rename/delete；rename 只允许 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)`、`FILE_RENAME_INFORMATION`、目录相对简单叶名，新建 flags=0、更新 flags=`0x00000003`。canonical 删除在同一已验证 handle 上使用 FileDispositionInfoEx POSIX flags，成功后关闭该 handle，再由 retained directory handle确认名称消失和目录 identity；失败进入 RecoveryRequired。禁止 class 10、SetFileInformationByHandle class 22/class 3、绝对目标与其他 fallback。
- 新建/更新/删除/temp cleanup 禁止 `File.Replace`、ReplaceFileW、`File.Move`、MoveFileW/MoveFileExW、`File.Delete`、DeleteFileW、File.SetAccessControl/FileInfo.SetAccessControl、SetNamedSecurityInfo，禁止关闭验证句柄后按路径修改。
- payload 前、提交前、提交后与删除前后都必须复核 FILE_ID_INFO、NumberOfLinks=1、non-reparse、final path、SYSTEM owner、精确 DACL、content 和 Records 目录 identity。post-commit 无法证明时返回 `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`、进入 RecoveryRequired，不按路径清理。
- 配对 `.bak`/`.tmp-*` 也必须满足同一 file security 才可 auxiliary；不匹配返回 `FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID`，保留构件、invalid++、blocking=true。
- 文件级公开错误、messages、retryable=false、field=null 与优先级固定为 D-022.11；底层 Win32、路径、SID、DACL/SDDL、FILE_ID 只进受保护日志。本合同不授权 SACL、Audit ACE 或阶段 6 能力。

### D-023 受保护路径复核

- `IProtectedPathSecurityVerifier` 的 request/result、路径种类、执行顺序与错误码固定为 D-023.1。ExpectedPath 只能由组合根从 `%ProgramFiles%\FolderSessionLock`、`%ProgramData%\FolderSessionLock\Recovery`、Records 与 Replay 固定路径生成。
- verifier 必须使用 handle-based final path、OPEN_REPARSE_POINT、本地固定 NTFS、VolumeSerialNumber/FILE_ID_128 前后复核、owner/DACL/显式 ACE/继承保护校验。失败按执行顺序返回精确 `FSL_E_PROTECTED_PATH_*`，不得统一为 internal。
- InstallDirectory owner 仅 SYSTEM/TrustedInstaller；Recovery/Replay owner 仅 SYSTEM。Recovery/Replay DACL 必须 protected，且只允许 SYSTEM、Administrators、服务 SID 完全控制；普通用户不得读取、列出、写入、删除、WriteDac 或 WriteOwner。
- 禁止 `AllowAllProtectedPathSecurityVerifier`。生产 Windows verifier 未实现或 readiness 无法验证时必须 blocking，禁止生产 recovery 与 CreateLock。

### 服务 readiness 与 CreateLock gate

- `recovery-service` 启动扫描一次后保持运行，不周期扫描恢复记录。跨进程readiness唯一为D-030受保护machine snapshot；无公共readiness Pipe。唯一publisher为service，普通UI/consent-broker/recovery-once只读。
- Readiness目录owner SYSTEM；protected DACL精确为SYSTEM/Administrators/service SID FullControl与Users ReadAndTraverse。canonical/temp owner SYSTEM；protected DACL精确为前三者FullControl与Users Read。普通用户不能创建、修改、替换、删除或改安全。publisher mutex固定`Global\FolderSessionLock.RecoveryReadiness.v1`。
- snapshot严格UTF-8 without BOM、1..16384 bytes、schema1精确十二字段。四状态矩阵、sequence、heartbeat 10 seconds、`validUntil=published+30 seconds`、future tolerance 5 seconds固定为D-030。缺失、schema不支持、stale、读取/owner/DACL/identity失败、State非Ready、blocking、扫描未完成、有remaining或主错误时全部fail closed。
- publisher/reader/delete必须retained handle绑定identity、安全与content；publish使用FlushFileBuffers和class65相对原子replace，delete使用verified handle disposition。禁止路径型move/replace/delete和关闭验证句柄后按名称操作。
- CreateLock 在路径和 ACL 写入前必须通过 readiness；失败固定返回 `FSL_E_RECOVERY_BLOCKING` / `Folder restrictions cannot be created until recovery is complete.` / retryable true / field null。
- RecoveryBlocked 期间不得创建新限制、删除非法构件、猜测损坏记录或降低安全要求；ValidatePath 与脱敏 GetStatus 可继续。
- Cleanup 进入 `RecoveryRequired`、ACL 状态未知或恢复失败时，不得用 scheduler error 覆盖对应 Cleanup task error，也不得报告清理完成。
- 受保护内部日志保存 scheduler error code、脱敏 scheduler exception、第一个及其余 Cleanup task errors、`taskId` 或受保护关联标识、完整遍历标志和 `RecoveryRequired` 标志。公开响应不得包含 scheduler exception 堆栈、内部类型名、SID、SDDL、恢复记录路径、凭据或令牌。
- scheduler生产loop的未预期非取消异常只允许 `lock_task.scheduler.loop.exception` / `The lock task scheduler loop terminated unexpectedly.`，protected logger固定`component = Scheduler`、`level = Error`。预期token取消的`OperationCanceledException`不记录。该合同不得用于lifecycle stop、Cleanup failure、task状态转换、已有更具体错误或logger failure；内部记录不得包含异常message、`ToString()`、stack、内部类型、路径、SID、HRESULT或Win32 message，且不得进入公开响应、覆盖Cleanup first-task error或阻止Cleanup。
- administrative Cleanup 的 `RemoveLockAsync` 异常固定使用 `lock_task.administrative_cleanup.exception` 与 `The administrative cleanup ended without a confirmed result.`；ACE 已移除但 `Completed` 状态记录失败固定使用 `lock_task.administrative_cleanup.state_update_failed` 与 `The lock was removed but its completed state could not be recorded.`。两者均为 `UnrecoverableError` 并进入 `RecoveryRequired`；内部日志只记录稳定 code 和受保护关联字段，不记录异常 message 或 stack。

### D-030 生产分类与 Protected Logger

- production duration固定60000..86400000ms。scheduler每consent-broker进程一个Active owner和串行loop，只用monotonic timestamp，最大30-second分段重算；禁止Windows Task Scheduler、多Timer、fire-and-forget、UI或多进程共享到期责任。
- repository安全来源只允许从retained target handle逐级handle-relative检查`.git|.hg|.svn`；synchronization只允许Cloud Files handle API与可信initiating token的SkyDrive Known Folder。SkyDrive必须先用`IKnownFolderManager::GetFolderIds`的S_OK GUID二进制集合证明注册；不含SkyDrive时唯一原因`KnownFolderNotRegistered`并允许`Exists=false`。注册存在后固定flags0调用`SHGetKnownFolderPath`，禁止CREATE/DONT_VERIFY/DEFAULT_PATH；调用前pointer为null，失败返回的非null pointer也必须释放。完整`0x80070002`/`-2147024894`和`0x80070003`/`-2147024893`是仅有的路径不存在HRESULT；与注册缺失合计三个允许继续场景。GetFolderIds非S_OK、`0x80070057`/`0x80004005`/`0x80070005`/`0x80070006`/`0x8007052E`/`0x80070520`/`0x80070522`、raw 2/3、低16位伪装及其他结果全部fail closed。S_OK必须有非空绝对路径并在释放pointer后执行retained handle/reparse/final path/identity关系检查。禁止HRESULT_CODE、mask、raw Win32/NTSTATUS/重编号、E_INVALIDARG未注册解释，以及环境变量、cwd、PATH、CLI/用户roots、注册表或第三方配置。
- protected logger唯一provider为`ProtectedJsonLinesLoggerProvider`。Logs root和三个模式目录、所有文件owner SYSTEM，protected DACL只允许SYSTEM、Administrators、service SID FullControl；普通用户不可列出、读取或写。consent-broker不能创建/修复root，只能同handle安全创建自身文件。
- 每进程文件名、十四字段JSONL、4096-byte行、LF、无BOM、每事件flush、8MiB/UTC日rotation、14days、每模式32关闭文件、全局256MiB固定为D-030。只删除安全已关闭非活跃文件；异常artifact不删除并使用`FSL_E_PROTECTED_LOG_ARTIFACT_INVALID`。
- logger初始化失败固定`FSL_E_PROTECTED_LOGGER_UNAVAILABLE`。consent-broker Pipe前失败exit28；运行中有副作用时先Cleanup，exit27优先；service启动失败不得Running，运行中失败使readiness RecoveryBlocked后受控停止；recovery-once使用exit15。

参考：

- [Microsoft：Named Pipe Security and Access Rights](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
- [Microsoft：Impersonating a Named Pipe Client](https://learn.microsoft.com/en-us/windows/win32/ipc/impersonating-a-named-pipe-client)

限制：已控制同一用户会话的恶意进程也具有相同 Account SID 和 Logon SID。

当前 D-031 本地单用户管理员 Release 可显式 unsigned；它仍必须位于管理员保护目录，普通用户不得替换或修改，IPC 必须限制本机访问、使用最小 Pipe DACL、验证客户端身份并防重放。unsigned 不提供 publisher 身份保证，且不得宣称适用于公开或企业分发。

当前 Stage 4 控制器不公开 publisher pin 或 signing certificate 参数，固定把精确空
`BrokerPublisherThumbprint` 写入 assembly metadata，且无 signed/SignTool 执行分支。
App runtime verifier 对 null/精确空值不调用 platform，对空白或其他畸形非空值
fail closed，并为未来 runtime configuration 保留有效 40 位十六进制 thumbprint 的
原 signed 模式；当前控制器不可选择。应用在任何 UAC、
Pipe、replay、恢复记录或 ACL 副作用前，先完成固定 Program Files 路径、安装目录
ACL、普通文件/non-reparse、final path 与文件 identity 验证，再使用无 UI
`WinVerifyTrust`（仅 signed 模式）验证 Authenticode、提取 signer thumbprint 并与 pin
精确匹配，最后重新验证 final path 与文件 identity。signed 模式的签名无效、无
signer、publisher 不匹配，或任一模式 identity 改变，均只返回 `FSL_E_BROKER_PATH_UNTRUSTED` /
`The elevated broker installation could not be verified.` / `retryable=false` /
`field=null`，不得启动 Broker。

阶段 4 的 Broker/Service 安装根固定为 `%ProgramFiles%\FolderSessionLock`，ACL 为 `SYSTEM: FullControl`、`Administrators: FullControl`、`Users: ReadAndExecute`，不为 `Authenticated Users` 额外授予写权限。禁止从仓库 `bin`、`obj`、TEMP、用户目录或网络路径注册服务。

当前 Stage 4 run 不创建或信任测试证书。发布、安装和 D-026 证据必须逐一确认固定六 PE 为实际 `NotSigned` 且 signer 为 null。真实签名证书采购、托管、公开发布和企业签名流水线需要未来决定；App runtime verifier 的原 signed fail-closed 路径保留，但当前 Stage 4 控制器不能进入。

## 10. 路径安全

- v1 仅支持本机固定 NTFS 普通目录。
- 目标及所有祖先组件必须无 reparse point。
- 字符串规范化不作为安全身份；使用稳定目录句柄、最终路径、卷标识和目录文件标识。
- DACL 读取、应用、后置验证和移除必须通过同一持续持有目录句柄；不能在事务中重新按字符串路径打开。
- ACL 应用前后复核同一目录身份，并验证替换对象 ACL 从未改变。
- UI 验证后路径替换为 junction、symlink 或其他对象时安全失败。
- 禁止 UNC、映射盘、远程卷、可移动卷、FAT、exFAT、其他未经验证文件系统、磁盘根、系统路径、用户配置文件根、同步目录、仓库和安装目录。repository只按retained-handle祖先marker分类；同步只按Cloud Files handle API和initiating-user SkyDrive Known Folder分类，任何分类不可证明均拒绝。
- 新任务与活动任务规范化后相同、目录身份相同或存在祖先/后代关系时拒绝。

## 11. 审计与访问警告

访问警告默认关闭。阶段 1 至阶段 5 禁止自动修改 Audit File System、添加 SACL 或依赖 Security 日志。阶段 6 开始前必须独立批准目标 SACL、审计策略、Security 日志权限和通知语义。

- `4656`：请求对象句柄；访问被拒时产生 Failure；仅在对象 SACL 包含所需审计 ACE 时生成。
- `4663`：权限已被使用；无 Failure 事件；不得用作失败访问来源。
- 事件允许延迟、缺失、重复、覆盖和多对一/一对多映射。
- 去重与限流是强制要求；不承诺每个 I/O 一次通知。
- Unlock 只移除应用创建的精确 SACL ACE；不整体替换 SACL，不静默改变全局审计策略。

参考：

- [Microsoft：Event 4656](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4656)
- [Microsoft：Event 4663](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4663)

## 12. 测试安全

- ACL 集成测试只允许 `%TEMP%\FolderSessionLock.Tests\<Guid>\`。
- 每个测试使用 `try/finally` 恢复 ACL。
- 测试前证明目录可创建、读、写、枚举、删除。
- 测试后证明所有操作恢复并可删除目录。
- 禁止测试真实用户目录、仓库、同步目录、系统目录、网络路径或 reparse path。
- 恢复失败立即停止阶段 3。
- 阶段 2 Core 测试只使用内存对象和可控时间源，不访问真实目录，不需要管理员权限，不使用 `Thread.Sleep`。
- 阶段 2 Windows 占位服务对创建和四种明确解除意图均返回 `windows.acl.not_implemented`，不得把占位行为解释为真实锁定成功。
- 阶段 3 新增真实 Windows 实现，但产品 Broker 尚未组合该实现；直接调用只存在于 Windows 临时目录集成测试。
- 阶段 3 真实测试验证权限矩阵、同句柄添加与移除、rollback、匹配 0/1/>1、ACL 漂移、父目录不变、继承边界和路径替换。每次写入使用 `try/finally` 与进程级安全停止门，最终临时目录残留为 0。
- 阶段 4 唯一获准特权集成环境为计算机名 `FSL-STAGE4-VM`、Windows 11 Pro/Enterprise 专用可丢弃 VM、快照 `FolderSessionLock-Stage4-Clean`。当前机器 `AGREELIN` 不满足该条件。
- 非获准 VM 只能进行设计、代码实现、单元测试、非特权测试和静态审查；不得创建/删除服务、使用 LocalSystem、配置自动启动、执行登录前测试、UAC、注销、重启、Program Files/ProgramData ACL 或签名系统测试。
- VM 只允许操作服务 `FolderSessionLockRecovery`；禁止修改其他服务或 SCM 全局配置。最多 3 次注销和 3 次完整重启。
- 注销/重启前必须保存证据、输出场景编号、确认目标仅为 `%TEMP%\FolderSessionLock.Tests\<Guid>`、恢复记录已原子提交、不存在仓库或真实用户目录目标。每轮后验证 ACL、服务、记录、证书信任和临时目录清理。
- 阶段 4 仅使用当前本地管理员账户。明确禁止创建 `FSL-Standard`、`FSL-Admin` 或其他专用测试账户；真实双账户证据不属于完成门。
- 证据保存于 `docs\evidence\stage-4\<RunId>\`；不得包含密码、凭据、私钥、令牌、未脱敏用户名或敏感测试内容。精确工件与 manifest 字段以 `D-026` 为准。

## 13. 残余风险

- 已打开句柄继续访问。
- 父目录 `DeleteChild` 绕过目标自身 ACL。
- 同用户恶意进程调用未签名 Broker。
- 外部 ACL 漂移导致无法精确归属。
- Broker 崩溃或断电留下 ACE。
- Logon SID 在重启后回收。
- 自动启动恢复服务故障、恢复记录损坏或权限不足可导致遗留 ACE 需要人工处理。
- 外部主体删除并重建完全相同 ACE 时无法证明来源。
- 标准用户 over-the-shoulder elevation 不受 v1 身份模型支持。
- UAC 被拒绝导致残留 ACE 无法立即清理。
- 审计事件量、延迟、缺失和通知洪泛。

## 14. 未来 Stage 7 公开或企业生产发布 checkpoint

该 checkpoint 当前不激活，只有另一个明确的公开/企业/签名产品决定才能激活。未激活、缺少真实签名证书或缺少签名流水线不得阻止 D-031 本地如实 unsigned Stage 4 完成或 Stage 5 entry。若未来激活，以下任一项存在时禁止公开或企业生产发布；这些条件不把当前本地 Release 改写为 signed：

- Broker 未代码签名。
- Broker 或托管恢复模式的自动启动服务位于普通用户可修改目录。
- 普通用户可替换或修改 Broker。
- IPC 允许远程访问、Pipe DACL 过宽、客户端身份验证缺失或请求可重放。
- 暴露任意命令、脚本、PowerShell、cmd、任意文件写入或调用方提供的任意 ACL 描述符。
- 接受跨账户 elevation 或服务账户代替当前用户创建限制。
- 对来源无法验证或与恢复记录不一致的 ACE 自动删除。
- 通过重建整个 DACL 解决漂移或恢复失败。
- ACL 恢复失败却报告成功。
- 支持范围外路径绕过准入验证。
- 阶段 6 未获批准即修改审计策略、添加 SACL 或读取 Security 日志。

## 15. Stage 4 发布与证据完整性

- Stage 4 发布信任边界固定包含六个第一方 PE：
  `FolderSessionLock.App.exe`、`FolderSessionLock.App.dll`、
  `FolderSessionLock.Broker.exe`、`FolderSessionLock.Broker.dll`、
  `FolderSessionLock.Core.dll`、`FolderSessionLock.Windows.dll`。缺少任何一个，
  或发布目录出现额外 `FolderSessionLock.*` executable/DLL，均阻止发布。
- 当前本地 run 的六个 PE 必须全部由 `Get-AuthenticodeSignature` 实测为
  `NotSigned` 且 signer null；`signature-verification.txt` 逐文件绑定 SHA-256。
  Finalize 从受保护 state 取得 ReleaseRoot/ReleaseDescriptorSha256，重验 frozen
  descriptor、精确六 PE 集合和实际文件 hash，再与有序 evidence hash 精确比较；
  任意格式合法但不相等的 64-hex hash 必须拒绝。
  当前控制器不公开 pin/certificate 参数、不调用 SignTool、不创建测试证书。App
  runtime verifier 的未来有效 pin 模式保留原 `WinVerifyTrust` 合同，但控制器不可选择。
- UI 对 Broker 的验证使用单一 `WinVerifyTrust` provider state：在同一已验证
  state 中取得 signer 证书和 thumbprint，完成后执行 state close。不得通过第二次文件
  打开或独立证书解析替代 signer 绑定。
- `Logs\v1`、三个日志 mode 目录和 `Readiness` 目录的显式 ACE 均为
  `AceFlags.None`；只有 Recovery/Replay 容器按各自权威合同使用继承 ACE。Stage 4
  VM 测试必须由生产 protected logger/readiness reader 实际接受安装出的描述符。
- 控制器状态同时写入 `state.json` 和 append-only JSONL journal。每个命令重新验证
  RunId、机器、分支、commit、转换序号以及 journal 中记录的 state SHA-256；只编辑
  state 文件不能推进阶段。
- canonical `test-results.trx` 必须由一次真实 `dotnet vstest` 直接生成。唯一 counter
  集合须满足 `executed = passed = total`，failed/notExecuted/error/timeout/aborted
  全为 0，UnitTestResult 数量精确等于 total，且每项 outcome 为 Passed；禁止合并时
  伪造或归零 skipped 计数。
- 卸载和清理绑定预先记录的 final path、NTFS file ID、SHA-256、release manifest
  hash 与精确 ACL。出现 reparse、替换对象、identity/ACL 漂移或未知文件即拒绝清理；
  产品安装目录和 ProgramData 目录不得递归删除。
- 当前 run 不创建证书；pre-state、cleanup 与残留检查必须证明没有 run-specific
  certificate，且不得把既有未知证书纳入删除范围。`cleanup-results.txt` 必须写入
  精确 `CertificatesRemaining=0`，FinalizeEvidence 必须验证。
- Release descriptor 在 Publish 后冻结精确、区分大小写的完整文件集合，并绑定
  manifest、SHA256SUMS、每个 payload 的长度和 SHA-256；后续验证和复制不得重写或
  重新认可已变化的发布目录，复制前后均复核源与目标。
- Stage 4 journal 使用 write-through append、previous-entry hash chain 和独立
  anchor；state 仅为可重建 cache。仅不完整 torn tail 可恢复；完整未 anchor 记录、
  截断、anchor 不匹配以及 state+journal 联合篡改均拒绝。
- 安装对象在 mutation 前写 WAL intent，成功后写 applied proof；失败时按严格逆序、
  验证 identity 后回滚。未知对象和 replacement 必须保留并报告失败。
- Preflight 后仓库只允许当前 RunId evidence 精确变化；tracked 源码变化、其他
  untracked 文件和 other-run evidence 均阻止命令。
- 服务删除前必须用结构化 SCM snapshot 精确验证全部固定字段和 Stopped 状态。
- Dormant Windows Kits SignTool trust helpers 不可由当前固定 unsigned 公共控制器
  到达；若未来另行批准签名控制器，必须重新审查其 final path、non-reparse
  ancestors、owner、DACL、Microsoft signer、SPKI allowlist 与执行前后 file identity。
- CP10 在仓库外 LocalApplicationData 下维护 current-user DPAPI 保护的随机 HMAC
  key 与双槽 anchor，绑定 run/machine/repository/branch/commit 以及 prestate、
  journal、WAL、state 和内部 anchor 的精确 length/hash。Finalization 删除 key、
  两个 slot 和 run-specific anchor 目录。
- Install、Uninstall 与 Cleanup 共用 hash-chained durable adapter：protected
  Intent 在 side effect 前，完整 Applied proof 在其后，每条 WAL record 随即由
  外部 anchor 保护。schema 3 在 Begin 前冻结完整有序 plan/hash；FileCopyAtomic
  使用 transaction-derived 同目录 temp，并在创建前绑定 temp/final absence、
  parent identity/ACL 与 source identity/content。中途恢复只允许删除 final 不存在、
  source 仍完整、parent proof 不变、且对象为 ordinary、non-reparse、single-link、
  safe owner/DACL、长度不超过冻结长度、内容为 source 精确前缀的 temp；任一证明
  失败均保留对象并禁止伪造 RolledBack/Aborted。真实 PowerShell 5.1
  worker-process matrix 以父进程 Process.Kill 覆盖 Begin、Intent、temp create、
  mid-write、Flush、rename、Applied 与 Commit 窗口。
- 该 WAL/reconcile 合同假设 controller/executor 是受信且唯一的写入者；它不是对同一
  用户恶意进程、管理员、LocalSystem、VM/磁盘快照回滚的安全边界。current-user
  DPAPI/HMAC 双槽 anchor 只检测其覆盖范围内的本地不一致，不是外部 witness，也不提供
  anti-rollback 保证；D-026 证据不得把这些非目标宣称为已解决。
- reviewer verdict 只允许单个大写 `PASS` 或 `FAIL` token；重复、冲突、正文和
  大小写别名均拒绝，且仅 `PASS` 允许 finalization。
