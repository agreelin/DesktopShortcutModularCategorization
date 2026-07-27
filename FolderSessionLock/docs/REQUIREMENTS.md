# Folder Session Lock 需求规格

状态：阶段 3 与阶段 4 CP1–CP9 已完成；CP10 工具实现的最近独立验证为 799/799、0 failed、0 skipped、Release 0 warning/0 error。D-031 已将当前交付范围修订为本地单用户管理员；CP10 的同账户 UAC、SCM、LocalSystem、ProgramData/ProgramFiles ACL、真实 service SID ACL、恢复、重启/注销与 D-026 schema v2 证据尚未完成，阶段 4 不得完成。审计功能尚未实现。

## 1. 产品目标

Folder Session Lock 是 Windows 当前交互登录会话中的用户态自我约束工具。用户选择本机文件夹并设置时长；应用验证安全条件后添加会话级 ACL 拒绝规则；到期后自动移除该规则。

当前支持部署范围固定为 `LOCAL_SINGLE_USER_ADMINISTRATOR_ONLY`：仅由当前本地 Administrators 成员在本机使用，可使用同账户 UAC consent。多用户、跨账户 elevation、企业部署、远程使用、敌对同用户防护和公开分发不属于当前范围。

产品不保存普通业务任务历史。为保证正常到期解除、异常退出恢复和新会话遗留 ACL 清理，架构保存最小、受保护、事务化恢复记录；恢复完成后尽快删除。

所有产品 solution、源码、测试和项目文档必须位于仓库子目录 `FolderSessionLock/`。不得替换或修改仓库现有无关项目。八份阶段 0 项目文档已经迁入最终路径，仓库根不得保留同名副本。

## 2. 术语

- Account SID：账户稳定身份；跨登录会话不变。
- Logon SID：访问令牌组中带登录标志的 `S-1-5-5-X-Y`；标识一次登录会话。
- 任务：规范化目录、目录稳定身份、Logon SID、时长、截止点与精确 ACE 元组的组合。
- 恢复记录：在已确认 DACL 稳定性信任假设下，支持精确 ACE 元组匹配、ACL 校验和恢复判断的最小数据；它不能密码学证明 ACE 来源；成功解锁后删除。
- Broker：唯一真实 ACL 写入主体；包含同账户 consent elevation 的交互控制模式，以及由自动启动 Windows 服务托管的恢复专用模式。

## 3. 功能需求

### 3.1 用户流程

- `FR-001` 用户可选择一个本机目录。
- `FR-002` UI 必须显示 Broker 最终确认的规范化绝对路径。
- `FR-003` 用户可设置有效限制时长；production `LockDurationPolicy` 包含式范围固定为 60000..86400000 milliseconds（60 seconds..24 hours），不得由 UI、调试配置或隐藏默认值扩大或改写。Broker 必须独立验证；越界返回 `FSL_E_DURATION_OUT_OF_RANGE`、field `payload.durationMilliseconds`、retryable false。
- `FR-004` 创建任务前必须验证路径、文件系统、目录身份、权限和恢复能力。
- `FR-005` 需要提升时必须显示 UAC 步骤；用户拒绝 UAC 时不得显示“已锁定”。
- `FR-006` 用户可查看活动任务的路径、开始时间、到期时间、剩余时间、状态和错误。
- `FR-007` 到期时 Broker 必须执行一次幂等解锁并验证恢复。
- `FR-008` 支持多个互不重叠目录任务。
- `FR-009` UI 关闭或崩溃后，活动任务由 Broker 继续计时；不得因关闭 UI 自动解除任务。
- `FR-010` 不同登录会话不得续跑旧任务，只允许清理旧 ACE。
- `FR-010A` v1 只支持同一账户、同一交互会话的 consent elevation；提升 Broker 的 Account SID、Logon SID 和 Session ID 必须与 UI 会话一致。身份不一致时安全失败并显示“不支持跨账户提升”。
- `FR-010B` 必须提供 Broker 的受信启动恢复模式，由自动启动 Windows 服务以 LocalSystem 身份在系统启动期间、交互登录前执行。旧任务视为失效；该模式只验证并清理旧 Logon SID ACE，不创建限制、不恢复任务或剩余时间。

### 3.2 路径准入

v1 只接受本机固定磁盘、NTFS 文件系统、普通目录和可安全规范化并验证身份的路径。

必须拒绝：

- 空路径、不存在路径、文件路径。
- 磁盘根目录。
- 用户配置文件根、Desktop、Documents、Downloads。
- OneDrive 和已识别同步根。
- Windows、Program Files、ProgramData、系统目录。
- 仓库根及其后代、应用安装目录及其后代。
- UNC、远程卷、映射网络盘、可移动卷、FAT、exFAT 和其他未经验证文件系统。
- 目标或任一祖先组件包含 reparse point 的路径，包括符号链接、junction、mount point。
- 无法取得稳定目录句柄、卷标识、目录文件标识或 ACL 恢复权限的目录。
- UI 展示路径与 Broker 句柄解析的最终路径不一致的目录。

### 3.3 重复与嵌套

- `FR-011` 同一卷标识和目录文件标识已有活动任务时，拒绝新任务。
- `FR-012` 活动任务之间存在祖先或后代关系时，拒绝后发任务。
- `FR-013` 不以字符串前缀判断父子关系；按规范化目录组件和稳定目录身份判断。
- `FR-014` 重复请求不得延长任务、增加第二条 ACE 或重复执行 ACL 修改。

### 3.4 ACL 行为

- `FR-015` 锁定主体必须为发起会话的 Logon SID，不得改用 Account SID。
- `FR-016` 只添加一条显式 `Deny` ACE；目标仅为选定目录；使用目录与对象继承；不修改父目录。
- `FR-017` 禁止 `FullControl`；拒绝权限矩阵见 `FolderSessionLock/docs/SECURITY.md`。
- `FR-018` 必须保留 `ReadPermissions`、`ChangePermissions`、`TakeOwnership`、`Synchronize`，用于检查和恢复。
- `FR-019` 不整体替换 DACL，不关闭继承，不修改原有或无关 ACE。
- `FR-020` 若锁定前存在完全相同的显式 ACE，拒绝任务。
- `FR-021` 解锁只允许在 DACL 稳定性信任假设成立时，移除与任务记录的 SID、权限掩码、类型、继承、传播和目录身份全部一致且数量恰好为一的 ACE。
- `FR-022` ACE 数量大于一、目录身份变化、ACL 所有权不明、恢复记录与 ACL 校验不一致，或存在外部删除后重建同元组 ACE 的证据时，不得猜测删除；进入人工恢复状态。NTFS ACE 无来源标签，外部重建完全相同 ACE 时无法自动区分来源。
- `FR-023` 锁定和解锁必须幂等，并包含写前记录、后置验证和失败回滚。
- `FR-023A` DACL 读取、添加、后置验证和移除必须使用持续持有的同一目录句柄。实现 API 无法基于该句柄完成安全描述符操作时，操作必须拒绝。

