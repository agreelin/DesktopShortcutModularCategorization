# Folder Session Lock 架构决定

状态值：`已决定`。

2026-07-23：`D-001` 至 `D-030` 均已决定。八份阶段 0 项目文档已经迁入 `FolderSessionLock/`，不存在根目录同名副本。阶段 4 的恢复记录、隔离 VM、系统操作、精确标识符、签名、人工验证、Broker IPC v1 协议、同账户 consent elevation 生产生命周期、跨进程 readiness、生产分类与 protected logger 已由用户明确批准。

## D-001：.NET 8 WPF

- 状态：已决定。
- 决定：v1 使用 C#、.NET 8、WPF、MVVM。
- 理由：Windows-only；稳定桌面与 Win32 互操作；避免 WinUI 3 部署复杂度。

## D-002：Logon SID 作为锁定主体

- 状态：已决定。
- 决定：使用访问令牌中的 `S-1-5-5-X-Y` Logon SID，不使用 Account SID。
- 结果：同账户其他登录会话不匹配；重启后 SID 数值回收，必须由 Broker 恢复专用模式清理遗留 ACE。

## D-003：普通 UI 与独立 Broker

- 状态：已决定。
- 决定：UI 普通权限运行；Broker 是任务、计时、真实 ACL 操作、恢复记录和清理的唯一所有者。启动恢复由同一受信 Broker 的恢复专用运行模式执行，不引入第二个任意 ACL 写入主体。
- 结果：UI 关闭或崩溃不解除任务；UI 不直接引用真实 ACL 写入实现。

## D-004：最小恢复记录

- 状态：已决定。
- 决定：不采用严格零持久化；采用最小、受保护、事务化恢复记录；恢复完成后尽快删除。
- 允许数据：任务 ID、规范化目录路径、创建规则时的登录会话标识、精确 ACE 描述、必要 ACL 校验信息、创建时间、计划到期时间、清理状态，以及实现恢复事务所必需的版本、目录身份和完整性信息。
- 唯一用途：正常到期解除；Broker/UI 异常退出后的恢复；新登录或系统重启后的遗留 ACL 清理。
- 禁止数据：普通任务历史、用户访问历史、文件内容、目录内容、长期行为分析数据和已完成任务的无必要记录。

## D-005：单条显式 Deny ACE

- 状态：已决定。
- 决定：只在目标目录添加一条显式 Deny ACE，使用目录/对象继承；不修改父目录；不整体替换 DACL；不关闭继承。

## D-006：最小 Deny 权限矩阵

- 状态：已决定。
- 决定：拒绝枚举、读取、创建、写入、扩展属性、遍历/执行、删除子项、属性修改和删除所需权限；不拒绝 `ReadPermissions`、`ChangePermissions`、`TakeOwnership`、`Synchronize`；禁止 `Deny FullControl`。
- 结果：目标是阻止当前交互用户的新普通用户态请求执行枚举、读取、创建、写入、删除、重命名、移动和修改；恢复进程保留读取和修改 DACL 所需能力。
- 限制：不承诺阻止父目录 `DeleteChild`、旧句柄、管理员或特权绕过。

## D-007：完全相同 ACE 冲突

- 状态：已决定。
- 决定：锁定前已有完全相同显式 ACE 时拒绝任务；解锁匹配数大于一或状态与恢复记录不一致时停止自动删除。
- 理由：NTFS ACE 没有应用私有来源标签。

## D-008：路径支持范围

- 状态：已决定。
- 决定：v1 只支持本机固定磁盘、NTFS 文件系统、普通目录和可安全规范化并验证身份的路径。
- 拒绝：UNC、映射网络盘、FAT、exFAT、其他未经验证文件系统、磁盘根、系统目录、用户配置文件根、仓库、应用安装目录和支持范围外路径。

## D-009：拒绝 reparse path

- 状态：已决定。
- 决定：目标或任一祖先组件包含 symlink、junction、mount point 或其他 reparse point 时拒绝。
- 结果：使用持续目录句柄、卷标识和目录文件标识防止字符串路径替换。

## D-010：重复和父子路径

- 状态：已决定。
- 决定：规范化后相同、目录身份相同、祖先目录或后代目录已有活动任务时，拒绝后发任务；不合并、不延长、不增加第二 ACE。

## D-011：UI 关闭行为

- 状态：已决定。
- 决定：UI 关闭或崩溃不解除限制；Broker 继续计时，到期自动解锁。
- UI 职责：创建任务、查看状态、显示剩余时间、展示错误、请求解除允许解除的任务。

## D-012：新登录会话行为

- 状态：已决定。
- 决定：注销、系统重启或新登录会话开始后，旧任务失效；只清理旧 ACE，不恢复旧任务或剩余限制时间。

## D-013：访问警告默认关闭

- 状态：已决定。
- 决定：阶段 1 至阶段 5 不修改 Audit File System、SACL 或 Security 日志权限；阶段 6 开始前必须通过独立批准门。
- 结果：审计不可用、权限不足或用户拒绝时，核心 ACL 限制仍可工作。

## D-014：访问警告是尽力而为

- 状态：已决定。
- 决定：批准后优先研究 `4656` Failure；`4663` 不作为失败事件；通知去重限流；不承诺每个 I/O 一次弹窗。

## D-015：Broker 生产发布安全

- 状态：已决定。
- 开发：允许未签名构建用于本机测试，必须标识为非生产构建。
- 公开/企业生产分发：作为未来 Stage 7 checkpoint，当前不激活；必须由另一个明确的公开/企业/签名产品决定激活。若未来激活，Broker 必须代码签名。D-031 当前本地单用户管理员交付固定允许如实 unsigned，缺少真实签名证书或签名流水线不得阻止 Stage 4 完成或 Stage 5 entry；它仍安装到管理员保护目录、普通用户不得替换或修改，恢复专用模式由自动启动 Windows 服务托管，IPC 必须限制访问并验证客户端身份，只公开强类型最小 ACL 接口。
- 发布阻断：只有未来公开/企业 checkpoint 被独立决定激活后，Broker 未签名才是该分发方式的阻断；安装目录普通用户可写、Broker 可被普通用户替换、IPC 身份验证缺失、暴露任意命令/脚本/PowerShell/cmd/任意 ACL 描述符在当前本地范围仍始终阻断。

## D-016：仓库布局

- 状态：已决定。
- 决定：在当前仓库创建独立产品根目录 `FolderSessionLock/`。
- 约束：所有产品 solution、源码、测试和项目文档位于该目录；不得将现有仓库内容整体替换为 Folder Session Lock；不得修改无关项目；不得替换根 `README.md` 或进行无关修改，只允许维护简短项目导航。
- 阶段 1：第一个 checkpoint 建立该目录边界，禁止在仓库根创建产品 solution 或项目。

## D-017：Broker 受信启动恢复模式

- 状态：已决定。
- 决定：使用同一受信 Broker 的恢复专用运行模式，由自动启动 Windows 服务以 LocalSystem 身份在系统启动期间、交互登录前执行。
- 权限：恢复模式只允许读取机器范围受保护恢复记录并精确清理旧会话 ACE；禁止创建、延长或恢复限制任务。
- 记录保护：只允许 LocalSystem 和提升后的同账户 Broker 访问，使用机器范围完整性/机密性保护。
- 启动合同：遗留记录扫描必须在交互登录前完成；服务未就绪或清理失败时保持恢复阻断状态、保留记录并报告，不得宣称成功或覆盖 DACL。
- 验收：重启/登录测试证明测试用户首次访问目标前已执行既定清理。
- 阶段 0 当时未命名具体服务名、项目名和存储路径；用户已于 2026-07-19 通过 `D-022`、`D-023`、`D-024` 固化全部精确值，本条不再表示待确认项。

## D-018：ACE 来源识别边界

- 状态：已决定。
- 决定：接受有限 DACL 稳定性信任假设。
- 匹配依据：规范化路径、Logon SID、Allow/Deny 类型、权限掩码、继承标志、传播标志、任务 ID 对应的最小恢复记录和必要 ACL 校验信息。
- 限制：外部程序创建完全相同 ACE 元组时，Windows DACL 无法密码学证明来源。
- 处理：不得只按 SID 和掩码删除；状态与记录不一致时停止并报告；禁止重建整个 DACL。

## D-019：UAC 提升账户范围

- 状态：已决定。
- 决定：v1 仅支持同一 Windows 账户、同一交互会话的 consent elevation。
- 拒绝：其他管理员账户凭据、跨账户 elevation、远程管理员控制、服务账户代替当前用户创建限制。
- 用户提示：身份不一致时安全失败并显示“不支持跨账户提升”。

## D-020：阶段 0 文档最终物理位置

- 状态：已决定。
- 决定：八份阶段 0 项目文档全部位于 `FolderSessionLock/`，不作为仓库级总控文档保留在根目录。
- 唯一权威来源：
  - `FolderSessionLock/docs/REQUIREMENTS.md`
  - `FolderSessionLock/docs/ARCHITECTURE.md`
  - `FolderSessionLock/docs/SECURITY.md`
  - `FolderSessionLock/docs/DECISIONS.md`
  - `FolderSessionLock/PLAN.md`
  - `FolderSessionLock/TASKS.md`
  - `FolderSessionLock/ACCEPTANCE.md`
  - `FolderSessionLock/DEVLOG.md`
- 禁止：根目录同名副本、独立维护的内容副本、符号链接入口、双路径同步更新和按当前位置猜测权威来源。
- 规则分层：根 `AGENTS.md` 只保存仓库级规则；`FolderSessionLock/AGENTS.md` 保存项目级技术、安全和阶段规则。冲突时采用更严格安全规则，项目实现细节以项目级规则为准。
- 导航：根 `README.md` 只提供简短链接，不复制项目文档实质内容。
- 结果：D-016 与 D-020 的路径冲突已解除。

## D-021：阶段 2 单调计时、状态所有权与解除意图

- 状态：已决定。
- 决定：任务到期由 `IClock` 单调 timestamp 的 elapsed 决定，UTC 仅保存显示时间；`IClock` 同时提供可取消 delay，生产实现使用 `TimeProvider.System`。
- 状态所有权：全部任务为不可变快照；`LockTaskManager` 在单一同步门内完成冲突检查、添加和状态替换。到期扫描必须先取得 `Active -> Unlocking` 转换所有权，只有获胜调用方可请求解除。
- 解除意图：只允许 `Expiration`、`Recovery`、`TestCleanup`、`AdministrativeCleanup`；不定义用户或 UI 解除意图。`IFolderLockService.RemoveLockAsync` 必须显式接收意图，不保留无意图重载。
- 终态：`Completed` 与 `RecoveryRequired` 无出站转换。确定的应用失败进入 `ActivationFailed`，确定的解除失败进入 `UnlockFailed`，已发生平台操作但结果不确定时进入 `RecoveryRequired`。
- 结果：墙钟、时区和夏令时变化不改变实际 elapsed；重复或并发扫描最多发出一次到期解除；scheduler 取消不解除活动任务。

## D-022：阶段 4 恢复记录容器与事务

- 状态：已决定。
- 固定根目录：`%ProgramData%\FolderSessionLock\Recovery`。
- 活动记录目录：`%ProgramData%\FolderSessionLock\Recovery\Records`。
- 每任务记录：`%ProgramData%\FolderSessionLock\Recovery\Records\<RecordId>.fslr`。
- 临时文件：`%ProgramData%\FolderSessionLock\Recovery\Records\<RecordId>.tmp-<Guid>`。
- 历史或中断备份构件：`%ProgramData%\FolderSessionLock\Recovery\Records\<RecordId>.bak`。v1 正常 writer 不创建新的 `.bak`。
- 路径固定在编译时定义；调用方和命令行不得覆盖。每个活动任务一个 canonical 记录；完成清理只通过已验证 canonical 文件句柄删除 `.fslr`。配对 `.tmp-*`/`.bak` 按 D-022.10 与 D-022.11 分类和保留，不自动按路径删除；不保留普通任务历史、访问历史、文件内容或目录内容。
- 二进制容器固定为：4 bytes ASCII `FSLR`；2 bytes little-endian `ContainerVersion`；2 bytes little-endian `Flags`；4 bytes little-endian `ProtectedPayloadLength`；随后为 DPAPI `ProtectedPayload`。当前 `ContainerVersion = 1`。
- `ProtectedPayload` 是 UTF-8 JSON 经 `ProtectedData.Protect(..., DataProtectionScope.LocalMachine)` 保护后的二进制。`optionalEntropy` 为 UTF-8 purpose `FolderSessionLock.RecoveryRecord.v1` 的 SHA-256；entropy 只用于用途隔离，不是秘密密钥。
- 容器头不得保存未加密路径、SID、ACE 信息或错误细节。解密或完整性验证失败时不得猜测或部分解析。
- payload 精确字段：

```json
{
  "schemaVersion": 1,
  "writerVersion": "1.0",
  "recordId": "12345678-1234-4234-8234-123456789abc",
  "taskId": "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
  "state": "Prepared",
  "normalizedPath": "D:\\Example\\LockedFolder",
  "volumeSerialNumber": "0123456789abcdef",
  "fileIdHigh": "1084818905618843912",
  "fileIdLow": "506097522914230528",
  "accountSid": "string",
  "logonSid": "string",
  "windowsSessionId": 1,
  "aceType": "Deny",
  "accessMask": 1179927,
  "inheritanceFlags": 3,
  "propagationFlags": 0,
  "aceFingerprintSha256": "366092caef8b4ccd9a05728cc017b2b155a9f8aa74358e6df901e0554a8239f7",
  "baselineDaclSha256": "62fffcf46d188397e84da5b800129f54cacc87fe86ef9ca1f9eac9c6eef2db17",
  "postApplyDaclSha256": null,
  "createdUtc": "2026-07-19T16:30:00.0000000Z",
  "expiresUtc": "2026-07-19T18:30:00.0000000Z",
  "lastUpdatedUtc": "2026-07-19T16:30:00.0000000Z",
  "cleanupAttemptCount": 0,
  "lastErrorCode": "string-or-null",
  "lastErrorMessage": "sanitized-string-or-null"
}
```

