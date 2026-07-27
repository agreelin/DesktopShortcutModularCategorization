# Folder Session Lock 架构

状态：阶段 3 与阶段 4 CP1–CP9 已完成；CP10 工具实现最近验证为 799/799、0 failed、0 skipped、Release 0 warning/0 error。D-031 将当前交付范围修订为本地单用户管理员；同账户 UAC、SCM、LocalSystem、ProgramData/ProgramFiles ACL、真实 service SID ACL、恢复、重启/注销与 D-026 schema v2 证据尚未完成，阶段 4 不得完成。

## 1. 选择 WPF

v1 使用 .NET 8 WPF。理由：Windows-only 范围明确；WPF 在 .NET 8 下具有稳定的桌面、MVVM、权限提升和 Windows API 互操作路径；不引入 WinUI 3 的部署与运行时复杂度。WinUI 3 属于 v1 非目标。

## 2. 仓库与解决方案结构

产品根目录固定为：

```text
<repository>/FolderSessionLock/
```

所有产品 solution、源码、测试和项目文档必须位于该目录。八份阶段 0 文档已经迁入最终位置，仓库根不存在同名副本。仓库现有其他内容和根 `README.md` 不得被替换或进行无关修改；根 README 只提供项目导航。

阶段 1 起的产品结构：

```text
FolderSessionLock.App
    WPF UI、ViewModels、用户交互、Broker IPC 客户端

FolderSessionLock.Core
    领域模型、时钟抽象、任务状态、验证规则、接口

FolderSessionLock.Windows
    Logon SID、ACL、路径/卷/目录身份检查、权限检查、事件日志

FolderSessionLock.Broker
    按需提升的本地控制进程、计时、恢复记录、ACL 所有权

FolderSessionLock.Core.Tests
    Core 单元测试

FolderSessionLock.App.Tests
    ViewModel 单元测试

FolderSessionLock.Windows.Tests
    Windows 临时目录集成测试

Broker 受信启动恢复模式
    由自动启动 Windows 服务以 LocalSystem 身份托管；登录前清理遗留 ACE；不恢复旧任务
```

恢复模式使用与交互控制模式相同的受信 Broker 二进制和 ACL 实现。阶段 4 已确认服务名、项目名、入口、存储路径、安装路径和启动参数，精确值见 `D-022` 至 `D-026`；不得静默改名或创建功能重复的第二个提升项目。

当前部署拓扑由 D-031 固定为一个当前本地管理员账户。UI 与 consent-broker 必须保持同 Account SID、Logon SID 和 Session；跨账户只作为不支持路径 fail closed，不允许为验证创建第二账户。

依赖方向：

```text
App -> Core
App -> Broker IPC client
Broker -> Core
Broker -> Windows
Windows -> Core
Core -> 无 WPF、无 Windows UI、无 ACL API
Tests -> 对应被测项目
```

阶段 1 的实际项目数为七个：四个产品项目与三个测试项目。阶段 1 中 `App` 只引用 `Core`；Broker IPC 客户端依赖留待阶段 4 的强类型协议实现。

## 3. 进程模型

- App：普通权限运行；展示 Broker 最终确认的路径和状态；不直接修改真实 ACL。
- Broker：唯一真实 ACL 写入主体。交互控制模式按需 UAC 提升并拥有任务、计时和正常清理；恢复专用模式由自动启动 Windows 服务以 LocalSystem 身份托管，只清理遗留 ACE。
- UI 关闭或崩溃：Broker 继续活动任务。
- Broker 正常退出：先尝试解锁全部活动任务；未恢复的任务保留恢复记录并报告。
- Broker 崩溃或断电：ACL 仍在磁盘；下次 Broker 启动按恢复记录处理。
- 新登录会话：Broker 恢复模式将旧任务视为失效，只清理旧 ACE，不续跑任务或剩余时间。
- 启动顺序：自动启动 Windows 服务在系统启动期间、交互登录前运行 Broker 恢复模式并完成遗留记录扫描。
- 失败行为：服务未就绪或清理失败时保持恢复阻断状态、保留记录并报告；不得宣称清理成功或覆盖 DACL。

## 4. 会话身份

Account SID 跨会话稳定，不能作为 v1 锁定主体。Logon SID 位于访问令牌组，格式 `S-1-5-5-X-Y`，标识一次登录会话。

UI 在 UAC 前从自身当前进程令牌精确读取 Account SID、唯一 Logon SID、Windows Session ID，并保存 PID 与 `GetProcessTimes` creation FILETIME。Broker 不信任该 SID 文本；bootstrap 通过 CLI 只接收 PID、creation FILETIME 和 Session ID，在创建 Pipe 前重新打开 UI 进程和 token，重取三项身份并与 Broker Account SID/Session ID 比较。

