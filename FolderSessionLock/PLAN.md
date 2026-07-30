# Folder Session Lock 分阶段计划

规则：每次只执行用户明确指定的阶段，或 `stage_director` 返回 `READY` 的紧邻下一阶段 payload；按 `DISCOVER -> PLAN -> EXECUTE -> VERIFY -> REVIEW -> ITERATE -> STATE -> GATE`；仅通过阶段门自动转换，`BLOCKED`、`PROJECT_COMPLETE`、人工审批门或自动转换上限时停止。

## 阶段 0：可行性分析与架构决策

目标：把需求转为可实现、可测试、可恢复的规格。

Checkpoint：

1. 读取附件、仓库规则、现有文件和 git diff。
2. 定义产品能力边界、威胁模型和 v1 非目标。
3. 定义 Account SID/Logon SID、会话生命周期和恢复策略。
4. 定义 ACL 添加、后置验证、精确移除和回滚。
5. 定义 Broker、Named Pipe、身份验证和权限边界。
6. 定义路径、reparse、TOCTOU、重复和嵌套策略。
7. 定义可选审计能力边界。
8. 创建状态文档并由 reviewer 审查。

验证：

```powershell
git status --short --branch
git diff --check
git diff -- AGENTS.md README.md FolderSessionLock/AGENTS.md FolderSessionLock/docs/REQUIREMENTS.md FolderSessionLock/docs/ARCHITECTURE.md FolderSessionLock/docs/SECURITY.md FolderSessionLock/docs/DECISIONS.md FolderSessionLock/PLAN.md FolderSessionLock/TASKS.md FolderSessionLock/ACCEPTANCE.md FolderSessionLock/DEVLOG.md
```

无 solution；`.NET` 命令不适用。

完成门：阶段 0 文档齐全；`D-001` 至 `D-020` 已决定；八份文档只存在于 `FolderSessionLock/` 唯一权威路径；阶段 1–7 有客观验收；reviewer `PASS`；`FolderSessionLock/TASKS.md` 和 `FolderSessionLock/DEVLOG.md` 已更新。

## 阶段 1：解决方案骨架与测试基础

前置：阶段 0 文档迁移与 reviewer 验收完成，用户明确启动阶段 1。

Checkpoint：

1. 在仓库中创建独立产品根 `FolderSessionLock/`；不得修改无关项目或根 `README.md`。
2. 在产品根内创建 solution、四个产品项目和三个测试项目；不得在仓库根创建产品 solution 或项目。
3. 固化依赖方向；记录 Broker 的恢复专用模式由自动启动 Windows 服务以 LocalSystem 身份托管，但不猜测具体服务名、项目名或存储路径。
4. 创建附件指定接口、统一结果类型、DI 和日志基础。
5. 创建严格临时目录测试工具。
6. 创建最小 smoke tests、README 和运行说明；所有项目文档写入 `FolderSessionLock/`。

非目标：真实 ACL、Broker 提升、WPF 完整界面、系统审计修改。

验收：