- SID 使用标准字符串；日期使用 UTC RFC3339；`lastErrorMessage` 必须移除凭据、文件内容和敏感用户数据。以上目录身份和摘要值是固定测试向量，不是运行时常量。
- 当前版本固定为 `containerVersion = 1`、`schemaVersion = 1`、`writerVersion = 1.0`。v1 读取器只接受容器版本 1 和 schema 1；更高版本、未知必需字段、未知状态或字段类型错误返回 `RecoveryRecordUnsupported`，不得忽略关键字段继续清理 ACL。
- v1 禁止原地修改记录、路径型 move/replace/delete 和新建 `.bak`。未来迁移必须另行确认版本合同，不得复用旧的 ReplaceFileW/backup 流程。
- v1 提交和更新按 D-022.11 使用 Records 目录持续句柄、temp/old canonical 持续文件句柄与 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)`；删除继续使用同一 canonical handle 的 `SetFileInformationByHandle(FileDispositionInfoEx)`。全部 mutation 后执行同句柄验证；不得 fallback 到 `File.Replace`、`ReplaceFileW`、`File.Move`、`MoveFile*`、`File.Delete` 或 `DeleteFileW`。
- 任一步失败保留最后一个已验证有效记录；禁止先写 ACL 后首次创建恢复记录。`Prepared` 必须在 ACL 写入前完成原子提交；后置验证成功后更新为 `Applied`；清理前更新为 `CleanupPending`；无法安全清理更新为 `CleanupFailed` 并保留记录。
- 恢复处理：`Prepared` 且无精确 ACE 时删除过期记录；`Prepared` 且存在精确 ACE 时安全清理；`Applied` 时验证目录身份和 ACE 后清理；状态、目录身份或 ACL 元组不一致时停止并写 `CleanupFailed`。

### D-022.1 目录身份原始值与恢复记录编码

- 目录身份必须从锁定操作持有的同一个持续目录句柄调用 `GetFileInformationByHandleEx(directoryHandle, FileIdInfo, FILE_ID_INFO)` 读取。权威原始值仅为 `FILE_ID_INFO.VolumeSerialNumber` 的 `UInt64` 和 `FILE_ID_INFO.FileId.Identifier[0..15]` 的完整 16 bytes。
- 禁止通过路径重开句柄、混用 `BY_HANDLE_FILE_INFORMATION.nFileIndexHigh/Low`、使用路径字符串作为身份，或将 `GetVolumeInformation` 的 32-bit volume serial 与 `FILE_ID_INFO.VolumeSerialNumber` 混用。目录身份比较必须同时比较完整 UInt64 volume serial 和全部 16 个 FILE_ID_128 bytes。
- `volumeSerialNumber` 为 JSON string，精确 16 个 ASCII 小写十六进制字符，保留前导零，无 `0x`、分隔符、空白、正负号或大写；正则 `^[0-9a-f]{16}$`；格式化等价于 `UInt64.ToString("x16", CultureInfo.InvariantCulture)`。解析前先验证精确语法，再使用 `NumberStyles.AllowHexSpecifier` 与 `InvariantCulture` 解析。
- `fileIdLow` 固定为 `BinaryPrimitives.ReadUInt64LittleEndian(Identifier.AsSpan(0, 8))`；`fileIdHigh` 固定为 `BinaryPrimitives.ReadUInt64LittleEndian(Identifier.AsSpan(8, 8))`。两个字段均为 UInt64 十进制 ASCII JSON string，`InvariantCulture`，正则 `^(0|[1-9][0-9]*)$`，禁止前导零、符号、分组符和空白，最大值 `18446744073709551615`。
- 反向重建必须分别以 little-endian 将 low 写入 bytes 0..7、high 写入 bytes 8..15，并比较重建后的完整 16 bytes。
- 固定目录身份向量：volume UInt64 `0x0123456789abcdef`；Identifier hex `000102030405060708090a0b0c0d0e0f`；输出必须为 `volumeSerialNumber = 0123456789abcdef`、`fileIdLow = 506097522914230528`、`fileIdHigh = 1084818905618843912`；反向重建必须得到原 16 bytes。

### D-022.2 摘要通用规则与 ACE fingerprint

- `aceFingerprintSha256`、`baselineDaclSha256`、`postApplyDaclSha256` 均为 SHA-256 的 64 个小写十六进制 ASCII 字符，无前缀或分隔符。输入只能是本决定定义的精确二进制字节，不得使用 SDDL、JSON、XML、`ToString()`、SID 字符串拼接、结构体内存转储、BinaryFormatter、运行时对象序列化、整个 SECURITY_DESCRIPTOR、自相对包装、owner、group、SACL、排序后的 ACE 或 ACL 未使用尾部字节。
- ACL/ACE 必须从同一持续目录句柄调用 `GetSecurityInfo(..., DACL_SECURITY_INFORMATION, ...)` 读取；使用 `GetAce` 按 Windows 原始索引顺序取得每个 ACE，长度只取 `ACE_HEADER.AceSize`。DACL 必须通过 `IsValidAcl`；每个 AceSize 至少为 ACE_HEADER 大小、为 4 的倍数且不得越过 ACL 有效区域。
- `Prepared.aceFingerprintSha256` 保存按同一 `FSLACE` v1 规范对准备写入的规范 ACE bytes 计算的预期 fingerprint。`Applied` 前必须针对写入后从目标 DACL 重新读取到的唯一精确 ACE 重新计算实际 fingerprint，并要求与预期值相同；不得以写前值替代写后验证。定位元组为 AceType、AceFlags、AceSize、AccessMask、SID binary、显式 ACE 且未设置 `INHERITED_ACE`；匹配 0 或大于 1 均失败。
- ACE fingerprint 输入固定为：offset 0 的 ASCII `FSLACE` 6 bytes；offset 6 format version `0x01`；offset 7 reserved `0x00`；offset 8 UInt32 little-endian aceLength；offset 12 起为重新读取的 ACE bytes `[0..AceSize-1]`。`aceLength == AceSize`，总长 `12 + aceLength`。
- fingerprint 包含 ACE type、flags、size、access mask、完整 SID binary 以及对象 ACE 的 GUID/应用数据；不包含 ACE 索引、DACL header、其他 ACE、owner、group、SACL、security descriptor control、路径、taskId 或 recoveryRecordId。fingerprint 不能单独授权恢复，仍须结合目录身份、Logon SID、ACE 元组、继承/传播标志、恢复状态和调用主体。

### D-022.3 baseline/postApply DACL 摘要

- `baselineDaclSha256` 在添加应用 ACE 前从持续句柄读取；必须先生成摘要、写入并原子验证 `Prepared`，之后才允许修改 ACL。无法取得 baseline 时不得添加 ACE。
- `postApplyDaclSha256` 在 ACE 写入和后置验证后从同一持续句柄重新读取；不得用 baseline bytes 加本地 ACE 推导。顺序固定为重新读取 DACL → 定位唯一应用 ACE → 计算 fingerprint → 生成 DACL 规范 bytes → 计算 postApply digest → 验证预期变化 → 更新记录为 `Applied`。
- DACL wrapper 固定为：ASCII `FSLDACL` 7 bytes；version `0x01`；`daclPresent` 1 byte；`daclIsNull` 1 byte；UInt16 little-endian `daclControlFlags`；`aclRevision` 1 byte；3 reserved zero bytes；UInt32 little-endian `aceCount`；随后按原 DACL 顺序写 ACE records。每个 record 为 UInt32 little-endian aceLength 加精确 ACE bytes。
- `daclPresent` 来自 `GetSecurityDescriptorDacl`；`daclIsNull` 仅在 present 且 pDacl 为 NULL 时为 `0x01`。CreateLock 必须拒绝 missing DACL 和 NULL DACL；正常 baseline/postApply 为 present=1、null=0。
- `daclControlFlags = securityDescriptorControl & 0x1504`，只包含 `SE_DACL_PRESENT 0x0004`、`SE_DACL_AUTO_INHERIT_REQ 0x0100`、`SE_DACL_AUTO_INHERITED 0x0400`、`SE_DACL_PROTECTED 0x1000`。不含 owner/group defaulted、SACL flags、RM control、SELF_RELATIVE 或 `SE_DACL_DEFAULTED`。
- `aclRevision` 使用目标 ACL 原始值，包括 2 或 4；不得统一改写。`aceCount` 必须等于实际枚举并验证的 `ACL.AceCount`。摘要不使用 `ACL.AclSize` 原始范围，不包含 Sbz1/Sbz2、未使用空间或分配容量。
- DACL digest 不包含 owner、primary group、SACL、SECURITY_DESCRIPTOR revision、SELF_RELATIVE、路径或目录身份；包含掩码 0x1504 的 DACL control、ACL revision、ACE 原始顺序和每个 ACE 的有效 bytes。
- 三个摘要仅为状态验证证据。即使当前 DACL digest 等于 postApply，也必须继续验证完整目录身份、记录版本、taskId/recordId、Account/Logon SID、Session 规则、ACE type/flags/mask/SID/fingerprint、ACL 当前状态、调用模式和主体。

### D-022.4 固定摘要测试向量

- ACE bytes：`01031c00890012000103000000000005050000000100000002000000`。fingerprint 输入：`46534c41434501001c00000001031c00890012000103000000000005050000000100000002000000`。预期 SHA-256：`366092caef8b4ccd9a05728cc017b2b155a9f8aa74358e6df901e0554a8239f7`。
- Baseline ACE bytes：`00031400ff011f00010100000000000512000000`；元数据 present=1、null=0、control=`0x0004`、revision=2、count=1。DACL input：`46534c4441434c010100040002000000010000001400000000031400ff011f00010100000000000512000000`。预期 SHA-256：`62fffcf46d188397e84da5b800129f54cacc87fe86ef9ca1f9eac9c6eef2db17`。
- PostApply 按原顺序先放 deny ACE、再放 baseline allow ACE；元数据 present=1、null=0、control=`0x0004`、revision=2、count=2。DACL input：`46534c4441434c010100040002000000020000001c00000001031c008900120001030000000000050500000001000000020000001400000000031400ff011f00010100000000000512000000`。预期 SHA-256：`0bd878690d59d8de240e84199560b65db09c2f473dffc717aabb75642566f026`。

### D-022.5 CP5 必测矩阵

- 目录身份：16 位小写 volume serial、前导零、大写/`0x`/15/17 字符拒绝、固定 high/low 与反向向量、UInt64 max、全零 FILE_ID、任一 FILE_ID byte 或 volume serial 改变即不相等、禁止 32-bit volume serial 替代。
- ACE fingerprint：固定向量；修改 type/flags/mask/SID 时摘要改变；AceSize 不合法或与实际长度不等时拒绝；匹配 0/>1 失败；证明 fingerprint 来自 post-write DACL 重新读取值。
- DACL digest：固定 baseline/postApply；ACE 顺序、control、revision、有效 byte 改变时摘要改变；owner/group/SACL/SELF_RELATIVE/未使用尾部变化时摘要不变；missing/null DACL 拒绝；postApply 必须 OS 重读而非本地推导。

### D-022.6 `.fslr` v1 容器、错误与严格长度

- 文件布局精确为 offset 0 的 4-byte ASCII `FSLR`、offset 4 UInt16 little-endian `ContainerVersion`、offset 6 UInt16 little-endian `Flags`、offset 8 UInt32 little-endian `ProtectedPayloadLength`、offset 12 起 N-byte `ProtectedPayload`。固定头长 12；文件总长必须严格等于 `12 + ProtectedPayloadLength`，禁止 padding、checksum、第二 payload、尾随零、注释或扩展区。
- Magic bytes 固定 `46 53 4c 52`；不匹配返回 `FSL_E_RECOVERY_RECORD_MAGIC_INVALID` / `The recovery record header is invalid.` / retryable false / field null。不得尝试其他格式。
- `ContainerVersion = 0x0001`；其他值返回 `FSL_E_RECOVERY_RECORD_VERSION_UNSUPPORTED` / `The recovery record version is not supported.` / retryable false / field `containerVersion`，不得降级或部分解析。
- v1 `Flags = 0x0000`，允许掩码 `0x0000`；writer 永远写 `00 00`。任何非零、未知、保留或未来位返回 `FSL_E_RECOVERY_RECORD_FLAGS_UNSUPPORTED` / `The recovery record flags are not supported.` / retryable false / field `flags`，不得屏蔽后继续。DPAPI 固定方式不由 flags 选择。
- `ProtectedPayloadLength` 是 UInt32 little-endian DPAPI blob 精确字节数，范围 1..262144。必须在分配前验证。零或超限返回 `FSL_E_RECOVERY_RECORD_LENGTH_INVALID` / `The recovery record payload length is invalid.` / retryable false / field `protectedPayloadLength`；`12 + length` 使用 checked arithmetic，溢出同码。
- `fileLength < 12` 或 remaining bytes 少于声明长度返回 `FSL_E_RECOVERY_RECORD_TRUNCATED` / `The recovery record is truncated.` / retryable false / field null，不使用部分 payload、不补零、不调用 DPAPI。remaining bytes 大于声明长度返回 `FSL_E_RECOVERY_RECORD_TRAILING_DATA` / `The recovery record contains unexpected trailing data.` / retryable false / field null，即使多余数据全为零也拒绝。
- ProtectedPayload 固定由 `ProtectedData.Protect/Unprotect`、`DataProtectionScope.LocalMachine` 和 `SHA-256(UTF8("FolderSessionLock.RecoveryRecord.v1"))` entropy 处理。解密失败返回 `FSL_E_RECOVERY_RECORD_UNPROTECT_FAILED` / `The recovery record could not be decrypted.` / retryable false / field null；公开错误不得含异常、Win32 文本、blob、entropy 或身份信息。
- 解密明文必须为 UTF-8 without BOM 的单一 JSON object，最大 131072 bytes；超限返回 `FSL_E_RECOVERY_PAYLOAD_TOO_LARGE`。JSON 语法、非法 UTF-8、BOM、重复字段或尾随 JSON value 返回 `FSL_E_RECOVERY_PAYLOAD_MALFORMED`；缺失、多余、类型、格式或范围错误返回 `FSL_E_RECOVERY_PAYLOAD_SCHEMA_INVALID`。

### D-022.7 Recovery payload 25 字段精确类型

- 全部 25 字段始终必须存在；允许 null 的字段也不得省略。字段名区分大小写，writer 按 D-022 payload 示例顺序输出。禁止注释、尾随逗号、数字字符串替代 JSON 数字、浮点替代整数、宽松 Guid/date/SID/hash、未知 enum/flags 或未明确允许的 null。

| field | JSON / .NET | 精确规则 |
|---|---|---|
| `schemaVersion` | integer / Int32 | 固定 1，非 null；其他值 `FSL_E_RECOVERY_PAYLOAD_VERSION_UNSUPPORTED` |
| `writerVersion` | string | 固定大小写敏感 `1.0`，非 null |
| `recordId` | string / Guid | 非空小写 Guid D、RFC 4122 variant；v1 version 4 允许，其他已固定生成方式须由 planner 从代码记录 |
| `taskId` | string / Guid/TaskId | 非空小写 Guid D，非 null |
| `state` | string / RecoveryRecordState | 仅 Prepared、Applied、CleanupPending、CleanupFailed，大小写敏感 |
| `normalizedPath` | string / FolderPath | 1..32767 UTF-16 code units；Stage 3 规范化绝对本地固定 NTFS 非根、非 UNC/device/reparse、无 NUL/ADS/未规范化 dot segment；恢复仍重验身份 |
| `volumeSerialNumber` | string / UInt64 | 精确 16 位小写 hex，D-022.1 |
| `fileIdHigh` | string / UInt64 | 规范十进制 ASCII，0..UInt64.MaxValue，D-022.1 |
| `fileIdLow` | string / UInt64 | 同 fileIdHigh |
| `accountSid` | string / SecurityIdentifier | canonical、最大 184 ASCII chars、输入与重新格式化完全一致；账户 SID，禁止 Logon/BUILTIN/capability/service SID、名称/别名/空白 |
| `logonSid` | string / SecurityIdentifier | canonical `S-1-5-5-X-Y`，authority 5、首 subauthority 5、恰好后续两个 subauthority |
| `windowsSessionId` | integer / UInt32 | 0..4294967295；禁止负数、浮点、指数和字符串 |
| `aceType` | string / AccessControlType | 固定 `Deny` |
| `accessMask` | integer / UInt32 | 1..UInt32.MaxValue；parser 验范围，恢复执行器要求等于 Stage 3 v1 mask，禁止 GENERIC_ALL/FullControl/WRITE_DAC/WRITE_OWNER/未授权位；不支持返回 `FSL_E_RECOVERY_ACCESS_MASK_UNSUPPORTED` |
| `inheritanceFlags` | integer / InheritanceFlags | 允许掩码 0x03，值 0/1/2/3；v1 writer 写 Stage 3 精确值，未知位拒绝 |
| `propagationFlags` | integer / PropagationFlags | 允许掩码 0x03，值 0/1/2/3；v1 writer 写 Stage 3 精确值，未知位拒绝 |
| `aceFingerprintSha256` | string / 32-byte digest | 必需非 null、64 位小写 hex、非全零；Prepared 为预期 fingerprint，Applied 前以 OS 重读实际值确认相同 |
| `baselineDaclSha256` | string / digest | 必需非 null、64 位小写 hex、非全零；Prepared 前完成 |
| `postApplyDaclSha256` | string/null / digest | 始终存在；Prepared 必须 null；其他状态按 D-022.8；非 null 为 64 位小写 hex、非全零、来自 OS 重读 |
| `createdUtc` | string / DateTimeOffset | 精确 `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` UTC，非 null，不晚于 lastUpdated |
| `expiresUtc` | string / DateTimeOffset | 同格式；必须大于 created 且符合 Duration 范围 |
| `lastUpdatedUtc` | string / DateTimeOffset | 同格式；不早于 created 或上一提交版本；每次状态更新必须变化 |
| `cleanupAttemptCount` | integer / Int32 | 0..1000000；Prepared/Applied=0，CleanupPending/Failed>=1；到 1000000 停止自动重试并保持 CleanupFailed |
| `lastErrorCode` | string/null | 非 null 时 1..128，`^FSL_E_[A-Z0-9_]+$`，无空白/换行/路径/SID/原始错误文本；状态规则见 D-022.8 |
| `lastErrorMessage` | string/null | 非 null 时 1..256 Unicode scalar values，稳定脱敏，无换行控制、stack/type/user/SID/path/SDDL/ACE/credential/token/key/blob；状态规则见 D-022.8 |

### D-022.8 状态与跨字段矩阵

| state | postApplyDaclSha256 | lastErrorCode | lastErrorMessage | cleanupAttemptCount |
|---|---|---|---|---:|
| `Prepared` | 必须 null | 必须 null | 必须 null | 必须 0 |
| `Applied` | 必须非 null | 必须 null | 必须 null | 必须 0 |
| `CleanupPending` | 必须非 null | 必须 null | 必须 null | 必须 >= 1 |
| `CleanupFailed` | 必须非 null | 必须非 null | 必须非 null | 必须 >= 1 |

- 不符合矩阵返回 `FSL_E_RECOVERY_PAYLOAD_STATE_INVALID` / `The recovery record state fields are inconsistent.` / retryable false / field `state`；不得自动修正或补 null。
- Prepared 表示 baseline 已读取、预期 ACE fingerprint 已计算、记录可原子提交且 ACL 尚未确认应用；`postApplyDaclSha256` 必须 null，禁止空字符串、全零、预计 hash 或本地推导值，且 `lastUpdatedUtc == createdUtc` 为 v1 writer 规则。
- Applied 必须已写 ACE、OS 重读 DACL、定位唯一 ACE、验证实际 fingerprint 并计算实际 postApply。CleanupPending 开始每次清理前先递增 count、原子提交并清空旧 error。CleanupFailed 两个 error 必须同时非空且保留 `.fslr`。
- 必须验证 `createdUtc <= lastUpdatedUtc`、`createdUtc < expiresUtc`。CleanupPending/Failed 可因到期或获准提前清理发生，reader 不以 `lastUpdatedUtc >= expiresUtc` 作为 schema 必需条件；清理授权由 intent 和主体决定。
- 未知 state/aceType、flags 未知位、不支持 accessMask/schema/container、非零 Flags、UInt64 截断、负转 unsigned 或宽松数字全部拒绝，不映射默认 enum、不屏蔽未知位。

### D-022.9 writer、失败行为与必测矩阵

- v1 writer 固定：构造完整 25 字段 → 验证状态矩阵 → 严格 JSON → UTF-8 without BOM → 明文 <=131072 → DPAPI LocalMachine → blob 1..262144 → 写 Magic/version 1/flags 0/length/blob → flush-to-disk → 回读完整临时文件并验证总长、头、flags、DPAPI、25 字段、状态矩阵 → 原子提交。writer 不得生成 reader 会拒绝的记录。
- 任一容器或 payload 失败：不修改 ACL、不删除/覆盖 `.fslr`、不猜测恢复、不迁移版本；仅写受保护诊断、返回稳定错误并将目录标记为需人工恢复检查；单条损坏记录不得触发无关路径扫描。
- 容器测试至少覆盖 flags 0/bit0/bit15/all、writer 0；length 0/1/262144/262145；header<12、声明大于/小于实际、零/非零尾随、checked overflow、DPAPI 失败、明文>131072、BOM、非法 UTF-8、JSON 尾随数据。
- 全部 25 字段至少覆盖缺失、多余、非法 null、类型、上下界、大小写、未知 enum/flags、空/大写 Guid、非 UTC/非 7 位日期、SID 非法/非 canonical、Logon SID 结构、hash 大写/长度/全零、accessMask 0、cleanup count 负数/>1000000。
- 状态矩阵测试必须覆盖每个允许组合及 Prepared postApply/error/count 非法、Applied postApply null/error、CleanupPending postApply null/error/count0、CleanupFailed postApply/error null/空 error/count0。

### D-022.10 恢复目录枚举、多记录 Cleanup 与结果摘要

- 状态：用户于 2026-07-21 最终确认。本条覆盖此前缺失或冲突的恢复执行规则。
- 固定目录为 `%ProgramData%\FolderSessionLock\Recovery\Records`。必须先完成 D-023 安全复核，再完整顶层枚举、分类、数量检查和稳定排序，之后才允许开始清理。禁止递归、边枚举边修改 ACL、跟随 symbolic link、junction、mount point 或其他 reparse point。
- 总顶层条目上限为 4096，规范 `.fslr` 上限为 1024。观察到第 4097 个总条目或第 1025 个规范记录时返回 `FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED`，不处理记录、不修改 ACL、不删除构件，`recoveryBlocking = true`。
- 唯一规范活动文件名为 `<recordId>.fslr`，`recordId` 必须是小写、非空 Guid D，正则语义为 `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.fslr$`。文件名 recordId 与 payload `recordId` 不一致时返回 `FSL_E_RECOVERY_RECORD_ID_MISMATCH`，该记录失败但继续遍历。
- 规范 `.fslr` 按完整文件名使用 `StringComparer.Ordinal` 升序；第一个记录级主错误按该稳定顺序确定，不按异步完成、时间戳、严重度或异常到达顺序。
- 合法 `.bak` 为 `<recordId>.bak`，合法 `.tmp-*` 为 `<recordId>.tmp-<tempId>`；两个 Guid 均为小写 Guid D。同 recordId 的 `.fslr` 存在时，只有普通文件、非 reparse、NumberOfLinks=1、SYSTEM owner、D-022.11 精确文件 DACL、同一 Records 目录全部通过，才计入 `auxiliaryArtifactCount`；否则返回 `FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID`、计入 invalid 并 blocking。配对构件不解析、不提交、不修复、不删除。对应 `.fslr` 不存在时分别返回 `FSL_E_RECOVERY_BACKUP_ORPHANED` 或 `FSL_E_RECOVERY_TEMP_ORPHANED`，仍先做句柄级安全读取，计入 `invalidArtifactCount`，保留构件并设置 blocking。
- 大写 Guid、非 Guid `.fslr`、未知扩展名、不合规 `.bak`/`.tmp-*`、无扩展名文件、子目录、reparse entry、异常 alternate data stream、设备或特殊文件统一为 `FSL_E_RECOVERY_ARTIFACT_INVALID`；不作为记录打开、不删除，计入 `invalidArtifactCount` 并继续分类。
- 完整枚举无法证明时使用目录级 `FSL_E_RECOVERY_DIRECTORY_OPEN_FAILED`、`FSL_E_RECOVERY_DIRECTORY_ENUMERATION_FAILED` 或 `FSL_E_RECOVERY_ENTRY_METADATA_FAILED`。完整枚举后单条规范 `.fslr` 的打开、读取、长度、安全信息、DPAPI、更新或删除失败是记录级错误，继续处理后续记录，不升级为 enumeration failure。
- 单条记录失败后继续遍历。只有 SCM stop/cancellation、进程级安全前置失效、恢复目录身份变化、D-023 目录被替换、无法维持安全执行环境或顶层进程终止时停止开始新记录。
- `CleanupPending` 原子提交后进入 ACL 临界区。临界区内暂时忽略取消，必须完成精确 ACE 检查、移除尝试、后置验证，并到达删除成功、`CleanupFailed` 或 `RecoveryRequired` 后才响应取消。
- 每个规范记录最终恰好属于 `Cleaned`、`AlreadyClean`、`Failed`、`RecoveryRequired`、`Skipped`。`AlreadyClean` 只在目录身份和 ACL 状态证明不存在未知副作用且记录成功删除时成立；不得仅因未找到 ACE 判定。
- 结构化摘要精确字段为：`canonicalRecordCount`、`processedRecordCount`、`cleanedCount`、`alreadyCleanCount`、`failedCount`、`recoveryRequiredCount`、`skippedCount`、`auxiliaryArtifactCount`、`invalidArtifactCount`、`remainingRecordCount`、`recoveryBlocking`、`primaryErrorCode`。前十个计数为 Int32 0..4096；`recoveryBlocking` 为 Boolean；`primaryErrorCode` 为 string 或 null。
- 不变量：`processedRecordCount = cleanedCount + alreadyCleanCount + failedCount + recoveryRequiredCount`；`canonicalRecordCount = processedRecordCount + skippedCount`。`remainingRecordCount` 是扫描结束时仍存在的规范 `.fslr` 数量。
- `failedCount > 0`、`recoveryRequiredCount > 0`、`skippedCount > 0`、`invalidArtifactCount > 0`、`remainingRecordCount > 0`、D-023 失败、枚举不完整、超限或 readiness 不可证明时 `recoveryBlocking = true`；只有全部为零且安全检查通过时为 false。
- 目录级前置错误发生在记录处理前时为主错误；记录处理开始后，稳定顺序中的第一个非成功记录结果为主错误。scheduler error 只写受保护内部日志，永不覆盖主错误，也不把全部成功 Cleanup 变为失败。

### D-022.11 恢复记录文件级 owner、DACL、句柄提交与 TOCTOU

- 状态：用户于 2026-07-21 最终批准，并在 Windows 11 `10.0.22631` 实证后明确批准 rename API 勘误。本条覆盖所有 reader 强制 owner 但 writer 不设置、依赖父目录继承、关闭验证句柄后按路径修改、ReplaceFileW/File.Replace/File.Delete，以及使用 `SetFileInformationByHandle(FileRenameInfoEx)` 执行相对目录句柄 rename 等冲突旧规则。
- 适用文件：CanonicalRecord `<recordId>.fslr`、TemporaryRecord `<recordId>.tmp-<tempId>`、BackupRecord `<recordId>.bak`；recordId/tempId 均为小写非空 Guid D，固定位于 `%ProgramData%\FolderSessionLock\Recovery\Records`，调用方不得覆盖路径。

#### 文件 owner 与精确 DACL

- 三类文件唯一允许 owner 为 `NT AUTHORITY\SYSTEM` / `S-1-5-18`。当前交互用户、consent Broker Account SID、Administrators、服务 SID、TrustedInstaller、CREATOR OWNER 或未知 SID 全部拒绝；owner mismatch 不降级。
- 三类文件 DACL 完全相同：present=true、null=false、`SE_DACL_PROTECTED`、无 inherited ACE、显式 ACE 精确 3、ACL revision 2；禁止 Deny/object/callback/conditional/unknown ACE。
- ACE 顺序精确为：0 SYSTEM `S-1-5-18`、1 Administrators `S-1-5-32-544`、2 `NT SERVICE\FolderSessionLockRecovery` 固定服务 SID；全部 `ACCESS_ALLOWED_ACE_TYPE`、AccessMask `0x001F01FF`、AceFlags `0x00`、无继承。禁止额外 ACE、替代 SID、CREATOR OWNER 或只读 ACE。
- service SID 无法解析返回 `FSL_E_RECOVERY_FILE_SERVICE_SID_UNAVAILABLE`。writer 使用 `DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION`，禁止 `UNPROTECTED_DACL_SECURITY_INFORMATION`。writer 不修改 primary group、SACL、Mandatory Integrity Label 或 Audit ACE；reader 不以 primary group 为条件。

```csharp
public enum RecoveryRecordFileKind
{
    CanonicalRecord,
    TemporaryRecord,
    BackupRecord
}