已连接 Pipe 的 `FSL_E_ACCOUNT_SID_MISMATCH` 是 D-027 握手层诊断；连接前 bootstrap Account SID 不同使用 consent-broker exit 20。UI elevation 边界把这两条跨账户路径统一为 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`，但不得转换 Logon SID、Session、PID、identity unavailable、Pipe access 或 unauthorized 错误。

该模型只支持当前本地管理员账户、同一交互会话的 consent elevation。跨账户 elevation、远程管理员控制和服务账户代执行均属于不支持路径并 fail closed；跨账户仅用合成身份单元测试验证，不创建标准用户或第二管理员账户。

Logon SID 数值在操作系统重启后回收。因此注销后令牌不再匹配不等于磁盘 ACL 已恢复；恢复记录和清理流程是强制架构组成。

## 5. 创建任务流程

1. UI 收集路径和时长，不宣称路径安全。
2. Broker 字符串规范化并拒绝空路径、不存在路径、文件路径和磁盘根。
3. Broker 验证本机固定磁盘和 NTFS；拒绝 UNC、映射网络盘、远程卷、可移动卷、FAT、exFAT、其他未经验证文件系统、系统目录、用户配置文件根、仓库和应用安装目录。
4. Broker 按路径组件检查 symlink、junction、mount point 和其他 reparse point，任一命中即拒绝。
5. Broker 打开稳定目录句柄，通过该句柄的 `GetFileInformationByHandleEx(FileIdInfo)` 取得最终路径、UInt64 volume serial 和完整 16-byte FILE_ID_128；该句柄持续持有到 ACL 事务完成。
6. Broker 验证 UI 展示路径与句柄最终路径一致，验证恢复所需权限。
7. Broker 检查规范化后相同、目录身份相同、祖先和后代活动任务；任一冲突即拒绝。
8. Broker 通过同一持续目录句柄读取 DACL、继承状态和 ACE 多重集合；存在完全相同显式 ACE 时拒绝；按 D-022 `FSLDACL` v1 wrapper 计算 baseline digest。
9. Broker 将 16 位 volume serial、FILE_ID_128 high/low、baseline digest 和任务显式恢复事实写入 `Prepared` 恢复事务，并完成 flush、回读与原子提交。
10. Broker 通过同一持续目录句柄只添加一条显式 Deny ACE，不替换 DACL、不关闭继承、不修改父目录。无法使用句柄写入安全描述符时拒绝操作。
11. Broker 通过同一持续目录句柄重新读取 DACL，验证新增 ACE 恰好一条、原 ACE 多重集合和继承状态未变化；对唯一写后 ACE 计算 `FSLACE` fingerprint，对完整写后 DACL 计算 `FSLDACL` postApply digest。
12. 验证成功后恢复事务进入 `Applied`；任务开始计时。
13. 验证失败时只回滚新增 ACE；回滚失败进入人工恢复状态并停止继续创建任务。

## 6. 解锁流程

1. 使用恢复记录中的规范化路径、卷标识和目录文件标识打开并持续持有同一目录对象句柄。
2. 通过该句柄重新读取当前 DACL。
3. 精确匹配 ACE 数量为 0：按幂等解锁处理，完成后置验证。
4. 精确匹配 ACE 数量为 1、恢复记录与必要 ACL 校验信息一致且 DACL 稳定性信任假设成立：通过同一持续句柄移除该 ACE；保留全部其他 ACE 和继承状态。
5. 精确匹配 ACE 数量大于 1、目录身份变化、恢复记录与 ACL 校验不一致、所有权不明或外部重建同元组 ACE：禁止猜测删除，进入人工恢复状态。
6. 通过同一持续句柄重新读取 DACL，验证目标 ACE 已移除且无关规则未变化。
7. 验证成功后删除恢复记录；失败时保留记录并报告。

## 7. ACL 规则

- 主体：任务 Logon SID。
- 类型：显式 `Deny`。
- 继承：目录继承与对象继承。
- 传播：无额外限制。
- 目标：仅目标目录。
- 掩码：见 `FolderSessionLock/docs/SECURITY.md`。

NTFS ACE 没有应用私有标签。因此“锁定前存在完全相同 ACE”是拒绝条件；解锁时匹配数量必须为 0 或 1。若外部主体删除应用 ACE 后重建完全相同 ACE，元组和恢复记录不能证明来源。自动移除仅在威胁模型排除该外部改写的 DACL 稳定性信任假设下成立；否则进入人工恢复。

## 8. 恢复记录

严格零持久化与可靠崩溃恢复互斥。ACL 本身持久化；Broker 只保存内存状态时，崩溃后无法证明 ACE 归属和目录身份。

最小恢复记录仅包含：任务 ID、规范化目录路径、创建规则时的 Logon SID 和 Windows Session ID、卷标识、目录文件标识、精确 ACE 元组、必要 ACL 校验信息、创建时间、计划到期时间、清理状态，以及恢复事务必需的架构版本和完整性信息。

约束：

- 不存储普通任务历史、用户访问历史、文件内容、目录内容、目录枚举、长期行为分析数据或已完成任务的无必要记录。
- 原子更新；机器范围完整性/机密性保护；恢复记录访问限制为 LocalSystem 和提升后的同账户 Broker，普通权限 UI 无直接访问权。
- 写前 `Prepared`，后置验证后 `Applied`，解锁验证后删除。
- 同会话恢复清理责任；注销、系统重启或不同 Logon SID 只清理旧 ACE，不恢复旧任务或剩余时间。
- 恢复记录损坏时禁止自动猜测删除 ACE，进入人工恢复流程。

Broker 恢复模式由自动启动 Windows 服务以 LocalSystem 身份托管，读取机器范围受保护恢复记录，并在交互登录前查找、验证和清理旧 Logon SID ACE。清理成功后尽快删除记录；失败时保留记录、保持恢复阻断状态并给出明确诊断。它不得创建限制、恢复任务、恢复剩余时间或通过整体 DACL 覆盖处理不一致。

恢复记录物理布局固定为：

```text
%ProgramData%\FolderSessionLock\Recovery
%ProgramData%\FolderSessionLock\Recovery\Records
%ProgramData%\FolderSessionLock\Recovery\Records\<RecordId>.fslr
%ProgramData%\FolderSessionLock\Recovery\Records\<RecordId>.tmp-<Guid>
%ProgramData%\FolderSessionLock\Recovery\Records\<RecordId>.bak
```

路径为编译时常量，不接受 IPC 或命令行覆盖。每任务一个记录；成功清理后删除记录、临时文件和备份；不保留普通历史、访问历史、文件内容或目录内容。

`.fslr` 容器固定为 12-byte header：4 bytes ASCII `FSLR`、little-endian UInt16 `ContainerVersion = 1`、UInt16 `Flags = 0`、UInt32 `ProtectedPayloadLength`，随后为 DPAPI payload。v1 flags 允许掩码为 0；blob 长度 1..262144；文件总长必须严格等于 `12 + length`，禁止任何尾随字节。短头/短 payload、尾随、错误 magic/version/flags/length 使用 D-022.6 固定错误并在 DPAPI 前拒绝。

payload 是 UTF-8 JSON，经 `ProtectedData.Protect`/`Unprotect`、`DataProtectionScope.LocalMachine` 保护；entropy 是 UTF-8 `FolderSessionLock.RecoveryRecord.v1` 的 SHA-256。解密明文最大 131072 bytes，必须为 without BOM 的单一 object。

payload 字段和大小写固定为：`schemaVersion`、`writerVersion`、`recordId`、`taskId`、`state`、`normalizedPath`、`volumeSerialNumber`、`fileIdHigh`、`fileIdLow`、`accountSid`、`logonSid`、`windowsSessionId`、`aceType`、`accessMask`、`inheritanceFlags`、`propagationFlags`、`aceFingerprintSha256`、`baselineDaclSha256`、`postApplyDaclSha256`、`createdUtc`、`expiresUtc`、`lastUpdatedUtc`、`cleanupAttemptCount`、`lastErrorCode`、`lastErrorMessage`。精确 JSON 类型与允许值见 `D-022`。

全部 25 字段始终存在。状态矩阵固定：Prepared 的 postApply/error 为 null、count=0；Applied 的 postApply 非 null/error null、count=0；CleanupPending 的 postApply 非 null/error null、count>=1；CleanupFailed 的 postApply/error 均非 null、count>=1。Prepared 保存预期 ACE fingerprint，Applied 前以 OS 重读实际 fingerprint 确认；Prepared 禁止预计或本地推导 postApply。

目录身份固定编码为：UInt64 `FILE_ID_INFO.VolumeSerialNumber` 的 16 位小写 hex；FILE_ID_128 bytes 0..7 为 little-endian `fileIdLow`，bytes 8..15 为 little-endian `fileIdHigh`，两个字段为无前导零 UInt64 十进制 string。恢复比较必须重建并比较完整 16 bytes；不得混用 32-bit volume serial、路径身份或另一个句柄。

ACL 摘要只使用 D-022 定义的 binary wrapper：单 ACE 使用 `FSLACE` v1；baseline/postApply DACL 使用 `FSLDACL` v1、原 ACE 顺序、有效 ACE bytes、原 ACL revision 和 `securityDescriptorControl & 0x1504`。不包含 owner、group、SACL、SELF_RELATIVE、SDDL、整个 SECURITY_DESCRIPTOR 或 ACL 未使用空间。postApply 与 fingerprint 必须从写后 OS DACL 重读，不能本地推导。

版本固定为 `schemaVersion = 1`、`writerVersion = 1.0`。v1 只接受容器版本 1 和 schema 1；更高版本、未知必需字段、未知状态或类型错误返回 `RecoveryRecordUnsupported`。不原地修改旧记录；v1 writer 不创建 `.bak`，未来迁移必须另行批准文件安全和版本合同。

事务提交：持有 `Global\FolderSessionLock.RecoveryStore.v1` → 打开 Records 目录持续句柄 → CREATE_NEW/ShareMode0/OPEN_REPARSE_POINT/WRITE_THROUGH 创建 tempHandle → 同句柄 identity 与 SYSTEM owner/精确 DACL → 写入、FlushFileBuffers、同句柄回读 → 提交前复核 → tempHandle 调用 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)`，以 Records directory handle 和相对简单叶名执行新建或 POSIX replace → 同句柄 post-commit 与目录映射验证。删除使用 canonicalHandle FileDispositionInfoEx。任何失败按 D-022.11 保留责任，禁止绝对路径和其他 fallback。

