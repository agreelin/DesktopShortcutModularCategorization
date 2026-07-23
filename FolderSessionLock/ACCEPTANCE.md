# Folder Session Lock 验收标准

## 1. 通用完成门

阶段完成必须同时满足：

- 本阶段全部客观验收项满足。
- 所有适用自动测试通过。
- 适用时 Release build 通过。
- reviewer 输出 `PASS`。
- ACL 测试目录已恢复、可访问、可删除。
- `FolderSessionLock/TASKS.md` 和 `FolderSessionLock/DEVLOG.md` 已更新。
- 未通过缩小声明、跳过测试、吞异常或修改验收标准制造通过。

## 2. 通用验证

阶段 1 起：

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-restore
dotnet format --verify-no-changes
```

阶段 0 无 solution；上述命令不适用。阶段 0 使用 git、文件存在性和文档一致性验证。

## 3. 阶段 0

- 根 `AGENTS.md`、`FolderSessionLock/AGENTS.md` 和八份项目文档全部存在且非空。
- `D-001` 至 `D-020` 状态均为 `已决定`；确认内容在需求、架构、安全和计划中一致。
- 八份项目文档只存在于 `FolderSessionLock/` 最终路径；仓库根无同名副本、无符号链接入口、无第二权威源。
- 产品能力不使用“绝对阻止任何访问”等不可验证声明。
- ACL 能与不能保证的边界明确。
- Account SID、Logon SID 和重启回收风险明确。
- Broker 恢复专用模式已定义为唯一遗留 ACL 清理主体；由自动启动 Windows 服务以 LocalSystem 身份在交互登录前托管；新会话只清理旧 ACE，不恢复任务或剩余时间。
- 当前会话绑定、正常到期、UI/Broker 崩溃、注销、关机、断电行为明确。
- 严格零持久化与崩溃恢复冲突明确。
- 最小恢复记录方案、允许/禁止数据、唯一用途和恢复后删除规则已决定。
- Broker 是否必要、权限模型、IPC ACL、调用方身份和同用户限制明确。
- ACE 掩码、添加、后置验证、精确移除、幂等和回滚明确。
- ACE 无来源标签、外部同元组重建不可区分及 DACL 稳定性信任假设明确。
- 原 DACL/SACL、继承、无关主体和父目录保护规则明确。
- 重复目录、父子目录和稳定目录身份策略明确。
- reparse、UNC、映射盘、非 NTFS 和 TOCTOU 策略明确。
- 访问警告的 `4656`/`4663` 边界、去重和批准门明确。
- v1 非目标明确。
- 阶段 1–7 均有 checkpoint、验证和客观验收。
- 阶段 0 设计与文档位置不存在待确认项。
- reviewer `PASS`。
- `FolderSessionLock/TASKS.md`、`FolderSessionLock/DEVLOG.md` 已更新。

## 4. 阶段 1

- solution、四个产品项目和三个测试项目存在且依赖方向符合 `FolderSessionLock/docs/ARCHITECTURE.md`。
- solution、源码、测试和项目文档全部位于仓库子目录 `FolderSessionLock/`。
- 仓库根无产品 solution 或项目；无关项目和根 `README.md` 无变化。
- restore、Release build、tests、format 全部通过。
- Core 不引用 WPF、Windows UI 或 ACL API。
- 未执行真实 ACL、Broker 提权或审计策略修改。
- 临时目录工具只能创建 `%TEMP%\FolderSessionLock.Tests\<Guid>\`。
- reviewer `PASS`。

## 5. 阶段 2

- Core 测试无需管理员权限。
- 不使用 `Thread.Sleep`。
- 任务 ID、路径、时长策略和时长值对象覆盖有效、默认、空、相对、零、负值、最小值、最大值和超上限测试；Core 不访问文件系统。
- 全部合法、非法和同状态转换有测试；`Completed`、`RecoveryRequired` 无出站；非法转换状态不变，同状态不改时间或错误。
- `LockTaskManager` 在同一同步门内执行重复 ID、Same、Ancestor、Descendant 检查、添加和状态替换；并发同路径仅一项成功；查询返回不可变快照。
- 激活成功、平台失败、返回不同 ID、异常、重复和并发激活有测试；Apply 最多一次；错误保留在任务状态。
- 单调时钟控制到期；UTC 仅用于显示；墙钟前拨、后拨、offset、时区或夏令时表示变化不改变 elapsed。
- 到期前不解除，精确到期和跨越到期点解除；剩余时间不为负。
- 到期扫描先原子 `Active -> Unlocking`，并发和重复扫描只发送一次 `Expiration` 解除。
- 解除成功进入 `Completed`；确定失败进入 `UnlockFailed`；结果不确定异常进入 `RecoveryRequired`；失败不得伪装为完成。
- 多任务不同顺序、同时到期、一次推进跨多个到期点和单任务失败隔离有测试。
- scheduler 使用可取消 delay，无 fire-and-forget 或忙轮询；取消不解除或完成活动任务。
- 解除意图精确为 `Expiration`、`Recovery`、`TestCleanup`、`AdministrativeCleanup`；无 User/UI 值或无意图重载。
- App、MainWindow、MainViewModel 不拥有 scheduler 或 folder lock service，不公开解除意图入口。
- Windows 创建和解除占位始终返回 `windows.acl.not_implemented`；不访问路径，不要求管理员权限。
- 未实现 ACL、DACL、SACL、Logon SID、Broker 提升、IPC、Named Pipe、持久化、服务、审计或完整 WPF。
- reviewer `PASS`。

## 6. 阶段 3

- 只在 Windows 本机临时 NTFS 目录运行 ACL 集成测试。
- Logon SID 从访问令牌精确读取，不回退为 Account SID。
- 路径、卷、reparse、目录身份和恢复权限验证完整。
- DACL 读取、添加、后置验证和移除绑定同一持续目录句柄。
- 锁定后目标会话的新访问命中拒绝矩阵。
- 枚举、读取、创建、写入、删除、重命名、移动、属性和子目录修改逐项验证。
- `ReadPermissions`、`ChangePermissions` 及精确移除应用 ACE 的恢复能力验证通过。
- 原 ACE 多重集合、继承状态和无关 SID 不变。
- 不使用 `FullControl`；不拒绝恢复权限；不修改父目录。
- Lock/Unlock 幂等；失败回滚。
- 路径替换测试证明替换对象 ACL 从未改变。
- 每测试 `try/finally`；目录最终可访问且可删除。
- 任一恢复失败导致阶段 `FAIL`。
- reviewer `PASS`。

## 7. 阶段 4

- `D-022` 至 `D-030` 全部为 `已决定`；实现中的路径、字段、大小写、标识符、参数、协议 envelope、错误码、consent-broker 退出码、readiness/logger schema、生产分类器和 CP6 cleanup first-task error 优先级与决定逐字一致。
- UI 不包含真实 ACL 写入。
- Broker API 仅包含白名单强类型命令，无任意命令执行。
- 请求命令关闭集合精确为 `ValidatePath`、`CreateLock`、`RemoveLock`、`GetStatus`，大小写敏感且无第五项。
- Pipe 消息使用 byte mode、4-byte little-endian `UInt32` 长度前缀、严格 UTF-8 without BOM、最大 65536 bytes；零长度、超限、不完整、额外字节、多 JSON、BOM、非法 UTF-8均拒绝。
- 请求精确六字段、响应精确七字段；成功 `result != null/error == null`，失败相反；无法解析 ID/command 时使用 null 和 `FSL_E_MALFORMED_MESSAGE`。
- 重复、多余、缺失、不允许 null、大小写错误、宽松 integer/Guid/date/enum、注释和尾逗号全部按 `D-027` 精确错误码拒绝。
- requestId 小写 Guid D、非空、10 分钟 replay；sentAtUtc 为 7 位小数 UTC `Z` 且 120 秒窗口；clientSessionId 与 OS Session 一致。
- Pipe DACL、Logon SID、Account SID、Session ID、连接进程、握手和防重放验证均有测试。
- consent-broker 连接只接受 `ClientHello -> ServerHello -> CommandRequest -> CommandResponse -> close`；跳过/重复/乱序、第二命令和响应后数据返回 D-027 固定错误并关闭。
- ClientHello 精确九字段；ServerHello 精确九字段且成功 result 精确三字段；CommandRequest 精确八字段；CommandResponse 精确七字段。handshakeVersion 固定 1。
- CLI request-id/session-id、ClientHello、CommandRequest 外层和内层应用 request 的 requestId、command、protocolVersion、session 逐项绑定；任一不一致返回 `FSL_E_REQUEST_BINDING_MISMATCH` 或更具体身份错误。
- clientNonce/serverNonce 为 Base64URL without padding、解码 32 bytes、非全零、密码学安全且不重用；connectionId 为每连接唯一非空小写 Guid D。
- bindingProof canonical string、LF、无尾随换行、SHA-256、Base64URL 和恒定时间比较与 D-027 完全一致；通过后握手不可再次消费。
- OS 客户端验证覆盖实际 Pipe PID、进程存活与启动时间、进程 Session、模拟令牌 Account SID/Logon SID/TokenSessionId、Broker 身份比较和 finally 恢复；不得信任客户端 SID 或用户名。
- `FSL_E_HANDSHAKE_REQUIRED`、`FSL_E_HANDSHAKE_VERSION_UNSUPPORTED`、`FSL_E_HANDSHAKE_EXPIRED`、`FSL_E_PROTOCOL_SEQUENCE_INVALID`、`FSL_E_REQUEST_BINDING_MISMATCH`、`FSL_E_CLIENT_PROCESS_MISMATCH`、`FSL_E_CLIENT_IDENTITY_UNAVAILABLE`、`FSL_E_ACCOUNT_SID_MISMATCH`、`FSL_E_LOGON_SID_MISMATCH`、`FSL_E_REQUEST_IN_PROGRESS` 的 message、retryable、field 和响应标识符与 D-027 一致。
- Replay Registry 路径精确为 `%ProgramData%\FolderSessionLock\Replay\v1`，文件为 `<ReplayKeySha256>.fsrr`，临时文件为 `<ReplayKeySha256>.tmp-<Guid>`；replay key、JSON 字段、state 和类型与 D-027 一致。
- Replay 登记使用 CreateNew 原子语义；受保护 mutex 精确为 `Global\FolderSessionLock.ReplayRegistry.v1`；并发相同 requestId 只有一个所有者，其他返回 `FSL_E_REQUEST_IN_PROGRESS` 或 `FSL_E_REPLAY_DETECTED`。
- ClientHello timeout 5 seconds、handshake 30 seconds、lease 60 seconds、renewal 20 seconds、execution limit 5 minutes、terminal retention 10 minutes；RecoveryRequired 不自动过期。
- 身份失败、握手超时、binding 失败、无副作用失败、rollback 成功/失败、成功、owner 崩溃、PID 重用和未知副作用分别进入 D-027 固定 state 与 terminalCode；新 Broker 不接管未过期或未知副作用请求。
- PID、Account SID、Logon SID、Session ID、identity unavailable、CLI/time/schema/version 和 unauthorized 失败均证明 Replay 文件不存在；CreateNew 只在完整身份与命令权限通过后调用。
- 首帧 CommandRequest 只返回 HANDSHAKE_REQUIRED；握手版本错误只返回 HANDSHAKE_VERSION_UNSUPPORTED；ServerHello 后超时只返回 HANDSHAKE_EXPIRED；成功 ServerHello 后重复 ClientHello/第二命令只返回 PROTOCOL_SEQUENCE_INVALID；active Replay 只返回 REQUEST_IN_PROGRESS；terminal/RecoveryRequired 只返回 REPLAY_DETECTED。
- ServerHello failure 无 connectionId，合法 requestId/允许 command 才回显；CommandResponse failure 必须回显服务端已接受 requestId/command/connectionId；恶意后续 frame 标识符不得进入响应。
- Pipe 名精确为 `FolderSessionLock.Broker.v1`；任意其他 Pipe 名安全失败。
- 未授权、其他会话、远程、畸形和重复请求行为确定。
- `ValidatePath`、`CreateLock`、`RemoveLock`、`GetStatus` 的 payload/result 精确字段、类型、条件、幂等和错误映射与 `D-027` 一致。
- 普通 UI 调用 RemoveLock 返回 `FSL_E_UNAUTHORIZED_CALLER`；客户端提供 intent、SID、ACL、路径、service/Pipe 名、命令或脚本字段返回 `FSL_E_FORBIDDEN_INPUT`。
- GetStatus 只返回同账户、同 Session 的内存任务；ByTaskId 不存在或越权统一 `FSL_E_TASK_NOT_FOUND`；公开错误不泄露内部细节。
- 恢复记录每个事务中断点有测试。
- 恢复路径精确为 `%ProgramData%\FolderSessionLock\Recovery\Records\<RecordId>.fslr`；临时和备份分别为 `<RecordId>.tmp-<Guid>` 与 `<RecordId>.bak`；调用方和命令行不能覆盖路径。
- 容器头精确为 ASCII `FSLR`、little-endian `ContainerVersion`/`Flags`/`ProtectedPayloadLength` 和 DPAPI payload；`containerVersion = 1`、`schemaVersion = 1`、`writerVersion = 1.0`。
- JSON payload 字段名称、类型和允许值与 `D-022` 完全一致；未知版本、必需字段、状态或类型返回 `RecoveryRecordUnsupported`。
- `volumeSerialNumber` 为精确 16 位小写 hex；FILE_ID_128 bytes 0..7/8..15 分别按 little-endian UInt64 写为 `fileIdLow`/`fileIdHigh` 十进制 string；固定向量和反向重建通过，任一 identity byte 或 volume 改变即不相等。
- `aceFingerprintSha256` 使用写后 OS DACL 重读的唯一 ACE 和 `FSLACE` v1 wrapper；匹配 0/>1、AceSize 非法或长度不一致均失败。
- `baselineDaclSha256`/`postApplyDaclSha256` 使用 `FSLDACL` v1 wrapper、原 ACE 顺序、有效 ACE bytes、ACL revision 和 `control & 0x1504`；三个固定 SHA-256 向量通过。
- ACE 顺序、DACL control、ACL revision或有效 ACE byte 改变时 digest 改变；owner、group、SACL、SELF_RELATIVE 或 ACL 未使用尾部改变时 digest 不变；missing/null DACL 被 CreateLock 拒绝。
- baseline 在 Prepared 前从同一持续句柄读取；postApply 和 fingerprint 必须在写后从 OS 重新读取，不得由 baseline 加本地 ACE 推导。摘要不能替代目录身份、ACE 元组、主体、记录状态和调用模式验证。
- `.fslr` v1 writer 永远写 version 1、flags 0；payload blob 1..262144，文件总长严格等于 header 12 + blob，短头/截断/尾随零或非零/未知 flags/版本/magic 均返回 D-022 固定错误且不调用 ACL。
- 解密明文 <=131072、UTF-8 without BOM、单一 JSON object、精确 25 字段；重复/缺失/多余/类型/范围/canonical Guid/date/SID/hash/enum/flags 矩阵全部拒绝测试通过。
- Prepared/Applied/CleanupPending/CleanupFailed 的 postApply/error/count 状态矩阵全部正反测试通过；Prepared postApply 必须 null，Prepared fingerprint 为预期值，Applied 实际值必须由 OS 重读并匹配。
- 任何损坏或不支持记录均不修改 ACL、不删除/覆盖 `.fslr`、不迁移版本、不扫描无关路径，并标记人工恢复检查。
- DPAPI 使用 `DataProtectionScope.LocalMachine`，entropy 是 UTF-8 `FolderSessionLock.RecoveryRecord.v1` 的 SHA-256；容器头不泄露路径、SID、ACE 或错误细节。
- `Prepared` 在任何 ACL 写入前完成 tempHandle owner/DACL、identity、flush、同句柄回读与 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)` 原子提交；新建 flags=0，更新保持 old/temp/directory handles 并使用 flags=`0x00000003` POSIX handle replace。canonical 删除在同一已验证 handle 上执行 FileDispositionInfoEx，成功后关闭该 handle，再由 retained directory handle确认名称消失与目录 identity。禁止 class 10、SetFileInformationByHandle class 22/class 3、绝对目标、ReplaceFileW/File.Replace/路径 move/delete 或其他 fallback；失败按 D-022.11 保留责任。
- 恢复目录 owner 为 `NT AUTHORITY\SYSTEM`，受保护显式 DACL 只允许 `NT AUTHORITY\SYSTEM`、`BUILTIN\Administrators`、`NT SERVICE\FolderSessionLockRecovery` 完全控制；普通用户和 UI 无直接访问。
- Broker 正常退出、崩溃、IPC 断开、路径替换均安全处理。
- Broker 正常退出或抽象会话结束触发时，无论 scheduler 成功或失败都执行 Cleanup，并按稳定任务顺序遍历全部适用任务；单任务失败不阻止剩余任务。
- scheduler/Cleanup 固定 2×2 测试全部通过：success/success 返回 Cleanup success count；success/failure 返回 Cleanup first-task error；failure/success 返回 Cleanup success count；failure/failure 返回 Cleanup first-task error。
- Cleanup 主错误按实际稳定处理顺序确定，不按异步完成顺序；后续 Cleanup errors 与 scheduler error 只进入受保护内部诊断，不替换主错误。
- `RecoveryRequired`、ACL 状态未知或恢复失败对外返回对应 Cleanup task error，不被 scheduler error 覆盖，不报告清理完成。
- 受保护内部日志包含 scheduler error code、脱敏 scheduler exception、首个及其余 Cleanup task errors、`taskId` 或受保护关联标识、完整遍历和 `RecoveryRequired` 标志；公开响应不包含 stack、内部类型名、SID、SDDL、恢复记录路径、凭据或令牌。
- `LockTaskScheduler`生产loop未预期非取消异常只产生`lock_task.scheduler.loop.exception` / `The lock task scheduler loop terminated unexpectedly.`；真实protected JSONL固定`component = Scheduler`、`level = Error`、精确code/message，且不含异常message、`ToString()`、stack、内部类型、路径、SID、HRESULT或Win32 message。
- 预期token已取消的`OperationCanceledException`不产生scheduler error日志；lifecycle stop、Cleanup failure、task状态转换、已有更具体错误和logger failure不复用该code/message。测试必须证明新值通过schema、已废弃旧值被拒绝、Cleanup first-task error不被覆盖且production源码无已废弃旧值。
- administrative Cleanup 的 `RemoveLockAsync` 抛异常精确产生 `lock_task.administrative_cleanup.exception` / `The administrative cleanup ended without a confirmed result.`；ACE 已移除但 `Completed` 状态记录失败精确产生 `lock_task.administrative_cleanup.state_update_failed` / `The lock was removed but its completed state could not be recorded.`。两者均为 `UnrecoverableError`，任务均进入 `RecoveryRequired`，测试不得接受 activation/expiration 专用错误替代。
- 同一账户 consent elevation 成功；其他管理员账户凭据安全失败并显示“不支持跨账户提升”。
- 跨账户拒绝在任何 ACL 写入前发生，稳定错误码为 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`。
- 身份错误分层精确：connected Pipe 的 `FSL_E_ACCOUNT_SID_MISMATCH` 保持 D-027 ServerHello failure；bootstrap Account SID不同 exit 20；UI只在这两条路径输出 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`。Logon SID、Session、PID、identity unavailable、Pipe access 与 unauthorized 错误不得转换。
- UI 在 UAC 前从自身 process token 取得 TokenUser、唯一 Logon SID、TokenSessionId，并保存 PID/creation FILETIME；Token读取失败、0/多 Logon SID、PID不存在、creation mismatch与PID重用测试通过。SID只在内存，不进入 CLI；Broker必须重开 UI process/token重新读取。
- consent-broker CLI 精确增加 `--client-process-id <UInt32>` 与 `--client-process-creation-filetime <UInt64 decimal>`；禁止 account/logon SID、用户名、管理员标志、role 与 pipe-sddl。bootstrap exit 21/22 映射分别为 identity unavailable/process mismatch。
- Pipe 只在 bootstrap identity与Session全部通过后创建；protected DACL精确为可信 UI Logon SID与Broker Account SID的 ReadWrite + Synchronize，并设置 `PIPE_REJECT_REMOTE_CLIENTS`。
- production Broker path 仅由 `SHGetKnownFolderPath(FOLDERID_ProgramFiles)` 得到固定安装路径；D-023 install directory、普通文件、non-reparse、final path、目录归属与identity验证通过。相对路径、cwd、PATH、环境变量、AppContext、仓库/bin、用户配置、CLI path与App Paths均被拒绝。
- UAC launcher固定使用 `ShellExecuteExW`、`runas`、已验证 path/directory、`SW_HIDE`、四个 D-029 flags、专用参数encoder与非空 process handle。`ERROR_CANCELLED`、一般失败、空 handle、无应用级UAC timeout及禁止 Process.Start/ProcessStartInfo/shell/token/logon/task/service替代均有测试。
- Broker client connect wait 15 seconds；UI Pipe/process race总等待20 seconds。连接前存活timeout只允许 TerminateProcess exit 29并等待5 seconds；成功返回 connect timeout，无法证明清理返回 process cleanup failed。Pipe连接后全部路径证明不会调用 TerminateProcess。
- consent-broker exit code关闭集合精确为0、2、20–29；exit 2精确映射`FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / false / null，响应不得包含CLI、参数、路径、命令行、Win32或异常细节；每个码的公开映射、连接前/后优先级与unknown early exit测试通过。不得直接返回 Win32/HRESULT/NTSTATUS/Exception.HResult/BrokerError hash或应用错误序号。
- 应用 `success:false` CommandResponse可以exit 0；response write失败且Cleanup成功exit 26，Cleanup/RecoveryRequired未安全收敛exit 27；合法CommandResponse不得被后续process exit改写。
- 每个 consent-broker process只创建一个listener、接受一个client、执行一次四帧握手和一个应用命令；第二连接/请求拒绝。ValidatePath/GetStatus/普通UI RemoveLock拒绝/CreateLock副作用前失败在响应送达后exit0；GetStatus不启动scheduler。
- CreateLock成功响应后UI关闭不终止Broker，scheduler继续持有唯一Active task直到Expiration Cleanup；成功exit0，Cleanup失败exit27。响应前断开按无副作用exit25、确定Active继续、未知副作用RecoveryRequired分类。
- production `BrokerCompositionRoot` 显式包含D-029列出的identity/path/ACL/recovery security/readiness/replay/frame/protocol/execution/task/scheduler/lifecycle/logging/clock依赖；静态测试确认无AllowAll、fake identity/readiness、in-memory recovery、test cleanup hook、test path或debug Broker path。
- 新错误 `FSL_E_BROKER_PATH_UNTRUSTED`、`FSL_E_ELEVATION_CANCELLED`、`FSL_E_ELEVATION_LAUNCH_FAILED`、`FSL_E_BROKER_LAUNCH_CONTRACT_INVALID`、`FSL_E_PIPE_INITIALIZATION_FAILED`、`FSL_E_BROKER_CONNECT_TIMEOUT`、`FSL_E_BROKER_EXITED_EARLY`、`FSL_E_BROKER_PROCESS_CLEANUP_FAILED` 的message/retryable/field与D-029一致。
- CP9在`AGREELIN`只记录wrapper/resolver/bootstrap/mapper/race/composition及fake自动测试；真实UAC、跨账户凭据、elevated Broker、Program Files安装/签名和FSL-Standard/FSL-Admin场景不得记录通过。
- D-030 readiness固定为ProgramData Known Folder下受保护machine snapshot，包含Readiness目录/canonical/temp、SYSTEM owner、四ACE protected DACL、Users只读、publisher mutex、十二字段严格JSON、四状态矩阵、sequence、10-second heartbeat、30-second有效期和5-second future tolerance。publish/read/delete全部retained-handle绑定，class65原子replace与FileDispositionInfoEx无路径fallback；全部内部错误对CreateLock映射`FSL_E_RECOVERY_BLOCKING`。
- production duration边界60000与86400000通过，59999与86400001拒绝；每consent-broker单Active owner/单scheduler loop，monotonic到期、最大30-second分段重算、UI断开不取消、scheduler error不覆盖Cleanup。不得使用Windows Task Scheduler或多Timer。
- repository分类从target retained handle逐级检查`.git|.hg|.svn`；synchronization只使用Cloud Files handle API、`IKnownFolderManager::GetFolderIds`与initiating-user `FOLDERID_SkyDrive`。测试必须覆盖：GetFolderIds S_OK不含/失败/含SkyDrive后才调用SH；GUID二进制比较；SH固定flags0且不出现CREATE/DONT_VERIFY/DEFAULT_PATH；调用前null pointer；S_OK有效/null/empty；完整`0x80070002`/`-2147024894`与`0x80070003`/`-2147024893`返回`Exists=false, Path=null`；`0x80070057`、`0x80004005`、`0x80070005`、`0x80070006`、`0x8007052E`、`0x80070520`、`0x80070522`、raw2/3、低16位2/3伪装与其他HRESULT统一fail closed；所有失败非null pointer释放；S_OK有效路径继续retained handle/reparse/final path/DirectoryIdentity/Same-Descendant检查。只有注册缺失与两个完整not-found HRESULT允许继续；禁止E_INVALIDARG未注册、HRESULT_CODE、facility mask、rawWin32/NTSTATUS/重编号。Cloud Files原始`0xC000CF13`与转换`0xD000CF13`规则保持不变。环境变量、cwd、PATH、CLI/用户roots不影响分类。
- production logger唯一`ProtectedJsonLinesLoggerProvider`；ProgramData `Logs\v1`三模式目录与文件SYSTEM owner、精确三ACE protected DACL，普通Users无读。每进程filename、十四字段JSONL、LF、无BOM、4096-byte行、sequence/redaction、每事件flush、8MiB/UTC-day rotation、14days、每模式32、总量256MiB及安全artifact规则全部测试通过。
- `FSL_E_PROTECTED_LOGGER_UNAVAILABLE`固定对象与consent-broker exit28、已有副作用先Cleanup且exit27优先、合法response precedence、service启动/运行中失败和recovery-once exit15全部通过。production composition静态确认无in-memory/always-ready、empty/always-not-sync、Console/Debug/Null/test logger或user-writable provider。
- 恢复记录只包含允许的数据类别，不包含普通历史、访问历史、文件/目录内容或长期分析数据。
- UI/Broker 异常退出后按恢复记录处理；恢复完成后尽快删除记录。
- 重启、注销和新会话后只清理旧 ACE，不恢复旧任务或剩余时间。
- 机器范围恢复记录只允许 LocalSystem 和提升后的同账户 Broker 访问，普通权限 UI 无直接访问权。
- 重启/登录测试证明测试用户首次访问目标前已完成既定遗留扫描和清理。
- 自动启动服务未就绪或清理失败时保持恢复阻断状态，不报告成功。
- 服务内部名、Display Name、Description、账户、启动类型、服务 SID、入口和 binPath 与 `D-024` 完全一致。
- 恢复记录与 ACL 不一致时停止自动删除；清理失败保留记录并诊断；不覆盖 DACL。
- 全部测试只修改临时目录。
- `D-027` 最低协议验收矩阵全部通过：正确请求、command 大小写/未知、重复/多余/缺失/null、Guid、日期、replay、过期、Session mismatch、payload 类型、长度/UTF-8/尾随数据、duration 类型/范围、禁止输入、UI RemoveLock、跨账户 status、CreateLock 幂等/冲突、错误脱敏和 response null 不变量。
- CP4 补充矩阵全部通过：正常/畸形握手；CLI/request/session/PID/Account/Logon/Session 绑定；identity unavailable；nonce、connectionId、bindingProof；协议顺序；并发 replay、lease/TTL、owner 崩溃、Abandoned、RolledBack、RecoveryRequired、普通用户 Replay 目录拒绝和并发过期清理唯一所有者。
- Broker/Service 只从 `%ProgramFiles%\FolderSessionLock` 注册；安装目录 ACL 为 `SYSTEM: FullControl`、`Administrators: FullControl`、`Users: ReadAndExecute`，不为 `Authenticated Users` 增加写权限。
- 特权集成验证只在 `FSL-STAGE4-VM`、快照 `FolderSessionLock-Stage4-Clean` 执行。非该机器的服务、LocalSystem、登录前、UAC、注销、重启、安装 ACL 和签名场景必须为 `BLOCKED`，不得标记通过。
- VM 测试账户为 `FSL-Standard` 与 `FSL-Admin`；凭据只由人工输入，不进入日志或证据。
- VM 测试签名验证有效签名、篡改失败、未签名拒绝和允许证书指纹；不生成、保存或提交生产私钥。
- `docs\evidence\stage-4\<RunId>\` 包含 `D-026` 规定的全部文件；`manifest.json` 字段和实际证据一致；`TASKS.md`、`DEVLOG.md` 引用 RunId。
- 每轮服务/UAC/注销/重启测试后，目标 ACL 已恢复、恢复记录已删除、服务状态已清理、临时目录可访问且可删除；任何未知状态阻止完成。
- `recovery-once` 参数错误在 D-023/枚举/ACL 前返回 exit 2 与 `FSL_E_INVALID_ARGUMENTS`；受保护路径失败返回 10；目录枚举不完整返回 11；总条目 4097 或规范记录 1025 返回 12 且零 ACL 写入；任一记录/构件阻塞返回 13；纯取消且临界区安全完成返回 14；无法映射的顶层内部故障返回 15；无记录和全部安全清理均返回 0。
- exit code 优先级测试精确覆盖 `InvalidArguments → ProtectedStorageSecurityFailure → RecoveryEnumerationFailure → RecoveryRecordLimitExceeded → RecoveryBlocked → Cancelled → InternalFailure → Success`；scheduler error 不覆盖记录主错误或成功结果。
- Records 只顶层枚举，不递归、不跟随 reparse、不边枚举边清理。规范 `.fslr` 文件名满足小写 Guid D 正则并按完整文件名 `StringComparer.Ordinal` 升序；文件名/payload id mismatch 返回 `FSL_E_RECOVERY_RECORD_ID_MISMATCH` 并继续。
- `.bak` 与 `.tmp-*` 在存在同 id `.fslr` 时，只有 filename、普通文件、non-reparse、links=1、SYSTEM owner、精确文件 DACL、同 Records 目录全部通过才 auxiliary；否则 `FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID`。孤立时分别返回 `FSL_E_RECOVERY_BACKUP_ORPHANED`、`FSL_E_RECOVERY_TEMP_ORPHANED`。全部保留、不自动删除/重命名/提交。
- 完整枚举后的单记录打开、读取、长度、安全信息、DPAPI、更新或删除失败作为记录级失败继续遍历；目录级 I/O 只有无法证明枚举完整时使用 `FSL_E_RECOVERY_DIRECTORY_*`。
- 多记录处理按稳定顺序继续；`CleanupPending` 后取消不得中断 ACL 临界区。每条记录恰好为 Cleaned、AlreadyClean、Failed、RecoveryRequired、Skipped；AlreadyClean 必须证明目录/ACL 安全并删除记录。
- 结构化摘要包含 D-022.10 十二个精确字段，十个计数范围 0..4096，两个计数不变量通过；remaining 为扫描结束仍存在的规范 `.fslr`。任一失败、RecoveryRequired、Skipped、invalid、remaining、D-023/枚举/上限/readiness 失败均 blocking=true。
- `recovery-service` 状态机、SCM 状态映射、StartPending checkpoint、一次启动扫描、无周期扫描、RecoveryBlocked CreateLock 拒绝、Stop/取消/临界区和 wait hint 测试与 D-024.2 一致。
- `RecoveryReadinessSnapshot` schema=1精确十二字段；缺失/损坏/不支持/不可读/stale/非Ready/blocking/未完成/remaining/primary error全部fail closed。CreateLock gate必须在路径与ACL前执行，失败精确返回`FSL_E_RECOVERY_BLOCKING`固定错误对象。
- `IProtectedPathSecurityVerifier`、四种 PathKind、request/result null 不变量、ExpectedPath 组合根来源与二十步执行顺序测试通过。生产 verifier 缺失返回 `FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED`；测试 fake 必须显式注入；仓库无 AllowAll verifier。
- `WindowsProtectedPathSecurityVerifier` 的 handle final path、OPEN_REPARSE_POINT、本地固定 NTFS、FILE_ID_128 前后复核、owner、DACL present/null、显式 ACE、protected inheritance、普通用户无高风险权限及 D-023 精确错误码矩阵必须通过。
- InstallDirectory 仅允许 SYSTEM/TrustedInstaller owner；Recovery/Replay 仅 SYSTEM owner。安装目录、Recovery/Replay、service SID ACL 创建与验证、普通用户替换/删除拒绝、ACL/owner 篡改和 TOCTOU 真实测试只在 `FSL-STAGE4-VM` 执行；`AGREELIN` 不得记录为通过。
- `.fslr`、`.tmp-*`、`.bak` 的 SYSTEM owner 正例通过；当前 Account、Administrators、service SID、未知 owner 均拒绝。DACL missing/null/unprotected/inherited、少/多 ACE、Users/Authenticated Users/Everyone、Deny/callback/object/unknown、mask 非 `0x001F01FF`、AceFlags 非 0 均拒绝。
- `IRecoveryRecordFileSecurity` 只接受 SafeFileHandle；writer 必须证明 payload 写入前已同 handle 设置并回读 SYSTEM owner与精确 DACL；owner/DACL 设置失败零 payload；service SID解析失败、SeRestorePrivilege启用失败、revert失败、temp安全失败/清理失败均返回 D-022.11 固定错误，revert failure 阻止后续写入和 CreateLock。
- 新建保持 temp/directory handles 并用 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)`、`FILE_RENAME_INFORMATION`、flags=0、`RootDirectory=recordsDirectoryHandle` 和相对简单叶名；目标存在 `FSL_E_RECOVERY_FILE_ALREADY_EXISTS`。更新始终保持 old/temp/directory handles，flags 精确为 `0x00000003`；不支持/失败无 fallback，v1 正常更新不创建 `.bak`。测试必须证明 production 不调用 class 10、SetFileInformationByHandle class 22/class 3 或绝对目标。
- post-commit 验证 temp identity、links=1、SYSTEM owner、精确 DACL、完整 payload、recordId/taskId/state/摘要、唯一目录映射和 Records identity；任一失败 `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`、UnrecoverableError、任务/Replay RecoveryRequired、保留文件。
- canonical 删除使用同一验证 handle FileDispositionInfoEx DELETE|POSIX；disposition 成功后关闭该 handle，再通过 retained Records directory handle确认 canonical 名称消失并复核目录 identity。不支持/调用失败使用专用错误；名称仍存在、枚举失败、identity 变化或无法证明关闭/删除时进入 RecoveryRequired。无 File.Delete/DeleteFileW、路径重试、重新打开后删除或删除 replacement。
- temp 提交前失败只通过同一 tempHandle 删除；清理失败主错误为 `FSL_E_RECOVERY_TEMP_CLEANUP_FAILED` 并 blocking，原错误仅受保护诊断。
- 并发/TOCTOU 测试覆盖受保护 `Global\FolderSessionLock.RecoveryStore.v1` 单一 writer、验证后提交/删除前名称替换、FILE_ID变化、Records identity变化、auxiliary安全不匹配、原/替换/临时文件均保持可证明状态。
- 静态扫描确认 Recovery store 产品代码无 File.Replace、ReplaceFileW、File.Move/MoveFileW/MoveFileExW、File.Delete/DeleteFileW、File.SetAccessControl/FileInfo.SetAccessControl、SetNamedSecurityInfo，且无关闭验证句柄后按路径修改模式。
- reviewer `PASS`。