public sealed record RecoveryRecordFileIdentity(
    ulong VolumeSerialNumber,
    ulong FileIdHigh,
    ulong FileIdLow,
    uint NumberOfLinks);

public sealed record RecoveryRecordFileSecuritySnapshot(
    RecoveryRecordFileKind FileKind,
    RecoveryRecordFileIdentity Identity,
    string OwnerSid,
    bool DaclPresent,
    bool DaclIsNull,
    bool DaclProtected,
    int ExplicitAceCount);

public interface IRecoveryRecordFileSecurity
{
    ValueTask<Result<RecoveryRecordFileSecuritySnapshot>>
        ApplyAndVerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken);

    ValueTask<Result<RecoveryRecordFileSecuritySnapshot>>
        VerifyAsync(
            SafeFileHandle fileHandle,
            RecoveryRecordFileKind fileKind,
            CancellationToken cancellationToken);
}
```

- 接口只接收已打开 `SafeFileHandle`，不接受路径且不得内部按路径重开。`ApplyAndVerifyAsync` 只用于新建未提交 `.tmp-*`；已提交 `.fslr` 和现有 `.bak` 只允许 Verify，reader 不自动修复。

#### consent Broker writer 与 privilege

- writer 创建 `.tmp-*` 后，必须通过同一持续句柄显式设置 SYSTEM owner 和精确受保护三 ACE DACL；不得依赖创建者 owner、父目录继承、默认 DACL、安装器或服务后修复。
- 若当前 owner 非 SYSTEM，临时启用提升进程令牌 `SeRestorePrivilege`，同句柄设置 owner，并在 finally 恢复原启用状态；禁止用 `SeTakeOwnershipPrivilege` 把 owner 改为当前用户。无法启用返回 `FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE`；无法恢复返回 `FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED`，Broker 停止后续记录写入、不提交 temp、不继续 CreateLock。
- 同句柄执行等价 `SetSecurityInfo(SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, SYSTEM SID, exact DACL)`，随后立即同句柄重读验证。禁止 `SetNamedSecurityInfo`、`File.SetAccessControl`、`FileInfo.SetAccessControl`。

#### 跨进程锁、目录句柄与临时文件

- 所有 writer 持有固定 mutex `Global\FolderSessionLock.RecoveryStore.v1`；mutex DACL 只允许 SYSTEM、Administrators、服务 SID，普通用户不得创建或抢占。
- 前置顺序：取得 mutex → 打开 Records 持续目录句柄 → D-023 → 保存目录 VolumeSerialNumber/FILE_ID_128。
- 通过 Records 目录句柄和简单叶名称 `CREATE_NEW` 创建 temp，ShareMode=0，flags=`FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_WRITE_THROUGH`，访问至少 GENERIC_READ/WRITE、READ_CONTROL、WRITE_DAC、WRITE_OWNER、DELETE、SYNCHRONIZE；已存在不得覆盖。
- 写 payload 前同一 tempHandle 验证普通文件、非 reparse、同卷、NumberOfLinks=1、FILE_ID_INFO、final path 属于固定 Records，保存 identity；设置/验证安全后再验证 identity。失败不得写 payload。
- 安全通过后才写完整 header/payload、`FlushFileBuffers`、同句柄 seek/readback，并验证容器、DPAPI、25 字段。提交前同句柄再次验证 identity、links、reparse、owner/DACL、内容与叶名称 recordId。

#### 新建、更新与提交后验证

- 新建：保持 tempHandle 和 recordsDirectoryHandle 打开，通过 user-mode `NtSetInformationFile(tempHandle, FILE_RENAME_INFORMATION, FileRenameInformationEx = 65)` 提交；`Flags = 0`、`RootDirectory = recordsDirectoryHandle`、`FileName = <recordId>.fslr` 相对简单叶名、`FileNameLength` 为 UTF-16 byte 数，buffer 长度至少为 `sizeof(FILE_RENAME_INFORMATION) + FileNameLength`，tempHandle 必须包含 DELETE。目标存在返回 `FSL_E_RECOVERY_FILE_ALREADY_EXISTS`。提交后 tempHandle 继续代表 canonical。
- 更新：保持 old canonical、temp、Records 三个句柄打开。old handle 验证 file kind、reparse、links、identity、owner/DACL、容器/payload、recordId/taskId/state；准备并验证新 temp；提交前在 old handle 复核全部事实。
- 更新只允许 tempHandle 调用 user-mode `NtSetInformationFile`，information class 精确为 `FileRenameInformationEx = 65`，结构为 `FILE_RENAME_INFORMATION`，flags 精确为 `FILE_RENAME_REPLACE_IF_EXISTS | FILE_RENAME_POSIX_SEMANTICS = 0x00000003`，`RootDirectory = recordsDirectoryHandle`，`FileName = <recordId>.fslr` 相对简单叶名。production rename 禁止 `FileRenameInformation = 10`、`SetFileInformationByHandle(FileRenameInfoEx = 22)`、`FileRenameInfo = 3`、绝对目标路径或其他 fallback。不支持返回 `FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED`；失败返回 `FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED`；原始 NTSTATUS/DOS code 只进受保护日志。v1 writer 不创建新 `.bak`。
- 提交后保持新 canonical handle，依次验证 FILE_ID=temp identity、links=1、SYSTEM owner、精确 DACL、完整内容与 recordId/taskId/state/摘要；通过 Records 目录句柄确认唯一 canonical 名称映射到同 FILE_ID，再复核目录 identity。任何失败返回 `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED` / UnrecoverableError，任务与 Replay 进入 RecoveryRequired，保留文件，不按路径删除或猜测恢复。

#### 句柄删除与 temp 失败清理

- 删除 canonical：持有 mutex、Records 目录句柄和 canonical 持续句柄；同句柄验证 identity/owner/DACL/container/payload/recordId/taskId/允许终态并完成提交前 leaf mapping 复核，再对同一 handle 调用 `SetFileInformationByHandle(FileDispositionInfoEx, FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS)`；disposition 成功后关闭该 canonical handle，再通过 retained Records directory handle 确认 canonical 叶名已从 visible namespace 消失，最后复核目录 identity。名称仍存在、枚举失败、目录 identity 变化或无法证明关闭/删除时进入 `RecoveryRequired`；禁止路径重试、按名称删除、重新打开后删除或删除 replacement。
- 不支持句柄删除返回 `FSL_E_RECOVERY_FILE_HANDLE_DELETE_UNSUPPORTED`；删除调用失败返回 `FSL_E_RECOVERY_FILE_DELETE_FAILED`；禁止 File.Delete/DeleteFileW/Directory.Delete 或关闭验证句柄后按路径删除。
- 提交前 temp 失败只通过同一 tempHandle FileDispositionInfoEx 删除。成功返回原始错误；失败或无法证明删除时主错误改为 `FSL_E_RECOVERY_TEMP_CLEANUP_FAILED`，原错误只进受保护诊断，blocking=true。

#### 稳定错误与公开消息

- `FSL_E_RECOVERY_FILE_SERVICE_SID_UNAVAILABLE`：`The recovery service identity could not be resolved.`
- `FSL_E_RECOVERY_FILE_OWNER_PRIVILEGE_UNAVAILABLE`：`The recovery file owner could not be assigned securely.`
- `FSL_E_RECOVERY_FILE_PRIVILEGE_REVERT_FAILED`：`The recovery file security privilege could not be restored.`
- `FSL_E_RECOVERY_FILE_OWNER_SET_FAILED`：`The recovery file owner could not be set.`
- `FSL_E_RECOVERY_FILE_DACL_SET_FAILED`：`The recovery file permissions could not be set.`
- `FSL_E_RECOVERY_FILE_SECURITY_READ_FAILED`：`The recovery file security information could not be read.`
- `FSL_E_RECOVERY_FILE_IDENTITY_READ_FAILED`：`The recovery file identity could not be read.`
- `FSL_E_RECOVERY_FILE_OWNER_MISMATCH`：`The recovery file owner is not trusted.`
- `FSL_E_RECOVERY_FILE_DACL_MISSING`：`The recovery file permissions are missing.`
- `FSL_E_RECOVERY_FILE_DACL_NULL`：`The recovery file permissions are unsafe.`
- `FSL_E_RECOVERY_FILE_INHERITANCE_INVALID`：`The recovery file permissions must not be inherited.`
- `FSL_E_RECOVERY_FILE_DACL_MISMATCH`：`The recovery file permissions do not match the required policy.`
- `FSL_E_RECOVERY_FILE_IDENTITY_MISMATCH`：`The recovery file identity changed during the operation.`
- `FSL_E_RECOVERY_FILE_ALREADY_EXISTS`：`A recovery record with the same identifier already exists.`
- `FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED`：`The platform cannot safely replace the recovery record.`
- `FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED`：`The recovery record could not be replaced atomically.`
- `FSL_E_RECOVERY_FILE_HANDLE_DELETE_UNSUPPORTED`：`The platform cannot safely delete the recovery record.`
- `FSL_E_RECOVERY_FILE_DELETE_FAILED`：`The recovery record could not be deleted.`
- `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`：`The committed recovery record could not be verified.`
- `FSL_E_RECOVERY_TEMP_CLEANUP_FAILED`：`A temporary recovery file could not be removed safely.`
- `FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID`：`A recovery artifact does not satisfy the required security policy.`
- 上述公开对象 `retryable=false`、`field=null`。Win32/HRESULT、路径、owner SID、DACL/SDDL、FILE_ID 只写受保护日志。

#### 错误优先级、禁止 API 与测试边界

- 优先级：D-023 目录错误 → 文件 identity/reparse → security read → owner mismatch → DACL mismatch；提交前 temp 清理失败覆盖原错误；replace 失败用 atomic replace failed；replace 成功后任何验证失败统一 post-commit；delete 调用失败用 delete failed；删除后目录枚举无法确认进入 RecoveryRequired。scheduler error 仅内部记录。
- Recovery store 产品代码禁止 `File.Replace`、ReplaceFileW、`File.Move`、MoveFileW/MoveFileExW、`File.Delete`、DeleteFileW、`File.SetAccessControl`、`FileInfo.SetAccessControl`、SetNamedSecurityInfo，禁止 `Verify(path) → CloseHandle → Modify(path)`。
- 必测 owner/DACL、writer privilege/security、handle rename/replace/delete、post-commit、temp cleanup、mutex、TOCTOU、auxiliary security 与无路径 fallback 矩阵以 ACCEPTANCE.md 为准。
- `AGREELIN` 只允许接口/wrapper、fake、TEMP handle rename/delete、failure injection、静态扫描和普通验证；真实 ProgramData SYSTEM owner、service SID DACL、LocalSystem writer、普通用户拒绝和重启恢复只在 `FSL-STAGE4-VM`。

## D-023：恢复目录 ACL、安装位置与服务身份

- 状态：已决定。
- 恢复目录所有者固定为 `NT AUTHORITY\SYSTEM`；使用受保护显式 DACL。
- 允许项固定为：
  - `NT AUTHORITY\SYSTEM`：`FullControl`，`ThisFolderSubfoldersAndFiles`。
  - `BUILTIN\Administrators`：`FullControl`，`ThisFolderSubfoldersAndFiles`。
  - `NT SERVICE\FolderSessionLockRecovery`：`FullControl`，`ThisFolderSubfoldersAndFiles`。
- 不向 `Users`、`Authenticated Users`、`Everyone`、当前交互用户或普通 UI 授权。普通用户不得列出、读取、创建、修改或删除恢复记录；UI 只通过受限 IPC 查询脱敏状态。
- 安装程序创建并验证 DACL；服务启动时复核目录所有者和 DACL；异常时安全失败，不使用 Deny ACE 修补错误 Allow ACL。卸载清理仅在服务停止且不存在未清理记录后执行。
- 安装根固定为 `%ProgramFiles%\FolderSessionLock`；数据根固定为 `%ProgramData%\FolderSessionLock`。
- `%ProgramFiles%\FolderSessionLock` ACL：`SYSTEM: FullControl`、`Administrators: FullControl`、`Users: ReadAndExecute`；不为 `Authenticated Users` 额外授予写权限。普通用户不得创建、替换、修改或删除 Broker/Service 二进制。
- 服务二进制必须从受保护安装目录注册；禁止从仓库 `bin`、`obj`、TEMP、用户目录或网络路径注册生产式服务。
- 恢复记录、日志和证据位置分别固定为 `%ProgramData%\FolderSessionLock\Recovery\Records\`、`%ProgramData%\FolderSessionLock\Logs\v1\`、`%ProgramData%\FolderSessionLock\TestEvidence\Stage4\<RunId>\`。ProgramData 按用途拆分 ACL；普通用户不得访问 `Recovery` 或 `Logs\v1`，readiness 的 Users 只读例外精确由 D-030 定义。

### D-023.1 受保护路径只读复核接口与精确策略

```csharp
public enum ProtectedPathKind
{
    InstallDirectory,
    RecoveryRoot,
    RecoveryRecordsDirectory,
    ReplayDirectory
}