### 3.5 生命周期与恢复

- `FR-024` Broker 是任务、计时和真实 ACL 写入的唯一所有者；UI 是客户端。启动恢复服务只托管同一受信 Broker 的恢复专用模式。
- `FR-025` 正常到期、正常 Broker 退出、注销或系统结束会话通知时，Broker 尝试清理全部活动 ACE。
- `FR-025A` scheduler 是否成功都不得阻止 Cleanup 启动或继续；Cleanup 必须按稳定任务顺序遍历全部适用任务，单任务失败不得提前终止剩余任务。
- `FR-025B` Cleanup 对外主错误固定为稳定处理顺序中的第一个 task error；后续 task error 只进入受保护内部日志和诊断汇总，不得替换主错误。异步完成顺序不得改变主错误。
- `FR-025C` scheduler error 仅进入受保护内部日志，不得覆盖 Cleanup first-task error，也不得把全部成功的 Cleanup 伪造为失败。固定结果矩阵为：scheduler success + Cleanup success 返回 Cleanup success count；scheduler success + Cleanup failure 返回 Cleanup first-task error；scheduler failure + Cleanup success 返回 Cleanup success count；scheduler failure + Cleanup failure 返回 Cleanup first-task error。
- `FR-025D` Cleanup 进入 `RecoveryRequired`、ACL 状态未知或恢复失败时，必须按对应 Cleanup task error 对外返回，不得被 scheduler error 替换，不得声称清理完成。
- `FR-025E` 受保护内部日志必须保留 scheduler error code、scheduler exception 的脱敏诊断、第一个及其余 Cleanup task errors、`taskId` 或受保护关联标识、Cleanup 是否完整遍历及是否存在 `RecoveryRequired`。`LockTaskScheduler` 生产循环遇到未预期的非取消异常时，唯一内部错误为 code `lock_task.scheduler.loop.exception`、message `The lock task scheduler loop terminated unexpectedly.`；protected logger 固定 `component = Scheduler`、`level = Error`。预期 token 已取消的 `OperationCanceledException` 不记录；该合同不得用于 lifecycle stop、Cleanup failure、task 状态转换、已有更具体错误或 logger failure。公开响应不得包含该错误，也不得包含异常 message、`ToString()`、stack、内部类型名、路径、SID、HRESULT、Win32 message、SDDL、恢复记录路径、凭据或令牌。
- `FR-025F` administrative Cleanup 调用 `RemoveLockAsync` 抛异常时，内部错误精确为 code `lock_task.administrative_cleanup.exception`、message `The administrative cleanup ended without a confirmed result.`、category `UnrecoverableError`，任务进入 `RecoveryRequired`。ACE 已移除但 `Completed` 状态记录失败时，内部错误精确为 code `lock_task.administrative_cleanup.state_update_failed`、message `The lock was removed but its completed state could not be recorded.`、category `UnrecoverableError`，任务进入 `RecoveryRequired`。
- `FR-026` 突然断电或 Broker 崩溃后，同一登录会话内重新启动的控制进程必须读取最小恢复记录，恢复清理责任。
- `FR-027` 新 Logon SID 或系统重启后，Broker 恢复模式只清理旧 ACE；不得恢复旧任务或剩余限制时间。
- `FR-028` 清理需要 UAC 且用户拒绝时，必须明确报告残留 ACE，不得删除恢复记录。
- `FR-029` 成功移除 ACE 并验证后，必须删除恢复记录，不保留普通任务历史。
- `FR-029A` 自动启动恢复服务与 Broker 必须位于 `%ProgramFiles%\FolderSessionLock`。恢复记录固定存放于 `%ProgramData%\FolderSessionLock\Recovery\Records`，目录所有者为 `NT AUTHORITY\SYSTEM`，受保护显式 DACL 只允许 `NT AUTHORITY\SYSTEM`、`BUILTIN\Administrators` 和 `NT SERVICE\FolderSessionLockRecovery` 完全控制；普通 UI 和普通用户不得直接访问。记录使用 DPAPI `DataProtectionScope.LocalMachine` 和固定 purpose entropy 保护。清理失败时保留记录并给出明确诊断，不得覆盖整个 DACL。
- `FR-029B` 恢复服务必须在交互登录前启动并完成遗留记录扫描。服务未就绪或清理失败时不得报告恢复成功，必须保持恢复阻断状态；阶段 4/7 必须用重启与登录测试证明测试用户首次访问目标前已完成既定清理。
- `FR-029C` 恢复记录容器扩展名必须为 `.fslr`，头部精确为 ASCII `FSLR`、little-endian `ContainerVersion`、`Flags`、`ProtectedPayloadLength` 和 DPAPI payload；当前 `containerVersion = 1`、`schemaVersion = 1`、`writerVersion = 1.0`。v1 遇到其他版本、未知必需字段、未知状态或字段类型错误必须返回 `RecoveryRecordUnsupported`。
- `FR-029D` 每个活动任务一个 `<RecordId>.fslr`，临时文件为 `<RecordId>.tmp-<Guid>`；路径编译时固定。v1 正常 writer 不创建新的 `<RecordId>.bak`。新建和更新必须按 D-022.11 保持 Records 目录、temp/old canonical 文件持续句柄并使用 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)`；canonical 删除使用同一已验证 handle 的 FileDispositionInfoEx，成功后关闭该 handle，再由 retained directory handle 确认名称消失。禁止 ReplaceFileW、File.Replace、路径 move/delete 与 fallback。每次提交必须同句柄 flush、回读、安全和 payload 验证。
- `FR-029E` 状态必须精确为 `Prepared`、`Applied`、`CleanupPending`、`CleanupFailed`。`Prepared` 在任何 ACL 写入前原子提交；后置验证成功后写 `Applied`；清理前写 `CleanupPending`；无法安全清理时写 `CleanupFailed` 且保留记录。
- `FR-029F` 目录身份必须从锁定操作的同一持续目录句柄通过 `GetFileInformationByHandleEx(FileIdInfo)` 取得 UInt64 `FILE_ID_INFO.VolumeSerialNumber` 和完整 16-byte FILE_ID_128。`volumeSerialNumber` 精确为 16 位小写 hex；bytes 0..7/8..15 分别按 little-endian UInt64 编码为十进制 `fileIdLow`/`fileIdHigh`；恢复时重建并比较完整 16 bytes。禁止 32-bit volume serial、路径重开句柄或 `BY_HANDLE_FILE_INFORMATION` 混用。
- `FR-029G` `aceFingerprintSha256` 必须对写后从同一持续句柄重新读取并唯一定位的精确 ACE bytes 使用 D-022 `FSLACE` v1 binary wrapper；`baselineDaclSha256` 和 `postApplyDaclSha256` 必须对原始 ACE 顺序、有效 ACE bytes、ACL revision 和掩码 `0x1504` 的 DACL control 使用 `FSLDACL` v1 wrapper。禁止 SDDL、整个 SECURITY_DESCRIPTOR、owner/group/SACL、对象序列化、排序 ACE 和 ACL 未使用尾部字节。
- `FR-029H` baseline digest 必须在 `Prepared` 原子提交前取得；postApply digest 和 ACE fingerprint 必须在写后从 OS 重新读取，不得由 baseline 加本地 ACE 推导。三个摘要只是状态证据，不能替代目录身份、ACE 元组、主体、记录状态和调用模式验证。
- `FR-029I` `.fslr` v1 header 固定 12 bytes：Magic `FSLR`、UInt16 version=1、UInt16 flags=0、UInt32 protected length，全部 little-endian。DPAPI blob 长度必须为 1..262144，文件总长严格等于 `12 + length`；短头/短 payload 为 truncated，任何尾随字节为 trailing data，非零 flags/未知版本/错误 magic 分别返回 D-022 固定错误，校验前不得调用 DPAPI或按恶意长度分配。
- `FR-029J` 解密明文必须为 <=131072 bytes 的 UTF-8 without BOM 单一 JSON object，精确包含全部 25 字段；大小写敏感，重复/多余/缺失/宽松数字/日期/Guid/SID/hash/enum/flags 全部按 D-022 固定错误拒绝。所有字段始终存在，只有 `postApplyDaclSha256`、`lastErrorCode`、`lastErrorMessage` 按状态允许 null。
- `FR-029K` 四状态矩阵固定为：Prepared postApply/error=null、count=0；Applied postApply非 null/error=null、count=0；CleanupPending postApply非 null/error=null、count>=1；CleanupFailed postApply/error均非 null、count>=1。Prepared 保存预期 ACE fingerprint，但 postApply 必须 null；实际 fingerprint 和 postApply 必须写后从 OS 重读。
- `FR-029L` 恢复执行必须先完成 D-023，再完整顶层枚举 `%ProgramData%\FolderSessionLock\Recovery\Records`、分类、检查总条目 4096/规范 `.fslr` 1024 上限、按完整小写文件名 `StringComparer.Ordinal` 升序，之后才允许清理。禁止递归、跟随 reparse、边枚举边修改 ACL。
- `FR-029M` 规范活动文件仅为小写 Guid D `<recordId>.fslr`。同 id 的合法 `.bak`/`.tmp-<Guid>` 仅计 auxiliary；无对应 `.fslr` 时分别为 `FSL_E_RECOVERY_BACKUP_ORPHANED`、`FSL_E_RECOVERY_TEMP_ORPHANED`。非法文件、子目录、reparse 或未知构件统一 `FSL_E_RECOVERY_ARTIFACT_INVALID`；保留构件、继续分类并设置 blocking。
- `FR-029N` 单条记录失败后继续稳定遍历。`CleanupPending` 原子提交后进入 ACL 临界区，必须到达删除成功、`CleanupFailed` 或 `RecoveryRequired` 才响应取消。记录结果互斥为 `Cleaned`、`AlreadyClean`、`Failed`、`RecoveryRequired`、`Skipped`；未找到 ACE 不能单独证明 AlreadyClean。
- `FR-029O` 恢复摘要字段精确为 `canonicalRecordCount`、`processedRecordCount`、`cleanedCount`、`alreadyCleanCount`、`failedCount`、`recoveryRequiredCount`、`skippedCount`、`auxiliaryArtifactCount`、`invalidArtifactCount`、`remainingRecordCount`、`recoveryBlocking`、`primaryErrorCode`；计数为 Int32 0..4096，并满足 D-022.10 两个计数不变量。任何失败、RecoveryRequired、跳过、非法构件、剩余记录、D-023/枚举/上限/readiness 失败均 blocking=true。
- `FR-029P` `recovery-once` 唯一退出码为 0 Success、2 InvalidArguments、10 ProtectedStorageSecurityFailure、11 RecoveryEnumerationFailure、12 RecoveryRecordLimitExceeded、13 RecoveryBlocked、14 Cancelled、15 InternalFailure，优先级按上述顺序再到 Success。不得返回 Win32/HRESULT/NTSTATUS/记录错误码；详细错误使用稳定 `FSL_E_*`。无记录与全部清理成功均返回 0。
- `FR-029Q` `recovery-service` 只执行一次启动扫描，之后持续托管 readiness，不周期扫描。内部状态固定为 `StartPending → Preflight → Scanning → Ready/RecoveryBlocked → Stopping → Stopped`；Ready 与 RecoveryBlocked 均为 SCM Running。Stop 不得中断 ACL 临界区，未开始记录计 Skipped。
- `FR-029R` readiness schema 固定为 1，类型与字段精确采用 D-024.2 并由 D-030 扩展为十二字段受保护机器范围 snapshot。snapshot 缺失、无效、stale、非 Ready、blocking、未完成扫描、有剩余记录或主错误时均视为 blocking。
- `FR-029S` CreateLock 必须在路径与 ACL 写入前通过 readiness gate；不满足 Ready 唯一成功条件时返回 `FSL_E_RECOVERY_BLOCKING` / `Folder restrictions cannot be created until recovery is complete.` / retryable true / field null。
- `FR-029T` D-023 `IProtectedPathSecurityVerifier` 接口、顺序、owner、DACL、显式 ACE、继承保护与错误码以 D-023.1 为唯一合同。ExpectedPath 只由受信组合根提供；生产 verifier 缺失时必须返回 `FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED`，不得进入 Ready、执行生产 recovery 或 CreateLock；测试只允许显式 fake，禁止 AllowAll verifier。
- `FR-029U` CanonicalRecord `.fslr`、TemporaryRecord `.tmp-*`、BackupRecord `.bak` 唯一 owner 均为 SYSTEM `S-1-5-18`。文件 DACL present/non-null/protected、ACL revision 2、无继承，精确三个显式 Allow ACE：SYSTEM、Administrators、固定服务 SID，mask `0x001F01FF`、flags 0、按该顺序；禁止额外/Deny/object/callback/conditional/unknown ACE。
- `FR-029V` `RecoveryRecordFileKind`、`RecoveryRecordFileIdentity`、`RecoveryRecordFileSecuritySnapshot`、`IRecoveryRecordFileSecurity` 签名以 D-022.11 为准，只接受 SafeFileHandle。ApplyAndVerify 只用于未提交 temp；canonical/bak 只 Verify，reader 不修复。
- `FR-029W` consent writer 必须同 temp handle 设置 SYSTEM owner 与精确 DACL。owner 非 SYSTEM 时仅临时启用 `SeRestorePrivilege`，finally 恢复；禁止 SeTakeOwnershipPrivilege。无法启用/恢复分别返回 `FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE`、`FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED`，revert 失败停止后续记录写入和 CreateLock。
- `FR-029X` 所有 writer 必须持有 `Global\FolderSessionLock.RecoveryStore.v1` 受保护 mutex。临时文件通过 Records 目录句柄、简单叶名称、CREATE_NEW、ShareMode 0、OPEN_REPARSE_POINT、WRITE_THROUGH 创建；payload 前完成 identity/links/final path/owner/DACL 验证，提交前后保持句柄并复核。
- `FR-029Y` 新建与更新只允许 tempHandle 调用 user-mode `NtSetInformationFile`，information class 精确为 `FileRenameInformationEx = 65`，结构为 `FILE_RENAME_INFORMATION`，`RootDirectory` 为持续 Records 目录句柄，目标为相对简单叶名；新建 flags=0，更新 flags=`0x00000003` 且同时保持 old canonical/temp/directory handles。production 禁止 class 10、SetFileInformationByHandle class 22/class 3、绝对目标和任何 fallback。不支持/失败继续使用 D-022.11 专用错误。canonical 删除在已验证同一 handle 上执行 FileDispositionInfoEx DELETE|POSIX，成功后关闭该 handle，再用 retained directory handle 确认名称消失并复核目录 identity；temp cleanup 合同保持不变。
- `FR-029Z` 配对 `.bak`/`.tmp-*` 只有 filename、普通文件、非 reparse、links=1、SYSTEM owner、精确 DACL、同 Records 目录全部通过才 auxiliary；否则 `FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID`、invalid++、blocking=true，不删除或修复。
- `FR-029AA` D-022.11 规定的 21 个 `FSL_E_RECOVERY_FILE_*`/artifact 错误码、固定 messages、retryable=false、field=null 与错误优先级必须逐字实现；原子提交后验证失败固定为 `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`。底层代码/路径/SID/DACL/FILE_ID 只进受保护日志。
- `FR-029AB` Recovery store 产品代码禁止 File.Replace/ReplaceFileW、File.Move/MoveFile*、File.Delete/DeleteFileW、File.SetAccessControl/FileInfo.SetAccessControl、SetNamedSecurityInfo 以及关闭验证句柄后按路径修改。

恢复记录只允许包含：任务 ID、规范化目录路径、创建规则时的登录会话标识、精确 ACE 描述、必要 ACL 校验信息、创建时间、计划到期时间、清理状态，以及恢复事务所必需的版本、目录身份和完整性信息。

恢复记录禁止包含：普通任务历史、用户访问历史、文件内容、目录内容、长期行为分析数据和已完成任务的无必要记录。

### 3.6 Broker 与 IPC

- `FR-030` WPF UI 默认普通权限；真实 ACL 操作只在 Broker 内执行，包括交互控制模式和恢复专用模式。
- `FR-031` Broker 只接受 `ValidatePath`、`CreateLock`、`RemoveLock`、`GetStatus`。
- `FR-032` 禁止任意命令、shell、PowerShell、cmd、脚本和任意 ACL 描述符输入。
- `FR-033` Named Pipe 只允许本机连接；DACL 只允许目标 Logon SID 和 Broker 自身。
- `FR-034` Broker 必须验证 Account SID、Logon SID、Windows Session ID、连接进程身份、一次性握手值和请求重放状态。
- `FR-035` Broker 必须独立重复全部路径和权限验证，不信任 UI 结果。
- `FR-035A` Broker 令牌与 UI Account SID 或 Logon SID 不一致时必须拒绝；v1 不通过另一管理员账户代执行目标会话 ACL 操作。
- `FR-035B` 当前 D-031 本地 Release 可显式 unsigned，但 Broker 必须安装在管理员保护目录，普通用户不得替换或修改。unsigned 状态必须如实记录为 `NotSigned`/null signer；安装目录普通用户可写或 Broker 可替换始终阻断。公开/企业分发的真实签名门需要未来决定。
- `FR-035C` IPC 必须只公开强类型最小 ACL 接口；禁止任意命令、脚本、PowerShell、cmd 和调用方提供的任意 ACL 描述符。
- `FR-035D` 服务名必须为 `FolderSessionLockRecovery`，Display Name 为 `Folder Session Lock Recovery Service`，Description 为 `Removes verified Folder Session Lock ACL entries left by previous Windows logon sessions.`；服务账户为 `LocalSystem`，启动类型为 `Automatic`，`DelayedAutoStart = false`，启用服务 SID `NT SERVICE\FolderSessionLockRecovery`。
- `FR-035E` 服务入口固定为 `FolderSessionLock.Broker.exe --mode recovery-service`；隔离 VM 一次性诊断入口固定为 `FolderSessionLock.Broker.exe --mode recovery-once`；交互 Broker 参数固定为 `--mode consent-broker --pipe-name FolderSessionLock.Broker.v1 --session-id <UInt32> --request-id <lowercase Guid D> --client-process-id <UInt32> --client-process-creation-filetime <UInt64 decimal>`。Account SID、Logon SID、用户名、角色、管理员标志或 Pipe SDDL 不得进入 CLI。未知参数、自定义恢复路径、任意 Pipe 名、任意 service name/binPath、任意 ACL 描述符和脚本参数必须安全失败。
- `FR-035F` 跨账户提升必须在 Pipe、Replay、恢复记录、路径和 ACL 操作前由 Broker bootstrap 拒绝，consent-broker exit 20，UI 最终错误为 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED` / `Cross-account elevation is not supported.` / retryable false / field null。已连接 Pipe 的 `FSL_E_ACCOUNT_SID_MISMATCH` 仍为 D-027 传输诊断，只在 UI elevation 边界转换；Logon SID、Session、PID、identity unavailable、Pipe access 与 unauthorized 错误禁止转换。
- `FR-035G` Named Pipe 固定为 `\\.\pipe\FolderSessionLock.Broker.v1`、byte mode、每连接一个请求/响应。每条消息使用 4-byte little-endian `UInt32` 长度前缀和严格 UTF-8 without BOM JSON，正文 1..65536 bytes；长度、读取、BOM、UTF-8、尾随数据或多消息违规必须拒绝。
- `FR-035H` 请求 envelope 必须精确包含 `protocolVersion`、`requestId`、`command`、`clientSessionId`、`sentAtUtc`、`payload`；响应必须精确包含 `protocolVersion`、`requestId`、`command`、`success`、`serverTimeUtc`、`result`、`error`。精确类型、格式、null 条件和错误对象以 `D-027` 为准。
- `FR-035I` JSON 解析必须大小写敏感、重复字段预检测、未知/多余/缺失字段拒绝、严格 integer/Guid/date/enum 类型，不接受注释、尾逗号、宽松数字或未允许 null。
- `FR-035J` requestId 必须为非空小写 Guid D，最近 10 分钟不可重用；sentAtUtc 必须为 7 位小数 UTC `Z` 且与服务端差不超过 120 秒；clientSessionId 必须与 OS 客户端 Session ID 一致。
- `FR-035K` 通用错误码、公开 message、retryable 和 field 语义必须与 `D-027` 一致。内部异常只返回 `FSL_E_INTERNAL` 和固定 message `The operation could not be completed.`，技术细节进入受保护日志。
- `FR-035L` `ValidatePath` payload 只能含 `path`；成功 result 精确返回 `normalizedPath`、`volumeRoot`、`volumeSerialNumber`、`fileIdHigh`、`fileIdLow`、`fileSystem`、`driveType`、`isReparsePoint`、`isAllowed`。`volumeSerialNumber` 为 16 位小写 hex；file ID high/low 按 D-022.1 的 FILE_ID_128 little-endian 映射为 UInt64 十进制 string。CreateLock 必须重新验证，不信任先前结果。
- `FR-035M` `CreateLock` payload 只能含 `taskId`、`path`、`durationMilliseconds`，严格映射现有领域值对象。只有 Prepared、ACL apply/post-verify、Applied 和 Active 全部成功后才能返回 D-027 精确 result。相同参数 task ID 幂等；不同参数冲突。
- `FR-035N` 普通 UI 禁止 `RemoveLock`。请求只能含 `taskId`、`recoveryRecordId`；客户端不得提供 intent。服务端按 OS 身份和启动模式映射 Expiration、Recovery 或 TestCleanup；v1 公开 IPC 不支持 AdministrativeCleanup 或 UserRequested。
- `FR-035O` `GetStatus` payload 固定含 `queryType` 和 `taskId`；支持 `ByTaskId` 与 `CurrentSession`。只返回同账户、同交互 Session 的内存任务；任务对象和脱敏 error 精确字段以 `D-027` 为准；不得描述为历史或审计日志。
- `FR-035P` 客户端提供 SID、ACL/SDDL/ACE、恢复/安装路径、服务/Pipe 名、命令/脚本/可执行路径、`LockRemovalIntent` 或清理模式时，即使值为空也返回 `FSL_E_FORBIDDEN_INPUT`。
- `FR-035Q` consent-broker 每连接必须严格执行 `ClientHello -> ServerHello -> CommandRequest -> CommandResponse -> close`；握手和命令帧均使用 D-027 固定 framing/UTF-8/schema；recovery-service 不接受普通 UI 公共 IPC。
- `FR-035R` ClientHello 必须精确九字段，包含 handshakeVersion 1、CLI 绑定 requestId/session、OS 比对 PID、32-byte Base64URL clientNonce 和 120-second sentAtUtc；ServerHello 必须精确九字段，成功 result 精确含 connectionId、32-byte serverNonce、30-second expiresUtc。
- `FR-035S` CommandRequest 必须精确八字段并封装六字段应用请求；CommandResponse 必须精确七字段并封装七字段应用响应。CLI、ClientHello、CommandRequest 外层与内层的 requestId、command、protocolVersion、session 必须按 D-027 完全绑定。
- `FR-035T` bindingProof 必须使用 D-027 固定 LF canonical string、SHA-256、Base64URL without padding 和恒定时间比较；成功后握手立即消费，不接受第二个 CommandRequest。
- `FR-035U` 服务端必须按 D-027 固定顺序从 OS 取得 Pipe 客户端 PID、存活/启动时间、进程 Session、模拟令牌 Account SID、Logon SID、TokenSessionId，并在 finally 恢复身份；不得信任客户端 SID或回退用户名。
- `FR-035V` 身份、握手、序列和绑定失败必须使用 D-027 固定错误码、message、retryable、field 和响应标识符规则，包括 `FSL_E_CLIENT_PROCESS_MISMATCH`、`FSL_E_CLIENT_IDENTITY_UNAVAILABLE`、`FSL_E_ACCOUNT_SID_MISMATCH`、`FSL_E_LOGON_SID_MISMATCH`、`FSL_E_SESSION_MISMATCH`。
- `FR-035W` Replay 必须跨并发 Broker 进程，以 `%ProgramData%\FolderSessionLock\Replay\v1` 受保护 Registry、固定 replay key、精确 JSON schema、`FileMode.CreateNew` 原子登记、受保护 `Global\FolderSessionLock.ReplayRegistry.v1` 互斥锁和所有权四元组实现。
- `FR-035X` Replay 状态、5/30/60-second timeout/lease、20-second renewal、5-minute execution limit、10-minute terminal retention、RecoveryRequired 无限保留、失败撤销、崩溃与 PID 重用处理必须逐字符合 D-027；相同 requestId 不得因业务 retryable 或 taskId 幂等而重用。
- `FR-035Y` Replay CreateNew 必须只在完整 ClientHello/CLI/time、OS 客户端 PID/令牌身份、Broker Account/Logon/Session 比较和命令权限全部通过后发生；所有 schema、版本、绑定、时间、PID、身份、Session 或授权失败绝不创建 Replay 文件。
- `FR-035Z` AwaitClientHello 首个有效 frame 非 ClientHello 只返回 HANDSHAKE_REQUIRED ServerHello failure；成功 ServerHello 后的状态顺序错误只返回 PROTOCOL_SEQUENCE_INVALID CommandResponse failure。六个握手/序列/Replay 错误的 retryable、field、frame、标识符回显/null 和 Replay 行为必须使用 D-027 唯一映射。
- `FR-035ZA` 普通 UI 必须在 UAC 前从自身当前进程令牌取得 TokenUser、唯一 `SE_GROUP_LOGON_ID` Logon SID 与 TokenSessionId，并取得 UInt32 PID 与 `GetProcessTimes` creation FILETIME UInt64。SID 只保存在 UI 内存；Broker 必须在创建 Pipe 前通过 PID+creation time 重开 UI 进程和 token，重新取得身份，禁止信任 CLI 或 UI SID 文本。
- `FR-035ZB` bootstrap PID/token/identity 不可用使用 consent-broker exit 21 → `FSL_E_CLIENT_IDENTITY_UNAVAILABLE`；creation FILETIME 不匹配使用 exit 22 → `FSL_E_CLIENT_PROCESS_MISMATCH`；Account SID 不同使用 exit 20。只有以上及 Session 检查全部成功后，Pipe DACL 才允许精确包含可信 UI Logon SID 与 Broker Account SID 的 ReadWrite + Synchronize，并继续设置 `PIPE_REJECT_REMOTE_CLIENTS`。
- `FR-035ZC` production Broker 路径只能由 `SHGetKnownFolderPath(FOLDERID_ProgramFiles)` 解析为 `<FOLDERID_ProgramFiles>\FolderSessionLock\FolderSessionLock.Broker.exe`。UAC 前必须通过 D-023 InstallDirectory、普通文件、non-reparse、final-path 与目录归属验证；失败返回 `FSL_E_BROKER_PATH_UNTRUSTED`。禁止环境变量、当前目录、`AppContext.BaseDirectory`、PATH、相对路径、仓库/bin、用户配置、CLI path 或 App Paths。
- `FR-035ZD` production UI 必须使用 `ShellExecuteExW`，固定 `runas`、已验证 Broker path/install directory、`SW_HIDE` 和 `SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI | SEE_MASK_UNICODE`；必须取得非空 process handle。`ERROR_CANCELLED` → `FSL_E_ELEVATION_CANCELLED`；其他失败或空 handle → `FSL_E_ELEVATION_LAUNCH_FAILED`。UAC 提示无应用级超时，不得用 Process.Start、ProcessStartInfo.Verb、shell、token/logon API、Task Scheduler 或临时服务替代。
- `FR-035ZE` Broker 创建 Pipe 后只等待一个客户端 15 seconds，超时 exit 24。UI 从 ShellExecuteExW 成功返回起最多 20 seconds 并发等待 Pipe 或 process exit；连接前仍存活且未连接时可 `TerminateProcess(..., 29)` 并等待最多 5 seconds。清理成功返回 `FSL_E_BROKER_CONNECT_TIMEOUT`；无法证明清理返回 `FSL_E_BROKER_PROCESS_CLEANUP_FAILED`。Pipe 一旦连接，UI 永远不得 TerminateProcess。
- `FR-035ZF` consent-broker 退出码关闭集合固定为 0、2、20、21、22、23、24、25、26、27、28、29，语义与优先级以 D-029 为准，不得返回 Win32/HRESULT/NTSTATUS/Exception.HResult 或应用错误序号。exit 2 唯一映射为 `FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / retryable false / field null；公开对象不得包含 CLI、参数、路径、命令行、Win32 或异常细节。应用 `success:false` 响应可 exit 0；Cleanup failure 优先 exit 27，response write failure在 Cleanup 成功时 exit 26；合法 CommandResponse 不得被后续退出码改写。
- `FR-035ZG` 每个 consent-broker 进程只允许一个 Pipe server instance、一个连接、一次四帧握手和一个应用命令/响应；响应后关闭 listener，禁止第二 Accept。ValidatePath、GetStatus、普通 UI RemoveLock 拒绝与 CreateLock 副作用前失败在响应送达后 exit 0；GetStatus 不启动长期 scheduler。
- `FR-035ZH` CreateLock 成功响应后 Pipe 关闭但 Broker 保持运行，由 scheduler 持有唯一 Active task；到期安全 Cleanup 后 exit 0，无法安全完成 exit 27。UI 响应前断开时，无副作用 exit 25；确定 Active lock 继续到期；未知副作用进入 RecoveryRequired。production composition 必须包含 D-029 列出的全部真实安全依赖，禁止 fake/AllowAll/in-memory/test/debug 依赖；缺少任一安全依赖 fail closed exit 28。
- `FR-035ZI` 跨进程 readiness 唯一使用 `%ProgramData%\FolderSessionLock\Readiness\recovery-readiness.v1.json` 受保护机器范围 snapshot，ProgramData 由 `SHGetKnownFolderPath(FOLDERID_ProgramData)` 取得；不新增公共 Pipe endpoint。唯一 publisher 为 `FolderSessionLockRecovery` 服务；UI、consent-broker 与 recovery-once 只能读，recovery-once 不得覆盖 canonical。
- `FR-035ZJ` Readiness 目录 owner SYSTEM，protected DACL 精确允许 SYSTEM/Administrators/service SID FullControl 与 Users ReadAndTraverse；canonical/temp owner SYSTEM，protected DACL 精确允许前三者 FullControl与 Users Read。publisher mutex 固定 `Global\FolderSessionLock.RecoveryReadiness.v1`，普通用户不得写、替换、删除、改安全或抢占 publisher ownership。
- `FR-035ZK` readiness JSON 必须严格 UTF-8 without BOM、1..16384 bytes、精确十二字段：`schemaVersion`、`serviceName`、`serviceInstanceId`、`sequence`、`state`、`recoveryBlocking`、`scanStartedUtc`、`scanCompletedUtc`、`publishedUtc`、`validUntilUtc`、`remainingRecordCount`、`primaryErrorCode`。四状态矩阵、Guid/UTC/sequence/error格式与时效以 D-030 为准；每10 seconds heartbeat，validUntil固定published+30 seconds，future tolerance最多5 seconds。
- `FR-035ZL` readiness publish/read/delete必须使用 retained directory/file handles、OPEN_REPARSE_POINT、identity/links/owner/DACL/content复核、`FlushFileBuffers`、user-mode `NtSetInformationFile(FileRenameInformationEx=65, flags=0x00000003)` 和 FileDispositionInfoEx。禁止路径型 move/replace/delete。缺失、malformed、stale、安全或identity变化全部 fail closed；内部十个 `FSL_E_RECOVERY_READINESS_*` 错误对CreateLock统一映射 `FSL_E_RECOVERY_BLOCKING`。
- `FR-035ZM` scheduler每 consent-broker 进程至多拥有一个Active task、一个`LockTaskScheduler`和一个串行loop；每轮按monotonic timestamp计算remaining，等待`min(remaining, 30 seconds)`后重读状态与时间。禁止Windows Task Scheduler、多个Timer、每task线程、fire-and-forget、UI scheduler或多进程共享到期所有权。
- `FR-035ZN` repository classifier只从已验证target handle逐级handle-relative遍历到卷根并检查`.git`、`.hg`、`.svn`普通文件或目录；命中返回`FSL_E_PATH_REPOSITORY_FORBIDDEN`，indeterminate返回`FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE`。禁止环境变量、cwd、git.exe、PATH、CLI、用户配置或注册表root。
- `FR-035ZO` synchronization classifier只使用`CfGetSyncRootInfoByHandle`与可信发起UI token的SkyDrive Known Folder。Cloud Files原始`STATUS_CLOUD_FILE_NOT_UNDER_SYNC_ROOT`固定`0xC000CF13`/`-1073688813`，HRESULT固定`0xD000CF13`/`-805253357`；wrapper只比较两个已批准not-under-sync-root HRESULT，不比较原始NTSTATUS、不掩码。SkyDrive先创建`IKnownFolderManager`并要求`GetFolderIds == S_OK`，按GUID二进制值精确查找`FOLDERID_SkyDrive`；集合不含时以内部原因`KnownFolderNotRegistered`返回`Exists=false, Path=null`，任何非S_OK fail closed。只有注册后才固定调用`SHGetKnownFolderPath(FOLDERID_SkyDrive, KF_FLAG_DEFAULT = 0, initiatingUserToken, out path)`；禁止`KF_FLAG_CREATE`、`KF_FLAG_DONT_VERIFY`、`KF_FLAG_DEFAULT_PATH`，调用前path必须为null，失败返回非null pointer也必须`CoTaskMemFree`。完整HRESULT `0x80070002`/`-2147024894`表示当前用户实例或目标叶项不存在，`0x80070003`/`-2147024893`表示父路径链不存在；两者精确映射`Exists=false, Path=null`。仅上述注册缺失、`0x80070002`、`0x80070003`三个场景允许继续。`S_OK`必须返回非null非空绝对路径，复制受控string并释放pointer后再执行持续handle、non-reparse、final path、DirectoryIdentity与Same/Descendant检查。`0x80070057`、`0x80004005`、`0x80070005`、`0x80070006`、`0x8007052E`、`0x80070520`、`0x80070522`、raw Win32 2/3、低16位伪装及其他HRESULT全部返回`FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE`；禁止`HRESULT_CODE`、facility mask、raw Win32/NTSTATUS/重编号及E_INVALIDARG未注册解释。禁止OneDrive环境变量、路径猜测、第三方配置、进程名、窗口标题、PATH或用户roots。
- `FR-035ZP` production logger唯一为`ProtectedJsonLinesLoggerProvider`，固定根`%ProgramData%\FolderSessionLock\Logs\v1`与三个模式子目录。目录/文件owner SYSTEM，protected DACL精确只允许SYSTEM、Administrators、service SID FullControl；普通用户无列表、读取或写权限。consent-broker不创建/修复Logs root，日志文件创建后必须同handle设置并验证SYSTEM owner与DACL。
- `FR-035ZQ` protected log每进程独立文件，固定filename含UTC、PID、instance Guid、四位rotation index；UTF-8 without BOM JSON Lines，每行精确十四字段且含LF最多4096 bytes。禁止自由properties、SID/SDDL/ACL/DPAPI/nonce/proof/credential/token/private key/full path/command line/stack/Exception或FormatMessage。单文件8MiB、跨UTC日rotation、最多10000文件；保留14days、每模式32关闭文件、总量256MiB，稳定顺序只删除安全已关闭非活跃文件。
- `FR-035ZR` protected logger初始化失败固定`FSL_E_PROTECTED_LOGGER_UNAVAILABLE` / `The protected diagnostic logger could not be initialized.` / false / null。consent-broker严格CLI后、Pipe前失败exit28；运行中失败无副作用时exit28，有副作用时先完成lifecycle/Cleanup，Cleanup失败或RecoveryRequired时exit27优先；合法响应不得被后续exit28改写。recovery-service启动失败不得Running，运行中失败发布RecoveryBlocked并受控停止；recovery-once使用既有exit15。

### 3.7 可选访问警告

- `FR-036` 访问警告默认关闭；核心 ACL 功能不得依赖审计。
- `FR-037` 阶段 6 前必须取得修改 Audit File System、目标 SACL 和读取 Security 日志的明确批准。
- `FR-038` 实施时优先使用事件 `4656` Failure；不得把 `4663` 描述为失败访问事件。
- `FR-039` 通知按规范化路径、进程 ID、访问类型和短时间窗口去重限流。
- `FR-040` 不承诺每个底层 I/O 对应一次通知；事件允许延迟、缺失、重复或合并。

### 3.8 阶段 2 领域与调度合同

- `FR-041` 任务 ID、路径和时长必须使用经验证的值对象；Core 路径规范化不得访问文件系统，也不得承担 NTFS、reparse、系统目录或 ACL 验证。
- `FR-042` 任务状态固定为 `Created`、`Activating`、`Active`、`Unlocking`、`Completed`、`ActivationFailed`、`UnlockFailed`、`RecoveryRequired`；全部状态转换集中验证。
- `FR-043` `Completed` 与 `RecoveryRequired` 为终态；表外转换返回 `ValidationFailed` 且不修改任务快照；同状态转换返回 `NoChange` 且不修改时间或错误。
- `FR-044` 活动时长由单调 timestamp 计算；`StartedAtUtc` 和 `ExpectedExpiryUtc` 只用于显示。剩余时间不得为负。
- `FR-045` 到期扫描必须先原子取得 `Active -> Unlocking` 所有权，再使用 `Expiration` 意图请求解除；并发或重复扫描不得产生第二次解除请求。
- `FR-046` 内部解除意图固定为 `Expiration`、`Recovery`、`TestCleanup`、`AdministrativeCleanup`；不存在普通用户或 UI 解除意图。
- `FR-047` scheduler 必须提供一次扫描和单一串行可取消循环；生产按 D-030 使用 monotonic remaining 与最大30-second分段等待。取消只停止尚未开始的delay，不把活动任务标为 `Completed`；lifecycle随后执行既有Cleanup，ACL临界区取消无效，scheduler error只进入protected logger。
- `FR-048` 路径冲突检查、添加和状态替换必须在同一同步门内完成；规范化后相同、祖先和后代关系均拒绝；查询只返回不可变快照。
- `FR-049` WPF Window 和 ViewModel 不拥有 scheduler 或 ACL 生命周期，不公开提前解除入口。

### 3.9 阶段 4 恢复 payload 与验证证据

受保护 JSON payload 精确字段为：`schemaVersion`、`writerVersion`、`recordId`、`taskId`、`state`、`normalizedPath`、`volumeSerialNumber`、`fileIdHigh`、`fileIdLow`、`accountSid`、`logonSid`、`windowsSessionId`、`aceType`、`accessMask`、`inheritanceFlags`、`propagationFlags`、`aceFingerprintSha256`、`baselineDaclSha256`、`postApplyDaclSha256`、`createdUtc`、`expiresUtc`、`lastUpdatedUtc`、`cleanupAttemptCount`、`lastErrorCode`、`lastErrorMessage`。字段名称、大小写和结构不得改变；精确类型和允许值以 `docs/DECISIONS.md` 的 `D-022` 为准。

- 固定目录身份向量：volume `0x0123456789abcdef`、FILE_ID hex `000102030405060708090a0b0c0d0e0f` 必须编码为 volume `0123456789abcdef`、low `506097522914230528`、high `1084818905618843912`，并可反向重建原 16 bytes。
- 固定摘要向量及精确 binary inputs 以 D-022.4 为准：ACE fingerprint `366092caef8b4ccd9a05728cc017b2b155a9f8aa74358e6df901e0554a8239f7`、baseline DACL `62fffcf46d188397e84da5b800129f54cacc87fe86ef9ca1f9eac9c6eef2db17`、postApply DACL `0bd878690d59d8de240e84199560b65db09c2f473dffc717aabb75642566f026`。这些值只用于测试。
- 容器、25 字段类型/范围、状态 null 矩阵、跨字段时间关系、稳定错误码与必测矩阵以 D-022.6–D-022.9 为唯一精确来源；不得省略允许 null 的字段或自动修正不一致状态。

- `lastErrorMessage` 必须脱敏，不得包含凭据、文件内容或敏感用户数据。
- 恢复目录路径、容器头、字段、版本和 entropy purpose 不得从 IPC 或命令行输入。
- 阶段 4 特权集成测试仅允许计算机名 `FSL-STAGE4-VM`、Windows 11 Pro/Enterprise、快照 `FolderSessionLock-Stage4-Clean` 的专用可丢弃 VM。机器名不匹配时，服务、LocalSystem、自动启动、登录前执行、UAC、注销、重启、Program Files/ProgramData ACL 和签名系统测试必须停止；设计、实现、单元测试、非特权测试和静态审查可继续。
- ACL 测试目标仍只能为 `%TEMP%\FolderSessionLock.Tests\<Guid>`；ProgramData 和 Program Files 操作仅限获准 VM 的安装/恢复基础设施验证，不得作为锁定目标。
- 不得创建 `FSL-Standard`、`FSL-Admin` 或任何专用 Windows 测试账户。真实双账户 credential elevation/evidence 不属于 Stage 4 完成门；现有跨账户拒绝继续以不创建账户的单元测试 fail closed。
- 阶段 4 证据固定写入 `docs\evidence\stage-4\<RunId>\`，`RunId` 为 `yyyyMMddTHHmmssZ-<short-guid>`；`scenario-results.json` 与 `manifest.json` 必须使用 D-026 schema v2。
- 当前本地 Release 允许 unsigned；六个第一方 PE 必须如实记录 `Authenticode = NotSigned` 和 null signer。不得创建自签名/测试证书冒充正式签名；真实签名证书缺失不阻止本地交付。

## 4. 非功能需求

- 所有系统相关逻辑位于接口后，Core 不依赖 WPF、Windows UI 或 ACL API。
- 阶段 1 起所有产品产物写入 `FolderSessionLock/`；不得在仓库根创建产品 solution 或项目，不得修改无关项目。
- ACL、IPC、文件系统和事件日志操作异步执行，不阻塞 UI 线程。
- 任务、锁定、解锁、到期和 IPC 重试必须幂等。
- 活动计时使用可测试的单调时间源；UTC 只用于显示、日志和恢复记录时间字段。
- 日志不得记录目录内容、文件内容或普通任务历史。
- 路径验证后被替换时必须安全失败。
- 验证失败、ACL 后置验证失败、回滚失败或恢复失败时不得声称成功。

## 5. ACL 能力边界

ACL 对 ACE 生效后的新访问执行访问检查。它不撤销已打开句柄，不阻止管理员、SYSTEM、其他账户、其他会话、内核组件、备份/恢复特权或离线访问，不提供加密或内核驱动等价保证。完整边界见 `FolderSessionLock/docs/SECURITY.md`。

## 6. v1 非目标

- Explorer 快捷菜单。
- Windows 文件系统 minifilter 或其他内核驱动。
- WinUI 3、跨平台支持。
- 加密、数据防泄漏、恶意软件防护。
- 网络共享、映射盘、非 NTFS、可移动介质、reparse path。
- 修改父目录 ACL。
- 强制关闭已有句柄或进程。
- 其他管理员账户凭据、跨账户 elevation、远程管理员控制和服务账户代替当前用户创建限制。
- 新会话恢复旧任务或剩余限制时间。
- 永久任务历史、分析遥测、目录内容采集。
- 每个 I/O 精确一次弹窗。
- 默认修改系统审计策略。

## 7. 决策状态与阶段实现状态

`D-001` 至 `D-031` 均已决定。D-031 在当前部署范围上取代旧双账户与强制签名冲突条款。CP8 class 65 rename、FileDispositionInfoEx POSIX canonical 删除顺序和文件级安全实现已经通过最终 reviewer；CP10 的范围修订与剩余真实 VM 验证按固定串行 gate 推进。