## 8. 阶段 5

- UI 展示 Broker 确认的规范化路径。
- ACL 和 IPC 不阻塞 UI 线程。
- 快速重复点击不创建重复任务。
- 后置验证前不显示“已锁定”。
- 危险路径不可绕过；UAC 取消错误清晰。
- ViewModel 测试通过。
- 临时目录完成锁定、倒计时、到期和恢复。
- UI 关闭行为符合架构。
- reviewer `PASS`。

## 9. 阶段 6

- 开始前已有用户明确批准；无批准则阶段停止。
- 阶段 1 至阶段 5 不存在 Audit File System、SACL 或 Security 日志依赖。
- 只添加并移除目标目录和 Logon SID 的精确 SACL ACE。
- 不整体替换 SACL，不修改无关审计策略。
- `4656` Failure 产生尽力而为提示；`4663` 不作为失败事件。
- 事件去重限流；解析错误不崩溃；通知可降级。
- Unlock 后不再对任务提示。
- 审计不可用时核心锁定仍可用。
- reviewer `PASS`。

## 10. 阶段 7

- 自动测试全部通过。
- Explorer、CMD、PowerShell、第三方程序和附件矩阵均有记录。
- 所有临时目录 ACL 恢复并可删除。
- 无真实用户目录测试；无父目录 ACE；无 SYSTEM、Administrators、TrustedInstaller 修改。
- 无任意 IPC 命令；无用户内容日志。
- TOCTOU、ACL 漂移、Broker 崩溃、UAC 拒绝和通知洪泛均有结果。
- 生产 Broker 签名有效。
- Broker 和托管恢复模式的自动启动服务位于管理员保护目录；普通用户不能替换或修改 Broker。
- IPC 仅本机、DACL 最小、客户端身份验证和防重放通过。
- 不存在任意命令、脚本、PowerShell、cmd 或任意 ACL 描述符接口。
- 任一 `FolderSessionLock/docs/SECURITY.md` 发布阻断条件存在时验收失败。
- reviewer 无 `BLOCKER` 或 `HIGH`，输出 `PASS`。
- `FolderSessionLock/README.md` 和 `FolderSessionLock/docs/SECURITY.md` 记录已知限制。

## 11. 停止条件

- 单阶段 6 轮修复上限。
- 同一问题连续两次修复失败。
- ACL 无法安全恢复。
- 需要审计策略变更但无明确批准。
- 需求只能通过内核驱动实现。
- 存在必须由用户选择的设计冲突。