public sealed record ProtectedPathSecurityCheckRequest(
    ProtectedPathKind PathKind,
    string ExpectedPath);

public sealed record ProtectedPathSecurityCheckResult(
    bool IsTrusted,
    string? ErrorCode);

public interface IProtectedPathSecurityVerifier
{
    ValueTask<ProtectedPathSecurityCheckResult> VerifyAsync(
        ProtectedPathSecurityCheckRequest request,
        CancellationToken cancellationToken);
}
```

- `ExpectedPath` 只由受信组合根生成，CLI/IPC 不得提供。成功时 `ErrorCode = null`；`IsTrusted == false` 时 ErrorCode 必须非 null。接口不向普通 UI 暴露 SDDL、owner SID 或 ACE 列表。
- 生产复核顺序固定为：选择固定路径 → 规范化 → 用目录句柄及 `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS` 打开 → 拒绝 reparse → 读取并比较最终句柄路径 → 验证本地固定 NTFS → 读取 VolumeSerialNumber 与 FILE_ID_128 → 读取 owner/DACL → 验证 DACL present 且非 NULL → 验证 owner → 验证继承保护 → 验证必需显式 Allow ACE → 验证普通用户无写、改 ACL、改 owner、删除权限 → 拒绝未知高风险 Allow ACE → 再读目录身份并比较。任一步失败均 fail closed。
- InstallDirectory 允许 owner 仅 `NT AUTHORITY\SYSTEM`、`NT SERVICE\TrustedInstaller`。RecoveryRoot、RecoveryRecordsDirectory、ReplayDirectory 允许 owner 仅 `NT AUTHORITY\SYSTEM`。
- InstallDirectory 至少包含 `SYSTEM: FullControl`、`Administrators: FullControl`、`Users: ReadAndExecute`；`Users`、`Authenticated Users`、`Everyone`、交互用户 SID 不得获得 WriteData、AppendData、WriteAttributes、WriteExtendedAttributes、Delete、DeleteChild、WriteDac、WriteOwner 或 FullControl，不要求 Deny ACE。
- Recovery/Replay 目录必须包含 `SYSTEM: FullControl`、`Administrators: FullControl`、`NT SERVICE\FolderSessionLockRecovery: FullControl`，DACL 必须 `SE_DACL_PROTECTED`；普通用户主体不得读取、列出、创建、写入、删除、WriteDac 或 WriteOwner。
- 精确错误码按执行顺序选择：`FSL_E_PROTECTED_PATH_NOT_FOUND`、`FSL_E_PROTECTED_PATH_OPEN_FAILED`、`FSL_E_PROTECTED_PATH_REPARSE_POINT`、`FSL_E_PROTECTED_PATH_FINAL_PATH_MISMATCH`、`FSL_E_PROTECTED_PATH_VOLUME_UNSUPPORTED`、`FSL_E_PROTECTED_PATH_IDENTITY_UNAVAILABLE`、`FSL_E_PROTECTED_PATH_IDENTITY_CHANGED`、`FSL_E_PROTECTED_PATH_SECURITY_READ_FAILED`、`FSL_E_PROTECTED_PATH_OWNER_MISMATCH`、`FSL_E_PROTECTED_PATH_DACL_MISSING`、`FSL_E_PROTECTED_PATH_DACL_NULL`、`FSL_E_PROTECTED_PATH_DACL_MISMATCH`、`FSL_E_PROTECTED_PATH_INHERITANCE_INVALID`、`FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED`。不得统一折叠为 `FSL_E_INTERNAL`。
- 用户合同所称 CP6 必须完成接口、enum、结果模型、orchestration 位置、fail-closed、readiness、recovery-once/recovery-service 状态机、fake verifier 与单元测试；不得完成生产 Win32 owner/DACL 读取、SDDL 比较、Program Files/ProgramData ACL 配置、安装器/service SID ACL 写入或真实管理员集成测试。禁止 `AllowAllProtectedPathSecurityVerifier`。生产组合在 Windows verifier 未实现前必须返回 `FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED`，不得进入 Ready、执行生产 recovery 或 CreateLock；测试组合可注入显式 fake。
- 用户合同所称 CP8 必须完成 `WindowsProtectedPathSecurityVerifier`、handle-based final path、reparse、FILE_ID_128 前后复核、owner/DACL/ACE/继承验证、安装与 Recovery/Replay ACL 创建验证、service SID ACL、VM 真实安全测试、普通用户拒绝、篡改 fail-closed 与 TOCTOU 测试。完成前不得声称生产服务安全启动、ProgramData 已保护、Broker 不可替换或 D-023 完成。

## D-024：阶段 4 服务、Broker 与 IPC 精确标识符

- 状态：已决定。
- 内部 Service Name：`FolderSessionLockRecovery`。
- Display Name：`Folder Session Lock Recovery Service`。
- Description：`Removes verified Folder Session Lock ACL entries left by previous Windows logon sessions.`
- 服务账户：`LocalSystem`；启动类型：`Automatic`；`DelayedAutoStart = false`；启用唯一服务 SID `NT SERVICE\FolderSessionLockRecovery`。
- 服务固定入口：`FolderSessionLock.Broker.exe --mode recovery-service`。
- 一次性诊断入口：`FolderSessionLock.Broker.exe --mode recovery-once`。该模式不注册服务，只执行一次相同恢复扫描并按 D-024.1 返回 0/2/10/11/12/13/14/15；只允许隔离 VM 系统测试；不接受自定义路径；不创建新限制。
- 现有恢复宿主项目固定为 `FolderSessionLock.Broker`，项目文件 `src\FolderSessionLock.Broker\FolderSessionLock.Broker.csproj`。不得无理由创建功能重复的第二个提升权限项目；若必须拆分，先记录新架构决定、请求用户确认并重新经过 `stage_director`。
- Broker/Service 可执行文件固定为 `%ProgramFiles%\FolderSessionLock\FolderSessionLock.Broker.exe`；配套程序集位于 `%ProgramFiles%\FolderSessionLock\`。
- 服务注册 binPath 语义固定为 `"%ProgramFiles%\FolderSessionLock\FolderSessionLock.Broker.exe" --mode recovery-service`。
- 交互式同账户 consent Broker 参数固定为：`--mode consent-broker --pipe-name FolderSessionLock.Broker.v1 --session-id <UInt32> --request-id <lowercase Guid D> --client-process-id <UInt32> --client-process-creation-filetime <UInt64 decimal>`。
- `client-process-id` 与 `client-process-creation-filetime` 只用于重新打开并绑定发起 UI 进程；Account SID 与 Logon SID 不进入命令行。禁止 `--account-sid`、`--logon-sid`、`--user-name`、`--is-admin`、`--role`、`--pipe-sddl`。
- `pipe-name` 必须精确等于 `FolderSessionLock.Broker.v1`；不接受任意 Pipe 名。`session-id` 必须与实际交互会话一致；`request-id` 必须为单次 GUID 并进行防重放验证。
- 不接受自定义恢复路径、任意 ACL 描述符、脚本、shell、PowerShell、cmd 或未知参数；全部安全失败。
- 上述标识符已经确认，`planner` 不得另提名称或静默改名。任何变更必须记录提议、说明迁移和安全影响、重新请求用户确认，并由 `stage_director` 在确认前保持 `BLOCKED`。

### D-024.1 `recovery-once` 唯一退出码与优先级

- 固定入口为 `FolderSessionLock.Broker.exe --mode recovery-once`。执行顺序：参数检查 → D-023 安全前置 → 完整枚举与分类 → 数量上限 → 稳定顺序处理全部适用记录 → 输出结构化摘要 → 返回唯一整数退出码。
- 唯一退出码：`0 = Success`、`2 = InvalidArguments`、`10 = ProtectedStorageSecurityFailure`、`11 = RecoveryEnumerationFailure`、`12 = RecoveryRecordLimitExceeded`、`13 = RecoveryBlocked`、`14 = Cancelled`、`15 = InternalFailure`。禁止返回 Win32 error、HRESULT、Exception.HResult、NTSTATUS 或记录级错误码。
- 优先级固定为 `InvalidArguments → ProtectedStorageSecurityFailure → RecoveryEnumerationFailure → RecoveryRecordLimitExceeded → RecoveryBlocked → Cancelled → InternalFailure → Success`。
- `0` 同时覆盖无记录和全部记录安全完成；必须无非法/孤立构件、无 RecoveryRequired、无未验证/取消跳过记录且 `recoveryBlocking = false`。scheduler error 不影响成功。
- `2` 使用 `FSL_E_INVALID_ARGUMENTS`；未知、缺失、重复、目录/service/Pipe/ACL/SID/脚本或协议禁止参数均在 D-023、枚举和 ACL 前拒绝。
- `10` 仅用于 D-023 owner、DACL、目录身份、reparse 或策略错误；不枚举、不修改 ACL，blocking=true。
- `11` 仅用于安全前置通过后无法证明目录枚举完整；单记录 I/O 不使用 11。
- `12` 用于总条目 >4096 或规范 `.fslr` >1024；不清理、不修改 ACL、不删除构件，错误码 `FSL_E_RECOVERY_RECORD_LIMIT_EXCEEDED`。
- `13` 用于扫描完成后任一记录/ACL/构件/剩余记录安全阻塞；结构化摘要包含第一个记录级主错误。
- `14` 仅在外部取消导致至少一条未开始记录跳过，且取消前无记录错误、RecoveryRequired、非法构件或未知 ACL，所有已进入临界区记录到达安全终态时使用；否则返回 13。
- `15` 仅用于无法映射的进程级内部故障。已经存在记录级 Cleanup 主错误时仍返回 13，内部异常只写受保护诊断。

### D-024.2 `recovery-service` 生命周期、readiness 与 CreateLock gate

- 服务启动后执行一次启动恢复扫描，扫描后继续托管但不周期扫描；禁止 FileSystemWatcher 自动恢复、普通 UI 任意清理、创建新锁、恢复旧倒计时、网络访问。
- 内部状态机固定为 `StartPending → Preflight → Scanning → Ready 或 RecoveryBlocked → Stopping → Stopped`。Ready 与 RecoveryBlocked 均对应 SCM `SERVICE_RUNNING`。
- 完成参数、D-023、完整枚举、上限、扫描或确定阻塞、readiness 发布前不得报告 Running；StartPending 长操作更新 SCM checkpoint。
- `recoveryBlocking == false` 时进入 Ready 并保持空闲等待 Stop。blocking=true 时进入 RecoveryBlocked，仍报告 Running，但 CreateLock 返回 `FSL_E_RECOVERY_BLOCKING`；ValidatePath 与脱敏 GetStatus 可继续。
- D-023、依赖初始化或 readiness 发布失败时不得进入 Running，报告 `SERVICE_STOPPED` 和对应 `FSL_E_PROTECTED_PATH_*`、`FSL_E_RECOVERY_DIRECTORY_*` 或 `FSL_E_INTERNAL`；读取方无法取得有效 readiness 时按 blocking=true。
- Stop：立即进入 Stopping、blocking=true、停止开始新记录、取消非临界操作；ACL 临界区必须完成安全终态，未开始记录计 Skipped，刷新最终受保护状态后报告 Stopped。不得在临界区 `Environment.Exit`、FailFast 或立即终止。wait hint 到期时更新 checkpoint 并延长，无法证明安全终态则保留记录并进入 RecoveryRequired。

```csharp
public enum RecoveryReadinessState
{
    Starting,
    Ready,
    RecoveryBlocked,
    Stopping
}

public sealed record RecoveryReadinessSnapshot(
    int SchemaVersion,
    string ServiceName,
    Guid ServiceInstanceId,
    long Sequence,
    RecoveryReadinessState State,
    bool RecoveryBlocking,
    DateTimeOffset ScanStartedUtc,
    DateTimeOffset? ScanCompletedUtc,
    DateTimeOffset PublishedUtc,
    DateTimeOffset ValidUntilUtc,
    int RemainingRecordCount,
    string? PrimaryErrorCode);

public interface IRecoveryReadinessPublisher
{
    ValueTask PublishAsync(
        RecoveryReadinessSnapshot snapshot,
        CancellationToken cancellationToken);
}