状态固定为 `Prepared`、`Applied`、`CleanupPending`、`CleanupFailed`。`Prepared` 必须先于 ACL 写入原子提交；后置验证成功后写 `Applied`；清理前写 `CleanupPending`；安全清理失败写 `CleanupFailed` 并保留记录。

恢复目录所有者为 `NT AUTHORITY\SYSTEM`；受保护显式 DACL 只允许 `NT AUTHORITY\SYSTEM`、`BUILTIN\Administrators` 和 `NT SERVICE\FolderSessionLockRecovery` 以 `FullControl` 作用于 `ThisFolderSubfoldersAndFiles`。不为 `Users`、`Authenticated Users`、`Everyone`、当前交互用户或普通 UI 授权。服务启动时复核所有者和 DACL；异常时安全失败，不用 Deny ACE 修补错误 Allow ACL。

## 9. Broker IPC

Broker 只接受：

- `ValidatePath`
- `CreateLock`
- `RemoveLock`
- `GetStatus`

协议禁止命令行、PowerShell、cmd、脚本、任意文件写入、任意 ACL 描述符和任意命令执行。

v1 传输合同固定为：

- Windows Named Pipe `\\.\pipe\FolderSessionLock.Broker.v1`，byte mode，仅本机，最小 Pipe ACL。
- 每连接恰好一个请求和一个响应，响应后关闭；不支持批量或流式请求。
- 每条消息为 4-byte little-endian `UInt32` JSON 字节长度 + 严格 UTF-8 without BOM JSON；正文 1..65536 bytes；长度不符、额外字节、多 JSON、BOM 或非法 UTF-8 拒绝。
- 请求 envelope 精确六字段：`protocolVersion`、`requestId`、`command`、`clientSessionId`、`sentAtUtc`、`payload`。
- 响应 envelope 精确七字段：`protocolVersion`、`requestId`、`command`、`success`、`serverTimeUtc`、`result`、`error`。
- 成功时 `result` 非 null、`error` 为 null；失败反之。无法解析合法 requestId/command 时两者为 null并返回 `FSL_E_MALFORMED_MESSAGE`。
- 严格 schema：大小写敏感，禁止重复/多余/缺失字段、未允许 null、注释、尾逗号、NaN/Infinity、宽松数字/枚举/日期/Guid。重复字段在业务反序列化前由流式 reader 检测。
- requestId 为非空小写 Guid D；10 分钟 replay 窗口。时间格式固定 `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`，允许偏差 120 秒。Session ID 必须与 OS 令牌一致。
- 精确错误码、字段类型、命令 payload/result、权限矩阵和恶意输入测试以 `D-027` 为唯一精确协议源。

命令 DTO 与领域模型分离：

- `ValidatePath` payload 只有 `path`；成功返回规范路径、卷根、16 位小写 UInt64 volume serial、按 FILE_ID_128 little-endian half 编码的 file IDs、固定 NTFS/Fixed/false/true。成功不作为 CreateLock 的持续授权。
- `CreateLock` payload 只有 `taskId`、`path`、`durationMilliseconds`；严格 schema 后映射现有领域值对象。成功仅在 Prepared → ACE → ACL verify → Applied → Active 全部完成后返回。
- `RemoveLock` payload 只有 `taskId`、`recoveryRecordId`；普通 UI 禁止。客户端不得提供 intent；启动模式/OS 身份映射到 `Expiration`、`Recovery` 或 `TestCleanup`。
- `GetStatus` payload 固定 `queryType`、`taskId`；支持 `ByTaskId` 和 `CurrentSession`。只返回当前账户和交互 Session 的内存任务，不是历史数据库。

协议权限矩阵：

- 普通 UI：ValidatePath、CreateLock、GetStatus；RemoveLock 禁止。
- consent-broker 内部：RemoveLock 仅 Expiration。
- recovery-service/recovery-once：CreateLock 禁止，RemoveLock 仅 Recovery。
- 隔离 VM 测试清理主体：RemoveLock 仅 TestCleanup。