- `dotnet restore` 成功。
- Release build 成功。
- tests 全部成功。
- format 无变化。
- Core 不引用 WPF、Windows UI 或 ACL API。
- App、Windows、Broker 不执行真实 ACL 修改。
- 临时目录工具只生成 `%TEMP%\FolderSessionLock.Tests\<Guid>\`。
- solution、源码、测试和项目文档全部位于 `FolderSessionLock/`。
- 仓库无关项目和根 `README.md` 无变化。
- reviewer `PASS`。

## 阶段 2：领域模型、任务状态与倒计时调度

Checkpoint：

1. 创建 `FolderLockTaskId`、`FolderPath`、显式 `LockDurationPolicy`、`LockDuration`、状态和集中状态机；覆盖边界、合法、非法和同状态转换。
2. 创建不可变 `FolderLockTask`、`IFolderPathRelationService` 和单同步门 `LockTaskManager`；原子处理 ID/路径冲突、添加、查询和状态替换。
3. 创建 `LockTaskCoordinator` 激活流程；`IFolderLockService.RemoveLockAsync` 强制明确解除意图；覆盖成功、失败、ID 不一致、异常和并发激活。
4. 扩展 `IClock` 可取消 delay；记录 UTC 显示时间与单调 start timestamp；实现剩余时间和 exactly-once 到期解除。
5. 将 `ILockTaskScheduler` 固化为一次扫描与可取消循环；覆盖多任务顺序、同时到期、失败隔离、跨越多个到期点和取消。
6. 更新 Windows 安全占位、App 生命周期边界测试、README 和权威文档；运行完整验证和 reviewer。

非目标：真实 Windows ACL、Broker 提权、WPF 完整界面。

验收：

- Core 测试无需管理员权限。
- 不用 `Thread.Sleep`。
- 产品时长上下限只通过显式 `LockDurationPolicy` 输入；无隐藏默认值。
- `Completed` 和 `RecoveryRequired` 为终态；表外转换不修改快照。
- 到期前保持 Active；到期只触发一次解除。
- 系统时间前后变化不重复解除或错误延长。
- 多任务独立。
- 同目录和父子目录确定拒绝。
- 并发操作不产生重复任务或重复解除。
- scheduler 取消不解除活动任务；UI 与 Window 不拥有 scheduler 或解除入口。
- Windows 占位仍明确返回 `windows.acl.not_implemented`。
- reviewer `PASS`。

## 阶段 3：Logon SID 与 ACL 锁定引擎

前置：仅在 Windows；测试目标严格为临时 NTFS 目录；恢复策略已确认。

Checkpoint：

1. Logon SID 读取和严格验证。
2. 路径、卷、reparse 和稳定目录身份验证。
3. 绑定同一持续目录句柄的 ACL 差异算法和拒绝掩码。
4. Lock、Unlock、后置验证和 rollback。
5. 最小 Deny 权限矩阵逐项集成测试。
6. 恢复权限与临时目录清理测试。

验收：

- 锁定前创建、读、写、枚举、删除成功。
- 锁定后目标会话的新访问命中矩阵并拒绝。
- 枚举、读取、创建、写入、删除、重命名、移动、属性和子目录修改均按矩阵验证。
- `ReadPermissions`、`ChangePermissions` 和精确移除应用 ACE 的恢复路径可用。
- 原 ACE 多重集合、继承状态和无关 SID 不变。
- 路径替换测试证明替换对象 ACL 从未改变。
- 应用 ACE 恰好一条。
- 重复 Lock 不新增；重复 Unlock 安全。
- Unlock 后全部操作恢复。
- 每个测试使用 `try/finally`。
- 所有测试目录最终可访问且可删除。
- 恢复失败即阶段 `FAIL` 并停止。
- reviewer 重点检查 ACL diff 并输出 `PASS`。

## 阶段 4：提升 Broker、IPC 与恢复生命周期

前置决定：用户已确认 `D-022` 至 `D-030` 及 D-022.10/D-022.11/D-023.1/D-024.1/D-024.2，并批准 class 65 rename 与 canonical POSIX delete 关闭顺序勘误。CP1–CP9 已完成；CP10 recovery-authority capability 已在 commit `aa60c1c6cea2ea05648824acb10f5f3ec2342549`、tree `9b97428f3988c962e7d4b6899d3521f9cd3b7fc1` 冻结，reviewer 最终 `PASS`，`BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`。验证为 RAB 218/305、Formal 229/299、tooling 7/7、非环境依赖 807/807；未过滤 Core 174/174、App 494/501、Windows 140/141，共 808 passed、8 environment failures、0 skipped；Release build 0 warning/0 error，format、parser、diff、exports 均通过。真实 UAC、SCM、LocalSystem、ProgramData/ProgramFiles ACL、真实 service SID ACL、恢复、必要 restart/logoff、unsigned Release 与 D-026 仍继续阻止阶段 4 完成。缺少真实签名证书或签名流水线不阻止 D-031 本地 unsigned Stage 4 或 Stage 5。

环境门：唯一获准特权集成环境为 `FSL-STAGE4-VM`，Windows 11 Pro/Enterprise 专用可丢弃 VM，快照 `FolderSessionLock-Stage4-Clean`。机器名不匹配时，允许完成设计、代码、单元测试、非特权测试和静态审查；服务、LocalSystem、自动启动、登录前、UAC、注销、重启和 Program Files/ProgramData ACL 验证必须标记 `BLOCKED`，阶段 4 不得完成。当前位于 `FSL-STAGE4-VM`，但仅机器名匹配不构成 UAC、RunAs、restart/logoff 或系统 mutation 授权；仍须按每个 checkpoint 的明确授权执行。公开/企业签名系统验证不是当前 D-031 Stage 4 完成门。

当前 frozen execution 为 commit `3170d89cfd6066ba494170826cd43626d83c6789`、tree `6bee7c4db4c9adde0612aa7c67467a331d20263e`、state sequence 6 / `InstallStarted`、WAL 4；认证后的 current pre-recovery anchors 为 latest/previous generation 11/10，future successful recovery postcondition 才是 generation 14/13。recovery 为 3 directories/8 files，Release 为 22 files；Program Files 安装目录为空，ProgramData product root 不存在。当前没有 Formal source、Attempt003、新 latch、UAC 或系统执行。

CP10 剩余顺序不可重排：文档 synchronization 与 commit-freeze → 最终 RAB exact-two + FLB exact-three preparation（只生成并验证，不执行）→ 唯一 one-shot observer/UAC → recovery 成功后另行申请 fresh restart 授权 → 完成剩余 D-026 与 Release。最终 generation 后不得修改仓库或执行 restart；VM、D-026、restart/logoff、Release 与 Stage 4 完成项均保持未完成。

Checkpoint：

1. UI/Broker 进程边界。
2. `D-027` 强类型协议、四命令白名单、严格 JSON/schema、envelope、payload/result、错误模型和恶意输入拒绝。
3. 固定 `\\.\pipe\FolderSessionLock.Broker.v1` byte-mode 分帧、本机限制、最小 Pipe DACL、单请求连接和长度/UTF-8/尾随数据防护。
4. 实现 D-027 固定 `ClientHello -> ServerHello -> CommandRequest -> CommandResponse -> close`；CLI/request/session 绑定；OS Pipe 客户端 PID、Account SID、Logon SID、TokenSessionId；30-second handshake、bindingProof；机器范围 Replay Registry、CreateNew 原子登记、所有权四元组、60-second lease/20-second renewal/5-minute limit/10-minute retention、失败撤销和崩溃安全处理。
5. Broker 计时、内存状态和 `D-022` 固定 `.fslr` 恢复记录：固定路径、精确容器、精确 JSON 字段、DPAPI LocalMachine、版本拒绝、原子提交和中断点恢复。
   - 目录身份使用同一持续句柄的 UInt64 `FILE_ID_INFO.VolumeSerialNumber` 和完整 FILE_ID_128；volume 为 16 位小写 hex，low/high 为两个 little-endian UInt64 十进制 string。
   - ACE fingerprint 使用写后重读的唯一 ACE 与 `FSLACE` v1 wrapper；baseline/postApply 使用 `FSLDACL` v1 wrapper、原 ACE 顺序、有效 bytes、ACL revision 和掩码 `0x1504` 的 control flags。
   - 固定目录身份与三个 SHA-256 向量、反向重建、摘要敏感/不敏感字段、missing/null DACL 和写后重读矩阵全部通过。
   - `.fslr` header 固定 12 bytes、version 1、`Flags = 0`、blob 1..262144、文件总长精确匹配；明文 <=131072、严格 UTF-8/JSON、全部 25 字段和四状态 null 矩阵全部通过。
6. 崩溃、断线、正常退出、注销和断电恢复。
   - scheduler 是否失败都必须执行并完整遍历 Cleanup；任务顺序稳定，单任务失败不提前终止。
   - 对外错误固定为稳定顺序中的 Cleanup first-task error；其余 Cleanup errors 与 scheduler error 仅进入受保护内部诊断。
   - 固定结果矩阵：success/success 返回 Cleanup success count；success/failure 返回 Cleanup first-task error；failure/success 返回 Cleanup success count；failure/failure 返回 Cleanup first-task error。
   - scheduler生产loop未预期非取消异常固定为 `lock_task.scheduler.loop.exception` / `The lock task scheduler loop terminated unexpectedly.`，仅以protected `Scheduler`/`Error`记录；预期token取消不记录。该合同不得用于lifecycle stop、Cleanup failure、task状态转换、已有更具体错误或logger failure，不公开、不覆盖Cleanup first-task error、不阻止Cleanup，且不记录原异常message、类型、stack、路径、SID、HRESULT或Win32 message。
   - `RecoveryRequired`、ACL 状态未知或恢复失败不得被 scheduler error 覆盖；公开响应不得泄露 stack、内部类型、SID、SDDL、恢复记录路径、凭据或令牌。
   - `RemoveLockAsync` 抛异常固定返回 `lock_task.administrative_cleanup.exception` / `The administrative cleanup ended without a confirmed result.`；ACE 已移除但 `Completed` 状态记录失败固定返回 `lock_task.administrative_cleanup.state_update_failed` / `The lock was removed but its completed state could not be recorded.`；两者均为 `UnrecoverableError -> RecoveryRequired`。
7. TOCTOU 替换安全失败，所有 ACL 读写绑定同一持续句柄。
8. Broker 恢复专用模式、readiness 与 D-023 受保护路径。
   - 固定解析 `recovery-service`、`recovery-once` 与既有 `consent-broker`；禁止额外参数、任意路径、service/Pipe/binPath、ACL、SID、shell 或脚本输入。
   - 实现 D-022.10 顶层完整枚举、4096/1024 上限、规范文件/auxiliary/invalid 分类、Ordinal 排序、串行继续遍历、ACL 临界区、五类记录结果和十二字段摘要。
   - `recovery-once` 只返回 0/2/10/11/12/13/14/15，并按 D-024.1 唯一优先级映射。
   - `recovery-service` 启动扫描一次后持续托管，不周期扫描；实现 D-024.2 状态机与进程内publisher/reader接口。D-030跨进程machine snapshot、十二字段、heartbeat、安全文件和Stop删除属于CP9生产组合实现。
   - 先完成用户合同所称 CP6 的 `IProtectedPathSecurityVerifier`、enum、结果模型、orchestration、fail-closed、fake verifier 与单元测试；生产组合在 Windows verifier 前返回 `FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED`。
   - 再完成用户合同所称 CP8 的 `WindowsProtectedPathSecurityVerifier`、handle/final path/reparse/FILE_ID_128、owner/DACL/ACE/继承校验、安装与 Recovery/Replay ACL 创建验证、service SID ACL 及 `FSL-STAGE4-VM` 安全集成矩阵。
   - D-022.11：三类记录文件唯一 SYSTEM owner、精确 protected 三 ACE DACL（mask `0x001F01FF`）、`IRecoveryRecordFileSecurity`、SeRestorePrivilege finally、`Global\FolderSessionLock.RecoveryStore.v1`、Records/temp/old canonical 持续句柄、user-mode `NtSetInformationFile(FileRenameInformationEx = 65)` rename、同 handle FileDispositionInfoEx delete、`FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`、auxiliary security与禁止路径 API。
   - 只有 `FSL-STAGE4-VM` 可以产生 SCM、LocalSystem、ProgramFiles/ProgramData ACL 与其他特权或系统级 `PASS` 证据；任何其他机器只允许接口、控制流、产品代码、单元/非特权测试和静态审查，不得替代 VM 证据或完成阶段 4。
9. 当前本地管理员同账户 consent elevation；跨账户作为不支持路径 fail closed。
   - 身份错误按 D-029 分为 UI launcher、elevated bootstrap 与 connected Pipe handshake。bootstrap Account SID 不同 exit 20；connected `FSL_E_ACCOUNT_SID_MISMATCH` 只在 UI elevation 边界转换为 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`；Logon/Session/PID/identity/Pipe/unauthorized 错误禁止转换。
   - UI 在 UAC 前从自身 token 读取 Account SID、唯一 Logon SID、Session ID，并取得 PID + creation FILETIME。CLI 只增加 `--client-process-id` 与 `--client-process-creation-filetime`；Broker 在创建 Pipe 前重开 UI process/token并重新读取身份。
   - production Broker path 只从 `SHGetKnownFolderPath(FOLDERID_ProgramFiles)` 的固定安装路径取得并通过 D-023/file final-path/identity 验证；CP9 不完成 Authenticode。
   - production launcher 使用 `ShellExecuteExW(runas)`、固定 flags、非空 process handle、专用参数 encoder；实现 UAC取消/失败、Pipe/process race、15/20/5-second timeout/cleanup 和连接后禁止 TerminateProcess。
   - consent-broker 退出码关闭集合固定为 0、2、20–29；exit 2 精确映射 `FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / false / null且不泄露启动细节；应用失败响应可 exit 0；Cleanup/response/protocol/internal 优先级与 response precedence 按 D-029。
   - 每进程只允许一个 listener、一个连接和一个应用请求。CreateLock 成功响应后 Broker 继续运行到到期 Cleanup；UI 断开不得提前解除确定 Active lock，未知副作用进入 RecoveryRequired。
   - production composition 必须包含 D-029 列出的真实 identity/path/ACL/recovery/readiness/replay/protocol/task/lifecycle/logging/clock 依赖，禁止 fake/AllowAll/in-memory/test/debug 依赖。
   - D-030 readiness固定为ProgramData Known Folder下受保护machine snapshot，SYSTEM/四ACE DACL、publisher mutex、十二字段、10-second heartbeat、30-second validity、class65原子publish、retained-handle reader/delete与十个稳定内部错误；CreateLock继续统一公开`FSL_E_RECOVERY_BLOCKING`。
   - production `LockDurationPolicy`固定60000..86400000ms；scheduler每进程单Active owner/单loop，monotonic remaining与最大30-second分段重算。repository只按handle-relative祖先`.git|.hg|.svn`；sync先用Cloud Files handle API，再用`IKnownFolderManager::GetFolderIds`的S_OK二进制GUID集合确认SkyDrive注册，只有注册存在才以flags0和initiating token调用`SHGetKnownFolderPath`。注册缺失、完整`0x80070002`/`-2147024894`、完整`0x80070003`/`-2147024893`是仅有三个`Exists=false`场景；其他GetFolderIds/SH HRESULT全部fail closed。禁止CREATE/DONT_VERIFY/DEFAULT_PATH、E_INVALIDARG未注册、低16位/HRESULT_CODE/facility mask/raw2/3/NTSTATUS/重编号。所有非null native path在成功复制或失败后释放；S_OK必须非空绝对路径并继续retained handle/reparse/final path/identity检查。
   - production logger唯一为`ProtectedJsonLinesLoggerProvider`：ProgramData `Logs\v1`三模式子目录、SYSTEM/三ACE安全、每进程十四字段JSONL、每事件flush、8MiB/UTC日rotation、14days/32-per-mode/256MiB retention、固定redaction及`FSL_E_PROTECTED_LOGGER_UNAVAILABLE`。Pipe前初始化失败exit28；副作用后先Cleanup且exit27优先。
   - 非 VM 环境只实现 wrapper、resolver、identity/bootstrap、exit mapping、race abstraction、composition、fakes 与自动测试；真实同账户 UAC、elevated Broker、Program Files、SCM/LocalSystem 和恢复为 VM-only。真实跨账户凭据和专用测试账户已取消。
10. VM 内验证 unsigned 本地 Release、服务注册/启动、发布安全与 D-026 schema v2 证据；生产证书流水线为非目标。CP8 已负责的 owner/DACL/verifier 安全矩阵不得在此重复定义为可选项。
    - `CANCELLED / NOT REQUIRED`：Create `FSL-Standard`；Create `FSL-Admin`；validate standard-user to separate-admin credential elevation；collect real dual-account evidence；block Stage 5 solely on missing dual-account evidence。
    - 当前 Stage 4 控制器不公开 publisher pin 或 signing certificate 参数，固定写入精确空 `BrokerPublisherThumbprint`，不创建测试证书、不调用 SignTool，并逐一验证六个第一方 PE 为 `NotSigned`/null signer。App runtime verifier 的有效 pin signed fail-closed 单元合同保留，但当前控制器不可选择。
    - D-026 使用 `TRUSTED_SINGLE_USER_STAGE4_EXECUTOR_MODEL`，scenario-results 与 manifest 均为 schema v2，记录同账户 consent 而非跨账户场景。

验收：

- UI 无真实 ACL 写入引用。
- 未授权账户、其他会话、远程和畸形请求被拒绝。
- 无任意命令执行。
- 请求/响应 envelope、严格字段、基础类型、错误码和四命令 payload/result 与 `D-027` 完全一致。
- 4-byte little-endian 长度前缀、65536-byte 上限、严格 UTF-8 without BOM、重复/多余/缺失字段、尾随数据和宽松类型测试通过。
- requestId 小写 Guid D、10 分钟 replay、7 位小数 UTC 时间、120 秒窗口和 OS Session 比对测试通过。
- ClientHello/ServerHello/CommandRequest/CommandResponse 精确字段、严格序列、5-second ClientHello timeout、30-second handshake expiry、32-byte nonces、connectionId 和 bindingProof 测试通过。
- CLI、ClientHello、CommandRequest 外层/内层 requestId、command、protocolVersion、session 绑定测试通过；PID、Account SID、Logon SID、Session ID 和 identity unavailable 使用 D-027 固定错误对象。
- Replay Registry 固定路径、key、schema、state、protected ACL/mutex、CreateNew 原子登记、并发唯一所有者、lease/renewal/retention、RolledBack/RecoveryRequired/Abandoned 和崩溃处理测试通过。
- Replay CreateNew 只在完整 OS 身份、Broker identity 和命令权限通过后执行；所有身份/授权失败无 Replay 文件。不存在身份前登记分支。
- 六个握手/序列/Replay 错误按 D-027 唯一场景返回固定 ServerHello/CommandResponse、retryable、field 和标识符；同一场景不受实现顺序影响。
- 普通 UI RemoveLock 固定拒绝；服务端按身份/模式映射 Expiration、Recovery、TestCleanup；客户端 intent/ACL/SID/路径/命令字段返回 `FSL_E_FORBIDDEN_INPUT`。
- GetStatus 只返回同账户/Session 内存任务，跨身份不泄露存在性，错误脱敏符合 D-027。
- 重复任务 ID 不重复执行。
- 验证后路径替换被检测或安全失败。
- IPC 断开不重复应用。
- CP6 scheduler/Cleanup 四种组合、稳定首错顺序、完整遍历、并发/重复 Stop、`RecoveryRequired` 优先级、内部诊断保留和公开响应脱敏测试通过。
- CP6 两个 administrative Cleanup 内部错误的 code、message、`UnrecoverableError` category 和 `RecoveryRequired` 状态精确测试通过。
- 恢复记录事务覆盖每个中断点。
- `.fslr` 容器头、`containerVersion = 1`、`schemaVersion = 1`、`writerVersion = 1.0`、payload 字段名称/类型、purpose entropy 和 `RecoveryRecordUnsupported` 行为与 `D-022` 完全一致。
- `volumeSerialNumber` 精确为 16 位小写 hex；FILE_ID_128 high/low 映射、十进制格式、反向重建和固定向量与 D-022.1 完全一致；禁止复用旧 8 位格式。
- `FSLACE`/`FSLDACL` wrapper、control mask `0x1504`、ACE 原始顺序、有效 byte 范围和三个固定 SHA-256 向量与 D-022.2–D-022.4 完全一致；owner/group/SACL/SELF_RELATIVE/未使用尾部变化不得影响 DACL digest。
- Magic/version/flags/length/truncated/trailing/unprotect/明文上限和严格 payload 错误对象与 D-022.6 完全一致；在上限/文件长度验证前不得分配或调用 DPAPI。
- 25 字段 JSON/.NET 类型、范围、canonical Guid/date/SID/hash、enum/flags、字段必需性、四状态 null/count 矩阵和跨字段时间关系与 D-022.7–D-022.9 完全一致。
- `Prepared` 在 ACL 前完成同句柄安全设置、flush、回读和 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)` 原子提交；新建 flags=0，更新保持 old/temp/directory handles 并使用 flags=`0x00000003` POSIX replace。production 禁止 class 10、SetFileInformationByHandle class 22/class 3、绝对目标或其他 fallback。canonical 删除使用验证过的同一 handle FileDispositionInfoEx，成功后关闭该 handle，再由 retained directory handle 确认名称消失与目录 identity；失败进入 RecoveryRequired，禁止路径重试或删除 replacement。
- 恢复目录 owner/DACL 精确符合 `D-023`，普通用户和 UI 无直接访问；服务启动时复核并安全失败。
- 跨账户身份在不创建真实第二账户的单元测试中安全失败并显示“不支持跨账户提升”；真实双账户 VM evidence 不要求。
- 跨账户拒绝错误码为 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`，且 ACL 写入前拒绝。
- consent-broker CLI 精确包含固定 mode/pipe/session/request 与 `--client-process-id <UInt32>`、`--client-process-creation-filetime <UInt64 decimal>`；不包含 SID、用户名、角色或 Pipe SDDL。
- UI token snapshot、PID/creation-time binding、Broker bootstrap token reread、Pipe-before-create Account/Session 比较和 Pipe DACL 主体全部通过 D-029 正反测试。
- connected `FSL_E_ACCOUNT_SID_MISMATCH` 只在 UI boundary 转换；Logon/Session/PID/identity/Pipe/unauthorized 错误不转换。
- production Broker path、D-023 install verification、non-reparse/final-path/identity、known-folder来源和禁止 cwd/PATH/bin/debug path 测试通过。
- `ShellExecuteExW` 固定字段/flags、`ERROR_CANCELLED`、一般失败、空 process handle、专用参数编码和 UI-thread/UAC timeout 边界测试通过。
- Pipe ready/process-exit race、Broker 15-second connect timeout、UI 20-second total wait、pre-connect terminate 29 + 5-second proof、cleanup failure和 post-connect TerminateProcess 禁令测试通过。
- consent-broker exit 0/2/20–29 的唯一映射、连接前/连接后优先级、应用失败 exit 0、response write 26、Cleanup 27、unknown early exit 与合法响应优先测试通过。
- 每进程单 listener/单连接/单请求、第二连接拒绝、GetStatus 无 scheduler、CreateLock success 后 UI关闭但 Broker继续到期 Cleanup、disconnect副作用分类和 production composition 无 fake/AllowAll 测试通过。
- D-030 readiness四状态矩阵、十二字段严格JSON、sequence/time、owner/DACL、hard-link/reparse、class65 publish、stale/crash/Stop/delete、reader到`FSL_E_RECOVERY_BLOCKING`映射测试通过。
- 60000/59999/86400000/86400001、single scheduler/Active owner、monotonic/UTC jump/30-second分段、disconnect与scheduler/Cleanup优先级测试通过。
- repository marker与ancestor/reparse/indeterminate、Cloud Files精确HRESULT、initiating-token SkyDrive/failure/final-identity及禁止环境/CLI roots测试通过。
- protected logger三模式filename、SYSTEM/精确DACL、十四字段/LF/BOM/4096/sequence/redaction、8MiB/UTC rotation、14days/32/256MiB、安全artifact、exit28/27/15/service-readiness及production无Console/Debug/Null测试通过。
- 重启、注销和新会话后只清理旧 ACE，不恢复旧任务或剩余时间。
- 重启/登录测试证明测试用户首次访问目标前已完成既定遗留扫描和清理。
- 自动启动服务未就绪或清理失败时保持恢复阻断状态，不报告成功。
- 恢复记录与 ACL 不一致时停止自动删除，不覆盖 DACL。
- 启动恢复清理失败时保留记录并产生明确诊断。
- 当前本地 Release 明确允许 unsigned，安装目录权限、普通用户不可替换、identity/hash/TOCTOU 门仍须验证；不得扩张为公开或企业分发。
- 隔离 VM 逐一验证固定六 PE 的实际状态为 `NotSigned`、signer null并记录 SHA-256；Finalize 通过受保护 state 的 ReleaseRoot/ReleaseDescriptorSha256 重新验证 frozen descriptor、精确六 PE 集合和实际文件 hash，并要求 evidence hash 精确相等；不创建测试证书，不伪造签名。
- Broker 正常退出尝试清理全部任务。
- 全部集成测试仅修改临时目录。
- 证据目录、必需文件、`scenario-results.json` 与 `manifest.json` 精确结构符合 D-026 schema v2；`TASKS.md`、`DEVLOG.md` 引用 RunId，reviewer 核验 manifest 与实际工件一致。
- 特权验证只在 `FSL-STAGE4-VM` 执行；当前机器名不匹配时不得将系统级场景标记为通过。
- reviewer `PASS`。

阶段 4 非目标：阶段 5 完整 WPF UI；阶段 6 Audit File System、SACL、Security 日志和访问失败通知；生产证书采购、私钥托管和发布签名流水线；任意服务名、任意 binPath、任意 Pipe 名、任意恢复路径或任意 ACL 描述符；宽松 JSON、同连接批量/流式请求、UI 提前解除、持久任务历史。

阶段 5 只由仍适用的 Stage 4 完成门阻止；缺少第二账户、`FSL-Standard`、`FSL-Admin`、真实双账户 evidence 或真实签名证书不得单独阻止进入。

## 阶段 5：WPF 前端

Checkpoint：

1. 文件夹选择和规范化路径展示。
2. 时长输入和验证。
3. 活动任务列表和状态展示。
4. 异步 Broker 调用。
5. 错误、UAC 取消和危险路径阻止。
6. ViewModel 测试。

验收：

- UI 线程无阻塞文件系统或 IPC。
- 快速重复点击不创建重复任务。
- Broker 成功并后置验证前不显示“已锁定”。
- 危险路径无法绕过。
- 临时目录完成锁定、倒计时、到期和恢复。
- UI 关闭后任务按架构继续。
- 错误状态可见。
- reviewer `PASS`。

## 阶段 6：可选访问审计与警告

批准门：用户明确批准 Audit File System、目标 SACL、Security 日志权限、恢复方式、系统影响和尽力而为语义。未批准则停止，不调用 coder。阶段 1 至阶段 5 不得提前加入这些系统变更。

Checkpoint：

1. 审计策略和权限探测。
2. 精确目标 SACL 增删。
3. `4656` Failure 解析和任务关联。
4. 去重、限流和通知降级。
5. 解锁清理和审计不可用降级。

验收：

- 只添加目标目录和目标 Logon SID 的精确 SACL ACE。
- 原 SACL 和无关审计策略不变。
- `4656` Failure 测试产生提示。
- `4663` 不被错误视为失败访问。
- 重复事件被限流。
- Unlock 后任务事件不再提示。
- 解析错误不崩溃；Toast 失败降级。
- 审计不可用时核心锁定仍可用。
- reviewer `PASS`。

## 阶段 7：端到端测试与安全加固

Checkpoint：

1. 自动测试矩阵。
2. Explorer、CMD、PowerShell、第三方程序人工矩阵。
3. 崩溃、UAC 拒绝、IPC 断线、时钟、ACL 漂移。
4. 独立安全审查。
5. 生产发布阻断审查。
6. 清理证明、README 和已知限制。

验收：

- 附件测试矩阵全部有结果记录。
- 自动测试全部通过；人工清单完成。
- 所有临时目录 ACL 恢复并可删除。
- 无真实用户目录测试。
- 无父目录 ACE；无 SYSTEM、Administrators、TrustedInstaller 修改。
- 无任意 IPC 命令；无用户内容记录。
- TOCTOU 测试安全失败；通知无洪泛。
- 未来 Stage 7 的公开/企业/签名 checkpoint 当前不激活；只有另一个明确的公开/企业/签名产品决定才能激活。未激活或缺少签名不得阻止 D-031 本地 unsigned Stage 4 完成或进入 Stage 5；若未来激活，才要求公开/企业 Broker 签名有效。Broker 和托管恢复模式的自动启动服务仍须位于管理员保护目录，普通用户不能替换或修改 Broker。
- IPC 为本机最小权限、调用方身份已验证、请求不可重放、无任意命令接口。
- 只有上述未来 checkpoint 经独立决定激活后，公开/企业发布阻断条件才适用；当前本地 unsigned 产物不得被标记为公开/企业发布。
- reviewer 无 `BLOCKER` 或 `HIGH`，并输出 `PASS`。
- `FolderSessionLock/README.md` 和 `FolderSessionLock/docs/SECURITY.md` 记录全部已知限制。