public interface IRecoveryReadinessReader
{
    ValueTask<RecoveryReadinessSnapshot> ReadAsync(
        CancellationToken cancellationToken);
}
```

- `SchemaVersion = 1`。D-030 将跨进程表示固定为受保护机器范围快照文件，并补充 `ServiceName`、`Sequence`、`PublishedUtc`、`ValidUntilUtc`。snapshot 缺失、schema 不支持、读取/owner/DACL/identity 验证失败、过期、State != Ready、blocking=true、ScanCompletedUtc=null、RemainingRecordCount !=0 或 PrimaryErrorCode !=null 时读取方必须视为 blocking。
- CreateLock 必须在路径和 ACL 写入前读取 readiness；仅 `State == Ready && RecoveryBlocking == false && RemainingRecordCount == 0 && PrimaryErrorCode == null` 时继续，否则返回 code `FSL_E_RECOVERY_BLOCKING`、message `Folder restrictions cannot be created until recovery is complete.`、retryable true、field null。

## D-025：阶段 4 隔离 VM 与系统操作授权

- 状态：已决定。
- 唯一获准特权集成测试机器：计算机名 `FSL-STAGE4-VM`；专用、可丢弃、可快照回滚的 Windows 11 Pro/Enterprise VM；快照 `FolderSessionLock-Stage4-Clean`。
- 当前机器名不等于 `FSL-STAGE4-VM` 时，服务安装、LocalSystem、自动启动、登录前执行、UAC、注销、重启、Program Files/ProgramData ACL 和签名系统测试必须停止并报告环境阻塞；设计、代码、非特权测试和静态审查可以继续。
- 禁止在宿主物理机、公司生产设备、域控制器或含真实用户数据的 VM 执行上述特权测试。
- 只允许创建、查询、配置、描述、设置 Automatic、设置 LocalSystem、启用服务 SID、启动、停止、重启、验证失败行为、删除并验证删除后的服务 `FolderSessionLockRecovery`。禁止修改其他服务、SCM 全局配置或调用方提供的任意 service name/binPath。
- 登录前唯一业务动作：读取受保护恢复记录 → 验证目录身份 → 验证精确应用 ACE → 安全移除旧 ACE → 验证访问控制恢复 → 删除已完成恢复记录。禁止恢复任务、重启倒计时、创建新 ACE、访问网络、读取目录内容、扫描无关目录或修改审计策略。
- 仅在该 VM、当前本地管理员账户下批准同账户 consent elevation、最多 3 次注销、最多 3 次完整重启、UI 关闭后后台行为和服务启动前后状态测试。跨账户路径仅保留不创建第二账户的 fail-closed 单元测试，不收集真实双账户 VM 证据。
- 注销或重启前必须保存测试证据，确认目标仅为 `%TEMP%\FolderSessionLock.Tests\<Guid>`，恢复记录已原子提交，不存在仓库或真实用户目录目标，并输出场景编号。每轮前后必须恢复已知快照或完成清理验证。
- 当前环境 `AGREELIN` 不是获准 VM；本决定记录轮不得执行任何上述系统操作。

## D-026：阶段 4 本地 unsigned 发布与人工验证证据

- 状态：已决定；schema v2 与 D-031 取代本决定旧的测试证书、双账户和 schema v1 完成条件。
- 当前 Stage 4 run 不创建或使用测试签名证书，不要求 publisher pin。六个第一方 PE 必须逐一验证实际 Authenticode 状态为 `NotSigned` 且 signer 为 null；不得把 unsigned 记录为 signed。Finalize 必须使用受保护 state 的 ReleaseRoot 与 ReleaseDescriptorSha256 重新验证 frozen descriptor、精确六 PE 集合与实际文件 SHA-256，并要求 `signature-verification.txt` 的有序记录逐项精确相等；不得信任 evidence 自报 hash。
- 当前 Stage 4 公共控制器不公开 publisher pin 或 signing certificate 参数，固定把精确空 `BrokerPublisherThumbprint` 写入 App assembly metadata，且无 signed/SignTool 执行分支。生产组合仍须先通过固定 Program Files 路径、owner/DACL、文件身份、hash/TOCTOU 与不可替换性门。App runtime verifier 对 null/精确空值不调用 Authenticode platform，对空白/畸形非空 pin fail closed，并为未来 runtime configuration 保留有效 40 位 pin 的精确 signed fail-closed 合同；当前 Stage 4 控制器不可选择该路径。
- 当前 run 的自动构建、测试和非交互验证由 Codex/coder 执行；同账户 UAC consent、注销和重启由当前本地管理员人工批准；最终结果由用户与 reviewer 确认。Codex 不请求、读取、记录或回显密码。
- 跨账户拒绝稳定错误码和任何 ACL 写入前的 fail-closed 行为保留，但跨账户 elevation 不属于当前支持范围，且不创建第二账户、不收集真实双账户 VM 证据。
- 证据仓库目录固定为 `docs\evidence\stage-4\<RunId>\`，`RunId` 格式为 `yyyyMMddTHHmmssZ-<short-guid>`。
- 必需证据文件：`manifest.json`、`commands.txt`、`build-results.txt`、`test-results.trx`、`service-config.txt`、`service-status-before.txt`、`service-status-after.txt`、`signature-verification.txt`、`acl-before.txt`、`acl-locked.txt`、`acl-after-recovery.txt`、`recovery-record-transitions.txt`、`access-probe-results.json`、`application-events.txt`、`cleanup-results.txt`、`reviewer-verdict.md`。`cleanup-results.txt` 必须包含精确 `CertificatesRemaining=0`，且 FinalizeEvidence 必须验证。人工场景可附 `screenshots\uac-consent.png` 与 `screenshots\post-reboot-recovery.png`。
- `scenario-results.json` schema v2 顶层精确字段为 `schemaVersion`（Int32=2）、`runId`、`sameAccountConsentPassed`、`preLoginRecoveryPassed`、`aclRestored`、`temporaryDirectoriesRemoved`、`recoveryRecordsRemoved`、`remainingRisks`（string array）与非空 `scenarios`。每个 scenario 精确字段为 `scenarioId`、`description`、`expectedResult`、`actualResult`、`result`、`evidenceFiles`；result 仅 `PASS|FAIL|BLOCKED`，evidenceFiles 必须非空、位于本 RunId evidence 根内且实际存在。
- `manifest.json` 精确字段：

```json
{
  "evidenceSchemaVersion": 2,
  "runId": "string",
  "stage": 4,
  "gitCommit": "string",
  "machineName": "FSL-STAGE4-VM",
  "osVersion": "string",
  "startedUtc": "RFC3339 UTC",
  "completedUtc": "RFC3339 UTC",
  "executor": "human-and-codex",
  "productScope": "LOCAL_SINGLE_USER_ADMINISTRATOR_ONLY",
  "executorModel": "TRUSTED_SINGLE_USER_STAGE4_EXECUTOR_MODEL",
  "serviceName": "FolderSessionLockRecovery",
  "scenarios": [
    {
      "scenarioId": "string",
      "description": "string",
      "expectedResult": "string",
      "actualResult": "string",
      "result": "PASS | FAIL | BLOCKED",
      "evidenceFiles": ["string"]
    }
  ],
  "buildPassed": true,
  "testsPassed": true,
  "authenticodeStatus": "NotSigned",
  "sameAccountConsentPassed": true,
  "preLoginRecoveryPassed": true,
  "aclRestored": true,
  "temporaryDirectoriesRemoved": true,
  "recoveryRecordsRemoved": true,
  "reviewerVerdict": "PASS | FAIL",
  "remainingRisks": ["string"]
}
```

- 证据不得记录密码、凭据、私钥、令牌、未脱敏个人用户名或敏感测试内容；命令输出移除凭据和不必要个人路径。`TASKS.md` 与 `DEVLOG.md` 引用 RunId，reviewer 核验 manifest 与实际证据一致。

## D-027：Broker IPC v1 精确协议

- 状态：已决定。
- 传输：Windows Named Pipe，固定名 `FolderSessionLock.Broker.v1`，本地路径语义 `\\.\pipe\FolderSessionLock.Broker.v1`；仅本机、最小 Pipe ACL。服务端从 OS 取得客户端进程、Account SID、Logon SID 和 Session ID，不信任 JSON 身份声明。
- 连接：一个连接只允许一个请求和一个响应；响应完成后关闭；不支持批量或流式请求。
- 分帧：byte mode；4-byte little-endian `UInt32` 长度前缀 + UTF-8 JSON 正文；最大 65536 bytes。长度 0、超限、读取长度不符、消息后额外字节、多 JSON 值、UTF-8 BOM、非法 UTF-8全部拒绝。
- JSON：UTF-8 without BOM；字段名、命令和枚举大小写敏感；禁止注释、尾随逗号、`NaN`、`Infinity`、数字字符串、浮点替代整数、同名重复字段、未知字段、缺失必需字段和未明确允许的 `null`。反序列化业务对象前必须用流式 reader 检测重复属性名，再进行严格 schema 校验。

### D-027.1 请求 envelope

每个请求精确包含六个字段：

```json
{
  "protocolVersion": 1,
  "requestId": "11111111-2222-3333-4444-555555555555",
  "command": "ValidatePath",
  "clientSessionId": 1,
  "sentAtUtc": "2026-07-19T16:30:00.0000000Z",
  "payload": {}
}
```

- `protocolVersion`：JSON number / .NET `Int32`，固定 1；其他值 `FSL_E_PROTOCOL_VERSION_UNSUPPORTED`。
- `requestId`：非空、小写 Guid D 格式 string；无大括号、必须有连字符。同一 Broker 实例最近 10 分钟不得重用；重复返回 `FSL_E_REPLAY_DETECTED`。
- `command`：string；允许值仅 `ValidatePath`、`CreateLock`、`RemoveLock`、`GetStatus`；大小写错误、空值或其他值返回 `FSL_E_UNKNOWN_COMMAND`。
- `clientSessionId`：JSON number / .NET `UInt32`；必须与 OS 客户端 Session ID 相等，否则 `FSL_E_SESSION_MISMATCH`。
- `sentAtUtc`：`yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`；必须 UTC、`Z`、7 位小数，与服务端 UTC 差不超过 120 秒；否则 `FSL_E_REQUEST_EXPIRED`。
- `payload`：必需 object；不得为 null、array、string、number 或 boolean。

### D-027.2 响应 envelope

成功响应精确包含七个字段：

```json
{
  "protocolVersion": 1,
  "requestId": "11111111-2222-3333-4444-555555555555",
  "command": "ValidatePath",
  "success": true,
  "serverTimeUtc": "2026-07-19T16:30:00.1000000Z",
  "result": {},
  "error": null
}
```

成功时 `protocolVersion = 1`，回显非 null `requestId`/`command`，`success = true`，`serverTimeUtc` 使用同一 UTC 格式，`result` 为非 null object，`error = null`。

失败响应使用相同七字段；`success = false`、`result = null`、`error` 非 null。若 requestId/command 尚未合法解析，则两者为 null，错误固定为 `FSL_E_MALFORMED_MESSAGE`。

`error` 精确字段：

```json
{
  "code": "FSL_E_PATH_NOT_ALLOWED",
  "message": "The selected folder is not allowed.",
  "retryable": false,
  "field": "payload.path"
}
```

- `code`：string，`FSL_E_<UPPER_SNAKE_CASE>`。
- `message`：string，最多 256 Unicode 字符；用户可显示且脱敏，不含 stack、内部类名、恢复记录、SID、凭据、SDDL、未脱敏系统路径或 Win32 调试缓冲。
- `retryable`：boolean，仅表示用新 requestId、相同语义稍后重试是否可能成功。
- `field`：string 或 null；字段错误使用完整 JSON path；非单字段错误为 null。
- `FSL_E_INTERNAL` 公开 message 固定为 `The operation could not be completed.`；内部异常仅写受保护日志并通过 requestId 关联。

### D-027.3 通用错误与 schema

- `FSL_E_UNKNOWN_COMMAND`：未知、大小写错误或空 command。
- `FSL_E_MALFORMED_MESSAGE`：长度、读取、大小、UTF-8、JSON 根、重复字段、尾随数据、token 类型、整数溢出、日期/Guid/enum 格式错误。
- `FSL_E_SCHEMA_VIOLATION`：缺少、多余、不允许 null、payload/command 不匹配或条件字段组合错误。
- `FSL_E_FORBIDDEN_INPUT`：客户端提供 SID、Logon SID、ACL mask、SDDL、ACE、恢复路径、安装路径、服务名、Pipe 名、shell、PowerShell、cmd、脚本、可执行文件路径、`LockRemovalIntent` 或用户清理模式。字段即使为空也拒绝。
- 其他通用码：`FSL_E_PROTOCOL_VERSION_UNSUPPORTED`、`FSL_E_REPLAY_DETECTED`、`FSL_E_REQUEST_EXPIRED`、`FSL_E_SESSION_MISMATCH`、`FSL_E_UNAUTHORIZED_CALLER`、`FSL_E_PIPE_ACCESS_DENIED`、`FSL_E_OPERATION_CANCELLED`、`FSL_E_INTERNAL`。
- 未知字段：`FSL_E_SCHEMA_VIOLATION`；缺失字段同码且 `field` 指向缺失字段；任何 object 重复同名属性：`FSL_E_MALFORMED_MESSAGE`。属性比较大小写敏感；`taskId` 与 `TaskId` 不重复，但 `TaskId` 是多余字段并返回 schema violation。

### D-027.4 基础类型

- Guid：小写 D 格式 string。
- 持续时间：`Int64` JSON integer milliseconds；字段名以 `Milliseconds` 结尾；不允许小数、指数、字符串或负数，除非字段另有规定。
- 日期：`yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` UTC string。
- 枚举：PascalCase、大小写敏感 string；不允许数值；未知值 `FSL_E_SCHEMA_VIOLATION`。
- 整数：必须 JSON integer token；不接受 `1.0`、`1e3`、前导正号或超出目标 .NET 类型范围。

### D-027.5 ValidatePath

请求 payload 只有 `path`：string，长度 1..32767，非空白、绝对本地路径；禁止 URI、UNC、设备路径、ADS、客户端 normalizedPath、volume serial、file ID 或 reparse 信息。

成功 result 精确字段：

```json
{
  "normalizedPath": "D:\\Example\\LockedFolder",
  "volumeRoot": "D:\\",
  "volumeSerialNumber": "0123456789abcdef",
  "fileIdHigh": "0",
  "fileIdLow": "123456789",
  "fileSystem": "NTFS",
  "driveType": "Fixed",
  "isReparsePoint": false,
  "isAllowed": true
}
```

- `volumeSerialNumber`：16 位小写 hex string，来源为 `FILE_ID_INFO.VolumeSerialNumber` UInt64；file IDs 按 D-022.1 将完整 FILE_ID_128 两个 8-byte little-endian half 编码为 UInt64 十进制 string；其余固定值如示例。
- 成功只代表本次检查；`CreateLock` 必须重新完整验证。
- 失败码：`FSL_E_PATH_EMPTY`、`FSL_E_PATH_NOT_ABSOLUTE`、`FSL_E_PATH_INVALID`、`FSL_E_PATH_NOT_FOUND`、`FSL_E_PATH_NOT_DIRECTORY`、`FSL_E_PATH_ROOT_FORBIDDEN`、`FSL_E_PATH_SYSTEM_FORBIDDEN`、`FSL_E_PATH_USER_PROFILE_ROOT_FORBIDDEN`、`FSL_E_PATH_APPLICATION_FORBIDDEN`、`FSL_E_PATH_REPOSITORY_FORBIDDEN`、`FSL_E_PATH_NETWORK_FORBIDDEN`、`FSL_E_PATH_DRIVE_TYPE_UNSUPPORTED`、`FSL_E_PATH_FILESYSTEM_UNSUPPORTED`、`FSL_E_PATH_REPARSE_POINT_FORBIDDEN`、`FSL_E_PATH_ACCESS_DENIED`、`FSL_E_PATH_IDENTITY_UNAVAILABLE`、`FSL_E_PATH_NOT_ALLOWED`；全部 `field = payload.path`。

### D-027.6 CreateLock

请求 payload 精确字段：`taskId`（非空小写 Guid D，映射现有 `FolderLockTaskId`）、`path`（映射现有 `FolderPath`，服务端重新规范化验证）、`durationMilliseconds`（JSON integer / .NET `Int64`，>0 且符合显式 `LockDurationPolicy`，映射 `LockDuration`）。禁止客户端 `expiresUtc`、`startedUtc` 或 remaining time。

必须执行 `JSON DTO → 严格 schema → 领域值对象 → 领域验证 → Broker 安全验证 → 执行`；DTO 不替代领域模型。

成功 result 精确字段：`taskId`、`normalizedPath`、`status` 固定 `Active`、`startedUtc`、`expiresUtc`、`durationMilliseconds`、`remainingMilliseconds`、`recoveryRecordId`、`idempotentReplay`。只有 Prepared 原子提交、ACE 写入、ACL 后置验证、记录 Applied、任务 Active 全部成功才响应成功。

- 相同 taskId、规范化路径和 duration 且已 Active：成功，`idempotentReplay = true`，不重复 ACE/记录。
- 相同 taskId、路径或 duration 不同：`FSL_E_TASK_ID_CONFLICT`，field `payload.taskId`，retryable false。
- 同路径不同 taskId：`FSL_E_PATH_ALREADY_LOCKED`。
- 父子重叠：`FSL_E_PATH_OVERLAP`。
- 其他错误：`FSL_E_DURATION_OUT_OF_RANGE`、`FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`、`FSL_E_RECOVERY_RECORD_WRITE_FAILED`、`FSL_E_ACL_APPLY_FAILED`、`FSL_E_ACL_POST_VERIFY_FAILED`、`FSL_E_ACL_ROLLBACK_FAILED`、`FSL_E_RECOVERY_REQUIRED`。

### D-027.7 RemoveLock

- 普通 WPF UI 禁止调用，返回 `FSL_E_UNAUTHORIZED_CALLER`；用户不能提前解除。
- 允许主体：`FolderSessionLockRecovery` 服务/recovery-service、recovery-once、同一 Broker 内部到期 scheduler、编译条件启用的隔离 VM 测试清理主体。其他本地进程、用户、Session、跨账户提升、未签名测试程序均禁止。
- 角色由 OS 身份和 Broker 启动模式判断；客户端不得提供角色。
- 客户端不得发送 `LockRemovalIntent`。映射：内部 scheduler → `Expiration`；recovery-service/recovery-once → `Recovery`；批准测试清理 → `TestCleanup`。v1 公开 IPC 不支持 `AdministrativeCleanup` 或 `UserRequested`。
- 禁止 payload 字段：`intent`、`removalIntent`、`reason`、`force`、`acl`、`ace`、`sddl`、`recoveryPath`；出现即 `FSL_E_FORBIDDEN_INPUT`。
- 请求 payload 精确为 `taskId`、`recoveryRecordId` 两个必需 Guid string；恢复记录为事实源，必须验证对应。
- 成功 result 精确字段：`taskId`、`recoveryRecordId`、`removalIntent`（`Expiration|Recovery|TestCleanup`）、`previousStatus`、`status` 固定 `Completed`、`removedUtc`、`aceRemoved`、`recoveryRecordDeleted`、`idempotentReplay`。
- 幂等只在精确 ACE 不存在、ACL 已预期解锁且恢复记录证明已清理时成立；返回 `idempotentReplay = true`、`aceRemoved = false`、`recoveryRecordDeleted = true`。状态不一致禁止幂等成功。
- 失败：`FSL_E_UNAUTHORIZED_CALLER`、`FSL_E_TASK_NOT_FOUND`、`FSL_E_RECOVERY_RECORD_NOT_FOUND`、`FSL_E_RECOVERY_RECORD_MISMATCH`、`FSL_E_PATH_IDENTITY_CHANGED`、`FSL_E_ACL_STATE_MISMATCH`、`FSL_E_ACL_REMOVE_FAILED`、`FSL_E_ACL_POST_VERIFY_FAILED`、`FSL_E_RECOVERY_RECORD_DELETE_FAILED`、`FSL_E_RECOVERY_REQUIRED`。

### D-027.8 GetStatus

- 请求固定含 `queryType`、`taskId`。`ByTaskId` 要求非空 Guid；`CurrentSession` 要求 taskId 显式 null；其他组合 `FSL_E_SCHEMA_VIOLATION`。
- `ByTaskId` 返回 Broker 内存仍保留的单个任务，可含 Active、Completed、失败；不从历史 DB 恢复。`CurrentSession` 返回当前会话内存仍保留的全部 `Created`、`Activating`、`Active`、`Unlocking`、`Completed`、`ActivationFailed`、`UnlockFailed`、`RecoveryRequired`；不是持久历史。
- result 精确字段：`queryType`、`tasks` array。
- 每个 TaskStatusItem 精确字段：`taskId`、`normalizedPath`、`status`、`startedUtc`、`expiresUtc`、`durationMilliseconds`、`remainingMilliseconds`、`canUserRemove`、`recoveryRequired`、`error`。
- `startedUtc`/`expiresUtc` 可 null；`remainingMilliseconds` 最小 0，Completed/到期后 0，服务端可信时钟计算；`canUserRemove` 固定 false；`recoveryRequired` 与状态一致。
- task `error` 为 null 或仅含 `code`、`message`、`retryable`；无 `field`。不得公开 stack、Exception 类型、内部/恢复路径、SDDL、ACE、Account/Logon SID、token、Pipe ACL、证书私钥、凭据、调试缓冲或其他用户/Session 任务。
- 只返回调用方自身账户与交互 Session 任务。`ByTaskId` 不存在或不属于当前身份统一 `FSL_E_TASK_NOT_FOUND`，不泄露存在性。其他错误：`FSL_E_UNAUTHORIZED_CALLER`、`FSL_E_SESSION_MISMATCH`。

### D-027.9 权限矩阵与实现边界

- 普通 UI：允许 ValidatePath/CreateLock/GetStatus；禁止 RemoveLock。
- consent-broker 内部：ValidatePath/CreateLock/GetStatus；RemoveLock 仅 Expiration。
- recovery-service：ValidatePath 仅恢复验证；禁止 CreateLock；RemoveLock 仅 Recovery；GetStatus 仅恢复状态。
- recovery-once：ValidatePath 仅恢复验证；禁止 CreateLock；RemoveLock 仅 Recovery；GetStatus 仅诊断。
- 测试清理主体：仅隔离 VM 测试构建允许 RemoveLock → TestCleanup。
- 协议 DTO 必须与领域模型分离，位于独立协议项目或清晰独立命名空间；WPF DTO 不得成为 Broker 协议模型。若新增项目改变架构，由 planner 说明；不得更改外部合同。
- 实现必须使用显式 JSON 属性名、严格大小写、未知字段拒绝、重复字段检测、schema 二次验证、最大长度、CancellationToken 和读取超时；客户端错误不得成为未处理异常。
- 最低恶意输入/验收矩阵：正确请求；command 大小写/未知；重复/多余/缺失/null 字段；Guid 非法/大写；日期非 UTC/非 7 位；replay；过期；Session mismatch；payload 类型；超长/长度不符/非法 UTF-8/尾随数据；duration 浮点/字符串/越界；客户端 SID/ACL/removalIntent；UI RemoveLock；GetStatus 跨账户；CreateLock 幂等/冲突；错误脱敏；内部异常固定错误；成功 error null；失败 result null。
- 字段名、类型、命令大小写、错误码、Pipe 名、envelope、RemoveLock 权限、时间/Guid 表示、未知字段策略不得静默修改。必须变更时记录兼容性/安全影响，停止实现，经过 `stage_director` 并重新请求用户确认。

### D-027.10 固定连接序列与握手帧

- consent-broker 公共 IPC 每连接固定序列为 `ClientHello -> ServerHello -> CommandRequest -> CommandResponse -> 服务端关闭`。禁止跳过握手、重复 ClientHello、ServerHello 前发送 CommandRequest、第二个 CommandRequest、响应后额外数据、批处理、流式、复用和多路复用。
- AwaitClientHello 状态下第一个语法有效、schema 可识别帧不是 ClientHello 时，只返回 `FSL_E_HANDSHAKE_REQUIRED` ServerHello failure；成功 ServerHello 后收到不符合当前状态的语法有效帧时，只返回 `FSL_E_PROTOCOL_SEQUENCE_INVALID` CommandResponse failure。不得对同一场景选择其他顺序错误码。`recovery-service` 不接受普通 UI 公共 IPC。
- 所有握手与命令传输帧复用 D-027 的 4-byte little-endian `UInt32` 长度、1..65536 bytes、严格 UTF-8 without BOM JSON 和严格 schema。
- ClientHello 顶层精确九字段：`frameType`、`handshakeVersion`、`protocolVersion`、`requestId`、`command`、`claimedClientProcessId`、`clientSessionId`、`clientNonce`、`sentAtUtc`。`frameType = ClientHello`；两个版本均为 Int32 固定 1；PID 为 UInt32 且 1..UInt32.MaxValue；`clientNonce` 为 Base64URL without padding、解码 32 bytes、非全零、密码学安全随机且不得重用。
- ServerHello 顶层精确九字段：`frameType`、`handshakeVersion`、`protocolVersion`、`requestId`、`command`、`success`、`serverTimeUtc`、`result`、`error`。成功 result 精确为 `connectionId`、`serverNonce`、`expiresUtc`；connectionId 为每连接唯一随机非空小写 Guid D；serverNonce 为密码学安全随机 Base64URL without padding 32 bytes；expiresUtc 固定为 serverTimeUtc + 30 seconds。失败 result 为 null、error 非 null；无法解析合法 requestId/command 时两者为 null、错误为 `FSL_E_MALFORMED_MESSAGE`、message 固定 `The handshake message is malformed.`。
- CommandRequest 顶层精确八字段：`frameType`、`handshakeVersion`、`protocolVersion`、`requestId`、`command`、`connectionId`、`bindingProof`、`request`；request 是 D-027.1 六字段应用请求。
- CommandResponse 顶层精确七字段：`frameType`、`handshakeVersion`、`protocolVersion`、`requestId`、`command`、`connectionId`、`response`；response 是 D-027.2 七字段应用响应。发送完整响应后 Flush、停止读取、关闭 Pipe、销毁 nonce 和连接状态。

### D-027.11 CLI、握手与应用请求绑定

- `CLI --request-id = ClientHello.requestId = CommandRequest.requestId = CommandRequest.request.requestId`，四者均为字节语义相同的小写 Guid D。
- `CLI --session-id = ClientHello.clientSessionId = CommandRequest.request.clientSessionId = OS 客户端 Session ID = Broker 进程 Session ID`。
- ClientHello.command、CommandRequest.command、CommandRequest.request.command 必须相同；三个 protocolVersion 必须相同。任一绑定不一致返回 `FSL_E_REQUEST_BINDING_MISMATCH`。
- 一个 requestId 只允许一个 Broker 启动实例、一个 Pipe 连接、一次握手、一个应用命令和一个应用响应；连接后不得更换 requestId。
- binding canonical string 精确为 `FSL-BIND-V1\n{requestId}\n{command}\n{connectionId}\n{clientNonce}\n{serverNonce}\n{clientSessionId}`，仅 LF 分隔、末尾无换行；clientSessionId 使用无前导零十进制 ASCII。
- bindingProof 为 `Base64URL-without-padding(SHA-256(UTF8(canonical binding string)))`，解码 32 bytes，服务端恒定时间比较。不匹配返回 `FSL_E_REQUEST_BINDING_MISMATCH`、message `The request is not bound to the active handshake.`、retryable false、field `bindingProof`。通过后握手立即标记已消费。

### D-027.12 身份验证顺序与错误对象

- consent-broker 固定处理顺序为：验证固定启动参数；读取第一个 frame；验证 framing/UTF-8/JSON/schema；要求 frameType=ClientHello；验证 handshakeVersion、protocolVersion、requestId、command、claimedClientProcessId、clientSessionId、clientNonce、sentAtUtc；验证 CLI requestId/session/pipe 绑定；取得 OS 实际客户端 PID；验证 PID、存活、启动时间和进程 Session；模拟 Pipe 客户端；读取 Account SID、Logon SID、TokenSessionId；在 finally 恢复 Broker 身份；读取 Broker Account SID、Logon SID、Session ID；比较全部 Account/Logon/Session；验证命令权限；计算 Replay key；原子 CreateNew Replay 登记；处理 active/terminal Replay；生成 connectionId/serverNonce/expiresUtc；原子更新 ChallengeIssued；发送成功 ServerHello。
- Replay 原子登记只允许发生在完整 OS 客户端身份验证、Broker identity 比较和命令权限验证全部成功之后。身份、CLI、schema、版本、时间或授权失败时绝不创建 Replay 登记；删除任何“身份失败后将已创建 Replay 标记 Failed”的兼容分支。
- `claimedClientProcessId != OS Named Pipe 客户端 PID`：`FSL_E_CLIENT_PROCESS_MISMATCH`，message `The connected client process does not match the handshake.`，retryable false，field `claimedClientProcessId`。
- 客户端 Account SID != Broker Account SID：`FSL_E_ACCOUNT_SID_MISMATCH`，message `The elevated broker account does not match the requesting account.`，retryable false，field null。
- 客户端 Logon SID != Broker Logon SID：`FSL_E_LOGON_SID_MISMATCH`，message `The broker and client do not belong to the same Windows logon session.`，retryable false，field null。
- ClientHello、CLI、客户端进程、客户端 Token 或 Broker 的 Session ID 任一不一致：`FSL_E_SESSION_MISMATCH`，message `The broker and client do not belong to the same Windows session.`，retryable false，field `clientSessionId`。
- 无法取得或证明客户端身份：`FSL_E_CLIENT_IDENTITY_UNAVAILABLE`，message `The client identity could not be verified.`，retryable false，field null。不得回退用户名、环境变量、客户端 SID 或其他非 OS 证明身份；错误不得公开 PID 或 SID。

### D-027.13 握手版本、时间与错误码

- `handshakeVersion` 为 Int32 固定 1；其他值返回 `FSL_E_HANDSHAKE_VERSION_UNSUPPORTED`。ClientHello 必须在 Pipe 连接后 5 seconds 内完整收到；成功 ServerHello 后 CommandRequest 必须在 30 seconds 握手有效期内完整收到，超时返回 `FSL_E_HANDSHAKE_EXPIRED`。
- 新增固定错误及 message：
  - `FSL_E_HANDSHAKE_REQUIRED`：`A valid handshake is required.`
  - `FSL_E_HANDSHAKE_VERSION_UNSUPPORTED`：`The handshake version is not supported.`
  - `FSL_E_HANDSHAKE_EXPIRED`：`The handshake has expired.`
  - `FSL_E_PROTOCOL_SEQUENCE_INVALID`：`The protocol message sequence is invalid.`
  - `FSL_E_REQUEST_BINDING_MISMATCH`：`The request is not bound to the active handshake.`
  - `FSL_E_CLIENT_PROCESS_MISMATCH`：`The connected client process does not match the handshake.`
  - `FSL_E_CLIENT_IDENTITY_UNAVAILABLE`：`The client identity could not be verified.`
  - `FSL_E_ACCOUNT_SID_MISMATCH`：`The elevated broker account does not match the requesting account.`
  - `FSL_E_LOGON_SID_MISMATCH`：`The broker and client do not belong to the same Windows logon session.`
  - `FSL_E_SESSION_MISMATCH`：`The broker and client do not belong to the same Windows session.`
  - `FSL_E_REPLAY_DETECTED`：`The request has already been used.`
  - `FSL_E_REQUEST_IN_PROGRESS`：`The request is already being processed.`

六个握手、序列与 Replay 错误的唯一完整合同：

- 错误优先级固定为：framing/UTF-8/JSON → `FSL_E_MALFORMED_MESSAGE`；可解析但 schema 错误 → `FSL_E_SCHEMA_VIOLATION`；AwaitClientHello 首帧非 ClientHello → `FSL_E_HANDSHAKE_REQUIRED`；handshakeVersion → `FSL_E_HANDSHAKE_VERSION_UNSUPPORTED`；protocolVersion → `FSL_E_PROTOCOL_VERSION_UNSUPPORTED`；CLI/ClientHello/CommandRequest 绑定 → `FSL_E_REQUEST_BINDING_MISMATCH`；PID/identity/session → 对应身份错误；active Replay → `FSL_E_REQUEST_IN_PROGRESS`；terminal/RecoveryRequired Replay → `FSL_E_REPLAY_DETECTED`；成功 ServerHello 后过期 → `FSL_E_HANDSHAKE_EXPIRED`；成功 ServerHello 后非法有效 frame → `FSL_E_PROTOCOL_SEQUENCE_INVALID`。同一场景不得匹配多个错误码。

| code | 唯一场景 | frame | retryable | field | Replay |
|---|---|---|---:|---|---|
| `FSL_E_HANDSHAKE_REQUIRED` | AwaitClientHello 的第一个语法有效、schema 可识别帧不是 ClientHello | ServerHello failure | true | `frameType` | 不创建 |
| `FSL_E_HANDSHAKE_VERSION_UNSUPPORTED` | 合法 ClientHello 的 handshakeVersion != 1 | ServerHello failure | false | `handshakeVersion` | 不创建 |
| `FSL_E_HANDSHAKE_EXPIRED` | 成功 ServerHello 后超过 expiresUtc，仍未收到完整可验证 CommandRequest | CommandResponse failure | true | null | Abandoned，terminalCode 同 code，保留 10 minutes |
| `FSL_E_PROTOCOL_SEQUENCE_INVALID` | 成功 ServerHello 后收到语法有效但不符合状态的 frame，包括重复 ClientHello、非 CommandRequest、第二 CommandRequest、响应开始后额外协议 frame或第二应用命令 | CommandResponse failure | false | `frameType` | 无副作用为 Failed；未知副作用为 RecoveryRequired |
| `FSL_E_REQUEST_IN_PROGRESS` | 完整身份与权限验证后，现有相同 Replay key 为未过期 Handshaking/ChallengeIssued/Executing | ServerHello failure | true | `requestId` | 不改记录、owner、lease 或 connectionId |
| `FSL_E_REPLAY_DETECTED` | 完整身份与权限验证后，现有相同 Replay key 为保留期内 Succeeded/Failed/RolledBack/Abandoned 或任意 RecoveryRequired | ServerHello failure | false | `requestId` | 不改记录、终态、retention 或 owner |

- ServerHello failure 顶层固定九字段，result=null、error 非 null，不生成 connectionId/serverNonce/expiresUtc。requestId 仅在输入为合法小写非空 Guid D 时回显，否则 null；不得用 CLI requestId 替代非法输入。command 仅在输入为四个精确允许值时回显，否则 null。
- HANDSHAKE_VERSION_UNSUPPORTED 的失败 ServerHello 顶层 handshakeVersion 固定为服务端支持值 1，不回显客户端不支持版本。
- CommandResponse failure 顶层固定使用已接受的 ClientHello requestId、command 和成功 ServerHello connectionId；内层 response 回显同一已接受 requestId/command，success=false、result=null、error 非 null。不得回显后续恶意 frame 中的篡改值。
- 成功 ServerHello 前的失败只发送 ServerHello failure；成功 ServerHello 后的失败只发送 CommandResponse failure。发送一个失败 frame 后 Flush、关闭、释放连接状态并清除 nonce；对端断开时不改用其他 frame，只记录受保护日志并按对应 Replay 终态处理。

### D-027.14 机器范围 Replay Registry

- Replay 根固定为 `%ProgramData%\FolderSessionLock\Replay\v1`；登记文件为 `<ReplayKeySha256>.fsrr`，临时文件为 `<ReplayKeySha256>.tmp-<Guid>`。普通 UI 无直接访问。
- 目录使用与恢复记录目录同等保护：SYSTEM、Administrators、`NT SERVICE\FolderSessionLockRecovery` 为 FullControl；普通 Users 和 Everyone 无访问；不为 Authenticated Users 额外授权。
- Replay canonical input 精确为 `FSL-REPLAY-V1\n{brokerAccountSid}\n{brokerLogonSid}\n{brokerSessionId}\n{requestId}`，仅 LF、末尾无换行；SID 使用标准字符串；session ID 使用无前导零十进制；requestId 使用小写 Guid D。文件名为该 UTF-8 输入 SHA-256 的 64-char lowercase hex。
- 受保护 JSON 精确字段：`schemaVersion`、`replayKeySha256`、`requestId`、`command`、`state`、`ownerProcessId`、`ownerProcessStartUtc`、`ownerNonce`、`connectionId`、`createdUtc`、`lastUpdatedUtc`、`leaseExpiresUtc`、`retentionExpiresUtc`、`terminalCode`。schemaVersion 固定 1；ownerProcessId 为 UInt32；ownerNonce 为随机非空 Guid；connectionId、retentionExpiresUtc、terminalCode 按状态允许 null。
- state 关闭集合：`Handshaking`、`ChallengeIssued`、`Executing`、`Succeeded`、`Failed`、`RolledBack`、`RecoveryRequired`、`Abandoned`。

### D-027.15 原子登记、所有权与并发

- Replay 登记发生在完整 ClientHello/schema/CLI/time 验证、完整 OS 客户端身份验证、Broker Account/Logon/Session 比较和命令权限验证全部通过之后，成功 ServerHello 发送之前；必须使用 `FileMode.CreateNew` 或等价原子仅不存在时创建，禁止先检查再普通创建。
- 未过期 `Handshaking|ChallengeIssued|Executing` 返回 `FSL_E_REQUEST_IN_PROGRESS`；未过期 `Succeeded|Failed|RolledBack|Abandoned` 返回 `FSL_E_REPLAY_DETECTED`；`RecoveryRequired` 永久返回 replay detected 并阻止同一 task 新 CreateLock。
- 过期 replacement 必须在机器范围 Registry 锁内回读、复核过期、证明非 RecoveryRequired、原子删除/归档后创建新登记。
- 所有权四元组为 `ownerProcessId + ownerProcessStartUtc + ownerNonce + connectionId`。只有所有者可更新 state、lease、terminalCode、完成、RolledBack 或 RecoveryRequired；PID 单独无效，必须比较启动时间。ServerHello 前 connectionId 可 null，生成后原子更新。
- 清理与过期替换互斥锁固定为 `Global\FolderSessionLock.ReplayRegistry.v1`，使用受保护 DACL，普通用户不得创建同名抢占对象。

### D-027.16 TTL、失败撤销与崩溃

- ClientHello 接收超时：`5 seconds after pipe connection`。
- 握手有效期：`30 seconds`。
- 执行 lease：`60 seconds`。
- 续租周期：`每 20 seconds`。
- 单请求最长执行：`5 minutes`。
- 终态 Replay 保留时间：`10 minutes from terminal state`。
- RecoveryRequired 无自动过期。
- ClientHello framing/UTF-8/JSON/schema/version/CLI binding/time、客户端 PID/identity/Session、Broker identity 比较或命令权限失败时绝不创建 Replay 登记。ServerHello 已发送但 CommandRequest 未到达则 Abandoned + `FSL_E_HANDSHAKE_EXPIRED` + 10-minute retention。
- bindingProof 失败、无副作用应用验证失败均进入 Failed 并保留 10 minutes；开始副作用且 rollback 成功进入 RolledBack；rollback 失败或状态未知进入 RecoveryRequired、terminalCode `FSL_E_RECOVERY_REQUIRED`、retentionExpiresUtc null；成功进入 Succeeded、terminalCode null、保留 10 minutes。
- 即使业务错误 retryable=true，相同 requestId 也不得重试；重试必须新 requestId。业务 taskId 幂等与传输 requestId replay 分离；查询已成功任务使用新 requestId + GetStatus。
- 请求超过 5 minutes 时取消，根据副作用进入 Failed、RolledBack 或 RecoveryRequired，不得删除 Replay 记录。
- lease 未过期不得接管。lease 过期时检查 owner PID、启动时间、恢复记录、ACL 与未知副作用；owner 仍存活不得接管；owner 退出且可证明无副作用则 Abandoned；存在恢复记录或无法证明无副作用则 RecoveryRequired；新 Broker 不得用相同 requestId 继续原命令。

### D-027.17 CP4 必测矩阵

- 握手：正常；缺失/多余/重复字段；requestId/session 与 CLI 不同；声明 PID 不同；Account/Logon/Session 不匹配；身份不可用；nonce 长度/重用；ServerHello 超时；CommandRequest 顺序错误/重复；响应后额外数据；bindingProof、connectionId、command、JSON requestId 不一致。
- Replay：首次登记；并发唯一所有者与 `FSL_E_REQUEST_IN_PROGRESS`；成功/失败/超时后 replay；10-minute terminal TTL；20-second renew；owner 存活不接管；无副作用崩溃 Abandoned；有恢复记录崩溃 RecoveryRequired；PID 重用；非所有者更新拒绝；RolledBack/RecoveryRequired；RecoveryRequired 不自动过期；普通用户无法访问 Replay 目录；并发过期清理唯一清理者。
- 最终勘误测试：PID/Account/Logon/Session/identity/unauthorized 失败均无 Replay 文件；只有完整身份和权限通过后尝试 CreateNew；首帧 CommandRequest 只返回 HANDSHAKE_REQUIRED；版本错误只返回 HANDSHAKE_VERSION_UNSUPPORTED；ServerHello 后超时只返回 HANDSHAKE_EXPIRED；ServerHello 后重复 ClientHello/第二命令只返回 PROTOCOL_SEQUENCE_INVALID；active Replay 只返回 REQUEST_IN_PROGRESS；terminal/RecoveryRequired 只返回 REPLAY_DETECTED。
- 响应标识符测试：ServerHello failure 无 connectionId，合法 requestId/command 才回显；非法值为 null；CommandResponse failure 永远使用已接受 requestId/command/connectionId；每个失败 result=null，error 精确四字段。

## D-028：CP6 scheduler 与 Cleanup 错误优先级

- 状态：已决定。
- 决定：`cleanup first-task error` 优先；scheduler error 仅写入受保护内部日志。
- 无论 scheduler 是否发生错误，Cleanup 都必须启动并继续执行。Cleanup 按既定稳定任务顺序处理全部适用任务，单个任务失败不得提前终止剩余任务。
- 对外主错误固定为 Cleanup 实际稳定处理顺序中的第一个 task error，不按异步完成顺序。后续 task errors 继续进入受保护内部日志和诊断汇总，但不得替换主错误。
- 固定 2×2 结果矩阵：

| scheduler | Cleanup | 对外结果 |
|---|---|---|
| success | success | Cleanup success count |
| success | failure | Cleanup first-task error |
| failure | success | Cleanup success count |
| failure | failure | Cleanup first-task error |

- scheduler error 不得阻止 Cleanup，不得覆盖 Cleanup first-task error，也不得把全部成功的 Cleanup 伪造为失败。
- scheduler生产loop未预期非取消异常的唯一稳定合同为 code `lock_task.scheduler.loop.exception`、message `The lock task scheduler loop terminated unexpectedly.`，只写 protected logger，固定`component = Scheduler`、`level = Error`。预期token已取消的`OperationCanceledException`不记录。该code/message不得用于lifecycle stop、Cleanup failure、task状态转换、已有更具体错误或logger failure；不得公开、覆盖Cleanup first-task error或阻止Cleanup。日志不得写异常message、`ToString()`、stack、内部类型、路径、SID、HRESULT或Win32 message。
- Cleanup 进入 `RecoveryRequired`、ACL 状态未知或恢复失败时，仍返回对应 Cleanup task error；不得被 scheduler error 替换，不得声称清理完成。
- 受保护内部日志必须保留 scheduler error code、scheduler exception 的脱敏诊断、第一个 Cleanup task error、其余 Cleanup task errors、`taskId` 或受保护关联标识、Cleanup 是否完整遍历及是否存在 `RecoveryRequired`。
- 公开响应不得包含 scheduler exception 堆栈、内部类型名、SID、SDDL、恢复记录路径、凭据或令牌。
- CP6 测试必须覆盖四种 scheduler/Cleanup 组合、稳定首错顺序、后续错误不中断遍历、`RecoveryRequired` 优先级和公开响应脱敏。
- administrative Cleanup 的两个内部错误合同精确固定：
  - `RemoveLockAsync` 抛异常：code `lock_task.administrative_cleanup.exception`；message `The administrative cleanup ended without a confirmed result.`；category `UnrecoverableError`；任务状态 `RecoveryRequired`。
  - ACE 已移除但 `Completed` 状态记录失败：code `lock_task.administrative_cleanup.state_update_failed`；message `The lock was removed but its completed state could not be recorded.`；category `UnrecoverableError`；任务状态 `RecoveryRequired`。
- 以上 code、message、大小写和标点不得按实现偏好改写；不得静默复用 activation 或 expiration 专用错误。

## D-029：同账户 consent elevation 与 consent-broker 生产生命周期

- 状态：已决定。
- 本决定补充 D-024、D-026 与 D-027，不改变已经通过 reviewer 的 CP4 四帧握手、CP6 lifecycle Cleanup 或 CP8 recovery 合同。

### D-029.1 身份错误分层与 UI 转换

- 身份错误分为：层 A UI elevation launcher、层 B elevated Broker bootstrap、层 C 已连接 Named Pipe 握手。
- `FSL_E_ACCOUNT_SID_MISMATCH` 只属于层 C。唯一触发条件为：通过 Named Pipe 客户端模拟令牌取得的 Account SID 不等于当前 elevated Broker 进程令牌的 Account SID。它只在 ClientHello、实际 Pipe 客户端 PID、客户端令牌与 Broker 令牌均已取得后产生，响应保持 D-027 的 ServerHello failure：message `The elevated broker account does not match the requesting account.`、retryable false、field null。
- `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED` 只属于层 A/B。连接前 bootstrap 从发起 UI 进程令牌重取 Account SID，与 Broker Account SID 不同则不得创建 Pipe、Replay、恢复记录或执行路径/ACL 操作，consent-broker 退出 20。UI 映射为 message `Cross-account elevation is not supported.`、retryable false、field null。
- 连接后若 UI 收到 `FSL_E_ACCOUNT_SID_MISMATCH`，UI elevation client 必须只向普通 UI 返回 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`，受保护诊断记录 `sourceCode = FSL_E_ACCOUNT_SID_MISMATCH`。不得同时显示两个错误。
- `FSL_E_LOGON_SID_MISMATCH`、`FSL_E_SESSION_MISMATCH`、`FSL_E_CLIENT_PROCESS_MISMATCH`、`FSL_E_CLIENT_IDENTITY_UNAVAILABLE`、`FSL_E_PIPE_ACCESS_DENIED`、`FSL_E_UNAUTHORIZED_CALLER` 禁止转换为跨账户提升错误。Account SID 相同但 Logon SID 不同仍返回 `FSL_E_LOGON_SID_MISMATCH`；Account/Logon SID 相同但 Windows Session 不同仍返回 `FSL_E_SESSION_MISMATCH`。