Named Pipe 方案：

- 本机连接；管道名固定为 `FolderSessionLock.Broker.v1`，同时使用独立高熵握手值。调用方不得提供其他 Pipe 名。
- Pipe DACL 只允许发起会话 Logon SID 和 Broker 自身。
- Broker 对客户端执行身份验证：Account SID、目标 Logon SID、Windows Session ID、连接进程身份、一次性握手值、请求重放状态。
- Broker 对每个请求重复路径、目录身份、权限和安全边界验证。
- 重复任务 ID 返回确定结果，不重复执行 ACL 变更。
- 客户端 JSON 中出现 SID、ACL/SDDL/ACE、恢复/安装路径、服务/Pipe 名、命令/脚本/可执行路径、`LockRemovalIntent` 或清理模式时返回 `FSL_E_FORBIDDEN_INPUT`，即使值为空也拒绝。

Pipe ACL、Logon SID 和随机握手不能对抗已经控制同一用户会话的恶意进程。D-031 本地单用户管理员 Release 可显式 unsigned，但必须安装在管理员保护目录、禁止普通用户替换并验证客户端身份；未签名状态必须如实记录。公开或企业生产分发仍须通过未来决定建立真实签名门。安装目录普通用户可写、Broker 可替换或 IPC 身份验证缺失在任何模式下均为发布阻断。

CP4 固定架构：

- consent-broker 连接状态机仅允许 ClientHello、ServerHello、CommandRequest、CommandResponse、close；四种 frame 的精确字段、版本、null 规则和序列错误以 D-027.10 为准。
- CLI request-id/session-id、ClientHello、CommandRequest 外层与内层应用 request 必须完全绑定。ClientHello 与 ServerHello 各使用 32-byte Base64URL nonce；CommandRequest 使用 D-027.11 的 canonical binding string、SHA-256 bindingProof 和恒定时间比较。
- ClientHello 后按 D-027.12 固定顺序验证 framing/schema/version/time、CLI 绑定、OS Pipe 客户端 PID/存活/启动时间、进程和令牌 Session、Account SID、Logon SID、Broker Account/Logon/Session 及命令权限；全部成功后才计算 Replay key 并原子 CreateNew。Pipe 客户端模拟必须 finally 恢复 Broker 身份；身份或授权失败绝不创建 Replay。
- Replay Registry 为机器范围 `%ProgramData%\FolderSessionLock\Replay\v1`，使用固定 replay key、精确 `.fsrr` schema、原子 CreateNew、所有权四元组和受保护 `Global\FolderSessionLock.ReplayRegistry.v1` 互斥锁；普通 UI 无直接访问。
- 状态为 Handshaking、ChallengeIssued、Executing、Succeeded、Failed、RolledBack、RecoveryRequired、Abandoned；timeout、lease、renewal、retention、失败撤销和 owner 崩溃规则严格见 D-027.14–D-027.16。RecoveryRequired 无自动过期，新 Broker 不得接管未知副作用。
- AwaitClientHello 首个有效非 ClientHello frame 使用 HANDSHAKE_REQUIRED/ServerHello；成功 ServerHello 后的非法序列使用 PROTOCOL_SEQUENCE_INVALID/CommandResponse。active Replay 使用 REQUEST_IN_PROGRESS/ServerHello；terminal 或 RecoveryRequired 使用 REPLAY_DETECTED/ServerHello。frame、retryable、field 与标识符只采用 D-027.13 表格。

## 10. 路径与 TOCTOU

- 字符串规范化只用于显示和初筛。
- 安全身份使用稳定目录句柄、卷标识和目录文件标识。
- 检查目标及所有祖先组件的 reparse 属性；任一命中即拒绝。
- 应用 ACL 前后复核目录身份。
- DACL 读取、写入、后置验证和移除全部绑定到同一持续持有目录句柄；不得在事务中重新按字符串路径打开目标。
- UI 验证后路径被替换时安全失败。
- 不跟随未知重解析目标。
- 不通过字符串前缀判断父子目录。

## 11. 重复与嵌套策略

- 同一目录身份已有活动任务：拒绝。
- 祖先或后代目录已有活动任务：拒绝。
- 不自动合并、不自动延长、不建立第二 ACE。

该策略消除继承 Deny、解锁顺序和多个计时所有者之间的冲突。

## 12. 计时与并发