### D-029.2 发起 UI 身份与 bootstrap

- UI 在 UAC 前通过 `OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY)` 与 `GetTokenInformation(TokenUser|TokenGroups|TokenSessionId)` 取得 Account SID、唯一 Logon SID 和 Windows Session ID。Logon SID 仍要求 TokenGroups 中完整包含 `SE_GROUP_LOGON_ID` 的匹配恰好一个；0 个或多个返回 `FSL_E_CLIENT_IDENTITY_UNAVAILABLE`。禁止回退用户名、`WindowsIdentity.Name`、环境变量、Account SID、桌面用户名或客户端 SID 字符串。
- UI 同时取得当前进程 `ProcessId` UInt32 与 `GetProcessTimes` 创建 FILETIME 的 UInt64 表示。内存模型固定为：

```csharp
public sealed record InitiatingClientIdentity(
    uint ProcessId,
    ulong ProcessCreationFileTime,
    string AccountSid,
    string LogonSid,
    uint WindowsSessionId);
```

- Account SID 与 Logon SID 只保存在 UI 内存，不进入命令行。consent-broker CLI 精确增加 `--client-process-id <UInt32>` 与 `--client-process-creation-filetime <UInt64 decimal>`；它们只用于 OS 对象定位与 PID 重用绑定，不是身份声明。
- Broker 在创建 Pipe 前依次：严格解析参数；以 `PROCESS_QUERY_LIMITED_INFORMATION` 打开 UI PID；确认进程存活；读取并比较创建 FILETIME；以 `TOKEN_QUERY` 打开 UI token；重取 TokenUser、唯一 Logon SID、TokenSessionId；读取 Broker 自身三项身份；比较 UI/Broker Account SID；比较 UI、Broker 与 CLI Session ID。Broker 不信任 UI 保存的 SID 文本。
- UI PID 不存在、已退出、无法打开或 token/Account/Logon/Session 无法读取：exit 21 → `FSL_E_CLIENT_IDENTITY_UNAVAILABLE`。创建 FILETIME 不同：exit 22 → `FSL_E_CLIENT_PROCESS_MISMATCH`。
- 只有 bootstrap 全部成功后才创建 Pipe。Pipe DACL 为 protected、无继承，精确包含可信 UI Logon SID 与 elevated Broker Account SID，均为 ReadWrite + Synchronize；继续设置 `PIPE_REJECT_REMOTE_CLIENTS`。禁止 CLI SID、UI Account SID 代替 UI Logon SID、Administrators、Authenticated Users、Users、Everyone 或桌面用户名。
- 连接前没有协议 Pipe，错误只能通过固定 consent-broker 退出码和 UI 持有的 Broker process handle 传递；UI 不读取 stdout、stderr、控制台文本、临时文件、注册表、Event Log 文本或弹窗标题。

### D-029.3 生产 Broker 路径与 UAC API

- production Broker 唯一路径为 `<FOLDERID_ProgramFiles>\FolderSessionLock\FolderSessionLock.Broker.exe`；Program Files 通过 `SHGetKnownFolderPath(FOLDERID_ProgramFiles, ...)` 取得。禁止 `%ProgramFiles%` 环境展开、当前工作目录、直接信任 `AppContext.BaseDirectory`、PATH、相对路径、仓库/bin 输出、用户配置、CLI Broker path 或 App Paths 搜索。
- UAC 前必须验证 D-023 InstallDirectory、Broker 普通文件、non-reparse、最终路径等于预期且位于已验证安装目录内，并记录文件 identity。失败返回 `FSL_E_BROKER_PATH_UNTRUSTED` / `The elevated broker installation could not be verified.` / false / null。CP9 不声称完成 Authenticode；签名/publisher 仍属 CP10。
- production launcher 固定使用 `ShellExecuteExW`：`lpVerb = runas`、`lpFile` 为已验证绝对 Broker 路径、`lpParameters` 为专用 Windows argument encoder 生成的固定参数、`lpDirectory` 为已验证安装目录、`nShow = SW_HIDE`；flags 精确为 `SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI | SEE_MASK_UNICODE`。
- 禁止仅使用 `Process.Start`、`ProcessStartInfo.Verb`、cmd、PowerShell、runas.exe、CreateProcessAsUser、CreateProcessWithTokenW、CreateProcessWithLogonW、Task Scheduler 或临时服务。参数只来自固定字面量、已验证 UInt32/UInt64、小写 Guid D 与固定 Pipe 名，不包含用户自由文本或 shell 解释。
- UAC 提示不设置应用级超时；调用离开 WPF UI 主线程但保留 owner window handle。应用取消令牌不得强制关闭已显示的系统 UAC UI。
- `ShellExecuteExW` FALSE 且 `ERROR_CANCELLED` → `FSL_E_ELEVATION_CANCELLED` / `The elevation request was cancelled.` / true / null；其他 FALSE 或成功但 `hProcess == NULL` → `FSL_E_ELEVATION_LAUNCH_FAILED` / `The elevated broker could not be started.` / false / null。不得用 `hInstApp > 32` 代替 process handle。

### D-029.4 Pipe 连接竞态与 launcher 清理

- Broker 创建 Pipe 后等待唯一客户端 15 seconds；无连接 exit 24，且不创建 Replay、不执行命令或 ACL。
- UI 在 `ShellExecuteExW` 成功后并发等待 Pipe 可连接或 Broker process handle signaled；连接前总上限 20 seconds，从 ShellExecuteExW 成功返回后开始。
- Broker 连接前退出时，UI 调用 `GetExitCodeProcess`，按固定表转换，关闭 process handle，不继续连接、不自动重启。未知退出码返回 `FSL_E_BROKER_EXITED_EARLY` / `The elevated broker exited before a secure connection was established.` / false / null。
- 20 seconds 到期、Pipe 从未连接且 Broker 仍存活时，UI 允许 `TerminateProcess(hProcess, 29)`，等待最多 5 seconds 后关闭 handle。清理成功返回 `FSL_E_BROKER_CONNECT_TIMEOUT` / `The elevated broker did not establish a secure connection in time.` / true / null。终止、等待或退出证明失败时主错误为 `FSL_E_BROKER_PROCESS_CLEANUP_FAILED` / `The unused elevated broker process could not be cleaned up safely.` / false / null，connect timeout 只进附加诊断。
- Named Pipe 一旦连接，UI 永远不得 `TerminateProcess`，包括用户取消、UI 关闭、协议超时、响应读取失败或 Broker 长期持有 Active task。连接后 UI 只能取消自身读取、关闭 Pipe、关闭自己的 process handle；Broker 按 CP4/CP6/CP7 收敛。

### D-029.5 consent-broker 固定退出码

- 本表只适用于 `--mode consent-broker`，不改变 recovery-once 的 0/2/10/11/12/13/14/15：

| exit | meaning |
|---:|---|
| 0 | ProtocolHandledOrLifecycleCompleted |
| 2 | InvalidArguments |
| 20 | CrossAccountElevationNotSupported |
| 21 | InitiatingClientIdentityUnavailable |
| 22 | InitiatingClientProcessMismatch |
| 23 | PipeInitializationFailed |
| 24 | ClientConnectTimeout |
| 25 | ProtocolFailedBeforeResponse |
| 26 | ResponseWriteFailed |
| 27 | LifecycleCleanupFailed |
| 28 | ProtectedLoggerUnavailableOrInternalFailure |
| 29 | LauncherTerminatedBeforeConnect |