- Core 的 `IClock` 同时提供 UTC、单调 timestamp、elapsed 计算和可取消 delay。生产 `SystemClock` 复用 `TimeProvider.System`；测试时钟可独立推进墙钟与单调时间。
- `FolderLockTask` 是不可变快照，保存 `StartedAtUtc`、`StartedTimestamp` 和 `ExpectedExpiryUtc`。UTC 字段只用于显示、日志和恢复记录；到期与剩余时间使用单调 elapsed，剩余时间下限为零。
- 状态转换只通过集中状态机和 `LockTaskManager` 的单一同步门执行。`Completed`、`RecoveryRequired` 无出站转换；表外转换不替换快照。
- 路径占用状态为 `Created`、`Activating`、`Active`、`Unlocking`、`UnlockFailed`、`RecoveryRequired`；`Completed`、`ActivationFailed` 不再占用路径。
- `IFolderPathRelationService` 将 Same、Ancestor、Descendant、Unrelated 关系判断留在平台适配边界；Core 不硬编码 Windows 安全路径规则。
- 到期扫描先原子执行 `Active -> Unlocking`。只有取得转换所有权的调用方可使用 `Expiration` 调用 `RemoveLockAsync`；成功进入 `Completed`，确定失败进入 `UnlockFailed`，结果不确定的异常进入 `RecoveryRequired`。
- `LockRemovalIntent` 只包含 `Expiration`、`Recovery`、`TestCleanup`、`AdministrativeCleanup`。`UnlockFailed -> Unlocking` 只允许后三种清理意图；普通 UI 没有解除意图。
- `LockTaskScheduler` 提供 `ProcessDueTasksAsync` 和 `RunAsync`。每 consent-broker 进程至多一个 Active task、一个 scheduler、一个串行 loop。每轮重读 task snapshot 和 monotonic timestamp；无 Active 即结束，remaining<=0 原子取得 `Active -> Unlocking`，否则等待 `min(remaining, 30 seconds)` 后重算。禁止 Windows Task Scheduler、多 Timer、每 task线程、fire-and-forget、UI scheduler或多进程共享到期所有权。
- D-028 固定 CP6 生命周期停止流程：cleanup first-task error 优先，scheduler error 仅写入受保护内部日志。停止流程先取消并等待 scheduler，再无条件执行 administrative Cleanup。Cleanup 对适用任务建立稳定快照顺序并完整遍历；每个任务仍通过集中状态机取得唯一 `Unlocking` 所有权，单任务失败不停止后续任务。
- 生命周期组合结果固定为：scheduler success/Cleanup success 返回 Cleanup success count；scheduler success/Cleanup failure 返回稳定顺序中的第一个 Cleanup task error；scheduler failure/Cleanup success 返回 Cleanup success count；scheduler failure/Cleanup failure 返回第一个 Cleanup task error。scheduler error 仅写入受保护内部日志。
- `LockTaskScheduler.RunAsync` 仅把生产 loop 的未预期非取消异常归一为 `lock_task.scheduler.loop.exception` / `The lock task scheduler loop terminated unexpectedly.`。lifecycle 只在收到该精确结果时以 protected `Scheduler` component、`Error` level写固定 catalog message；预期 token 取消不写该事件。lifecycle 自身 stop 异常、Cleanup failure、task 状态转换、已有更具体错误和 logger failure不得复用此 code/message；事件不携带原异常 message、`ToString()`、stack、内部类型、路径、SID、HRESULT或Win32 message。
- 后续 Cleanup task errors 只作为受保护附加诊断；主错误按 Cleanup 实际稳定处理顺序确定，不按异步完成顺序。`RecoveryRequired`、ACL 状态未知或恢复失败始终保持对应 Cleanup task error 的对外优先级。
- administrative Cleanup 的异常合同固定：`RemoveLockAsync` 抛异常使用 `lock_task.administrative_cleanup.exception` / `The administrative cleanup ended without a confirmed result.`；ACE 已移除但 `Completed` 状态记录失败使用 `lock_task.administrative_cleanup.state_update_failed` / `The lock was removed but its completed state could not be recorded.`。两者 category 均为 `UnrecoverableError`，任务均进入 `RecoveryRequired`。
- 活动进程内系统时钟前拨、后拨、时区或夏令时表示变化不得重复触发或延长任务。
- 系统重启或 Logon SID 改变后的行为仍由后续恢复阶段处理；阶段 2 不持久化或恢复任务。

## 13. 故障矩阵

| 故障 | 行为 |
|---|---|
| UI 正常关闭/崩溃 | Broker 继续计时；重新连接后读取状态 |
| IPC 断开 | 不重复应用 ACL；任务仍由 Broker 所有 |
| Broker 正常退出 | 无论 scheduler 是否失败都完整遍历 Cleanup；失败保留恢复记录并返回稳定顺序中的第一个 Cleanup task error；scheduler error 仅内部记录 |
| Broker 崩溃 | ACE 保留；下次启动恢复或清理 |
| 注销/关机通知 | 尽力解锁；失败保留恢复记录 |
| 突然断电 | 依赖后续启动和恢复记录清理 |
| 重启、注销或新会话开始 | 自动启动服务在交互登录前运行 Broker 恢复模式；只清理旧 ACE；不恢复任务或剩余时间；失败保留记录并阻断恢复成功状态 |
| 恢复记录损坏 | 禁止猜测删除；人工恢复 |
| 路径身份变化 | 禁止对新目录应用旧任务操作 |
| ACL 外部漂移 | 仅在精确所有权可证明时移除 |
| UAC 被拒绝 | 不报告成功；保留残留状态 |
| 标准用户输入另一管理员凭据 | Broker 身份不匹配；安全失败并显示“不支持跨账户提升” |

## 14. 访问警告扩展

访问警告为可选扩展点，默认关闭。阶段 1 至阶段 5 禁止修改 Audit File System、添加 SACL 或依赖 Security 日志。阶段 6 独立批准后，Windows 层可通过目标 SACL、Audit File System 和 Security 日志实现尽力而为的 `4656` Failure 检测、去重和通知。`4663` 无 Failure 事件，不作为失败访问来源。审计不可用不影响核心 ACL 限制。

## 15. 阶段 3 实现边界

- `WindowsSessionIdentityProvider` 使用当前进程访问令牌的 `TokenUser`、带完整 `SE_GROUP_LOGON_ID` 的唯一 `TokenGroups` 项和 `TokenSessionId` 返回 `SessionIdentity`；不回退 Account SID。
- `WindowsFolderPathValidator` 只接受本机固定 NTFS 普通目录，检查目标及祖先 reparse point，并持有包含卷身份、`FILE_ID_128` 和最终路径的持续目录句柄。
- `DirectoryAclEditor` 只接收 `SafeFileHandle`，通过 `GetSecurityInfo` 和 `SetSecurityInfo` 读取、添加、后置验证、rollback 和精确移除 DACL ACE；不接受路径，不请求 SACL，不重建整个 DACL。
- `WindowsFolderLockService` 在进程内保存任务 ID、规范化路径、目录身份、持续句柄和精确 ACL 操作记录；处理重复任务、路径冲突、幂等移除和 ACL 漂移安全失败。
- 创建 ACL 前后均通过独立核对句柄复核字符串路径仍映射到持续句柄的目录身份。核对句柄不执行 ACL 操作；路径替换时应用 ACE只可能写入原持续句柄对象，失败时通过该句柄精确 rollback。
- `FolderSessionLock.Windows.Tests` 可直接调用 Windows 实现，仅用于批准临时目录的平台集成验证。产品依赖方向仍为 `Broker -> Windows`；App 不引用 Windows。Broker 的真实组合、提升、IPC、持久化恢复和生命周期属于阶段 4。

## 16. 阶段 4 部署与运行合同