- 不得返回其他值或 Win32/HRESULT/NTSTATUS/Exception.HResult/BrokerError hash/应用错误序号。Broker 自身不得主动返回 29。
- exit 0 表示请求已按协议处理且 Broker 所有生命周期责任已安全结束；应用 `success:false` 响应仍可 exit 0。ValidatePath/GetStatus 响应、普通 UI RemoveLock 拒绝、CreateLock 副作用前失败均在响应成功送达且无顶层故障时 exit 0。
- exit 2 只用于 consent-broker CLI schema 失败，UI 映射 `FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / retryable false / field null。公开对象不得包含 CLI、参数、路径、命令行、Win32 或异常细节。20/21/22 分别映射跨账户、identity unavailable、process mismatch。23 映射 `FSL_E_PIPE_INITIALIZATION_FAILED` / `The elevated broker could not create its secure communication endpoint.` / false / null。24 映射 connect timeout。
- exit 25 表示已连接但未发送最终 ServerHello/CommandResponse、无未解决副作用；UI 未收到协议错误帧时映射 `FSL_E_BROKER_EXITED_EARLY`。exit 26 表示处理结果已形成但响应写出失败；Cleanup 失败时必须使用 27。exit 27 表示 scheduler/administrative/expiration Cleanup 或 RecoveryRequired 未达到安全完成。合法 CommandResponse 已收到后不得由后续 exit 27 改写。exit 28 用于 D-030 `FSL_E_PROTECTED_LOGGER_UNAVAILABLE` 或无法映射的顶层内部异常，不用于普通应用级失败。
- 连接前优先级：InvalidArguments → ProtectedLoggerUnavailable → InitiatingClientIdentityUnavailable → InitiatingClientProcessMismatch → CrossAccountElevationNotSupported → PipeInitializationFailed → ClientConnectTimeout → InternalFailure。protected logger 在严格 CLI 后、身份和 Pipe 前初始化。
- 连接后优先级：LifecycleCleanupFailed → ResponseWriteFailed → ProtocolFailedBeforeResponse → InternalFailure → ProtocolHandledOrLifecycleCompleted。scheduler error 继续只进受保护日志，不覆盖 Cleanup first-task error。

### D-029.6 生产组合与单请求生命周期

- UI 固定顺序：创建单个 requestId → 验证 DTO → 读取 readiness → 取得 UI identity snapshot → 验证生产 Broker 路径 → 构造 CLI → ShellExecuteExW → 验证 process handle → 等待 Pipe/提前退出 → ClientHello/ServerHello/CommandRequest/CommandResponse → UI 错误转换 → 关闭 Pipe/process handle。UI 请求结束不得终止仍拥有 Active task 的 Broker。
- Broker bootstrap 固定顺序：严格 CLI → protected logger → Broker identity → 打开/绑定 UI process → 重取 UI token identity → Account/Session 比较 → 可信 UI Logon SID → Pipe DACL → 唯一 Pipe server → production `BrokerCompositionRoot` → 唯一客户端 → 四帧协议；禁止第二连接。
- production `BrokerCompositionRoot` 必须组合：`WindowsSessionIdentityProvider`、`WindowsFolderPathValidator`、`WindowsFolderPathRelationService`、`DirectoryAclEditor`、`WindowsFolderLockService`、`RecoveryRecordStore`、`RecoveryRecordFileSecurity`、`ProtectedPathSecurityVerifier`、`RecoveryReadinessReader`、`ReplayRegistry`、`BrokerFrameCodec`、`BrokerProtocolCodec`、`BrokerExecutionPolicy`、`BrokerConnectionHandler`、`LockTaskManager`、`LockTaskCoordinator`、`LockTaskScheduler`、`BrokerLifecycleController`、`ILoggerFactory`、`IClock`。禁止 AllowAll/fake identity/fake readiness/in-memory recovery/test cleanup hook/test path/debug Broker path；缺少安全依赖 fail closed，exit 28。
- CreateLock 在路径/ACL 前继续使用 `IRecoveryReadinessReader`；仅 Ready 且不 blocking 时允许，否则返回现有 `FSL_E_RECOVERY_BLOCKING`。ValidatePath 不创建锁。
- 每个 consent-broker 进程只允许一个 Pipe server instance、一个连接、一次 ClientHello/ServerHello、一个 CommandRequest/CommandResponse；响应后关闭 listener，不再次 Accept。
- ValidatePath、GetStatus、普通 UI RemoveLock 拒绝、CreateLock 副作用前失败：响应送达后停止 lifecycle并 exit 0。GetStatus 不启动长期 scheduler。
- CreateLock 成功响应后 Pipe 关闭、UI 可退出、Broker 保持运行；scheduler 持有唯一 Active task，到期执行既有 Expiration Cleanup；安全完成 exit 0，无法安全完成 exit 27。
- UI 在响应前断开：无副作用时停止请求并 exit 25；已形成确定 Active lock 时继续到既定到期，不因 UI 断开提前解除；副作用未知时进入 RecoveryRequired。不得因响应未送达删除 `.fslr` 或撤销已确认 Active task。
- 能发送协议错误时优先发送既有 ServerHello/CommandResponse；无法发送时 UI 使用 process handle + fixed exit code。UI 已验证合法 CommandResponse 后，该响应为最终 UI 结果，后续退出码不得改写。

### D-029.7 新错误、日志与环境边界

- 新增固定错误：`FSL_E_BROKER_PATH_UNTRUSTED`、`FSL_E_ELEVATION_CANCELLED`、`FSL_E_ELEVATION_LAUNCH_FAILED`、`FSL_E_BROKER_LAUNCH_CONTRACT_INVALID`、`FSL_E_PIPE_INITIALIZATION_FAILED`、`FSL_E_BROKER_CONNECT_TIMEOUT`、`FSL_E_BROKER_EXITED_EARLY`、`FSL_E_BROKER_PROCESS_CLEANUP_FAILED`。全部 field=null；仅 elevation cancelled 与 connect timeout retryable=true，其余 false。
- protected 日志可记录 requestId、broker/client PID、consent exit code、source/mapped error code、连接/响应/副作用/RecoveryRequired 标志；禁止 SID 原文、nonce、bindingProof、完整 path payload、DPAPI blob、SDDL、凭据、UAC 输入或向普通 UI 暴露 stack。
- 非 VM 环境允许 launcher interface、ShellExecuteExW wrapper、Win32 mapping、Broker path resolver、UI identity snapshot、bootstrap verifier、PID+creation time、exit mapper、fake UAC/process/Pipe race、production composition static check、tests/build/format/security scan/reviewer。实际同账户 UAC、真实 elevated Broker、Program Files、SCM/LocalSystem 与恢复只在 `FSL-STAGE4-VM` 验证；真实跨账户凭据、`FSL-Standard`/`FSL-Admin` 和测试签名场景由 D-031 取消。
- CP9 必测矩阵覆盖身份分层、UI token/PID/creation time、UAC error/race/cleanup、production path、single connection/request、exit 0/26/27、response precedence、CreateLock long-lived lifecycle 与 production composition 禁止 fake/AllowAll。

## D-030：跨进程 Recovery Readiness、生产路径策略与受保护日志

- 状态：已决定。
- 本决定补充 D-023、D-024、D-027、D-028、D-029；冲突旧条款全部以 D-030 为准。

### D-030.1 跨进程 Recovery Readiness 机制与路径

- 唯一机制为受保护机器范围快照文件；不新增公共 Named Pipe readiness endpoint。唯一 publisher 为 `FolderSessionLockRecovery` 服务。普通 WPF UI、consent-broker、recovery-once 和受信任只读安装/诊断组件可读；普通 UI、consent-broker、recovery-once 和其他用户进程禁止发布。recovery-once 不得覆盖服务 canonical readiness。
- ProgramData 只通过 `SHGetKnownFolderPath(FOLDERID_ProgramData, ...)` 取得。固定目录 `%ProgramData%\FolderSessionLock\Readiness`，canonical `%ProgramData%\FolderSessionLock\Readiness\recovery-readiness.v1.json`，temp `%ProgramData%\FolderSessionLock\Readiness\recovery-readiness.v1.tmp-<lowercase-nonempty-Guid-D>`。禁止调用方路径、AppData、注册表、共享内存、环境变量、cwd、仓库、TEMP、stdout/stderr 或 Event Log 作为权威来源。
- 跨进程 publisher mutex 固定为 `Global\FolderSessionLock.RecoveryReadiness.v1`；DACL 只允许 SYSTEM、Administrators、`NT SERVICE\FolderSessionLockRecovery`。普通用户不得创建、修改打开或抢占；只有 recovery-service 可取得 publisher ownership。

### D-030.2 Readiness 目录和文件安全

- Readiness 目录 owner 只能为 SYSTEM `S-1-5-18`；DACL present、non-null、`SE_DACL_PROTECTED`，无 inherited、Deny、object、callback、conditional 或 unknown ACE。显式 Allow ACE 精确为：SYSTEM FullControl `0x001F01FF` ThisFolderOnly；Administrators FullControl `0x001F01FF` ThisFolderOnly；服务 SID FullControl `0x001F01FF` ThisFolderOnly；BUILTIN\Users ReadAndTraverse `0x001200A9` ThisFolderOnly。Users 不得 CreateFile/CreateDirectory/WriteData/AppendData/Delete/DeleteChild/WriteDac/WriteOwner。
- canonical 和合法 temp owner 只能为 SYSTEM。文件 DACL present、non-null、protected，精确四个显式 Allow、AceFlags 0、无继承：SYSTEM FullControl `0x001F01FF`；Administrators FullControl `0x001F01FF`；服务 SID FullControl `0x001F01FF`；BUILTIN\Users Read `0x00120089`。普通用户只能读取，不能修改、替换、删除或更改安全描述符；reader 不修复 owner/DACL。
- 目录缺失或安全不匹配时 recovery-service 不得 Ready，consent-broker fail closed。

### D-030.3 Readiness 十二字段与状态矩阵

- canonical 为严格 UTF-8 without BOM 单一 JSON object，长度 1..16384 bytes；拒绝 BOM、注释、尾逗号、重复/多余/缺失字段、多个 JSON value、宽松数字、大写 Guid、非七位小数 UTC。
- 精确十二字段及类型：`schemaVersion` Int32 固定 1；`serviceName` string 固定 `FolderSessionLockRecovery`；`serviceInstanceId` 小写非空 Guid D，每服务进程新建且实例内不变；`sequence` Int64 1..9223372036854775807，新实例从 1 开始，每次成功 publish 加 1，不回退或重复；`state` 仅 `Starting|Ready|RecoveryBlocked|Stopping`；`recoveryBlocking` boolean；`scanStartedUtc` 七位小数 UTC Z；`scanCompletedUtc` 同格式或 null；`publishedUtc` 同格式；`validUntilUtc` 必须精确等于 `publishedUtc + 30 seconds`；`remainingRecordCount` Int32 -1..1024；`primaryErrorCode` null 或 `^FSL_E_[A-Z0-9_]+$` 且最多 128 字符，不含路径、SID、HRESULT 或 Win32 message。
- `Starting`：blocking=true、scanCompleted=null、remaining=-1、error=null。`Ready`：blocking=false、scanCompleted非 null、remaining=0、error=null。`RecoveryBlocked`：blocking=true、scanCompleted非 null、remaining=0..1024、error非 null。`Stopping`：blocking=true、scanCompleted可 null、remaining=-1 或 0..1024、error可 null或保留此前阻塞错误。矩阵不一致为 `FSL_E_RECOVERY_READINESS_SCHEMA_INVALID`。

### D-030.4 Heartbeat、原子发布、读取与停止

- 服务安全前置通过后立即发布 Starting；扫描完成立即发布 Ready 或 RecoveryBlocked；保持 Running 时每 10 seconds heartbeat；Stop 到达立即发布 Stopping。heartbeat 不重扫恢复目录、不改变业务状态，只递增 sequence、publishedUtc、validUntilUtc。
- reader 时效必须满足 `publishedUtc <= nowUtc + 5 seconds`、`nowUtc <= validUntilUtc`、`validUntilUtc == publishedUtc + 30 seconds`；否则 `FSL_E_RECOVERY_READINESS_STALE`。Readiness 内部错误对 CreateLock 仍统一映射 `FSL_E_RECOVERY_BLOCKING`，内部码只进受保护日志。
- publish 固定顺序：mutex → retained Readiness directory handle及安全复核 → 同目录 `CREATE_NEW` temp → retained temp handle → SYSTEM owner及精确 DACL → 同句柄回读安全 → 写完整 JSON → `FlushFileBuffers` → 同句柄回读严格解析并验证矩阵/时间/sequence → tempHandle 调用 user-mode `NtSetInformationFile(FileRenameInformationEx = 65, relative canonical leaf, flags 0x00000003)` → retained new canonical handle验证 identity/owner/DACL/content → retained directory handle验证叶名映射 → 释放 mutex。禁止 File.Move/File.Replace/MoveFileEx/路径 rename/关闭句柄后按名称替换。
- reader 固定使用 retained directory handle 打开 canonical，`FILE_FLAG_OPEN_REPARSE_POINT`，验证普通文件、non-reparse、links=1、SYSTEM owner、精确 DACL、长度，再从同一 handle读取并验证十二字段、矩阵、时效，最后复核 identity/security。缺失、打开失败、identity/security变化、malformed、stale、unsupported schema、非 Ready 或 blocking=true全部 fail closed。
- SCM Stop：内部 Stopping → 发布 blocking Stopping → 禁止 CreateLock和新恢复记录 → 等待已进入 ACL 临界区记录安全收敛 → 最后 Stopping heartbeat → canonical 已验证 handle执行 FileDispositionInfoEx delete → close → retained directory确认叶名消失 → SERVICE_STOPPED。删除失败允许服务停止，但只记 protected logger、不路径重试；残留最多 30 seconds 后 stale，reader继续 fail closed。
- 服务启动只清理合法 `recovery-readiness.v1.tmp-<Guid>`；先验证 filename、SYSTEM owner、精确 DACL、non-reparse、links=1、固定目录，再用同一 handle删除。非法/未知/安全不匹配构件不删除，进入 RecoveryBlocked，错误 `FSL_E_RECOVERY_READINESS_ARTIFACT_INVALID`。服务崩溃后的 canonical 自然过期。
- 稳定内部错误：`FSL_E_RECOVERY_READINESS_NOT_FOUND`、`FSL_E_RECOVERY_READINESS_OPEN_FAILED`、`FSL_E_RECOVERY_READINESS_SECURITY_INVALID`、`FSL_E_RECOVERY_READINESS_IDENTITY_CHANGED`、`FSL_E_RECOVERY_READINESS_SCHEMA_INVALID`、`FSL_E_RECOVERY_READINESS_VERSION_UNSUPPORTED`、`FSL_E_RECOVERY_READINESS_STALE`、`FSL_E_RECOVERY_READINESS_PUBLISH_FAILED`、`FSL_E_RECOVERY_READINESS_DELETE_FAILED`、`FSL_E_RECOVERY_READINESS_ARTIFACT_INVALID`。

### D-030.5 生产时长、scheduler 与路径分类

- production `LockDurationPolicy` 固定包含式范围：Minimum 60 seconds / 60000 ms，Maximum 24 hours / 86400000 ms。`60000 <= durationMilliseconds <= 86400000`；0、负数、59999、86400001、浮点、指数、客户端 expiresUtc、隐藏默认、UI 扩大或 debug override 全部禁止。越界返回 `FSL_E_DURATION_OUT_OF_RANGE`、field `payload.durationMilliseconds`、retryable false；Broker 独立复核。
- 每 consent-broker 进程一个有效 IPC 请求、至多一个成功 CreateLock、至多一个 Active task、一个 `LockTaskScheduler`、一个串行 scheduler loop。禁止 Windows Task Scheduler、Windows service 定时任务、多个 Timer、每 task线程、fire-and-forget、UI scheduler 或多进程竞争同一 task。
- 到期只使用 `IClock` monotonic timestamp；UTC 只用于 UI、日志和恢复记录。每轮读取 task snapshot；无 Active 即结束；计算 remaining；remaining<=0 时原子 `Active -> Unlocking`，否则等待 `min(remaining, 30 seconds)`；醒来重新读取状态和 monotonic timestamp，不按上次 delay推定到期。UI 断开不取消 Active。lifecycle stop只取消未开始 delay后进入 administrative Cleanup；ACL 临界区取消无效。scheduler 非取消异常不伪造 Completed、不覆盖 Cleanup error并触发 lifecycle Cleanup。
- repository 不使用配置 root。对已验证 target handle逐级 handle-relative遍历到卷根，每个祖先检查关闭集合 `.git|.hg|.svn`，marker 可为普通文件或目录，使用 OPEN_REPARSE_POINT、不跟随、不读内容。命中返回 `FSL_E_PATH_REPOSITORY_FORBIDDEN`；遍历/打开/identity失败返回 `FSL_E_REPOSITORY_CLASSIFICATION_UNAVAILABLE` 并 fail closed。禁止 GIT_DIR/GIT_WORK_TREE/cwd/git.exe/PATH/仓库配置/CLI roots/用户设置/第三方注册表路径。
- synchronization仅使用两类Windows权威来源。Cloud Files规则保持：S_OK拒绝，只有Win32 not-under-root HRESULT与`0xD000CF13`允许继续，不接受原始NTSTATUS或mask。SkyDrive规则最终固定为：创建`IKnownFolderManager`，`GetFolderIds`必须S_OK并按GUID二进制精确查找`FOLDERID_SkyDrive`；不得用字符串、显示名或canonical name。集合不含SkyDrive时内部原因`KnownFolderNotRegistered`，精确返回`Exists=false, Path=null`；GetFolderIds任何非S_OK fail closed。注册存在后调用前将path置null，并固定`SHGetKnownFolderPath(FOLDERID_SkyDrive, KF_FLAG_DEFAULT = 0x00000000, initiatingUserToken, out path)`；禁止`KF_FLAG_CREATE`、`KF_FLAG_DONT_VERIFY`、`KF_FLAG_DEFAULT_PATH`。完整`unchecked((int)0x80070002)`/`-2147024894`表示当前用户实例或目标叶项不存在；`unchecked((int)0x80070003)`/`-2147024893`表示父路径链不存在；两者返回`Exists=false, Path=null`。只有注册缺失与这两个完整HRESULT允许继续。`0x80070057`、`0x80004005`、`0x80070005`、`0x80070006`、`0x8007052E`、`0x80070520`、`0x80070522`和其他HRESULT全部fail closed；禁止低16位、`HRESULT_CODE`、facility/severity mask、raw Win32 2/3、NTSTATUS、wrapper重编号或E_INVALIDARG未注册解释。S_OK必须path非null非空且绝对；复制受控string后释放pointer，再执行持续handle、reparse、final path、DirectoryIdentity与Same/Descendant检查。失败时native意外返回非null pointer也必须`CoTaskMemFree`。禁止OneDrive环境变量、`USERPROFILE\OneDrive`、Accounts注册表、第三方配置、进程名、窗口标题、PATH或调用方roots。
- ValidatePath/CreateLock 顺序固定：绝对路径与 NTFS → reparse/final path → DirectoryIdentity → 系统/用户/安装保护路径 → repository classifier → Cloud Files classifier → SkyDrive classifier → ACL capability → CreateLock前最终路径映射。任一 classifier indeterminate即 fail closed。

### D-030.6 Protected JSON Lines logger

- 唯一 production provider 为 `ProtectedJsonLinesLoggerProvider`，使用直接安全 file handle、严格 JSON Lines、单进程单文件，可承载 `Microsoft.Extensions.Logging`。禁止 Console/Debug/Trace/stdout/stderr/EventLog主日志、Serilog/NLog外部配置、用户可写目录、网络/HTTP sink或跨进程共享 append。
- ProgramData只由 Known Folder API取得。固定根 `%ProgramData%\FolderSessionLock\Logs\v1`，模式目录 `consent-broker`、`recovery-service`、`recovery-once`。三个模式共享 provider/schema/event catalog/redactor/security verifier/rotation/retention/error规则，但写不同目录和不同进程文件，绝不跨进程 append。
- Logs root与三个模式目录 owner SYSTEM；protected DACL精确三个显式 Allow：SYSTEM、Administrators、服务 SID，均 FullControl `0x001F01FF`；无 inherited、Users、Authenticated Users、Everyone、Deny/object/callback/unknown ACE。普通用户不可列出、读取、创建、修改、删除或改安全。目录仅安装程序或获准 service setup创建；consent-broker不创建或修复 root。
- 每日志文件 owner SYSTEM，DACL与目录相同。consent-broker保留同一 handle，按既有 SeRestorePrivilege规则设置 SYSTEM owner与精确 DACL并同句柄复核，失败时不写事件并停止启动；LocalSystem service也必须显式验证。
- 文件名固定 `yyyyMMddTHHmmssfffffffZ-<UInt32 pid>-<lowercase Guid D instanceId>-<0000..9999>.jsonl`；每进程独立，rotation保持 instanceId。UTF-8 without BOM，单 LF，一行一个 JSON object，每行包括 LF最多4096 UTF-8 bytes。
- 每行精确十四字段：`schemaVersion` Int32=1；`timestampUtc` 七位UTC Z；`sequence` Int64从1单文件递增；`level` 仅 Information/Warning/Error/Critical；`eventId` Int32 1..999999且来自编译期目录；`eventName` 编译期 PascalCase、最多64 ASCII；`mode` 仅 ConsentBroker/RecoveryService/RecoveryOnce；`component` 关闭集合至少 BrokerBootstrap/Elevation/Transport/Protocol/Replay/Recovery/Scheduler/Lifecycle/Readiness/Security/Logger；`processId` UInt32；`instanceId` 小写Guid D；`requestId`/`taskId` 小写Guid D或null；`errorCode` 为 FSL_E_*、lock_task.*或null、最多128；`message` 编译期固定、最多512 Unicode scalar values，不含自由 Exception.Message。禁止 arbitrary properties dictionary和生产 Trace/Debug。
- 禁止 Account/Logon SID、SDDL、ACE/ACL、DPAPI、nonce、bindingProof、密码/凭据/UAC输入/token/private key/certificate file、完整用户或 target path、Environment.CommandLine、stack、Exception.ToString、Win32 FormatMessage。允许 requestId、taskId、稳定码、模式、event ID、状态、计数、布尔和脱敏文件类别。路径关联仅允许 `SHA-256("FSL-PATH-LOG-V1\n" + normalizedPath)` 的64位小写hex。
- 每事件：固定 schema → redaction validation → UTF-8长度 → 单次完整 WriteFile → LF → FlushFileBuffers → sequence+1。所有级别每条事件都 flush；不得跳过 Error/Critical/side-effect/lifecycle/readiness边界。
- 单文件8 MiB；下一记录将超限或 UTC日期变化时rotation。每进程最多10000文件，达到后provider永久失败。保留14 days；每模式最多32个已关闭文件；Logs\v1总量256 MiB。cleanup只考虑非活跃关闭文件：先删过期，再按 LastWriteUtc升序、filename Ordinal升序删至每模式32，再同序删至总量256 MiB。不得删除 active、安全异常、reparse、links!=1、未知名或未知目录；此类构件 `FSL_E_PROTECTED_LOG_ARTIFACT_INVALID`，停止自动删除该构件。
- recovery-service启动清理一次，之后每24 hours清理日志；不构成恢复记录周期扫描。consent-broker只rotation/close自身文件；recovery-once只close自身文件。

### D-030.7 Logger 失败语义与环境边界

- 新稳定错误 `FSL_E_PROTECTED_LOGGER_UNAVAILABLE`，message `The protected diagnostic logger could not be initialized.`，retryable false，field null。原因包括 Known Folder/目录/D-023/owner/DACL/service SID/file create/security set/readback或总量硬限制无安全可删文件失败。
- consent-broker在严格 CLI 后、Pipe/Replay/request/副作用前初始化 logger；失败不建 Pipe、不发送伪协议响应，exit 28，UI映射上述错误。exit 28不用于应用/路径/UAC取消/Pipe timeout/Cleanup/ResponseWrite失败。
- provider写入/flush失败后永久 Failed。无副作用：停止请求，能响应则返回上述错误，最终 exit 28。已有确定副作用：继续既有 task lifecycle与Cleanup，最小 fixed diagnostic state留内存；Cleanup成功后 exit28，Cleanup失败或RecoveryRequired时exit27优先。合法 CommandResponse已送达后不得被后续exit28改写。
- recovery-service启动前logger失败不得 SERVICE_RUNNING，直接 SERVICE_STOPPED并记录结构化错误。运行中永久失败时，如readiness仍可安全工作则立即发布 RecoveryBlocked，禁止CreateLock，完成ACL临界区后受控停止；readiness发布也失败时依靠stale fail closed。recovery-once使用既有exit15，不新增退出码。
- `AGREELIN`允许 readiness接口/fake/TEMP原子快照、security failure injection、duration/scheduler单测、TEMP repository marker、Cloud Files/Known Folder fake、TEMP protected logger、rotation/retention/failure injection、生产组合静态扫描、build/test/format/reviewer。真实 ProgramData readiness/Logs ACL、service SID ACL、LocalSystem publisher、SCM Stop、真实跨进程普通用户读取、真实 OneDrive/Cloud Files矩阵、service/broker并发日志和重启后stale只允许 `FSL-STAGE4-VM`。

## D-031：Local Single-User Administrator Deployment Scope

- 状态：已决定。`LOCAL_SINGLE_USER_ADMINISTRATOR_ONLY` 是当前支持的唯一部署范围；本决定在账户、Stage 4 evidence、签名和发布范围上取代 D-025、D-026 及其他旧文档中的冲突条款。
- Current supported deployment is local single-user administrator only.
- `FSL-Standard` must not be created.
- `FSL-Admin` must not be created.
- No dedicated Windows test account may be created.
- Cross-account elevation is outside the supported deployment scope.
- Same-account UAC consent may be used.
- SCM and LocalSystem operations may be initiated by the current administrator account.
- Real dual-account VM evidence is not required.
- Existing fail-closed cross-account rejection may remain and must continue to be unit-tested without creating a real account.
- Lack of a real signing certificate does not block local unsigned release.
- Unsigned status must be reported accurately as `Authenticode = NotSigned` with a null signer.
- D-026 uses `TRUSTED_SINGLE_USER_STAGE4_EXECUTOR_MODEL`: the current local administrator, approved Codex automation, user-confirmed same-account UAC, and user-approved elevated PowerShell are trusted executors for this run.
- The evidence integrity contract detects corruption, partial writes, WAL/journal/order/schema/hash/binding mismatches, false completion and incomplete cleanup. It does not claim protection against the same trusted user replacing keys or evidence, replaying a complete old evidence set, administrator/LocalSystem compromise, VM rollback, or the absence of an external witness, public seal, TPM anti-rollback or non-repudiation.
- Multi-user, enterprise, hostile-same-user and public-distribution support require a future decision.
- `CANCELLED / NOT REQUIRED`: Create `FSL-Standard`; create `FSL-Admin`; validate standard-user to separate-admin credential elevation; collect real dual-account evidence; block Stage 5 solely on missing dual-account evidence.