- 服务内部名：`FolderSessionLockRecovery`。
- Display Name：`Folder Session Lock Recovery Service`。
- Description：`Removes verified Folder Session Lock ACL entries left by previous Windows logon sessions.`
- 服务账户：`LocalSystem`；启动类型：`Automatic`；`DelayedAutoStart = false`；启用唯一服务 SID `NT SERVICE\FolderSessionLockRecovery`。
- 现有 `FolderSessionLock.Broker` 项目是交互 Broker 与恢复服务宿主；项目文件为 `src\FolderSessionLock.Broker\FolderSessionLock.Broker.csproj`。
- 服务入口：`FolderSessionLock.Broker.exe --mode recovery-service`。
- 隔离 VM 一次性诊断入口：`FolderSessionLock.Broker.exe --mode recovery-once`；不注册服务、不接受路径参数、不创建新限制。
- 交互入口：`FolderSessionLock.Broker.exe --mode consent-broker --pipe-name FolderSessionLock.Broker.v1 --session-id <UInt32> --request-id <lowercase Guid D> --client-process-id <UInt32> --client-process-creation-filetime <UInt64 decimal>`。Account SID 与 Logon SID 不进入命令行。
- 服务注册 binPath：`"%ProgramFiles%\FolderSessionLock\FolderSessionLock.Broker.exe" --mode recovery-service`。
- 安装根：`%ProgramFiles%\FolderSessionLock`；Broker/Service 文件：`%ProgramFiles%\FolderSessionLock\FolderSessionLock.Broker.exe`；数据根：`%ProgramData%\FolderSessionLock`；readiness：`%ProgramData%\FolderSessionLock\Readiness\recovery-readiness.v1.json`；日志根：`%ProgramData%\FolderSessionLock\Logs\v1`。ProgramFiles/ProgramData 均通过对应 Known Folder API 取得，不使用环境变量作为信任来源。
- 安装目录 ACL：`SYSTEM: FullControl`、`Administrators: FullControl`、`Users: ReadAndExecute`；不为 `Authenticated Users` 增加写权限。禁止从仓库 `bin`、`obj`、TEMP、用户目录或网络路径注册服务。
- 未知参数、自定义恢复路径、任意 Pipe 名、任意 service name/binPath、任意 ACL 描述符、shell、PowerShell、cmd 和脚本全部安全失败。
- 跨账户提升在创建 Pipe、Replay、恢复记录、路径或 ACL 操作前拒绝，consent-broker exit 20；UI 错误码 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`，显示“不支持跨账户提升”。

## 17. 阶段 4 环境与证据合同

- 唯一特权集成环境：计算机名 `FSL-STAGE4-VM`，Windows 11 Pro/Enterprise 专用可丢弃 VM，快照 `FolderSessionLock-Stage4-Clean`。
- 非该机器时，服务、LocalSystem、自动启动、登录前执行、UAC、注销、重启、Program Files/ProgramData ACL 和签名系统测试停止；设计、实现、单元测试、非特权测试和静态审查继续。
- 当前机器 `AGREELIN` 不是获准 VM。
- VM 内只允许操作服务 `FolderSessionLockRecovery`；禁止修改其他服务或 SCM 全局配置。最多 3 次注销、3 次完整重启，每次前保存证据、确认目标只位于 `%TEMP%\FolderSessionLock.Tests\<Guid>`、记录已提交且无仓库或真实用户目录目标，并输出场景编号。
- 测试身份：仅使用当前本地管理员账户；不得创建 `FSL-Standard`、`FSL-Admin` 或替代专用测试账户。同账户 UAC consent 由当前用户人工确认。
- 当前本地 Release 使用显式 unsigned 模式；不得创建测试签名证书。六个第一方 PE 的实际 Authenticode 状态必须为 `NotSigned` 且 signer 为 null。
- 证据仓库目录：`docs\evidence\stage-4\<RunId>\`，RunId 为 `yyyyMMddTHHmmssZ-<short-guid>`。精确文件清单和 `manifest.json` 结构见 `D-026`；`TASKS.md`、`DEVLOG.md` 必须引用 RunId，reviewer 必须核验 manifest 与工件一致。
- 登录前恢复只读取受保护记录、验证目录身份和精确 ACE、移除旧 ACE、验证恢复并删除已完成记录；不恢复旧任务、不创建 ACE、不访问网络、不读取目录内容、不扫描无关目录、不修改审计策略。

## 18. 恢复批处理架构

固定执行链：

```text
参数验证
→ D-023 受保护路径复核
→ Records 顶层完整枚举与分类
→ 4096/1024 上限检查
→ 规范 .fslr 按 StringComparer.Ordinal 排序
→ 逐记录调用 CP7 单记录清理
→ 重新统计剩余记录
→ 生成结构化摘要与唯一退出码
```

- 不递归、不跟随 reparse、不边枚举边清理。规范记录为小写 Guid D `<recordId>.fslr`；`.bak`、`.tmp-*`、非法构件和 recordId mismatch 按 D-022.10 分类并保留。
- 单记录失败继续后续记录。`CleanupPending` 后进入 ACL 临界区，取消只在该记录达到删除成功、`CleanupFailed` 或 `RecoveryRequired` 后生效。
- 结果类别与摘要字段固定为 D-022.10。主错误是目录前置错误，或稳定顺序中的第一个非成功记录错误；scheduler error 仅内部记录。
- `recovery-once` 只返回 0、2、10、11、12、13、14、15，精确含义和优先级以 D-024.1 为准。

## 19. 恢复服务生命周期与 readiness

- `recovery-service` 启动时只扫描一次，之后通过 D-030 受保护机器范围 snapshot 托管 readiness，不周期扫描恢复记录、不使用 FileSystemWatcher、不提供公共 readiness Pipe。
- ProgramData 通过 `SHGetKnownFolderPath(FOLDERID_ProgramData)` 取得。Readiness 目录、canonical/temp、SYSTEM owner、四ACE protected DACL、Users只读边界及 `Global\FolderSessionLock.RecoveryReadiness.v1` publisher mutex固定为D-030；唯一publisher为recovery-service，UI/consent-broker/recovery-once只读。
- 内部状态：`StartPending → Preflight → Scanning → Ready/RecoveryBlocked → Stopping → Stopped`。Ready 与 RecoveryBlocked 均对应 `SERVICE_RUNNING`；启动前置、protected logger或readiness publish失败不得Running。
- snapshot为严格UTF-8 without BOM、1..16384 bytes、十二字段schema 1，含serviceName、instanceId、sequence、state、scan/publish/expiry时间、remaining与error。服务每10 seconds heartbeat，`validUntilUtc=publishedUtc+30 seconds`；reader允许最多5 seconds future skew，过期或任何矩阵/安全/identity错误fail closed。
- publish使用retained directory/temp handles、同句柄SYSTEM owner/DACL复核、FlushFileBuffers、class65相对原子replace和post-commit identity/security/content/leaf mapping验证。reader使用同一retained handle读取并前后复核。禁止路径move/replace/delete。
- `RecoveryBlocked` 拒绝 CreateLock，但允许 ValidatePath 与脱敏 GetStatus。CreateLock在路径/ACL前读取snapshot；只有完整Ready矩阵才继续，所有内部readiness错误统一公开为`FSL_E_RECOVERY_BLOCKING`。
- SCM Stop固定发布Stopping、阻断新CreateLock/记录、等待ACL临界区、最后heartbeat、canonical verified-handle delete、close、retained directory确认名称消失、Stopped。删除失败不路径重试；残留snapshot自然stale。
- `RecoveryReadinessState`、十二字段`RecoveryReadinessSnapshot`、publisher/reader接口固定为D-024.2与D-030。

## 20. 受保护路径安全分层

- 组合根只生成 InstallDirectory、RecoveryRoot、RecoveryRecordsDirectory、ReplayDirectory 的固定 ExpectedPath；CLI/IPC 不得输入。
- 非特权 orchestration 依赖 `IProtectedPathSecurityVerifier`，按 D-023.1 fail closed。生产 Windows verifier 未实现时使用 `FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED`，不能进入 Ready、恢复或 CreateLock；测试可注入显式 fake。
- 生产 `WindowsProtectedPathSecurityVerifier` 必须使用持续目录句柄、OPEN_REPARSE_POINT、final path、本地固定 NTFS、FILE_ID_128 前后复核、owner/DACL/ACE/继承保护校验；不得使用 SDDL 文本等价猜测。
- 用户合同原文分别规定“CP6”完成接口/状态机/fake、“CP8”完成 Windows verifier、ACL 创建验证与 VM 安全集成；项目现行 checkpoint 编排由 `PLAN.md`、`TASKS.md` 与下一次 `stage_director` 结论确定，不得静默重命名或删除任一边界。

## 21. 恢复记录文件句柄架构

- 三类文件 `.fslr`、`.tmp-*`、`.bak` 使用 `RecoveryRecordFileKind` 和同一 `IRecoveryRecordFileSecurity`；唯一 owner SYSTEM，DACL 为 protected ACL revision 2、三个按序显式 Allow FullControl ACE（SYSTEM、Administrators、服务 SID），mask `0x001F01FF`、flags 0，无继承或额外 ACE。
- writer 只对新 tempHandle 调用 ApplyAndVerify；canonical/bak 只 Verify。owner 设置只临时使用 `SeRestorePrivilege` 并 finally 恢复；revert failure 停止后续写入和 CreateLock。
- Records directory handle、old canonical handle、tempHandle 在提交/更新期间持续打开。新建与更新由 tempHandle 的 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)` 相对目录简单叶名完成；新建 flags=0，更新 flags=`0x00000003`，old handle 在 replace 期间保持打开。production 禁止 class 10、SetFileInformationByHandle class 22/class 3、绝对目标或其他 fallback；平台缺少该语义时 fail closed。
- canonical 删除只对已验证同一 handle 使用 FileDispositionInfoEx POSIX delete；disposition 成功后关闭该 handle，再由 retained directory handle 确认名称消失并复核目录 identity。名称仍存在、枚举失败、identity 变化或无法证明关闭/删除时进入 RecoveryRequired。temp 清理仍只使用同一 temp handle；禁止所有路径 move/replace/delete、重新打开后删除 replacement 与 SetNamedSecurityInfo。
- post-commit 按 temp identity、links、owner/DACL、完整 payload、唯一目录映射、目录 identity 顺序验证；失败统一 `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED` 并进入 RecoveryRequired。
- 配对辅助构件必须完成 handle identity、SYSTEM owner、精确 DACL验证后才 auxiliary；安全不匹配使用 `FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID` 并 blocking。
- `AGREELIN` 只验证 wrapper/fake/TEMP 句柄操作和 failure injection；真实 ProgramData SYSTEM owner/service SID DACL/LocalSystem writer/普通用户拒绝仅 `FSL-STAGE4-VM`。

## 22. consent elevation 与 consent-broker 生产生命周期

- production Broker 唯一路径为 `SHGetKnownFolderPath(FOLDERID_ProgramFiles)` 解析的 `<FOLDERID_ProgramFiles>\FolderSessionLock\FolderSessionLock.Broker.exe`。UAC 前先执行 D-023 InstallDirectory、普通文件、non-reparse、final path、目录归属和 file identity 验证；失败返回 `FSL_E_BROKER_PATH_UNTRUSTED`。CP10 继续负责 Authenticode/publisher。
- UI launcher 固定使用 `ShellExecuteExW`：`runas`、已验证绝对 Broker path、专用固定参数 encoder、已验证 install directory、`SW_HIDE`，flags 为 `SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI | SEE_MASK_UNICODE`。UAC 取消返回 `FSL_E_ELEVATION_CANCELLED`；其他失败或空 process handle 返回 `FSL_E_ELEVATION_LAUNCH_FAILED`。
- UI identity snapshot 固定为 `InitiatingClientIdentity(ProcessId, ProcessCreationFileTime, AccountSid, LogonSid, WindowsSessionId)`，仅存在内存。Broker bootstrap 先验证 PID+creation time，再重取 UI token identity；失败 exit 21/22，Account SID 不同 exit 20。只有成功后才以可信 UI Logon SID 与 Broker Account SID 创建 protected Pipe DACL并启用 `PIPE_REJECT_REMOTE_CLIENTS`。
- Broker Pipe connect 等待 15 seconds；UI 从 ShellExecuteExW 成功返回起并发等待 Pipe/process exit 20 seconds。连接前 timeout 可 `TerminateProcess(..., 29)` 并等待 5 seconds；成功返回 `FSL_E_BROKER_CONNECT_TIMEOUT`，无法证明清理返回 `FSL_E_BROKER_PROCESS_CLEANUP_FAILED`。连接后禁止 UI 终止 Broker。
- consent-broker exit code 关闭集合为 0、2、20、21、22、23、24、25、26、27、28、29。exit 2 映射固定为 `FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / false / null，且不公开 CLI、参数、路径、命令行、Win32 或异常细节。连接前优先级为 invalid args → identity unavailable → process mismatch → cross-account → pipe initialization → connect timeout → internal；连接后为 lifecycle cleanup → response write → protocol before response → internal → handled/lifecycle completed。应用失败响应可以 exit 0；合法 CommandResponse 优先于后续 process exit。
- 每个 consent-broker 进程固定一个 listener、一个客户端、一次 ClientHello/ServerHello/CommandRequest/CommandResponse，响应后不再 Accept。ValidatePath/GetStatus/普通 UI RemoveLock 拒绝/CreateLock 副作用前失败在响应送达后 exit 0；GetStatus 不启动 scheduler。
- CreateLock 成功响应后 Broker 保持运行，scheduler 持有唯一 Active task并在到期执行 Expiration Cleanup；安全完成 exit 0，Cleanup 或 RecoveryRequired 未安全收敛 exit 27。响应前断开：无副作用 exit 25；确定 Active task继续到期；未知副作用进入 RecoveryRequired。
- production `BrokerCompositionRoot` 必须显式组合 D-029 列出的 Windows identity/path/ACL、recovery file/security/readiness、Replay、protocol/transport、task/scheduler/lifecycle、logging 与 clock 依赖。禁止 AllowAll verifier、fake identity/readiness、in-memory recovery、test cleanup hook、test path或 debug Broker path；缺少安全依赖 fail closed exit 28。
- 非 VM 环境只实现 wrapper、resolver、identity/bootstrap、exit mapping、race abstraction、production composition 与 fake tests；实际同账户 UAC、elevated Broker、Program Files 安装、SCM/LocalSystem 与恢复只允许 `FSL-STAGE4-VM`。真实跨账户凭据和专用账户场景已由 D-031 取消。
- `BrokerPublisherThumbprint` 为 null 或精确空字符串时，App 使用显式 unsigned 本地模式且 Authenticode verifier 不调用 platform；固定 Program Files 路径、owner/DACL、普通文件、non-reparse、final path、identity、hash/TOCTOU 与安装不可替换门保持不变。仅空白或畸形非空 pin fail closed；有效 40 位 pin 保留原 signed 精确匹配。

## 23. D-030 生产路径分类与时长策略

- production `LockDurationPolicy` 固定包含式 60 seconds..24 hours（60000..86400000 ms）；Broker独立验证，UI和debug配置不能覆盖。
- repository classifier从已验证target handle逐级handle-relative到卷根检查`.git`、`.hg`、`.svn`文件或目录，OPEN_REPARSE_POINT且不读内容；命中拒绝，任何indeterminate fail closed。无repository root配置、环境变量、cwd、git.exe、PATH、CLI或用户设置。
- synchronization classifier先对retained target handle调用`CfGetSyncRootInfoByHandle(CF_SYNC_ROOT_INFO_STANDARD)`；Cloud Files只接受已批准的Win32与`0xD000CF13` not-under-root HRESULT。SkyDrive路径解析固定为：创建`IKnownFolderManager`→`GetFolderIds`必须S_OK→按GUID二进制精确查找`FOLDERID_SkyDrive`→仅注册存在时调用`SHGetKnownFolderPath(FOLDERID_SkyDrive, 0, initiatingUserToken, out path)`。注册集合不含SkyDrive返回`KnownFolderNotRegistered`/`Exists=false`；`0x80070002`/`-2147024894`与`0x80070003`/`-2147024893`分别表示当前用户实例或叶项、父链不存在并返回`Exists=false`。只有这三个场景允许继续。禁止CREATE/DONT_VERIFY/DEFAULT_PATH、字符串/显示名/canonical name注册比较、E_INVALIDARG未注册解释、低16位/facility mask/raw Win32 2/3/NTSTATUS/重编号。GetFolderIds非S_OK，SkyDrive HRESULT除S_OK/两个not-found外，或S_OK+null/empty/非绝对路径均fail closed。path调用前为null；任何返回的非null pointer均在受控字符串复制后或失败路径中`CoTaskMemFree`。S_OK有效路径继续retained handle、reparse、final path、DirectoryIdentity与Same/Descendant检查。
- ValidatePath/CreateLock固定顺序：基础绝对路径/NTFS → reparse/final path → identity → 系统/用户/安装保护 → repository → Cloud Files → SkyDrive → ACL capability → CreateLock前最终mapping。

## 24. Protected JSON Lines logging

- production唯一provider为`ProtectedJsonLinesLoggerProvider`，直接安全file handle；根`%ProgramData%\FolderSessionLock\Logs\v1`，模式子目录`consent-broker`、`recovery-service`、`recovery-once`。目录和文件owner SYSTEM，protected DACL只允许SYSTEM、Administrators、service SID FullControl；普通用户不可列出或读取。consent-broker不创建或修复Logs root。
- 每进程独立文件`yyyyMMddTHHmmssfffffffZ-<pid>-<instanceGuid>-<0000..9999>.jsonl`；UTF-8 without BOM、LF、每行一个精确十四字段object、含LF最多4096 bytes。单写入门、每事件完整WriteFile+LF+FlushFileBuffers。
- event字段、enum、固定message catalog、redaction与path hash精确按D-030。禁止SID、SDDL、ACL、DPAPI、nonce、bindingProof、credential/token/private key、full path/command line、stack/Exception/FormatMessage和自由properties。
- 8MiB或UTC跨日rotation，每进程最多10000文件；14days、每模式32关闭文件、全局256MiB。recovery-service启动及每24hours清理安全已关闭非活跃文件；其他模式不执行全局清理。安全异常构件不删除并返回`FSL_E_PROTECTED_LOG_ARTIFACT_INVALID`。
- logger初始化或永久写入失败使用`FSL_E_PROTECTED_LOGGER_UNAVAILABLE`。consent-broker strict CLI后、Pipe前初始化失败exit28；副作用后失败先完成lifecycle/Cleanup，exit27优先于28；合法response保持最终。service启动失败不Running，运行中失败发布RecoveryBlocked后受控停止；recovery-once使用exit15。

## 25. D-030 生产组合边界

- production composition必须注入真实machine readiness store、repository classifier、Cloud Files/SkyDrive classifier、fixed duration policy、single scheduler model和protected logger factory；禁止in-memory/always-ready、empty repository、always-not-sync、Console/Debug/Null/test logger或user-writable path provider。
- `AGREELIN`只验证interfaces、fakes、TEMP handle operations、failure injection、rotation/retention、static composition、build/tests/format/reviewer。真实ProgramData ACL、service SID、LocalSystem publisher、SCM Stop、跨用户读取、真实Cloud Files/OneDrive、生产Logs ACL、并发日志和reboot stale只在`FSL-STAGE4-VM`。
