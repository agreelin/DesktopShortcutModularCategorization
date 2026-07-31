# Folder Session Lock 开发日志

## 2026-07-18 — 阶段 0：可行性分析与架构决策

### DISCOVER

- 已读取用户 `/goal`、阶段 0 附件和技能说明。
- `.codegraph/` 不存在。
- 阶段开始时仓库仅有 `README.md`，标题为 `DesktopShortcutModularCategorization`。
- 阶段开始时无 solution、产品代码、测试和阶段 0 状态文档。
- Git 分支为 `main`，开始时工作树干净。

### PLAN

- 已调用只读 planner。
- 已定义 ACL 能力边界、Logon SID 会话方案、单 ACE 增删、恢复记录、Broker/IPC、路径安全、嵌套策略、审计限制、v1 非目标和阶段 1–7 验收。

### 架构摘要

- 锁定主体使用 Logon SID，不使用 Account SID。
- UI 普通权限；独立 Broker 按需提升并拥有计时、ACL 和恢复。
- 严格零持久化不能满足可靠崩溃恢复；方案为最小受保护恢复记录，成功解锁后删除。
- 只添加一条显式 Deny ACE；不使用 `FullControl`；不整体替换 DACL；不关闭继承；不修改父目录。
- 预存在完全相同 ACE 时拒绝；解锁匹配数大于一时禁止猜测删除。
- v1 只支持本机固定 NTFS 普通目录；拒绝网络、可移动、非 NTFS、系统路径、同步目录和所有 reparse path。
- 重复目录及父子目录重叠任务全部拒绝。
- 访问审计默认关闭；阶段 6 需要用户独立批准；优先研究 `4656` Failure，`4663` 不作为失败访问事件。

### 本阶段文件

- 根 `AGENTS.md`
- `FolderSessionLock/AGENTS.md`
- `FolderSessionLock/docs/REQUIREMENTS.md`
- `FolderSessionLock/docs/ARCHITECTURE.md`
- `FolderSessionLock/docs/SECURITY.md`
- `FolderSessionLock/docs/DECISIONS.md`
- `FolderSessionLock/PLAN.md`
- `FolderSessionLock/TASKS.md`
- `FolderSessionLock/ACCEPTANCE.md`
- `FolderSessionLock/DEVLOG.md`

### 明确未执行

- 未调用 coder。
- 未写产品代码、WPF 界面、solution 或测试代码。
- 未修改任何真实目录 ACL/SACL。
- 未启动或提升 Broker。
- 未修改 Windows Audit File System 或其他审计策略。
- 未进入阶段 1。

### REVIEW 与 VERIFY

- reviewer 第一轮结论为 `FAIL`：无 `BLOCKER`；4 个 `HIGH`。
- 已按规则仅修复 `HIGH`：
  - 记录 Logon SID 重启回收与按需 Broker 无启动顺序保证，新增启动前恢复决定门。
  - 记录外部删除并重建同元组 ACE 时来源不可证明，新增 DACL 稳定性信任假设决定门。
  - 强制全部 DACL 读写和后置验证绑定同一持续目录句柄。
  - v1 明确只支持同一账户 consent elevation；另一管理员账户凭据提升需重构。
- 第一轮与修复后验证均通过：九份文件存在且非空；关键主题覆盖；无禁止术语；无尾随空白；git 状态仅含阶段 0 文档。
- reviewer 复审结论：`PASS`；无 `BLOCKER`、无 `HIGH`。
- 阶段 0 无 solution，未运行 `dotnet restore/build/test/format`，未记录虚假通过。

### 阶段状态

- 阶段 0 文档工作完成。
- 当时阶段 1 前置条件未满足：用户尚未确认 `D-002` 至 `D-019`。
- 已停止；未进入阶段 1。

## 2026-07-18 — 阶段 0：十项设计决策确认更新

### DISCOVER 与 PLAN

- 已重新读取根 `AGENTS.md`、八份目标文档和 git 状态；`.codegraph/` 不存在。
- 已调用只读 planner，将用户十项确认转换为架构约束、安全边界、需求、测试、验收和发布阻断。

### 已确认并落档

- 产品 solution、源码、测试和项目文档目标根为 `FolderSessionLock/`；不修改无关项目或根 `README.md`。
- 接受最小恢复记录；限定允许数据、禁止数据、唯一用途和恢复后删除规则。
- UI 关闭后任务继续；Broker 是计时、真实 ACL 写入和恢复的唯一所有者。
- 接受用户态自我约束模型与最小 Deny 权限矩阵；明确不防御管理员、SYSTEM、TrustedInstaller、内核、WinRE、离线访问、备份/恢复特权和旧句柄。
- v1 仅支持本机固定 NTFS 普通目录；拒绝网络、其他文件系统、reparse path、系统/用户根、仓库、安装目录及重复/父子重叠任务。
- 开发允许未签名本机构建；生产 Broker 必须签名并位于管理员保护目录，IPC 和客户端身份验证为发布要求。
- v1 仅支持同账户 consent elevation；身份不一致显示“不支持跨账户提升”。
- Broker 恢复专用模式由自动启动 Windows 服务以 LocalSystem 身份在交互登录前托管；只清理旧会话 ACE，不恢复旧任务或剩余时间。
- 接受有限 DACL 稳定性信任假设；记录与 ACL 不一致时停止自动删除，禁止整体覆盖 DACL。
- 访问审计默认关闭；阶段 6 前必须通过独立批准门。

### 此前路径问题（已解决）

- 用户要求所有项目文档位于 `FolderSessionLock/`，同时本轮要求更新仓库根八份状态文档。
- 当时新增 `D-020` 记录该问题；最终位置已在后续迁移记录中确认。

### 明确未执行

- 未调用 coder。
- 未创建 `FolderSessionLock/`、solution、源码、测试或 WPF UI。
- 未修改任何 ACL/SACL，未启动 Broker 或恢复组件，未修改审计策略。
- 未进入阶段 1。

### 待完成

- 首轮 reviewer 结论：`FAIL`；无 `BLOCKER`，3 个 `HIGH`。
- 已修复：
  - Broker 成为唯一真实 ACL 写入主体；启动恢复使用同一 Broker 的恢复专用模式。
  - 恢复模式由自动启动 Windows 服务以 LocalSystem 身份在交互登录前托管；恢复记录改为机器范围保护，只允许 LocalSystem 和提升 Broker 访问。
  - 恢复记录只支持信任假设下的精确元组匹配与恢复判断，不宣称证明 ACE 来源。
- 当时修复后文档一致性验证通过：八份文件存在且非空；`D-001` 至 `D-019` 均为 `已决定`；D-020 随后完成确认；无禁止术语或尾随空白。
- reviewer 复审结论：`PASS`；无 `BLOCKER`、无 `HIGH`，1 个 `MEDIUM`。
- `MEDIUM`：恢复记录存储路径、格式、字段名称和加密 API 的后续设计表述与“唯一待确认项”存在文字边界；不影响本轮 PASS，后续阶段必须在既定安全合同内确定，不得扩大阶段 1 范围。
- `D-020` 已在后续“文档位置统一”记录中确认。

### 最终状态

- 十项确认决定已落入架构、安全、需求、计划、测试、验收和发布阻断。
- 阶段 0 决策确认更新完成。
- 未编写产品代码，未创建 `FolderSessionLock/`，未执行 ACL/SACL、提权、服务注册或审计策略修改。
- 未进入阶段 1。

## 2026-07-18 — 阶段 0：文档位置统一与 D-020 确认

### 迁移结果

- 八份阶段 0 项目文档已从仓库根迁入 `FolderSessionLock/` 最终路径。
- 根目录旧路径全部删除；未建立副本或符号链接。
- 文件在迁移前均未被 git 跟踪，因此无法生成 git rename 元数据；使用补丁系统的文件移动语义保留内容并确保唯一目标路径。
- 新建 `FolderSessionLock/AGENTS.md` 承载项目级技术、安全、Loop、构建测试和阶段规则。
- 根 `AGENTS.md` 仅保留仓库级规则、项目入口、唯一权威路径和全局安全边界。
- 根 `README.md` 只增加项目与文档导航，不复制项目文档内容。

### D-020 最终决定

- `D-020` 状态改为 `已决定`。
- 八份阶段 0 文档只以 `FolderSessionLock/` 下路径为权威来源。
- 禁止根目录同名副本、独立维护副本、符号链接入口和双路径更新。
- D-016 与 D-020 的路径冲突已解除。

### 明确未执行

- 未编写产品代码、未创建 solution 骨架、未执行 ACL/SACL、未启动 Broker 或服务、未修改审计策略。
- 未进入阶段 1。

### VERIFY 与 REVIEW

- 唯一权威源、链接和双启动位置验证通过：旧路径 0、目标路径 8、每个权威文件全仓库仅 1 份、重解析入口 0、失效 Markdown 链接 0。
- reviewer 首轮结论：`FAIL`；无 `BLOCKER`，1 个 `HIGH`。
- 已修复 D-016：根 README 不得替换或无关修改，只允许维护简短项目导航，与 D-020 一致。
- reviewer 复审结论：`PASS`；无 `BLOCKER`、`HIGH`、`MEDIUM` 或 `LOW`。

### 最终状态

- D-016 与 D-020 不再冲突。
- 阶段 1 文档与设计前置条件全部满足。
- 阶段 1 未开始；等待用户明确指定。

## 2026-07-18 — 阶段 1：解决方案骨架与测试基础

### DISCOVER 与 PLAN

- 用户已明确启动阶段 1。
- 已读取根与项目 `AGENTS.md`、八份权威文档、目录状态和 git diff；`.codegraph/` 不存在。
- 阶段开始时 `FolderSessionLock/` 只有项目文档，无 solution、源码或测试。
- 已按 planner 的五个 checkpoint 串行实施；未并行执行 coder 与 reviewer。

### 实现

- 创建 `FolderSessionLock.sln`。
- 创建四个产品项目：`FolderSessionLock.App`、`FolderSessionLock.Core`、`FolderSessionLock.Windows`、`FolderSessionLock.Broker`。
- 创建三个 xUnit 测试项目：`FolderSessionLock.Core.Tests`、`FolderSessionLock.App.Tests`、`FolderSessionLock.Windows.Tests`。
- 固化依赖方向：App 只引用 Core；Windows 引用 Core；Broker 引用 Core 与 Windows；测试引用对应产品项目。
- Core 创建六个接口，以及 `Result`、`Result<T>`、`Error`、`ErrorCategory`；错误分类为 `ValidationFailed`、`InsufficientPermissions`、`UnsupportedPath`、`PlatformError`、`RecoverableError`、`UnrecoverableError`。
- Windows 层只提供安全失败、禁用监控和系统时钟占位实现；不访问传入路径，不执行 ACL 操作。
- Broker 只输出阶段 1 骨架状态；无参数分派、任意命令、Named Pipe、提升或 ACL。
- WPF App 创建最小窗口、`MainViewModel`、Microsoft.Extensions 依赖注入与 Debug logging；App 未引用 Windows 或 Broker。
- 测试基础使用 .NET 8 模板固定包：`Microsoft.NET.Test.Sdk 17.8.0`、`xunit 2.5.3`、`xunit.runner.visualstudio 2.5.3`、`coverlet.collector 6.0.0`。
- App 使用已由 NuGet restore 验证的 `Microsoft.Extensions.DependencyInjection 8.0.1` 与 `Microsoft.Extensions.Logging.Debug 8.0.1`。
- 临时目录工具无外部路径参数，只创建 `%TEMP%\FolderSessionLock.Tests\<Guid>`，实现同步与异步释放，清理失败抛出带路径的 `IOException`。
- 新增项目 `README.md`；修正文档中六项目旧表述为四产品项目加三测试项目。

### VERIFY

- `dotnet restore`：成功；全部七项目已恢复。
- `dotnet build -c Release`：成功；0 warning，0 error。
- `dotnet test -c Release --no-restore`：成功；Core 13、App 3、Windows 6，共 22/22 通过，0 skipped。
- `dotnet format --verify-no-changes` 首次失败：新文件 LF 与 `.editorconfig` 的 CRLF 规则不一致，模板 `AssemblyInfo.cs` 也需空白格式化。
- 按规则运行一次 `dotnet format`；随后重新运行 Release build、完整 tests 和 `dotnet format --verify-no-changes`，全部成功。
- Release WPF 可执行文件受控启动，创建标题为 `Folder Session Lock` 的主窗口，并通过关闭请求正常退出；退出码为 0，无残留进程。
- 禁止 API 静态搜索无命中：无 `DirectorySecurity`、`SetAccessControl`、`FileSecurity`、`NamedPipe`、`runas`、`auditpol`、`System.Security.AccessControl` 或 `System.ServiceProcess`。
- 测试完成后 `%TEMP%\FolderSessionLock.Tests` 子目录数量为 0。

### 明确未执行

- 未执行真实 ACL/SACL 操作。
- 未修改 Windows 审计策略或读取 Security 日志。
- 未启动提升 Broker、UAC 或 Windows 服务。
- 未实现 Named Pipe、IPC 或任意命令执行入口。
- 未访问真实用户目录、系统目录或仓库目录作为测试目标。
- 未进入阶段 2。

### 当前状态

- reviewer 只读审查最终结论为 `PASS`。
- reviewer 无 `BLOCKER`、`HIGH` 或 `MEDIUM`。
- reviewer 记录 1 项 `LOW`：三个测试项目使用的 `xunit 2.5.3` 依赖图包含 NuGet 标记为 High 的旧传递依赖；该依赖未进入产品项目输出，后续应升级经验证的测试包组合并复跑完整验证。
- reviewer 记录非阻断测试缺口：临时目录清理失败诊断分支，以及三个 Windows 安全占位分支缺少直接自动测试。
- 阶段 1 完成门全部满足。
- 未进入阶段 2；等待用户明确启动后续阶段。

## 2026-07-18 — 阶段 2：领域模型、任务状态与倒计时调度

### DISCOVER 与 PLAN

- 用户已明确启动阶段 2。
- 已读取阶段 2 附件、两级 `AGENTS.md`、八份权威文档、现有 solution、源码、测试和 git 状态；`.codegraph/` 不存在。
- 已按 planner 的六个 checkpoint 串行实施；coder 实施结束时尚未执行 reviewer，reviewer 随后由主 agent 调用并完成首轮审查与复审。
- 阶段 1 reviewer 的测试依赖 `LOW` 和临时目录清理失败缺口不属阶段 2，保持原记录，未扩展范围处理。

### 实现

- 新增 `FolderLockTaskId`、`FolderPath`、`LockDurationPolicy`、`LockDuration`。时长上下限必须由调用方显式提供，无隐藏生产默认值；Core 路径值对象不访问文件系统。
- 新增八状态集中状态机。合法转换、非法转换、同状态 `NoChange`、`Completed`/`RecoveryRequired` 终态均有自动测试。
- 新增不可变 `FolderLockTask`、`LockTaskError`、`IFolderPathRelationService` 和单同步门 `LockTaskManager`。冲突检查、添加和状态替换在同一临界区完成；查询返回快照。
- 新增 `LockTaskCoordinator`。激活成功进入 `Active`；确定失败进入 `ActivationFailed`；返回不同任务 ID 或平台结果不确定异常进入 `RecoveryRequired`；重复和并发激活最多调用一次 Apply。
- `LockRemovalIntent` 精确为 `Expiration`、`Recovery`、`TestCleanup`、`AdministrativeCleanup`。`IFolderLockService.RemoveLockAsync` 强制显式意图，无无意图重载。
- `IClock` 新增可取消 delay；`SystemClock` 使用 `TimeProvider.System`；测试时钟可独立推进墙钟和单调 timestamp，无真实等待。
- `FolderLockTask` 保存 `StartedAtUtc`、`StartedTimestamp`、`ExpectedExpiryUtc`。UTC 只用于显示，单调 elapsed 决定到期；剩余时间下限为零。
- 到期扫描先原子取得 `Active -> Unlocking` 所有权。并发或重复扫描只发出一次 `Expiration` Remove；成功进入 `Completed`，确定失败进入 `UnlockFailed`，结果不确定异常进入 `RecoveryRequired`。
- `ILockTaskScheduler` 替换为 `ProcessDueTasksAsync` 和 `RunAsync`。循环使用可取消 delay；单任务失败不阻止同一扫描的其他任务；取消不解除或完成活动任务。
- App、MainWindow、MainViewModel 未注册或拥有 scheduler/lock service，未新增用户提前解除入口。Windows 占位对创建和四种解除意图均返回 `windows.acl.not_implemented`。
- 新增 `D-021`，同步更新需求、架构、安全、计划、验收和 README。

### VERIFY

- 每个 checkpoint 后运行对应 Core、App 或 Windows 测试；修复循环后最终 Core 89/89、App 7/7、Windows 11/11 通过。
- `dotnet restore`：成功；全部七项目恢复完成。
- `dotnet build -c Release`：成功；0 warning，0 error。
- `dotnet test -c Release --no-restore`：修复循环后成功；107/107 通过，0 skipped。
- `dotnet format --verify-no-changes` 首次失败：新增和修改文件使用 LF，与 `.editorconfig` 的 CRLF 规则不一致。
- 按规则运行一次 `dotnet format`；随后重新运行 Release build、完整 tests 和 `dotnet format --verify-no-changes`，全部成功。
- 产品代码禁止能力扫描未发现 ACL/DACL/SACL、Logon SID、Named Pipe、UAC、审计、注册表、AppData 或 Windows 服务实现；测试与 Core 未发现 `Thread.Sleep` 或真实等待。

### 明确未执行

- 未执行真实 ACL/DACL/SACL 操作，未访问真实目录作为锁定目标。
- 未实现 Logon SID、提升 Broker、IPC、Named Pipe、持久化、注册表、AppData、启动恢复服务、审计策略、Security 日志、Toast 或完整 WPF UI。
- 未要求管理员权限。
- 未进入阶段 3。

### 当前状态

- reviewer 首轮结论为 `FAIL`：无 `BLOCKER`，1 个 `HIGH`。
- 首轮 `HIGH`：显式时长策略允许 `TimeSpan.MaxValue`；平台 Apply 成功后计算 `ExpectedExpiryUtc` 可溢出，使任务停在 `Activating` 且异常外泄。
- coder 仅修复该 `HIGH`：日期溢出转换为 `lock_task.expiry.out_of_range` 失败；平台 Apply 成功后的 Active 状态记录失败或异常统一进入 `RecoveryRequired` 并保存 `LockTaskError`。
- 新增回归测试证明：Apply 恰好一次、任务进入 `RecoveryRequired`、错误可观察、scheduler 不处理该任务、Remove 次数为 0。
- 修复后主 agent 复验：Release build 0 warning/0 error；107/107 tests 通过；format 通过。
- 上一轮 reviewer 复审结论为 `PASS`；无 `BLOCKER`、`HIGH` 或 `MEDIUM`。
- reviewer 保留 2 个 `LOW`：阶段诊断文字仍包含“stage 1”；测试项目旧传递依赖风险仍存在。
- reviewer 保留 5 个非阻断测试缺口：Active 状态记录实际抛异常、多任务 Remove 抛异常隔离、Remove 期间取消、scheduler 非取消异常、临时目录清理失败诊断。
- 上一轮阶段 2 完成门检查已记录。
- 未进入阶段 3。

### 阶段门复核与文档 `HIGH` 修复

- 本轮 stage_director 返回 `BLOCKED`：`PLAN.md` 阶段 2 标题与用户原始启动附件第 1 行“执行阶段 2：领域模型、任务状态与倒计时调度。”不一致；`docs/REQUIREMENTS.md` 仍写 reviewer 阶段门待执行，与 `TASKS.md`、`DEVLOG.md` 已记录的上一轮最终 `PASS` 冲突。
- 根主线程实际复验：`dotnet restore` 成功；`dotnet build -c Release` 成功，0 warning、0 error；`dotnet test -c Release --no-restore` 成功，107/107 通过、0 failed、0 skipped；`dotnet format --verify-no-changes` 成功；`%TEMP%\FolderSessionLock.Tests` 临时测试目录残留 0。
- 本轮 reviewer 结论为 `FAIL`：无 `BLOCKER`、无运行时 `HIGH`；唯一 `HIGH` 为上述权威文档冲突。
- 本轮仅修复该 `HIGH`：`PLAN.md` 阶段 2 标题已统一为“阶段 2：领域模型、任务状态与倒计时调度”；`docs/REQUIREMENTS.md`、`TASKS.md`、`DEVLOG.md` 已同步当前 gate、验证、reviewer 和修复状态。
- 最终 reviewer 复审结论为 `PASS`；无 `BLOCKER`、无 `HIGH`，上轮两项 `LOW` 和五项测试缺口保持非阻断。
- 阶段 2 完成门重新满足。

## 2026-07-18 — 阶段 3：Logon SID 与 ACL 锁定引擎

### STAGE GATE 与自动转换

- stage_director 重新核验输出 `READY`；阶段 2 标题、reviewer 时间线、完整验证和临时目录清理证据一致。
- 下一阶段精确为“阶段 3：Logon SID 与 ACL 锁定引擎”。
- 根主线程已在同一个长期 goal 中读取并接受完整 `NEXT_STAGE_GOAL_PAYLOAD`；自动阶段转换计数为 1/8。
- 阶段 3 已开始；阶段 4 未开始。
- 本阶段严格限制真实 ACL 集成测试目标为自动创建的 `%TEMP%\FolderSessionLock.Tests\<Guid>\` 固定 NTFS 临时目录。
- 阶段 3 不实现提升、IPC、持久化恢复记录、Windows 服务、签名、安装、SACL、审计策略或 Security 日志。

### PLAN

- planner 只读规划结论为 `PROCEED`。
- `GetSecurityInfo` 与 `SetSecurityInfo` 均可接收同一持续目录句柄；阶段 3 不允许按字符串路径重新打开目标执行 ACL 事务。
- 六个 checkpoint 依次为：Logon SID；路径与目录身份；同句柄 DACL 差异；Lock/Unlock 与 rollback；权限矩阵真实测试；ACL 不变量、TOCTOU 与清理。
- 产品组合保持 `FolderSessionLock.Broker -> FolderSessionLock.Windows`；App 不引用 Windows。Windows.Tests 直接调用 Windows 实现只用于批准临时目录的平台集成验证。
- planner 未发现需要用户选择的新增架构或安全冲突。

### CHECKPOINT 1：Logon SID

- 新增 `WindowsSessionIdentityProvider`，通过 `OpenProcessToken` 和 `GetTokenInformation` 分别读取 `TokenUser`、`TokenGroups`、`TokenSessionId`。
- Logon SID 只接受带完整 `SE_GROUP_LOGON_ID` 标志的唯一组；0 个或多个匹配均失败，不回退 Account SID。
- SID 经 `IsValidSid` 验证并转换为规范 SID 字符串；令牌句柄使用 `SafeAccessTokenHandle`。
- 预取消在访问令牌前终止；本 checkpoint 未执行 ACL/SACL、提升、IPC、服务、审计或目录访问。
- 验证：Release build 0 warning、0 error；`WindowsSessionIdentityProviderTests` 5/5 通过、0 failed、0 skipped；format 和 `git diff --check` 通过。

### CHECKPOINT 2：路径、卷、reparse 与目录身份

- 新增 `DirectoryIdentity`、拥有持续 `SafeFileHandle` 的 `ValidatedDirectory`、`FolderPathSafetyPolicy`、`WindowsFolderPathValidator` 和 `WindowsFolderPathRelationService`。
- 验证绝对存在目录、本机固定磁盘、NTFS、目标及祖先 reparse、句柄最终路径、卷身份、`FILE_ID_128` 和 `READ_CONTROL/WRITE_DAC` 能力。
- 保护路径由构造参数与 Windows 环境 API 精确提供；路径关系按目录组件判断，不使用字符串前缀。
- 当前环境不允许 `Directory.CreateSymbolicLink`；测试改为仅在同一 GUID 临时根内通过 `FSCTL_SET_REPARSE_POINT` 创建目录 junction，无提升、无 shell、无系统配置变更。
- 验证：Release build 0 warning、0 error；checkpoint 2 tests 20/20 通过、0 failed、0 skipped；format 和 `git diff --check` 通过；临时目录残留 0。
- 本 checkpoint 未执行 ACL/SACL 写入。

### CHECKPOINT 3：同句柄 DACL 差异与最小 Deny 掩码

- `FolderDenyAccessMask` 仅组合既定权限，精确值为 `0x000101FF`，不包含 `ReadPermissions`、`ChangePermissions`、`TakeOwnership`、`Synchronize`，不使用 `FullControl`。
- `DirectoryAclSnapshot` 保存 Owner/Group、DACL 控制状态、原始 DACL、ACE 原始字节、顺序和多重计数。
- `DirectoryAclEditor` 仅接收持续 `SafeFileHandle`；使用 `GetSecurityInfo`/`SetSecurityInfo` 读取、添加、后置验证和精确移除，不接受路径、不请求 SACL、不整体恢复旧 DACL。
- 新增 `AclTestSafetyGate`；任一清理失败后阻止当前测试进程继续 ACL 写入。
- 所有真实 DACL 写入均位于验证后的 GUID 临时目录 `target`，并在 `finally` 中使用同一句柄精确移除。
- 验证：Release build 0 warning、0 error；checkpoint 3 tests 5/5 通过、0 failed、0 skipped；format 和 `git diff --check` 通过；临时目录残留 0。

### CHECKPOINT 4：Lock、Unlock、幂等与 rollback

- 新增 `ActiveFolderLockRecord` 与 `WindowsFolderLockService`；显式注入身份、路径验证、路径关系和 ACL editor，持续保存同一目录句柄与精确操作记录。
- 单同步门处理重复任务 ID、Same/Ancestor/Descendant、稳定目录身份和注册；重复创建不增加 ACE，重复移除幂等成功。
- `DirectoryAclEditor` 在移除写入前证明当前 ACL 精确为原快照加本次单 ACE；匹配 0/1/>1 与任何漂移均按既定安全规则处理，禁止先删后检查。
- 添加后验证失败只在可证明状态下精确 rollback；rollback 失败或 ACL 状态未知返回 `UnrecoverableError` 并保留记录及句柄。
- Core 将 Create/Remove 的 `UnrecoverableError` 映射为 `RecoveryRequired`；其他确定失败保持 `ActivationFailed`/`UnlockFailed`。
- 验证：Release build 0 warning、0 error；Core focused tests 18/18、Windows service/editor tests 12/12 通过，0 failed、0 skipped；format 和 `git diff --check` 通过；临时目录残留 0。

### CHECKPOINT 5：最小 Deny 权限矩阵

- 新增串行 `WindowsAclIntegrationTests` 与精确 Win32 access probe；每项真实访问均在 Lock 后新开句柄或发起新调用。
- 实际验证枚举、读取、创建、写入、创建子目录、删除、重命名、移动、遍历、扩展属性、普通属性、`DELETE` 与 `FILE_DELETE_CHILD` 均被拒绝。
- Lock 后新开 `READ_CONTROL`、`WRITE_DAC` 成功且 `GetSecurityInfo` 可用；`WRITE_OWNER`、`SYNCHRONIZE` 的 Lock 前后结果一致；拒绝掩码与四项恢复权限零交集。
- 继承 DACL 只通过 Lock 前持有的 `READ_CONTROL` 控制句柄内省；所有数据访问断言仍使用 Lock 后新访问。关闭继承的既有子对象未扩大产品保证。
- 所有测试使用 GUID 临时根、`try/finally` 和 `TestCleanup`；未执行 SACL、审计、owner 修改、提升或外部目录操作。
- 验证：`dotnet restore` 成功；Release build 0 warning、0 error；Category test 1/1、完整 tests 150/150 通过，0 failed、0 skipped；format 和 `git diff --check` 通过；临时目录残留 0。

### CHECKPOINT 6：ACL 不变量、TOCTOU 与清理

- `WindowsFolderPathValidator.VerifyCurrentPathMapping` 使用独立零访问核对句柄复核 reparse、最终路径和 `DirectoryIdentity`；核对句柄不用于 ACL。
- `WindowsFolderLockService` 在 ACL 写入前后复核路径映射。写入前替换直接失败且不写 ACE；写入后替换通过原持续句柄精确 rollback；Lock 完成后的路径替换由 Unlock 继续使用原持续句柄，只移除原目录 ACE。
- 测试专用 `IWindowsFolderLockServiceTestHook` 仅由内部构造暴露，公共构造不包含 hook。
- 不变量测试证明父目录 DACL、原 ACE 字节与顺序、控制/保护/继承状态和无关 SID 不变；Logon SID 应用 ACE恰好一条，Account SID无新增 Deny，Unlock 后目标 DACL 精确恢复。
- TOCTOU tests 3/3、不变量 test 1/1、Windows tests 60/60、solution tests 158/158 通过，0 failed、0 skipped；format 和 `git diff --check` 通过；每轮后临时目录残留 0。
- 截至 checkpoint 6 代码与测试实现完成时，阶段 3 reviewer 尚未执行；阶段 4 未开始。后续 reviewer 结论记录于本阶段 `REVIEW` 小节。

### FINAL VERIFY（reviewer 前）

- `dotnet restore`：退出码 0；全部项目已恢复。
- `dotnet build -c Release`：退出码 0；0 warning，0 error。
- `dotnet test .\tests\FolderSessionLock.Windows.Tests\FolderSessionLock.Windows.Tests.csproj -c Release --no-restore`：退出码 0；60/60 通过，0 failed，0 skipped；结束后临时目录残留 0。
- `dotnet test -c Release --no-restore`：退出码 0；Core 91/91、App 7/7、Windows 60/60，共 158/158 通过，0 failed，0 skipped；结束后临时目录残留 0。
- `dotnet format --verify-no-changes`：退出码 0。
- `git diff --check`：退出码 0；仅显示工作区原有根 `README.md` LF/CRLF 提示。
- 安全扫描未发现 `SetNamedSecurityInfo`、路径型 ACL 写入、Named Pipe、UAC、`auditpol`、SACL 或 Security 日志实现；App 仅引用 Core，Broker 仍是唯一引用 Windows 的产品可执行项目。
- `%TEMP%` 所在卷验证为 Fixed、NTFS、Healthy；`%TEMP%\FolderSessionLock.Tests` 最终子目录数为 0。

### REVIEW：首轮

- reviewer 结论为 `FAIL`；无 `BLOCKER`，2 个 `HIGH`。
- `HIGH-1`：活动 task ID 当前未比较规范化路径、时长和完整 `SessionIdentity` 即返回幂等成功，可能把未写 ACE 的不同请求报告为成功。
- `HIGH-2`：部分真实 ACL 测试的 `TemporaryTestDirectory.Dispose()` 删除失败只抛异常，未调用 `AclTestSafetyGate.Block`，后续 ACL 写测试仍可能继续。
- reviewer 记录 1 个 `MEDIUM`：`DirectoryAclEditor.LastUnrecoveredOperation` 为编辑器级状态，存在跨任务关联风险。
- reviewer 保留 2 个既存 `LOW` 与阶段 2/3 测试缺口。
- 修复轮次 1/6 只处理两个 `HIGH`；不处理 `MEDIUM`、`LOW` 或非阻断测试缺口。

### ITERATE：修复轮次 1/6

- 活动 task ID 仅在规范化 `FolderPath`、`Duration` 和完整 `SessionIdentity` 全部相等时幂等成功；路径、时长、Account SID、Logon SID 或 Windows Session ID 任一不同均返回 `windows.lock.task_id_conflict`。
- `ActiveFolderLockRecord` 保存完成精确请求比较所需的时长与完整会话身份；不同请求不会验证或写入第二路径。
- `TemporaryTestDirectory.Dispose()` 删除失败时先调用 `AclTestSafetyGate.Block`，再抛包含 GUID 路径的 `IOException`。
- 故障注入测试证明一次删除失败后 `EnsureCanWrite()` 必然拒绝；测试在 `finally` 中真实删除该 GUID 目录并恢复测试前 gate 状态。
- 按 reviewer 规则未修改 `LastUnrecoveredOperation` `MEDIUM`、两个既存 `LOW` 或其他非阻断测试缺口。
- 修复后验证：restore 成功；Release build 0 warning、0 error；Windows tests 66/66、solution tests 164/164 通过，0 failed、0 skipped；format 和 `git diff --check` 通过；临时卷 Fixed/NTFS/Healthy；每轮及最终临时目录残留 0。

### REVIEW：最终复审

- reviewer 最终结论为 `PASS`；首轮两个 `HIGH` 均已修复，无 `BLOCKER` 或 `HIGH`。
- 保留 1 个 `MEDIUM`：`DirectoryAclEditor.LastUnrecoveredOperation` 为编辑器级状态，存在跨任务关联风险。
- 保留 2 个既存 `LOW`：阶段诊断仍包含“stage 1”；测试项目旧传递依赖风险。
- 保留非阻断测试缺口：阶段 2 已记录的异常/取消分支，以及 Token畸形缓冲、ACL底层异常句柄保留和未恢复操作跨任务测试。
- 阶段 3 完成门满足；阶段 4 未开始。

### GATE：阶段 3 → 阶段 4

- stage_director 接受阶段 3 的构建、164/164 tests、reviewer `PASS`、ACL 恢复和临时目录残留 0 证据。
- verdict 为 `BLOCKED`；未生成 `NEXT_STAGE_GOAL_PAYLOAD`，阶段 4 未开始；自动阶段转换计数保持 1/8。
- 阻塞 1：恢复记录的精确存储路径、文件格式、字段名称、架构版本、原子提交方式、机器范围完整性/机密性保护 API 和访问 ACL 尚未取得用户确认。
- 阻塞 2：Windows 服务创建/删除、LocalSystem、自动启动、登录前执行、UAC、注销/重启、管理员保护安装目录、权限配置和代码签名尚未取得当前机器或隔离测试环境的明确授权。
- 阻塞 3：服务名、恢复模式入口、项目名、安装位置和启动参数等精确标识符尚未确认；用户可直接提供，或明确授权 planner 提案后再确认。
- 阻塞 4：重启/登录首次访问顺序、测试账户、UAC 人工交互、跨账户拒绝和签名验证的执行者、证书来源与结果采集方案尚未确认。
- 未修改 `docs/DECISIONS.md`：上述事项尚未获用户批准，不得预写为已决定。
- 2026-07-19 自动续行只读搜索 `C:\Users\lingl\.codex\attachments\`：仅找到阶段 4 高层 Broker/IPC 要求，没有恢复存储精确设计、服务/安装标识符、测试环境或系统操作授权；人工门保持未满足。

## 2026-07-19 — 阶段 4 决策阻塞解除与环境授权记录

### 用户明确批准

- 已读取附件 `C:\Users\lingl\.codex\attachments\87812840-50dc-4d41-a87e-7127e60d5f29\pasted-text.txt`。
- 用户明确解除此前四组阶段 4 决策阻塞，并要求在阶段门前将决定写入全部八份权威文档。
- 本节的新批准记录取代本日志上一节“GATE：阶段 3 → 阶段 4”中的四项缺失决定状态；上一节仅保留历史阶段门事实，不再表示当前未确认项。
- 新增并确认：
  - `D-022`：固定 `%ProgramData%\FolderSessionLock\Recovery\Records`、`.fslr` 容器、精确 payload 字段、版本策略、DPAPI LocalMachine、purpose entropy、flush/回读/原子替换和状态事务。
  - `D-023`：恢复目录 owner/DACL、`%ProgramFiles%\FolderSessionLock` 安装 ACL、ProgramData 用途隔离和固定日志/证据路径。
  - `D-024`：服务 `FolderSessionLockRecovery`、Display Name、Description、LocalSystem/Automatic/非延迟启动、服务 SID、Broker 模式、固定 Pipe 名和固定参数。
  - `D-025`：唯一特权测试环境 `FSL-STAGE4-VM`、快照 `FolderSessionLock-Stage4-Clean`、允许的服务/UAC/注销/重启/安装 ACL 操作及禁止范围。
  - `D-026`：VM 测试证书、`FSL-Standard`/`FSL-Admin` 人工场景、跨账户错误码、证据目录、必需文件和精确 `manifest.json`。
- `planner` 不得另提标识符方案或静默改名；任何变更必须重新取得用户确认并经过 `stage_director`。

### 当前环境核验

- 当前计算机名：`AGREELIN`。
- 当前计算机名不等于唯一获准特权集成测试机器 `FSL-STAGE4-VM`。
- 结论：设计、文档、代码、单元测试、非特权测试和静态审查可继续；服务安装/删除、LocalSystem、自动启动、登录前执行、UAC、注销、重启、Program Files/ProgramData ACL 和签名系统测试必须保持环境阻塞，不能记录为通过。

### 本轮文档更新

- 已更新 `docs/DECISIONS.md`、`docs/ARCHITECTURE.md`、`docs/SECURITY.md`、`docs/REQUIREMENTS.md`、`PLAN.md`、`ACCEPTANCE.md`、`TASKS.md`、`DEVLOG.md`。
- 阶段 3 的 164/164 tests、reviewer `PASS`、ACL 恢复和临时目录残留 0 历史证据未改写。
- 阶段 4 尚未开始；等待文档一致性验证和 `stage_director` 重新核验。

### 明确未执行

- 未创建、修改、启动、停止或删除 Windows 服务。
- 未启动提升进程或触发 UAC。
- 未执行注销或重启。
- 未修改任何目标目录、Program Files、ProgramData、仓库、用户目录或系统目录 ACL/SACL。
- 未创建测试证书、修改证书信任、签名二进制或执行 `WinVerifyTrust` 系统场景。
- 未调用 `planner`、`coder` 或 `reviewer`。

### 文档验证

- 首次验证脚本因当前 PowerShell 不支持 `String.Contains(value, comparisonType)` 重载而退出；该错误未修改文件，也不是文档失败。
- 改用 `IndexOf(..., StringComparison.Ordinal)` 后验证通过。
- 验证文件数：8。
- 精确检查通过：`D-022` 至 `D-026`、`.fslr` 字段、`FolderSessionLock.RecoveryRecord.v1`、`FolderSessionLockRecovery`、`FolderSessionLock.Broker.v1`、`FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`、`FSL-STAGE4-VM` 和当前环境阻塞记录。
- 旧四项未确认 checkbox 已消失；旧随机 Pipe 和“进入实现前再确认恢复格式”表述已消失。
- Markdown 围栏平衡；无尾随空白；未发现禁止术语；`git diff --check` 退出码 0，仅保留工作区原有根 `README.md` LF/CRLF warning。
- 未运行 `dotnet restore/build/test/format`：本步骤仅记录用户决定，未修改产品代码；阶段 3 的实际构建、164/164 tests、format、reviewer `PASS` 和清理证据仍为阶段门依据。

### STAGE GATE 与自动转换

- `stage_director` 只读核验 verdict：`READY`。
- 当前阶段：阶段 3：Logon SID 与 ACL 锁定引擎。
- 下一阶段：阶段 4：提升 Broker、IPC 与恢复生命周期。
- 阶段 3 的 164/164 tests、reviewer `PASS`、ACL 恢复、临时目录残留 0 和无 `RecoveryRequired` 证据继续有效。
- `D-022` 至 `D-026` 与用户原始批准附件一致，先前四组人工决定阻塞已解除。
- 自动阶段转换计数从 1/8 更新为 2/8。
- 根主线程已读取并接受完整 `NEXT_STAGE_GOAL_PAYLOAD`；阶段 4 开始。
- 当前机器 `AGREELIN` 只允许设计、代码、单元测试、非特权测试、临时目录安全测试、构建、格式和静态审查。
- `FSL-STAGE4-VM` 专属的服务、LocalSystem、Automatic、登录前、UAC、注销、重启、Program Files/ProgramData ACL、签名和 D-026 最终证据仍为阶段 4 完成前环境门；不得在当前机器伪造通过。

### 阶段 4 PLAN

- planner 只读规划结论：`PROCEED`。
- planner 保留 PLAN.md 十个 checkpoint，并将每项划分为 `AGREELIN` 可执行验证与 `FSL-STAGE4-VM` 专属验证。
- 实施顺序固定为 CP1 → CP10，一次只允许 coder 实现一个 checkpoint。
- CP1：UI/Broker 进程边界；Broker 成为唯一组合根；App 不引用 Windows 或真实 ACL 写入；不实现阶段 5 UI。
- CP2：四命令强类型协议和参数拒绝。
- CP3：固定 Pipe 与最小 DACL。
- CP4：身份、握手和防重放。
- CP5：D-022 `.fslr` 记录与事务。
- CP6：Broker 生命周期和崩溃/断线恢复模型。
- CP7：恢复 ACL 组合、持续句柄和漂移停止。
- CP8：固定恢复模式参数与服务抽象。
- CP9：同账户/跨账户提升身份边界。
- CP10：安装、签名、发布检查和 D-026 证据支持。
- 阶段 3 `DirectoryAclEditor.LastUnrecoveredOperation` 的 `MEDIUM` 直接影响持久恢复，将在 CP5/CP7 改为每任务显式操作证据；既存两个 `LOW` 不扩大范围处理。
- 当前环境完成 CP1–CP10 可执行范围后仍必须等待 `FSL-STAGE4-VM` 特权证据，阶段 4 不得标记完成。

### CHECKPOINT 1：UI/Broker 进程边界

- 新增 `BrokerCompositionRoot` 与 `BrokerRuntime`，只组合现有 `WindowsFolderLockService`、`LockTaskManager`、`LockTaskCoordinator` 和 `LockTaskScheduler`；不执行 ACL、scheduler 循环或系统配置。
- Broker 是唯一引用 `FolderSessionLock.Windows` 的产品可执行项目；App 精确只引用 Core，不引用 Broker、Windows、ACL API、`IFolderLockService` 或 `ILockTaskScheduler` 生命周期。
- App.Tests 增加确定性进程/项目引用边界测试；未创建新测试项目。
- coder focused：App tests 11/11 通过，0 failed，0 skipped。
- coder 完整回归：Core 91/91、Windows 66/66、App 11/11，共 168/168 通过，0 failed，0 skipped。
- coder Release build：0 warning，0 error；最终 format 和 `git diff --check` 通过；临时目录残留 0。
- 根线程复验：App focused 11/11，Release build 0 warning/0 error；App 引用仅 Core，Broker 引用 Core + Windows；CP2+ 禁止能力静态扫描 0 命中；临时目录残留 0；App/Broker 运行进程残留 0。
- 当前机器 `AGREELIN`；未启动 App/Broker，未执行服务、LocalSystem、UAC、注销、重启、Program Files/ProgramData ACL、证书、签名、SACL 或审计操作。
- CP1 完成；未实现 Named Pipe、协议命令、身份握手、防重放、`.fslr`、DPAPI、服务、提升、安装或签名。

### CHECKPOINT 2：强类型协议 — BLOCKED

- coder 按无猜测门重新读取两级规则、附件、D-022 至 D-026、需求、架构、计划、验收、Core/Broker 类型和测试。
- 未修改文件，未开始 CP3。
- 权威文档精确给出四个命令名：`ValidatePath`、`CreateLock`、`RemoveLock`、`GetStatus`，但未定义传输协议 envelope、版本、请求/响应字段、严格字段规则、序列化格式或协议错误码。
- 现有内部创建模型仅为 `FolderLockRequest(Guid TaskId, string FolderPath, TimeSpan Duration)`；权威文档未确认协议是否直接采用这些字段，也未确认字段名和序列化表示。
- `RemoveLock` 存在必须由用户决定的语义冲突：内部接口要求 `LockRemovalIntent`，而 `D-021` 只允许 `Expiration`、`Recovery`、`TestCleanup`、`AdministrativeCleanup`，明确不存在 User/UI 意图；IPC `RemoveLock` 的调用主体和映射未定义。
- `GetStatus` 未定义单任务/任务集合、活动/终态范围、字段、剩余时间表示、错误公开字段或脱敏规则。
- `ValidatePath` 未定义请求字段、规范化路径之外的响应字段和错误映射。
- coder 检查和根线程复核均确认仓库不存在既有协议类型、序列化合同或上述错误码。
- `git diff --check` 退出码 0，仅保留根 `README.md` 既有行尾 warning。
- 未运行构建或测试：CP2 在实现前被精确设计门阻止；CP1 已验证状态不变。
- 未运行 Broker/App，未执行 ACL、服务、UAC、证书或系统操作。
- 解除条件：用户精确确认通用协议、四命令 payload/response、错误模型和 `RemoveLock` 意图语义；确认后写入 `docs/DECISIONS.md` 及相关权威文档，再从 CP2 继续。

### CHECKPOINT 2：Broker IPC v1 用户决定

- 已读取附件 `C:\Users\lingl\.codex\attachments\d6dc5c9f-975c-402a-a8dc-c3ae403c5a5b\pasted-text.txt`。
- 用户明确解决 CP2 的协议设计阻塞，并确认 `D-027：Broker IPC v1 精确协议`。
- 本节取代上一节“CHECKPOINT 2：强类型协议 — BLOCKED”中的缺失协议状态；上一节保留为历史停止证据。
- 固定传输：`\\.\pipe\FolderSessionLock.Broker.v1`，byte mode，单请求/单响应连接，4-byte little-endian `UInt32` 长度，严格 UTF-8 without BOM JSON，正文最大 65536 bytes。
- 固定请求六字段：`protocolVersion`、`requestId`、`command`、`clientSessionId`、`sentAtUtc`、`payload`。
- 固定响应七字段：`protocolVersion`、`requestId`、`command`、`success`、`serverTimeUtc`、`result`、`error`；成功/失败 null 不变量已确认。
- 固定严格 schema：大小写敏感、重复/多余/缺失字段拒绝、无宽松数字/Guid/date/enum/null、预反序列化重复字段检测。
- 固定通用错误：`FSL_E_UNKNOWN_COMMAND`、`FSL_E_MALFORMED_MESSAGE`、`FSL_E_SCHEMA_VIOLATION`、`FSL_E_FORBIDDEN_INPUT`、`FSL_E_PROTOCOL_VERSION_UNSUPPORTED`、`FSL_E_REPLAY_DETECTED`、`FSL_E_REQUEST_EXPIRED`、`FSL_E_SESSION_MISMATCH`、`FSL_E_UNAUTHORIZED_CALLER`、`FSL_E_PIPE_ACCESS_DENIED`、`FSL_E_OPERATION_CANCELLED`、`FSL_E_INTERNAL`。
- 固定 ValidatePath、CreateLock、RemoveLock、GetStatus 的 payload/result 字段、类型、条件、幂等、错误映射和权限矩阵。
- 普通 UI 禁止 RemoveLock；客户端不得提供 `LockRemovalIntent`。服务端按身份/启动模式映射 Expiration、Recovery、TestCleanup。
- GetStatus 只返回相同账户和交互 Session 的内存任务，不提供历史或跨身份存在性信息。
- 协议 DTO 与领域模型分离；具体使用独立项目或清晰命名空间由 planner 在不改变外部合同的前提下说明。
- 已同步更新 `docs/DECISIONS.md`、`docs/ARCHITECTURE.md`、`docs/SECURITY.md`、`docs/REQUIREMENTS.md`、`PLAN.md`、`ACCEPTANCE.md`、`TASKS.md`、`DEVLOG.md`。
- CP2 尚未恢复编码；等待文档一致性验证和 `stage_director` 只读核验。
- 本轮未运行服务、Broker/App、ACL、UAC、注销、重启、Program Files/ProgramData ACL、证书、签名、SACL 或审计操作。

### Broker IPC 文档验证

- 八份权威文档全部存在、非空，Markdown 围栏平衡，无尾随空白。
- `D-027` 精确检查：请求字段 6/6、通用错误码 12/12、命令字段 26/26。
- 固定 Pipe、65536-byte 上限、7 位小数 UTC、跨账户错误码、内部固定 message、RemoveLock 映射和 GetStatus query 类型均存在。
- `ARCHITECTURE.md`、`SECURITY.md`、`REQUIREMENTS.md`、`PLAN.md`、`ACCEPTANCE.md`、`TASKS.md`、`DEVLOG.md` 同步标记通过。
- TASKS.md 中旧协议确认 checkbox 已消失；未发现禁止术语。
- `git diff --check` 退出码 0，仅显示工作区既有根 `README.md` LF/CRLF warning。
- 未运行 .NET 构建/测试：本步骤只更新文档，CP1 的 168/168 测试证据保持有效；CP2 尚未恢复编码。

### CP2 CONTINUE GATE

- `stage_director` 只读结论：`READY`。
- 当前阶段：阶段 4；CP1 已完成；CP2 协议设计阻塞已解除。
- `D-027` 覆盖分帧、严格 JSON、envelope、基础类型、错误模型、四命令 payload/result、RemoveLock 权限/intent、GetStatus 语义和最低恶意输入矩阵。
- 阶段 4 自动转换计数保持 2/8；本次为阶段内部续行。
- 当前代码仍停留在 CP1：尚无 Named Pipe、协议 DTO、`FSL_E_*` 或四命令实现，符合 CP2 尚未编码状态。
- 当前机器 `AGREELIN` 可继续非特权 CP2–CP10 实现；`FSL-STAGE4-VM` 最终环境门保持不变。
- 根主线程接受继续 payload，下一步重新调用只读 planner，等待完整交接后再恢复 coder。

### CP2 更新 PLAN

- planner 结论：`PROCEED`。
- 协议代码唯一位置：现有 `FolderSessionLock.Core` 项目的 `src\FolderSessionLock.Core\Protocol\`，命名空间 `FolderSessionLock.Protocol`；不新增项目，不改变 solution 或项目引用。
- App 与 Broker 继续通过 Core 共享协议 DTO；协议不得放入 App、Broker 或 Windows，WPF DTO 不得成为 Broker 协议模型。
- CP2 精确范围：传输无关 DTO、46 个 error code constants、请求/响应 envelope、BrokerError、四命令 payload/result、严格 JSON/schema、基础类型验证和纯服务端权限策略。
- CP2 不含 4-byte frame、65536-byte frame 限制、BOM/非法 UTF-8、Named Pipe、OS 身份、replay cache、120 秒时间窗口、握手、ACL、DPAPI、恢复文件、服务、UAC、安装或签名。
- CP2 纯权限策略通过不可序列化 `BrokerExecutionContext` 建模；客户端不能提供角色或 intent。普通 UI RemoveLock 固定拒绝，内部上下文分别映射 Expiration/Recovery/TestCleanup。
- CP3 负责 byte framing/Named Pipe；CP4 负责 OS 身份、握手、replay 和时间窗口。
- `LastUnrecoveredOperation` `MEDIUM` 在 CP5 禁止作为恢复事实源，CP7 删除共享状态并改为每调用 outcome；CP7 增加双任务交错测试。
- 当前无阻止 CP2 的待决策项；coder 只实现 CP2，完整验证后停止，不进入 CP3。

### CP2 EXECUTE 与 VERIFY

- coder 在现有 `FolderSessionLock.Core` 中新增独立 `FolderSessionLock.Protocol` 协议层；未新增项目，App/Broker/Windows 项目引用保持不变。
- 实现 `ValidatePath`、`CreateLock`、`RemoveLock`、`GetStatus` 四命令关闭集合，请求六字段、响应七字段、`BrokerError` 四字段、46 个固定错误码、四命令 payload/result、显式领域映射和纯服务端权限策略。
- 实现严格 JSON/schema：大小写敏感字段、重复字段、缺失字段、多余字段、不允许 null、token 类型、Int32/Int64 词法与溢出、小写非空 Guid D、7 位小数 UTC Z、枚举关闭集合和禁止客户端字段均按 D-027 返回确定错误。
- 普通 UI `RemoveLock` 固定拒绝；客户端不能提供角色或 removal intent；内部上下文只映射 `Expiration`、`Recovery`、`TestCleanup`。
- CP2 未实现 byte framing、Named Pipe、OS 客户端身份、握手、replay cache、120 秒时间窗口、ACL、DPAPI、恢复记录、服务、UAC、安装或签名。
- coder focused 验证：协议 45/45 passed、0 failed、0 skipped；App ProcessBoundary 4/4 passed、0 failed、0 skipped。
- coder 完整验证：`dotnet restore` 退出码 0；Release build 0 warning、0 error；Core 136/136、Windows 66/66、App 11/11，共 213/213 passed、0 failed、0 skipped；最终 `dotnet format --verify-no-changes` 退出码 0；`git diff --check` 退出码 0；CP3+ 越界扫描 0 命中；临时测试目录残留 0。
- 根主线程独立复验：协议 focused 45/45 passed；App ProcessBoundary/LifecycleBoundary 8/8 passed；`dotnet restore` 退出码 0；Release build 0 warning、0 error；完整测试 213/213 passed、0 failed、0 skipped；format 与 `git diff --check` 通过。
- 根主线程精确越界扫描未发现 Named Pipe、PipeSecurity、byte framing、DPAPI、Windows service、elevation、签名、OS Pipe 客户端身份或 replay cache 实现；`idempotentReplay` 为 D-027 规定的响应字段，不是 CP4 防重放缓存。
- 根主线程残留检查：`%TEMP%\FolderSessionLock.Tests` 子目录 0、文件 0。
- CP2 完成；下一 checkpoint 为 CP3 固定 Named Pipe 与 byte framing。阶段 4 仍在执行中，未进入阶段 5；`FSL-STAGE4-VM` 最终环境门保持不变。

### CP3 EXECUTE 与 VERIFY

- coder 在 `src\FolderSessionLock.Broker\Transport\` 实现固定端点、frame codec、单请求/响应连接处理和 Pipe server；未新增 NuGet、项目或 ProjectReference，Broker 继续仅引用 Core + Windows。
- Pipe 名固定为 `FolderSessionLock.Broker.v1`，完整路径语义为 `\\.\pipe\FolderSessionLock.Broker.v1`；调用方提供其他名称时拒绝。
- frame 固定为 4-byte little-endian `UInt32` 长度前缀与 1..65536 bytes 严格 UTF-8 without BOM JSON body；拒绝零长度、超限、前缀/body 不完整、BOM、非法 UTF-8、尾随字节、第二 frame 和多 JSON。
- 每连接只处理一个请求与一个响应后关闭；I/O 支持 CancellationToken、读取超时和确定错误映射；传输层复用 CP2 JSON body codec，不实现命令 handler。
- `.NET 8.0.11` 的 `NamedPipeServerStreamAcl.Create` 明确拒绝同时使用 `PipeOptions.CurrentUserOnly` 与自定义 `PipeSecurity`。为同时满足 D-027 的仅本机与精确 DACL，产品使用最小 `CreateNamedPipeW` 包装，固定 `PIPE_REJECT_REMOTE_CLIENTS = 0x00000008`、byte/read/wait mode，并包装 `SafePipeHandle`。
- Pipe DACL 为受保护、无继承，只含发起 Logon SID 与 Broker Account SID 两条 Allow；Windows 规范化权限为 ReadWrite 与 Synchronize。CP3 仅接收受信内部 SID 参数，不读取或认证客户端 OS 身份。
- coder 最终验证：transport focused 19/19、App 30/30、CP2 协议回归 45/45、solution 232/232 passed，0 failed、0 skipped；Release build 0 warning、0 error；format、`git diff --check`、CP4+ 越界扫描通过；临时目录与 App/Broker 进程残留 0。
- 根主线程独立复验得到相同计数与结果：transport 19/19、App 30/30、协议 45/45、solution 232/232；build、format、diff 和 CP4 越界扫描通过；`%TEMP%\FolderSessionLock.Tests` 目录 0、文件 0，产品进程 0。
- 当前 `AGREELIN` 未执行第二台机器远程 Pipe 连接验证；产品代码静态固定 remote-reject 标志。未执行服务、UAC、Program Files/ProgramData ACL、证书或签名系统操作。
- CP3 完成；下一 checkpoint 为 CP4 OS 身份、握手、防重放和服务端时间窗口。阶段 4 仍在执行中，未进入阶段 5。

### CP4 设计门：BLOCKED

- coder 在任何 CP4 写入前只读核验 D-024、D-027、原始 IPC 附件、八份权威文档、当前 Broker Transport、Core Protocol、Windows identity 代码和测试；本轮未修改 CP4 代码、未运行 CP4 测试、未进入 CP5。
- 握手承载未定义：权威文档要求“一次性高熵握手值”，但 D-027 请求 envelope 精确只有六字段且无握手字段；当前 Pipe 连接在连接建立后直接读取一个 D-027 frame。文件中没有 handshake、nonce 或 challenge 的精确格式、长度、生成方、传递顺序和读取边界。
- 握手绑定未定义：D-024 CLI 固定包含 `--request-id <Guid>`，D-027 JSON 也包含 `requestId`，但权威文件未定义握手值、CLI request-id 与 JSON requestId 是相等、派生还是互相独立。
- 握手错误未定义：46 个固定错误码和 D-027 通用错误中，没有把握手缺失、格式错误或值不匹配逐项映射到完整公开 `BrokerError`，也未定义失败响应的 requestId/command 是否为 null。
- 身份错误未定义：只有 JSON `clientSessionId` 与 OS Session ID 不一致被精确映射为 `FSL_E_SESSION_MISMATCH`；连接客户端 Account SID、Logon SID、连接进程身份不匹配各自的完整公开错误对象未定义。跨账户 elevation 错误码存在，但未明确等同于连接客户端 Account SID 检查。
- replay 原子语义未定义：D-027 只规定最近 10 分钟已处理 requestId 重用返回 `FSL_E_REPLAY_DETECTED`；未定义解析失败、身份失败、权限失败、命令成功/失败是否消费 ID，并发同 ID 的所有权、TTL 起点、提交和回滚规则。
- 解除条件：用户精确确认五项合同——握手载体与传输、握手/CLI request-id/JSON requestId 绑定、握手失败完整错误对象、三类身份不匹配完整错误对象、replay 注册点与并发原子语义。确认后必须同步权威文档并从阶段门重新核验；不得自行选择协议。
- 当前阶段保持阶段 4；CP1、CP2、CP3 完成证据有效；自动推进停止，不进入 CP5 或阶段 5。

## 2026-07-19 — CP4 握手、身份绑定与 Replay 最终合同

- 已读取用户附件 `C:\Users\lingl\.codex\attachments\67fe30c2-2d02-44d8-a036-32b353f86dd4\pasted-text.txt`。用户明确解除 CP4 五项设计阻塞，并要求同步八份权威文档后重新调用 `stage_director`。
- D-027 现固定 consent-broker 每连接序列：`ClientHello -> ServerHello -> CommandRequest -> CommandResponse -> close`。四种 frame 使用现有 length-prefix、严格 UTF-8/schema；字段集合分别为 9、9、8、7。
- CLI `--request-id`、ClientHello.requestId、CommandRequest 外层与内层 requestId 必须相同；CLI/session、ClientHello、内层 request 与 OS 客户端/Broker Session 必须相同。command 和 protocolVersion 同样绑定。
- ClientHello/clientNonce、ServerHello/connectionId/serverNonce/expiresUtc、CommandRequest/bindingProof canonical string 与固定 SHA-256 Base64URL 规则已经确认；握手成功后只允许一次 CommandRequest。
- OS 身份验证顺序已确认：实际 Pipe PID、进程存活/启动时间、进程 Session、模拟令牌 Account SID/Logon SID/TokenSessionId、finally 恢复、Broker identity 比较、命令授权。PID/SID/Session/identity unavailable 的完整错误对象已固定。
- 新增握手/身份/replay 错误码及固定 message：`FSL_E_HANDSHAKE_REQUIRED`、`FSL_E_HANDSHAKE_VERSION_UNSUPPORTED`、`FSL_E_HANDSHAKE_EXPIRED`、`FSL_E_PROTOCOL_SEQUENCE_INVALID`、`FSL_E_REQUEST_BINDING_MISMATCH`、`FSL_E_CLIENT_PROCESS_MISMATCH`、`FSL_E_CLIENT_IDENTITY_UNAVAILABLE`、`FSL_E_ACCOUNT_SID_MISMATCH`、`FSL_E_LOGON_SID_MISMATCH`、`FSL_E_REQUEST_IN_PROGRESS`；既有 Session/Replay 错误 message 同步固定。
- Replay Registry 固定为 `%ProgramData%\FolderSessionLock\Replay\v1`，使用隐藏 SID/requestId 的 SHA-256 key、精确 `.fsrr` schema、八状态、CreateNew 原子登记、所有权四元组和受保护 `Global\FolderSessionLock.ReplayRegistry.v1` mutex；普通 UI 无直接访问。
- timeout/TTL 固定为 ClientHello 5 seconds、handshake 30 seconds、lease 60 seconds、renewal 20 seconds、单请求 5 minutes、终态 10 minutes；RecoveryRequired 无自动过期。
- 解析、身份、握手超时、binding、应用验证、rollback、成功、owner 崩溃、PID 重用和未知副作用的 Failed/RolledBack/RecoveryRequired/Abandoned/Succeeded 规则已经确认。业务 taskId 幂等不允许传输 requestId 重放。
- 本轮只同步文档；未修改产品代码或测试，未实现 CP4，未运行服务、LocalSystem、UAC、注销、重启、Program Files/ProgramData ACL、证书、签名、SACL 或审计操作。
- 下一步：验证八份权威文档字段、错误码、路径、TTL、状态与附件一致，再由根线程调用只读 `stage_director`。READY 前不调用 coder，不进入 CP5。

### CP4 最终合同文档验证

- 八份权威文档同步检查：8/8 文件均包含 D-027、握手和 Replay 当前合同。
- D-027 精确必备项检查：50/50，包括四帧序列、12 个握手/身份/Replay 错误码及固定 message、Replay 路径/mutex、八状态、所有权字段、binding/replay canonical prefix、CreateNew 和全部 timeout/TTL 数值。
- Markdown 围栏全部平衡；尾随空白 0；禁止术语命中 0；TASKS 中旧未解除 CP4 阻塞 checkbox 0。
- `git diff --check` 退出码 0；仅显示工作区既有根 `README.md` LF/CRLF warning。
- 本步骤只验证文档，未运行 .NET build/tests，未修改代码，未执行服务、提升、ACL、重启、注销、签名、SACL 或审计操作。
- 根线程下一步调用项目级只读 `stage_director`。若 READY，读取并立即执行完整 NEXT_STAGE_GOAL_PAYLOAD；若 BLOCKED，只接受本合同未覆盖的新证据。

### CP4 最终合同 STAGE GATE：BLOCKED

- `stage_director` 确认用户附件已覆盖此前五项阻塞：四帧握手、CLI/JSON 绑定、身份错误、Replay Registry/所有权/TTL、失败撤销和崩溃处理；未重复要求这些内容。
- 历史阻塞 1（已由后续最终勘误解除）：当时 D-027.12 与 D-027.15 的 Replay 登记顺序互斥，身份失败登记行为不唯一。最终规则现为完整 OS/Broker 身份和命令权限验证成功后 CreateNew；身份或授权失败绝不登记。
- 历史阻塞 2（已由后续最终勘误解除）：当时六个握手/序列/Replay 错误缺少完整响应映射。最终规则现已逐场景固定唯一 code、retryable、field、ServerHello/CommandResponse 和标识符回显/null。
- verdict：`BLOCKED`；execution_mode：`STOP`；未生成 NEXT_STAGE_GOAL_PAYLOAD。CP4 未实现，CP5/阶段 5 未开始；未执行 VM 专属操作。

## 2026-07-19 — CP4 Replay 顺序与六错误最终勘误

- 用户明确本合同优先于此前冲突文本：Replay CreateNew 只发生在完整 ClientHello/CLI/time、OS 客户端 PID/进程/令牌身份、Broker Account/Logon/Session 比较和命令权限验证全部成功之后。
- 身份、Session、PID、schema、版本、binding、时间或 unauthorized 失败绝不创建 Replay；删除“身份失败后把已创建 Replay 标记 Failed”的兼容分支。
- AwaitClientHello 首个有效非 ClientHello frame 唯一返回 `FSL_E_HANDSHAKE_REQUIRED` ServerHello failure；成功 ServerHello 后的语法有效非法序列唯一返回 `FSL_E_PROTOCOL_SEQUENCE_INVALID` CommandResponse failure。
- 六个错误的唯一 frame/retryable/field 已固定：HANDSHAKE_REQUIRED=ServerHello/true/frameType；HANDSHAKE_VERSION_UNSUPPORTED=ServerHello/false/handshakeVersion；HANDSHAKE_EXPIRED=CommandResponse/true/null；PROTOCOL_SEQUENCE_INVALID=CommandResponse/false/frameType；REQUEST_IN_PROGRESS=ServerHello/true/requestId；REPLAY_DETECTED=ServerHello/false/requestId。
- ServerHello failure 只回显合法输入 requestId 和四项允许 command，不生成 connectionId；CommandResponse failure 只使用成功握手已接受的 requestId、command、connectionId，不使用恶意后续 frame 的篡改值。
- HANDSHAKE_EXPIRED 进入 Abandoned 并保留 10 minutes；PROTOCOL_SEQUENCE_INVALID 无副作用进入 Failed、未知副作用进入 RecoveryRequired；REQUEST_IN_PROGRESS/REPLAY_DETECTED 不修改原 Replay owner、lease、terminal 或 retention。
- 本轮仅同步文档；未修改产品代码/测试，未执行服务、注销、重启、系统配置、ACL/SACL、提升或签名操作。CP4、CP5、阶段 5仍未开始。
- 下一步：扫描并删除所有身份前 Replay 登记、身份失败保留 Replay、顺序错误二选一和错误响应缺字段文本；验证八份文档后调用 `stage_director`。

### CP4 最终勘误一致性验证

- 严格 UTF-8 读取并检查八份权威文档：8/8 文件均包含 D-027、握手和 Replay 当前合同。
- `docs/DECISIONS.md` 六错误完整合同表 6/6、核心 Replay 顺序与失败响应规则 4/4。
- 冲突短语扫描 5/5 无命中；Markdown 围栏全部平衡；尾随空白 0；禁止术语 0。
- `git diff --check` 退出码 0。
- 本验证未运行 .NET build/tests，未修改产品代码或测试，未执行服务、LocalSystem、UAC、注销、重启、Program Files/ProgramData ACL、证书、签名、SACL、审计或系统配置操作。
- 下一步由根线程调用项目级只读 `stage_director`；阶段门返回前不启动 planner、coder 或 reviewer。

## 2026-07-19 — 阶段 4 checkpoint 4 实现与审查完成

- 最终合同同步后，项目级只读 `stage_director` 返回 `READY`；下一项固定为阶段 4 CP4，自动转换计数保持 2/8。只读 planner 返回 `PROCEED`，无待决策项。
- 根线程严格串行调用唯一 coder；实现固定 `ClientHello -> ServerHello -> CommandRequest -> CommandResponse -> close`、CLI/request/session 绑定、32-byte nonce、connectionId、bindingProof、OS Named Pipe 客户端 PID/进程/Session/token 身份、Broker identity 比较和命令权限。
- Replay Registry 实现固定生产路径、SHA-256 key、14 字段 schema、八状态、`FileMode.CreateNew`、所有权四元组、60-second lease、20-second renewal、5-minute execution limit、10-minute terminal retention、RecoveryRequired 无过期、PID 重用和非 owner 拒绝。
- Replay CreateNew 位于完整 ClientHello/CLI/time、OS/Broker 身份和命令权限验证之后；身份、授权、schema、版本、binding 或时间失败不会创建 Replay。
- 六错误唯一 ServerHello/CommandResponse、retryable、field、Replay 与标识符规则已实现；真实超时与 malformed 第二帧分离。

### reviewer 首轮与第 1/6 轮修复

- reviewer 首轮结论为 `FAIL`，报告 4 个 `HIGH`：`RevertToSelf` 失败未硬停止；第二帧 malformed 被覆盖为 handshake expired；abandoned Registry mutex 未恢复；业务执行结果无法表达副作用/rollback Replay 终态。
- 第 1/6 轮只修复 4 个 `HIGH`：生产终止器使用 `Environment.FailFast` 且测试注入 fake；frame codec 增加真实 timeout 区分；`AbandonedMutexException` 按已取得 mutex 处理并由同线程 finally 释放；新增强类型 `BrokerExecutionOutcome` 映射 Succeeded、Failed、RolledBack、RecoveryRequired。
- reviewer 复查结论为 `PASS`，无 `BLOCKER` 或 `HIGH`。
- 保留非阻断 `MEDIUM`：内层 `BrokerResponseEnvelope` 的 requestId、command、protocolVersion 仍由内部 handler 提供，连接层未强制绑定到已接受标识符；本轮按规则未修复。

### CP4 最终验证

- 根线程 focused：Core Protocol 52/52、App CP4 55/55、Windows identity 5/5，全部通过，0 skipped。
- `dotnet restore`：通过。
- `dotnet build -c Release`：通过，0 warning、0 error。
- `dotnet test -c Release --no-restore -m:1`：Core 143/143、App 66/66、Windows 66/66，总计 275/275，0 failed、0 skipped。
- 并行 solution test 曾在未进入完整测试前因 CoreCLR `0x8007000E` 资源错误中止；串行 `-m:1` 完整执行并作为最终证据。
- `dotnet format --verify-no-changes`：通过；`git diff --check`：退出码 0，仅根 `README.md` 既有行尾提示。
- CP5 越界扫描：`.fslr`、`FSLR`、`ProtectedData`、`DataProtectionScope`、`RecoveryRecord.v1`、`ReplaceFileW` 均为 0 命中。
- 清理结果：`%TEMP%\FolderSessionLock.Tests` 目录/文件 0、工作区 `.fsrr` 0、TestResults 0、testhost/vstest/App/Broker 进程 0。
- 当前机器 `AGREELIN` 未执行 `%ProgramData%` Replay ACL、受保护全局 mutex DACL、普通用户访问拒绝、服务、LocalSystem、UAC、注销、重启、签名、SACL、审计或其他 `FSL-STAGE4-VM` 专属操作。
- CP5 保持未开始；下一步由根线程重新调用只读 `stage_director`。

## 2026-07-19 — 阶段 4 checkpoint 5 规划阻塞

- CP4 状态摘要同步后，项目级只读 `stage_director` 返回 `READY`；紧邻项为阶段 4 CP5：Broker 计时、内存状态与 D-022 固定 `.fslr` 恢复记录。
- 根线程严格串行调用只读 planner；planner 完整读取 D-022、D-023、D-024、D-027、现有目录身份、ACL 快照、Broker/Replay 调用链和 tests 后返回 `BLOCKED`。未调用 coder，未修改 CP5 产品代码或测试，未进入 CP6。
- 新阻塞 1：D-022 未规定恢复记录 `volumeSerialNumber` 的精确格式，也未规定原始 16-byte `FILE_ID_128` 到 `fileIdHigh`/`fileIdLow` 的字节范围、字节序和十进制转换；缺少固定输入/输出测试向量。
- 新阻塞 2：D-022 仅规定 `aceFingerprintSha256`、`baselineDaclSha256`、`postApplyDaclSha256` 为 lowercase hex SHA-256，未规定三者各自的精确输入字节，未说明是否包含 owner、group、control flags 或包装数据；缺少固定输入/摘要测试向量。
- 影响：自行选择编码会导致同一目录身份或 ACL 状态产生不同持久值，可能错误接受 ACL 漂移、错误拒绝安全清理或验证错误目录。项目规则禁止猜测。
- 解除条件：用户提供两组完整精确决定；根线程同步权威文档后重新调用 `stage_director` 和 planner。CP1–CP4 的实现、275/275 验证与 reviewer `PASS` 证据保持有效。

## 2026-07-19 — CP5 目录身份与 ACL 摘要最终合同

- 已读取用户附件 `C:\Users\lingl\.codex\attachments\a3173f6d-a52b-4031-b8d2-0c5b83c73311\pasted-text.txt`。本合同覆盖旧 8 位 volume serial、未定义 FILE_ID high/low、SDDL/整个 SECURITY_DESCRIPTOR/对象序列化/排序 ACE/ACL 未使用尾部摘要规则。
- 目录身份固定来源为同一持续句柄的 `FILE_ID_INFO.VolumeSerialNumber` UInt64 与完整 16-byte FILE_ID_128。volume 为 16 位小写 hex；bytes 0..7/8..15 分别按 little-endian UInt64 写为无前导零十进制 `fileIdLow`/`fileIdHigh`，恢复时反向重建完整 16 bytes。
- 固定目录向量：volume `0123456789abcdef`、FILE_ID `000102030405060708090a0b0c0d0e0f`、low `506097522914230528`、high `1084818905618843912`。
- `aceFingerprintSha256` 固定使用写后 OS DACL 重读的唯一 ACE 和 `FSLACE` v1 wrapper；`baselineDaclSha256`/`postApplyDaclSha256` 固定使用 `FSLDACL` v1 wrapper、原 ACE 顺序、有效 ACE bytes、ACL revision 和 `securityDescriptorControl & 0x1504`。
- 摘要排除 owner、group、SACL、SELF_RELATIVE、SDDL、整个 SECURITY_DESCRIPTOR、路径、目录身份、排序 ACE 和 ACL 未使用尾部。三个摘要仅为状态证据，不单独授权恢复。
- 固定摘要：ACE `366092caef8b4ccd9a05728cc017b2b155a9f8aa74358e6df901e0554a8239f7`；baseline `62fffcf46d188397e84da5b800129f54cacc87fe86ef9ca1f9eac9c6eef2db17`；postApply `0bd878690d59d8de240e84199560b65db09c2f473dffc717aabb75642566f026`。
- 本轮只同步文档；未运行服务、LocalSystem、UAC、注销、重启、生产 ProgramData ACL、签名、SACL、审计或系统配置操作。CP5 代码仍未开始，CP6 未开始。

### CP5 编码与摘要合同文档验证

- 八份权威文档 8/8 均包含 D-022、`volumeSerialNumber`、`FSLACE` 和 `FSLDACL` 当前合同。
- D-022 固定目录向量、两个 high/low 十进制值、三个 wrapper 输入、三个预期 SHA-256 和 control mask 共 11/11 必备值存在。
- 使用实际 SHA-256 计算复核三个固定输入，3/3 与用户批准摘要完全一致。
- 旧活动条款扫描 4/4 无命中：旧 `01abcdef` 示例、8 位 `volumeSerialNumber`、8 位卷序列号和 8 位 volume serial 均已删除或明确为被替换历史。
- Markdown 围栏全部平衡；尾随空白 0；禁止术语 0；`git diff --check` 退出码 0。
- 文档验证未运行 .NET build/tests，未修改产品代码或测试，未执行服务、提升、ACL、注销、重启、签名、SACL、审计或系统配置操作。

### CP5 编码与摘要合同 STAGE GATE：BLOCKED

- `stage_director` 确认用户附件已完整解除目录身份编码、FILE_ID_128 映射、`FSLACE`/`FSLDACL` wrapper、control mask、ACE 顺序、有效 byte 范围和三个固定 SHA-256 向量阻塞；未重复要求这些内容。
- 新阻塞 1：D-022 只定义容器含 UInt16 `Flags` 与 UInt32 `ProtectedPayloadLength`，未定义 v1 Flags 当前值、允许掩码、未知位行为、写入规则，也未定义 payload length 最小/最大值、与实际剩余文件长度的一致性及零长度、超限、截断、尾随字节处理。
- 新阻塞 2：D-022 未完整定义 recovery payload 25 字段的 JSON/.NET 类型、格式、范围、flags/enum 允许值、未知值处理和字段必需性；四状态下 `postApplyDaclSha256`、`lastErrorCode`、`lastErrorMessage` 的 null 组合未定义。Prepared 必须在 ACL 前提交，而 postApply 必须写后 OS 重读，当前非 null 示例无法唯一实现 Prepared。
- verdict：`BLOCKED`；execution_mode：`STOP`；未生成 NEXT_STAGE_GOAL_PAYLOAD。未调用 planner、coder 或 reviewer；CP5 代码未开始，CP6 未开始。
- 解除条件：用户提供上述两组完整合同；根线程同步八份权威文档并重新调用只读 `stage_director`。不得再次以本附件已确认的目录身份或摘要规则阻塞。

## 2026-07-19 — CP5 `.fslr` 容器与 Recovery payload 最终合同

- 已读取用户附件 `C:\Users\lingl\.codex\attachments\1a3ee981-d95b-4a8b-a05a-f8d22b42574c\pasted-text.txt`。v1 header 固定 12 bytes：Magic `FSLR`、version 1、flags 0、UInt32 protected length、DPAPI blob；允许 flags mask=0，任何非零拒绝。
- DPAPI blob 长度固定 1..262144，文件总长必须严格等于 `12 + length`；短头/短 payload、尾随零/非零、错误 magic/version/flags/length 使用固定错误并在分配/DPAPI 前拒绝。解密明文最大 131072 bytes。
- payload 必须为 UTF-8 without BOM 单一 object，全部 25 字段始终存在；字段类型、.NET 类型、范围、canonical Guid/date/SID/hash、enum/flags、unknown/null 规则已逐项固定。
- 状态矩阵固定：Prepared postApply/error=null、count0；Applied postApply非 null/error null、count0；CleanupPending postApply非 null/error null、count>=1；CleanupFailed postApply/error非 null、count>=1。Prepared 保存预期 ACE fingerprint，实际 fingerprint 和 postApply 在写后 OS 重读确认。
- 任一容器/payload失败不得修改 ACL、删除/覆盖记录、迁移版本或扫描无关路径；只返回稳定错误并标记人工恢复检查。
- 本轮仅同步文档；未修改产品代码或测试，未执行 ACL、服务、LocalSystem、UAC、注销、重启、安装、签名、SACL、审计或系统配置操作。CP5 代码仍未开始，CP6 未开始。

### CP5 容器与 payload 合同文档验证

- 八份权威文档 8/8 均包含 D-022、Flags、262144、131072、25 字段和 Prepared 当前合同。
- D-022 容器/payload 稳定错误码 13/13 存在；Prepared/Applied/CleanupPending/CleanupFailed 状态矩阵 4/4 完整。
- D-022 Prepared JSON 示例精确包含 25/25 字段，`postApplyDaclSha256 = null`。
- Markdown 围栏全部平衡；尾随空白 0；禁止术语 0；`git diff --check` 退出码 0。
- 本步骤只验证文档，未运行 .NET build/tests，未修改产品代码或测试，未执行 ACL、服务、提升、注销、重启、安装、签名、SACL、审计或系统配置操作。

## 2026-07-20 — 阶段 4 checkpoint 5 实现与审查完成

- `stage_director` 返回 `READY`；只读 planner 返回 `PROCEED`，确认两份 CP5 用户合同无剩余设计阻塞。根线程严格串行调用唯一 coder，未进入 CP6。
- 实现 UInt64 volume serial 与完整 FILE_ID_128、同句柄 FSLACE/FSLDACL、三个固定摘要向量、严格 25 字段 payload、13 个 recovery 错误码、DPAPI LocalMachine、12-byte `.fslr` 容器和原子文件 store。
- Windows ACL 流程固定为 PrepareAcl 零写入 → Prepared 原子提交 → ACL 写入/OS 重读 → Applied；清理固定为 CleanupPending → 精确移除 → 删除，失败写 CleanupFailed。Broker 保存 taskId/recoveryRecordId/requestId 独立事实映射。
- reviewer 首轮结论 `FAIL`，报告 2 个 `HIGH`：Prepared 记录删除失败被吞掉；D-022.5/D-022.9 强制矩阵与原子事务中断点覆盖不足。
- 第 1/6 轮只修复两个 `HIGH`：两处 Delete 失败优先传播 UnrecoverableError，使 task/effect/Replay 进入 RecoveryRequired；增加真实跨层删除失败测试及 identity、ACE/DACL、container、25 字段、状态矩阵和 store 四提交点故障注入。
- reviewer 复查结论 `PASS`，无 `BLOCKER` 或 `HIGH`。
- 保留非阻断问题：3 个 `MEDIUM`（accountSid 主体分类、lastErrorMessage 内容拒绝、lastUpdatedUtc 严格递增）和 1 个 `LOW`（临时文件清理异常可能覆盖稳定结果）。CP4 内层 response 标识符 `MEDIUM` 也保持未改。

### CP5 最终验证

- 根线程 focused：Core Protocol 52/52、App Recovery+Transport 239/239、Windows Security/Services/Integration 74/74，全部通过，0 skipped。
- `dotnet restore`：通过。
- `dotnet build -c Release --no-restore`：通过，0 warning、0 error。
- `dotnet test -c Release --no-restore -m:1`：Core 143/143、App 250/250、Windows 80/80，总计 473/473，0 failed、0 skipped。
- `dotnet format --verify-no-changes`：通过；`git diff --check`：退出码 0，仅根 `README.md` 既有行尾提示。
- CP6 扫描唯一文字命中为测试名 `DaclDigest_IgnoresSaclSelfRelativeAndUnusedAclTail`，用于证明 SACL/SELF_RELATIVE 不参与 DACL digest；无 CP6 产品能力。
- 清理：`%TEMP%\FolderSessionLock.Tests` 目录/文件 0；工作区 `.fslr`/`.tmp-*`/`.bak`/`.fsrr` 0；TestResults 0；testhost/vstest/App/Broker 进程 0。
- 当前机器 `AGREELIN` 未执行生产 ProgramData ACL、服务、LocalSystem、登录前恢复、UAC、注销、重启、安装、签名、SACL、审计或系统配置操作。
- CP6 保持未开始；下一步由根线程重新调用只读 `stage_director`。

## 2026-07-20 — 阶段 4 checkpoint 6 生命周期结果优先级决定

- CP5 状态同步后，项目级只读 `stage_director` 返回 `READY`；紧邻项为阶段 4 CP6：崩溃、断线、正常退出、注销和断电恢复。
- 只读 planner 确认 CP6 主体合同完整并给出 `BrokerLifecycleController`、administrative cleanup、disconnect/fault 测试和环境边界交接。
- 唯一 coder 在写代码前要求精确确认：scheduler 已失败，随后 mandatory administrative cleanup 也失败时，`StopAsync` 应返回哪个错误。根线程暂停 coder 并调用只读 planner 复核；未修改 CP6 产品代码或测试。
- 用户最终决定：cleanup first-task error 优先；scheduler error 仅写入受保护内部日志。该决定记录为 `D-028`，解除原生命周期结果优先级阻塞。
- Cleanup 在 scheduler 任意结果下都必须启动并按稳定任务顺序完整遍历；单任务失败不提前终止。第一个 Cleanup task error 是唯一对外主错误，后续 Cleanup task errors 仅为附加诊断，首错不按异步完成顺序选择。
- 固定 2×2 结果：scheduler success+Cleanup success 返回 Cleanup success count；success+failure 返回 Cleanup first-task error；failure+success 返回 Cleanup success count；failure+failure 返回 Cleanup first-task error。scheduler error 不覆盖 Cleanup 结果。
- `RecoveryRequired`、ACL 状态未知或恢复失败继续作为对应 Cleanup task error 对外返回，不得被 scheduler error 替换，不得声称清理完成。
- 内部日志保留 scheduler error code、脱敏 scheduler exception、首个及其余 Cleanup task errors、`taskId` 或受保护关联标识、完整遍历和 `RecoveryRequired` 标志；公开响应禁止 stack、内部类型名、SID、SDDL、恢复记录路径、凭据和令牌。
- 八份权威文档已同步该决定；下一步执行文档一致性验证并重新调用只读 `stage_director`。只有 verdict 为 `READY` 才继续 CP6 planner → coder → reviewer。
- 文档一致性验证通过：八份文件严格 UTF-8、Markdown 围栏平衡、尾随空白 0、禁用术语 0、旧歧义 0；8/8 文件含 D-028 与 cleanup first-task error 合同；`git diff --check` 退出码 0。
- 项目级只读 `stage_director` 重新核验后返回 `READY`，确认 D-028 已解除唯一生命周期结果阻塞；CP6 为紧邻执行项。根线程开始按 READY payload 严格串行调用 planner → coder → reviewer。
- 只读 planner 重新读取 D-028 和现有调用链后返回 `PROCEED`：Cleanup 按 `StartedTimestamp`、`Id.Value` 升序串行完整遍历；`StopAsync` 最终结果完全等于 Cleanup 结果；scheduler error 只进入受保护内部日志。
- 唯一 coder 在任何代码写入前发现新的精确合同缺口：现有文件没有 administrative Cleanup 的 `RemoveLockAsync` 抛异常及 ACE 已移除但 `Completed` 状态记录失败所需的内部 `Error.Code`/message。现有相邻字符串仅属于 activation 或 expiration，不能静默复用或按格式推导。
- coder 已暂停且未修改产品代码或测试；focused/build/full tests 未运行，CP6.2/CP6.3 未开始。解除条件为用户明确确认上述两个内部错误的精确 code 与 message；不得再把 D-028 scheduler/Cleanup 优先级作为阻塞。
- 用户批准精确合同：`RemoveLockAsync` 抛异常使用 `lock_task.administrative_cleanup.exception` / `The administrative cleanup ended without a confirmed result.`；ACE 已移除但 `Completed` 状态记录失败使用 `lock_task.administrative_cleanup.state_update_failed` / `The lock was removed but its completed state could not be recorded.`；两者均为 `UnrecoverableError -> RecoveryRequired`。
- 八份权威文档已同步上述 code、message、category、状态和禁止静默复用 activation/expiration 专用错误规则。下一步完成文档一致性验证并重新调用项目级只读 `stage_director`。
- 文档一致性验证通过：八份文件严格 UTF-8、Markdown 围栏平衡、尾随空白 0、禁用术语 0；8/8 文件同时包含两个精确错误；`git diff --check` 退出码 0。
- 项目级只读 `stage_director` 返回 `READY`，确认 D-028 与两个新增错误合同均已解除 CP6 阻塞；根线程按新 payload 重新串行执行 planner → coder → reviewer。
- 当前机器 `AGREELIN` 未执行真实注销、关机、重启、服务、LocalSystem、UAC、ProgramData ACL、签名、SACL、审计或系统配置操作。CP7 未开始。

## 2026-07-20 — 阶段 4 checkpoint 6 实现、恢复审计与最终验证

- `stage_director` 在 D-028 与两个 administrative Cleanup 精确错误同步后返回 `READY`；只读 planner 返回 `PROCEED`。根线程严格串行调用唯一 coder 和 reviewer。
- Core 新增 `ProcessAdministrativeCleanupAsync`：只处理 `Active`、`UnlockFailed`，按 `StartedTimestamp`、`Id.Value` 稳定升序串行完整遍历；每任务原子取得 `AdministrativeCleanup` 所有权；返回稳定顺序中的首个 Cleanup error，后续错误不停止遍历。
- Broker 新增 `BrokerLifecycleController` 与 `IBrokerSessionEndingSource`：scheduler 取消并等待后无条件 Cleanup；重复/并发 Stop 共享唯一 task；固定 2×2 结果完全等于 Cleanup 结果，scheduler error 只保留受保护诊断。
- 两个内部错误逐字实现并测试：`lock_task.administrative_cleanup.exception` 与 `lock_task.administrative_cleanup.state_update_failed`；两者均为 `UnrecoverableError -> RecoveryRequired`。
- IPC 断线、ServerHello 后断开和 CreateLock 响应写失败均不触发生命周期 Cleanup、不重复应用 ACL；成功副作用保持 `Active` / `Applied`，直到明确 Cleanup。
- 恢复故障注入证明 Prepare、MarkApplied、MarkCleanupPending、精确移除和 Delete 边界保留最后有效记录与恢复责任；未实现 CP7 恢复扫描或系统服务。
- 三份既有测试文件曾被全零覆盖。恢复严格使用 Codex session 非截断原文、成功 apply_patch 时间线及 Portable PDB document hash：RecoveryTransaction `322ee3865e8dfc9627f46b3f6c4695dbc430c77e60087413bea8e9bf7f7d491f`、Windows tests `48d3baa31ba2a79e6b99a786a8c9d70fc7a1efad37a9c5c5e0625d15a68965ce`、Pipe 在重放既有 patch 后 `63e5e3eddbafd82b59d202efaa2553a3060bf62be8e620dfc3e0da044890df9b`；再精确重放 CP6.3 patch。恢复后全部源码无 NUL，测试方法无重复。
- reviewer 首轮 `FAIL`：`LockTaskScheduler` 记录完整 Exception，且 `BrokerCompositionRoot` 使用三个 `NullLogger`。第 1/6 修复轮仅关闭两个 `HIGH`：scheduler 只记录固定 code 与关闭 category；组合根强制接收 `ILoggerFactory` 并注入 coordinator、scheduler、lifecycle logger。真实 scheduler 与组合根日志测试证明 exception 参数为 null、诊断可观察且无敏感文本。
- reviewer 复查最终 `PASS`，无 `BLOCKER`、`HIGH` 或新增非阻断问题。

### CP6 最终验证

- focused：Core lifecycle/state machine 40/40；App lifecycle/boundary 18/18；App disconnect/recovery 35/35；App 合并 53/53；Windows lifecycle/recovery 15/15，全部 0 failed、0 skipped。
- reviewer HIGH 修复 focused：Core scheduler/lifecycle 18/18；App lifecycle/process boundary 15/15。
- 根线程独立执行 `dotnet restore`：通过。
- 根线程独立执行 `dotnet build -c Release --no-restore`：通过，0 warning、0 error。
- 根线程独立执行 `dotnet test -c Release --no-restore -m:1`：Core 153/153、App 267/267、Windows 81/81，总计 501/501，0 failed、0 skipped。
- 根线程独立执行 `dotnet format --verify-no-changes`：通过；`git diff --check`：退出码 0，仅上级根 `README.md` 既有 LF/CRLF 提示。
- 扫描：190 个 `.cs`/`.csproj` 文件 NUL 0；CP7+、阶段 5、阶段 6实现能力 0，唯一宽泛命中为 CP4 已有 Replay Registry 服务 SID `FolderSessionLockRecovery`。
- 清理：`%TEMP%\FolderSessionLock.Tests` 目录/文件 0；工作区 `.fslr`/`.fsrr`/`.tmp-*`/`.bak` 0；TestResults 0；testhost/vstest/App/Broker 进程 0。
- 当前机器 `AGREELIN` 未执行真实注销、关机、重启、服务、LocalSystem、UAC、ProgramData/ProgramFiles ACL、签名、SACL、审计或系统配置操作。CP7 尚未开始；阶段 4 仍因 `FSL-STAGE4-VM` 特权验证和 D-026 证据缺失而不能完成。

## 2026-07-20 — 阶段 4 checkpoint 7 启动门

- 项目级只读 `stage_director` 核验 CP6 实现、reviewer `PASS`、根线程 501/501 独立复验、源码恢复证据和清理结果后返回 `READY`。
- 紧邻项为 CP7：恢复 ACL 组合、持续句柄和漂移停止；必须移除 `DirectoryAclEditor.LastUnrecoveredOperation` 编辑器级共享恢复事实源，并证明双任务交错无串扰。
- `FSL-STAGE4-VM` 特权验证与 D-026 证据仍缺失，阶段 4 不得完成或进入阶段 5；该环境门不阻止 `AGREELIN` 上执行 CP7 设计、代码、单元测试、批准临时目录测试和静态审查。
- 阶段 6 审计仍未获批准；CP7 禁止 Audit File System、SACL 和 Security 日志。根线程开始严格串行执行 planner → coder → reviewer。

## 2026-07-20 — 阶段 4 checkpoint 7 实现、修复与最终验证

- 只读 planner 返回 `PROCEED`，确认 CP7 合同完整：删除编辑器级共享恢复事实源，每次 ACL 调用显式传递独立 operation；恢复清理必须验证目录身份、ACE 元组、fingerprint、baseline/postApply digest、记录状态与调用模式，并把读取、定位、移除和后置验证绑定同一持续句柄。
- 唯一 coder 删除 `DirectoryAclEditor.LastUnrecoveredOperation`；`ApplyPreparedDenyAce` 与 `AddDenyAce` 通过 `out DirectoryAclOperation?` 返回本次调用证据；`WindowsFolderLockService` 不再手工构造写后恢复证据。
- `DirectoryAclEditor.RemoveDenyAce` 写前验证 handle、ACE 元组、fingerprint、baseline/postApply digest 与 0/1/>1 匹配数量；移除后通过同一句柄验证目标 ACE 为 0 且 digest 恢复 baseline。不一致时不写 DACL。
- Broker 新增内部单记录 `RecoveryRecordAclCleanup.Execute(Guid)`：从 `FileRecoveryRecordStore.Read` 开始，完成记录、路径、身份、SID/session、ACE 与摘要验证，前后执行 `VerifyCurrentPathMapping`，使用 `CleanupPending → 精确移除 → 后验 → Delete` 顺序。未实现记录扫描、CLI、服务、后台循环或公开恢复入口。
- TOCTOU 测试证明路径替换后 ACL 只作用原持续句柄对象，替换对象 ACL 不变；后置路径映射失败保留恢复记录。
- reviewer 首轮 verdict 为 `FAIL`，发现 1 个 `HIGH`：有效 `Applied` 记录遇到 ACL 漂移时直接返回 `FSL_E_ACL_STATE_MISMATCH`，未持久化 `CleanupFailed`。
- 第 1/6 修复轮仅关闭该 `HIGH`：新增 `FailValidatedDrift`；存在合法 post-apply evidence 的记录遇到路径、身份、ACE 数量、fingerprint、baseline 或 postApply 漂移时，原子写入 `CleanupFailed`，`CleanupAttemptCount + 1`，`LastErrorCode`/`LastErrorMessage` 使用稳定原始错误码，然后返回原始清理错误。`Prepared` 因 postApply 为 null 保持原文件不变。
- 回归测试真实添加无关 ACE，并证明返回 `FSL_E_ACL_STATE_MISMATCH`、完整 ACL snapshot 不变、应用 ACE 仍存在、记录未删除，以及 `Applied/count=0/null errors` 持久化为 `CleanupFailed/count=1/FSL_E_ACL_STATE_MISMATCH`。
- reviewer 复查最终 `PASS`，无 `BLOCKER` 或 `HIGH`。保留 1 个非阻断 `MEDIUM`：`CleanupPending` 在 attempt count 已达 1,000,000 的极端中断状态下尚未归一为 `CleanupFailed`；该路径不修改 ACL、不删除记录、不误报完成。

### CP7 最终验证

- 根线程独立执行修复 focused tests：App `RecoveryAclCleanup` 6/6、Windows `DirectoryAclEditorTests` 9/9，总计 15/15，0 failed、0 skipped。
- 根线程独立执行 `dotnet restore`：通过。
- 根线程独立执行 `dotnet build -c Release --no-restore`：通过，0 warning、0 error。
- 根线程独立执行 `dotnet test -c Release --no-restore -m:1`：Core 153/153、App 273/273、Windows 83/83，总计 509/509，0 failed、0 skipped。
- 根线程独立执行 `dotnet format --verify-no-changes` 与 `git diff --check -- .`：均退出码 0。
- 扫描：191 个 `.cs`/`.csproj` 文件 NUL 0；skip markers 0；`LastUnrecoveredOperation` 0；CP8+、服务、SACL、审计与产品恢复扫描越界 0。
- 清理：`%TEMP%\FolderSessionLock.Tests` 目录/文件 0；工作区 `.fslr`/`.fsrr`/`.tmp-*`/`.bak` 0；TestResults 0；testhost/vstest/App/Broker 进程 0。
- 当前机器 `AGREELIN` 未执行服务、LocalSystem、UAC、注销、重启、ProgramData/ProgramFiles ACL、签名、SACL、Audit File System、Security 日志或系统配置操作。阶段 4 仍因 `FSL-STAGE4-VM` 特权验证与 D-026 证据缺失而不能完成；CP8 必须由下一次 `stage_director` 结论决定。

## 2026-07-20 — 阶段 4 checkpoint 8 planner 合同阻塞

- CP7 完成后，项目级只读 `stage_director` 返回 `READY`，确认紧邻项为 CP8：固定恢复模式参数与服务抽象；当前机器 `AGREELIN` 只允许代码、非特权测试和静态审查。
- 根线程读取完整 CP8 payload 后调用只读 planner。planner 核验 `Program.cs`、`BrokerConsentOptions`、`BrokerCompositionRoot`、`FileRecoveryRecordStore`、`RecoveryRecordAclCleanup`、D-023–D-026 与 CP7 调用链后返回 `BLOCKED`；未调用 coder，未修改 CP8 产品代码或测试。
- 已确认现有事实：consent 入口只接受固定 8 参数；`recovery-service` 与 `recovery-once` 尚无参数类型、运行器或服务抽象；生产恢复路径固定为 `%ProgramData%\FolderSessionLock\Recovery\Records`；CP7 只提供 `RecoveryRecordAclCleanup.Execute(Guid)` 单记录清理入口。
- 新阻塞 1：缺少 `recovery-once` 精确结构化退出码，以及参数错误、无记录、全部成功、部分失败、全部失败、恢复目录不可访问、owner/DACL 失败、取消和内部异常的唯一映射。
- 新阻塞 2：缺少固定恢复目录枚举合同，包括纳入文件、非法文件名、扩展名大小写、额外后缀、`.bak`/`.tmp-*`、稳定排序、I/O/权限错误和记录数量或资源上限。
- 新阻塞 3：缺少多记录恢复结果合同，包括单条失败后是否继续、对外主错误、成功/失败/阻断计数、受保护诊断，以及“扫描完成”与“恢复成功”的区别。
- 新阻塞 4：缺少 `recovery-service` 生命周期合同，包括扫描后退出或持续托管、启动、停止、取消、readiness 和 recovery-blocking 的可观察状态。
- 新阻塞 5：缺少 D-023 owner/DACL 只读复核合同，包括接口职责、稳定错误码、是否先于记录枚举，以及 CP8 是否包含非写入式读取实现。
- 上述内容无法从现有权威文件和代码唯一提取；按禁止猜测规则停止 CP8。用户决定同步八份权威文档并重新通过 `stage_director` 与 planner 前，不得调用 coder。
- 本轮未创建、修改、启动、停止或删除 Windows 服务；未使用 LocalSystem、UAC、注销、重启；未修改 ProgramData/ProgramFiles ACL、签名、SACL、Audit File System、Security 日志或系统配置。

## 2026-07-21 — 恢复执行、服务生命周期与 D-023 最终合同

- 用户提供最终合同，明确覆盖并解除上一轮 CP8 planner 列出的五组缺失项。合同标题使用“当前 CP6”，正文分别规定“CP6 实现边界”和“CP8 实现边界”；权威文档保留该原文事实，项目现行 checkpoint 编排交由下一次 `stage_director` 核验，不静默改名。
- `recovery-once` 唯一退出码固定为 0 Success、2 InvalidArguments、10 ProtectedStorageSecurityFailure、11 RecoveryEnumerationFailure、12 RecoveryRecordLimitExceeded、13 RecoveryBlocked、14 Cancelled、15 InternalFailure；优先级按上述顺序后接 Success。禁止直接返回 Win32 error、HRESULT、Exception.HResult、NTSTATUS 或记录级错误码。
- Records 执行顺序固定为 D-023 → 顶层完整枚举/分类 → 总条目 4096 与规范 `.fslr` 1024 上限 → `StringComparer.Ordinal` 稳定排序 → 才开始清理。禁止递归、跟随 reparse 或边枚举边修改 ACL。
- 规范活动文件为小写 Guid D `<recordId>.fslr`。合法同 id `.bak`/`.tmp-*` 只计 auxiliary；孤立构件分别使用 `FSL_E_RECOVERY_BACKUP_ORPHANED`、`FSL_E_RECOVERY_TEMP_ORPHANED`；其他非法构件使用 `FSL_E_RECOVERY_ARTIFACT_INVALID`。全部保留，不自动删除、重命名、提交或推断 ACL。
- 单记录失败后继续稳定遍历。`CleanupPending` 后进入 ACL 临界区，取消/Stop 不得强制中断。结果类别固定为 Cleaned、AlreadyClean、Failed、RecoveryRequired、Skipped；结构化摘要固定十二字段和两个计数不变量，blocking 规则以 D-022.10 为准。
- `recovery-service` 固定启动扫描一次后持续托管，不周期扫描。内部状态为 StartPending → Preflight → Scanning → Ready/RecoveryBlocked → Stopping → Stopped；Ready 与 RecoveryBlocked 均对应 SCM Running。Stop 先阻断新记录，ACL 临界区必须完成安全终态。
- readiness 固定 schema 1、`RecoveryReadinessState`、`RecoveryReadinessSnapshot`、publisher/reader 接口。snapshot 缺失或任一不安全条件均 blocking。CreateLock 在路径/ACL 前执行 gate；失败精确返回 `FSL_E_RECOVERY_BLOCKING` 固定公开错误对象。
- D-023 固定 `ProtectedPathKind`、request/result、`IProtectedPathSecurityVerifier`、二十步 handle-based 检查顺序、owner/DACL/ACE/继承策略和十四个 `FSL_E_PROTECTED_PATH_*` 错误码。ExpectedPath 只由组合根生成；禁止 AllowAll verifier。
- 用户合同所称 CP6 完成接口、状态机、orchestration、fail-closed、fake 与单元测试，不实现生产 Win32 owner/DACL 或系统 ACL 写入；生产 verifier 缺失时返回 `FSL_E_PROTECTED_PATH_POLICY_UNSUPPORTED`。合同所称 CP8 完成 Windows verifier、ACL 创建验证、service SID ACL 与 VM 真实安全矩阵。
- 八份权威文档已按职责同步：完整决定写入 D-022.10、D-023.1、D-024.1、D-024.2；需求、架构、安全、计划、任务、验收和本日志均引用精确值并删除“上述五组事项未定义”的阻塞状态。
- 文档同步阶段未修改产品代码或测试，未运行 .NET 构建/测试，未执行真实 ACL、服务安装/启动/停止、LocalSystem、UAC、注销、重启、ProgramData/ProgramFiles ACL、签名、SACL、Audit File System、Security 日志或系统配置。
- 文档一致性验证通过：strict UTF-8 8/8、Markdown 围栏平衡、尾随空白 0、禁用词 0；八份文件 8/8 均包含 `recovery-once`、4096、`RecoveryReadinessSnapshot`、`IProtectedPathSecurityVerifier`、`FSL_E_RECOVERY_BLOCKING`；`git diff --check -- .` 退出码 0。
- 下一步只调用项目级只读 `stage_director`。若 READY，根线程读取完整 payload 后严格串行执行 planner → coder → reviewer；若 BLOCKED，只报告本合同未覆盖的新证据。

## 2026-07-21 — 阶段 4 checkpoint 8 实现与文件安全合同新阻塞

- 文档同步后，项目级只读 `stage_director` 返回 `READY`，确认合同所称 CP6/CP8 边界已纳入项目现行 checkpoint8。只读 planner 返回 `PROCEED`，根线程调用唯一 coder。
- coder 实现严格三模式参数、24 个错误码、D-023 verifier、Records 顶层枚举/分类/4096/1024/Ordinal、批量恢复与十二字段摘要、readiness/CreateLock gate、recovery-once 退出码和一次扫描服务状态机。生产 Recovery/Replay 正常启动路径不再创建或写 ACL；生产 readiness 不可用时 fail closed。
- 根线程首轮独立验证：Core 153/153、App 300/300、Windows 102/102，总计 555/555，0 failed、0 skipped；Release build 0 warning/0 error；format、diff、NUL、TEMP、恢复构件、TestResults、进程、服务和越界检查通过。
- reviewer 首轮 `FAIL` 报告 4 个 `HIGH`：恢复扫描删除配对 `.bak`；Stop 发布/报告/调用方取消可跳过 ACL 临界区等待；D-023 policy 接受冲突 Deny/未知 ACE；单记录读写未使用持续安全句柄，存在 reparse/替换 TOCTOU。
- 第 1/6 修复轮只处理四个 `HIGH`：扫描成功仅删除规范 `.fslr`；Stop 在 finally 不可取消等待；policy 保留并拒绝 Deny/callback/object/未知 ACE；记录枚举和读取增加 OPEN_REPARSE_POINT、FILE_ID、owner/DACL 与 identity 复核。两个 reviewer `MEDIUM` 保持未改。
- 根线程修复后独立验证：Core focused 10/10、App Recovery focused 226/226、Windows verifier/policy 20/20，总计 256/256；完整 Core 153/153、App 309/309、Windows 103/103，总计 565/565，0 failed、0 skipped；Release build 0 warning/0 error；format、diff及全部清理检查通过。
- reviewer 第一次复查确认 `.bak`、Stop、ACE 三个原 `HIGH` 已关闭，但返回 `FAIL`，保留 2 个 `HIGH`。
- `HIGH` 1：`VerifyTrackedIdentity` 关闭验证句柄后，`DeleteCanonicalRecord` 仍按路径 `File.Delete`，`Commit` 仍按路径 `File.Replace`；验证成功到路径修改之间可替换目标，更新/删除尚未原子绑定已验证记录句柄。
- `HIGH` 2：reader 对每条记录强制 SYSTEM owner，而 writer 使用普通 FileStream/Move/Replace 且不设置 SYSTEM owner；D-023 只固定目录 owner，没有定义 `.fslr`、`.tmp-*`、`.bak` 文件 owner。合法 consent Broker 记录可能被恢复 reader 拒绝。
- 第二项需要用户最终确认文件级 owner/DACL 合同，包括三类文件允许 owner、consent writer 设置/继承行为、提交前后验证顺序与稳定错误码。该决定前不得自行扩大 owner 集合或进入第 2/6 修复轮。
- 本轮未执行 SCM、LocalSystem、UAC、注销、重启、ProgramFiles/ProgramData ACL 写入、service SID 真实 ACL、签名、SACL、Audit File System、Security 日志或 VM 场景；当前机器仍为 `AGREELIN`。

## 2026-07-21 — CP8 恢复记录文件级安全与句柄绑定最终批准

- 用户最终确认三类文件：CanonicalRecord `<recordId>.fslr`、TemporaryRecord `<recordId>.tmp-<tempId>`、BackupRecord `<recordId>.bak`，固定于 Records，Guid 均小写非空 D 格式，调用方不得覆盖路径。
- 三类文件唯一 owner 均为 SYSTEM `S-1-5-18`。文件 DACL 必须 present/non-null/protected、ACL revision 2、无继承，精确三个按序显式 Allow ACE：SYSTEM、Administrators、固定服务 SID，mask `0x001F01FF`、AceFlags 0；禁止额外、Deny、object、callback、conditional 或未知 ACE。
- 固定 `RecoveryRecordFileKind`、`RecoveryRecordFileIdentity`、`RecoveryRecordFileSecuritySnapshot`、`IRecoveryRecordFileSecurity`；接口只接受 SafeFileHandle。ApplyAndVerify 只用于未提交 temp，canonical/bak 只 Verify，reader 不修复。
- consent writer 必须同 tempHandle 设置 SYSTEM owner与精确 DACL；owner 非 SYSTEM 时只临时启用 `SeRestorePrivilege`，finally 恢复，禁止 SeTakeOwnershipPrivilege。revert failure 停止后续记录写入、不提交 temp、不继续 CreateLock。
- 所有 writer 持有 `Global\FolderSessionLock.RecoveryStore.v1` 受保护 mutex；保持 Records directory、temp 与 old canonical handles。temp 使用 CREATE_NEW、ShareMode0、OPEN_REPARSE_POINT、WRITE_THROUGH；payload 前完成 identity/links/final path/owner/DACL。
- 当时批准的新建与更新合同为 tempHandle FileRenameInfoEx 相对 Records directory 简单叶名；该 rename API 条款随后因 Windows 11 实证失败并由用户批准的 `NtSetInformationFile(FileRenameInformationEx = 65)` 勘误替换。持续 handles、POSIX replace、专用错误、无 fallback 与 v1 不创建 `.bak` 保持不变。
- post-commit 保持新 canonical handle，验证 temp identity、links、owner/DACL、完整 payload、唯一目录映射与 Records identity；失败统一 `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`、UnrecoverableError、任务/Replay RecoveryRequired，保留当前文件。
- canonical 与 temp 删除只通过已验证同一 handle FileDispositionInfoEx DELETE|POSIX；禁止 File.Delete/DeleteFileW 和关闭句柄后按路径删除。temp cleanup 失败覆盖原错误并 blocking。
- 配对 `.bak`/`.tmp-*` 只有文件名、普通文件、non-reparse、links=1、SYSTEM owner、精确 DACL、同 Records 目录全部通过才 auxiliary；安全不匹配使用 `FSL_E_RECOVERY_ARTIFACT_SECURITY_INVALID` 并 blocking。
- D-022.11 记录 21 个文件级稳定错误码、固定 messages、retryable=false、field=null、错误优先级、禁止 API 与测试矩阵。底层 Win32/HRESULT、路径、SID、DACL/SDDL、FILE_ID 只进入受保护日志。
- 八份权威文档已同步，旧的 ReplaceFileW/File.Replace/路径删除、v1 `.bak` 创建、父目录继承和 reader/writer owner 不一致规则已改写。下一步完成文档一致性验证并调用只读 `stage_director`；READY 后进入第 2/6 修复轮，只修 reviewer 剩余两个 `HIGH`。
- 文档同步期间未修改产品代码或测试，未运行 .NET build/tests，未执行生产 ProgramData ACL、SCM、LocalSystem、UAC、注销、重启、签名、SACL、审计或 VM 操作。
- 文档一致性验证通过：strict UTF-8 8/8、Markdown 围栏平衡、尾随空白 0、禁用词 0、active conflict 0；八份文件 8/8 均包含 `0x001F01FF`、`IRecoveryRecordFileSecurity`、`Global\FolderSessionLock.RecoveryStore.v1`、FileRenameInfoEx、FileDispositionInfoEx、`FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`；`git diff --check -- .` 退出码 0。
- 下一步调用项目级只读 `stage_director`。若 READY，进入第 2/6 coder 修复轮且只修复 reviewer 剩余两个 `HIGH`；若 BLOCKED，只报告本合同未覆盖的新证据。

## 2026-07-21 — CP8 第 2/6 修复轮 FileRenameInfoEx 阻塞

- 项目级只读 `stage_director` 返回 `READY`；只读 planner 返回 `PROCEED`。唯一 coder 进入第 2/6 修复轮，只处理句柄 mutation TOCTOU 与 writer/reader 文件 owner/DACL 两个 `HIGH`。
- 已实现并编译：114 个协议错误码集合、D-022.11 四个公共文件安全类型、21 错误固定 message factory、永久写安全状态与 CreateLock gate、Windows file security/privilege、受保护 global mutex、directory-relative file platform、全句柄 FileRecoveryRecordStore，以及 transaction/cleanup/batch async 调用链。
- 旧 `_recoveryIdentities`、File.Replace/File.Move/File.Delete、v1 `.bak` writer 和路径 temp cleanup 已从产品 recovery store 设计中移除；98 个旧测试 API 编译错误已迁移清零。Broker 与 App.Tests 编译通过，solution Release build 0 warning/0 error，`git diff --check -- .` 通过。
- focused 恢复/pipe 矩阵实际结果：256 total、231 passed、25 failed。首个失败为 `WriteNewAsync` 的 `SetFileInformationByHandle(FileRenameInfoEx)` 返回 Win32 87，映射 `FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED`；后续 update、cleanup、pipe 失败主要为该失败的连锁结果。
- 环境证据：Windows 11 `10.0.22631`，TEMP 位于本机 NTFS C:。调用使用持续 Records 目录句柄、RootDirectory、简单叶名称和 ReplaceIfExists=false。
- coder 先修正了确定存在的 native last-error 捕获时机：在 `SetFileInformationByHandle` 返回后、`FreeHGlobal` 前立即读取。Win32 87 仍保持。
- 随后两次依据 Microsoft FILE_RENAME_INFO 文档调整 Records 目录句柄权限：加入 FILE_TRAVERSE；严格仅 FILE_TRAVERSE|FILE_READ_ATTRIBUTES。两次均仍返回 Win32 87；未证明改动和临时诊断输出已撤销。
- 同一问题连续两次修复失败，触发项目强制停止条件。coder 不再试错、不加入 File.Move/File.Replace 路径 fallback、不降低 D-022.11 标准；未运行全量 tests/format，未调用 reviewer。
- 失败测试留下 18 个空 `%TEMP%\FolderSessionLock.Tests` 子目录。根线程解析并验证所有目标严格位于批准 TEMP 根且为空后逐个删除；最终 TEMP 目录/文件 0。工作区恢复构件 0、TestResults 0、testhost/vstest/App/Broker 进程 0、`FolderSessionLockRecovery` 服务不存在。
- 新解除条件：取得 Windows 11 22631 上成功的 `FILE_RENAME_INFO_EX` 精确 buffer layout、flags、RootDirectory 与 handle access 实证，或由用户明确批准新的同句柄原子提交合同。此前 owner/DACL/privilege/handle禁止路径规则均已确定，不得重复询问。
- 本轮未执行生产 ProgramData ACL、SCM、LocalSystem、UAC、注销、重启、签名、SACL、审计或 VM 场景。

## 2026-07-21 — CP8 FileRenameInfoEx 隔离 ABI 证明

- 在批准的 `%TEMP%\FolderSessionLock.Tests\<Guid>` 内运行两组独立句柄 rename 探针；未修改产品代码、恢复记录合同、ACL、服务或系统配置。
- Microsoft SDK `FILE_RENAME_INFO` 定义与本机 ABI 一致：x64 `FileName` offset 20、结构大小 24。使用 `RootDirectory +` 相对叶名时，`20 + FileNameLength`、`24 + FileNameLength`、`24 + FileNameLength + NUL` 三种 buffer 均返回 Win32 87；buffer 长度和 NUL 不是解除条件。
- 定向矩阵结果：`FileRenameInfoEx` class 22 相对目标返回 87；`FileRenameInfo` class 3 相对目标返回 87；class 22 使用 `RootDirectory = NULL +` 绝对目标成功；class 3 使用同一绝对目标方式成功。
- 该结果排除 FileRenameInfoEx 整体不受支持、source handle 缺少 DELETE、目录句柄访问不足和结构长度四项原因；当前 Windows 11 `10.0.22631` 的阻塞精确收敛为 `SetFileInformationByHandle` 的 `RootDirectory +` 相对叶名组合。
- 两组探针均在 `finally` 删除测试根；最终 `%TEMP%\FolderSessionLock.Tests` 残留 0。未使用结果为产品引入绝对路径 mutation，因为 D-022.11 明确禁止该路径。
- 继续编码前仍需：Windows 11 22631 上该相对目录句柄组合的成功实证，或用户批准替代同句柄原子提交合同。不得继续试错、改用绝对路径或增加路径 fallback。

## 2026-07-21 — CP8 NtSetInformationFile 相对句柄原子 rename 实证

- Microsoft WDK 文档确认：user mode 使用 `NtSetInformationFile`；`FileRenameInformation = 10`、`FileRenameInformationEx = 65`；输入为 `FILE_RENAME_INFORMATION`；`RootDirectory` 支持目标目录句柄与简单相对名称；source handle 必须包含 DELETE；buffer 长度至少为结构大小加 `FileNameLength`。
- 在 Windows 11 `10.0.22631`、本机 NTFS、`%TEMP%\FolderSessionLock.Tests\<Guid>` 内使用 directory handle 的 `FILE_TRAVERSE | FILE_READ_ATTRIBUTES` 权限运行原生探针。
- `NtSetInformationFile`、class 65、flags 0、相对简单叶名的新建返回 `STATUS_SUCCESS`；source 名称消失、target 名称出现，rename 前的 temp handle 继续读取 `new`。
- class 10 控制组使用同一 `RootDirectory +` 相对叶名同样返回 `STATUS_SUCCESS`。
- `NtSetInformationFile`、class 65、flags `FILE_RENAME_REPLACE_IF_EXISTS | FILE_RENAME_POSIX_SEMANTICS = 0x00000003` 的更新返回 `STATUS_SUCCESS`；old canonical handle 在调用期间保持打开，完成后 temp handle 读取 `new`，old canonical handle 继续读取 `old`。
- 首次探针在成功 rename 后尝试按目标路径读取，被 temp handle 的 `ShareMode = 0` 正确拒绝；该诊断错误未作为 API 结果。修正后的探针仅通过 retained handles 回读并完整成功。
- 两次探针均在 `finally` 清理；最终 TEMP 残留 0。未修改产品代码、D-022.11、ACL、服务或系统配置。
- 该实证提供可执行的最小替代路径；在随后用户批准前，D-022.11 仍逐字要求 `SetFileInformationByHandle(FileRenameInfoEx)`，因此当时保持 `BLOCKED`。本条已由后续“用户批准 D-022.11 rename API 勘误”解除；FileDispositionInfoEx 删除合同、路径禁止、retained handles 与 post-commit 验证均未改变。

## 2026-07-21 — 用户批准 D-022.11 rename API 勘误

- 用户明确回复批准已实证的 `NtSetInformationFile(FileRenameInformationEx = 65)` 替代合同，解除 CP8 第 2/6 修复轮的 rename API 设计阻塞。
- 新建唯一合同：user-mode `NtSetInformationFile`、`FILE_RENAME_INFORMATION`、information class 65、flags 0、`RootDirectory=recordsDirectoryHandle`、相对简单 canonical 叶名；buffer 长度至少为结构大小加 UTF-16 `FileNameLength`，tempHandle 包含 DELETE。
- 更新唯一合同：同一 API/class/structure，flags 精确为 `FILE_RENAME_REPLACE_IF_EXISTS | FILE_RENAME_POSIX_SEMANTICS = 0x00000003`；old canonical、temp、Records directory handles 全程保持打开。
- production rename 禁止 `FileRenameInformation = 10`、`SetFileInformationByHandle(FileRenameInfoEx = 22)`、`FileRenameInfo = 3`、绝对目标路径与任何 fallback。
- 既有 `FSL_E_RECOVERY_FILE_ALREADY_EXISTS`、`FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_UNSUPPORTED`、`FSL_E_RECOVERY_FILE_ATOMIC_REPLACE_FAILED` 公开合同保持不变；原始 NTSTATUS/DOS code 只进入受保护日志。
- FileDispositionInfoEx canonical/temp 删除、绝对路径禁止、路径 fallback 禁止、post-commit、owner/DACL、privilege、mutex、auxiliary security 与恢复责任规则全部保持不变。
- 下一步先同步八份权威文档并运行一致性检查，再调用只读 `stage_director`；不得直接启动 coder。
- 八份权威文档已完成 class 65 勘误同步。实际一致性结果：strict UTF-8 8/8、`FileRenameInformationEx = 65` 8/8、Markdown 围栏平衡、尾随空白 0、禁用词 0、active conflict 0、`git diff --check -- .` 退出码 0。
- 清理复核：`%TEMP%\FolderSessionLock.Tests` 残留 0，工作区 `.fslr`/`.fsrr`/`.bak`/`.tmp-*` 恢复构件 0。
- 下一步调用项目级只读 `stage_director`。仅在 `READY` 后恢复第 2/6 修复轮并严格串行 planner → coder → reviewer。

## 2026-07-21 — CP8 class 65 实现与 FileDispositionInfoEx 关闭时序阻塞

- `stage_director` 在 class 65 文档同步后返回 `READY`；只读 planner 返回 `PROCEED`。唯一 coder 恢复第 2/6 修复轮，只处理两个 reviewer `HIGH`。
- production `Rename` 已迁移为 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)`；`FILE_RENAME_INFORMATION` 使用运行时 Marshal layout，buffer 为结构大小加 UTF-16 bytes，RootDirectory 为 retained Records handle，新建 flags 0、更新 flags `0x00000003`。NTSTATUS collision/unsupported/failure 映射保持既有公开合同。
- 新增固定 platform tests 展开为 9 cases，实际 9/9 passed、0 failed、0 skipped。Release build 0 warning、0 error；静态扫描确认 production rename 无 class 10/class 22/class 3、绝对目标或路径 fallback，SetFileInformationByHandle 仅保留 FileDispositionInfoEx delete。
- `GetLeafIdentity` 已从按名称 OpenExisting 改为 retained directory handle 的 `GetFileInformationByHandleEx` class `0x14`/`0x13` 与 `FILE_ID_EXTD_DIR_INFO` 枚举；ShareMode 0 下 post-commit FILE_ID mapping 通过。
- `FileRecoveryRecordStoreTests` 实际 13 total、12 passed、1 failed、0 skipped。唯一失败为 DeleteAsync：FileDispositionInfoEx=21、flags=3 返回成功，但 canonical delete handle 仍打开时，File.Exists 与 directory enumeration 仍看到名称；handle 关闭后名称才消失。
- Microsoft WDK `FILE_DISPOSITION_INFORMATION_EX` 明确：设置 POSIX semantics 时，link 在 POSIX delete handle 关闭后从 visible namespace 移除；其他已有 handles 仍可访问数据直到最后 handle 关闭。
- 当前 D-022.11 顺序为 disposition → directory handle 确认名称消失 → 关闭 canonical handle，与 WDK 平台语义冲突。根线程未批准 coder 调整顺序；coder 已撤销未授权的测试时序试验，产品 Delete 未修改。
- 当前验证：platform 9/9；store 12/13；build 0 warning/error；`git diff --check -- .` 退出码 0；TEMP 目录/文件残留 0。未运行完整 tests/format/reviewer。
- 所需用户决定：是否批准删除顺序改为“同一已验证 canonical handle 执行 FileDispositionInfoEx DELETE|POSIX → 关闭该 handle → retained Records directory handle 确认名称消失 → 复核 directory identity”。名称仍存在、枚举失败或无法证明关闭/删除时进入 RecoveryRequired；禁止路径重试、按名称删除或删除 replacement。

## 2026-07-21 — 用户批准 canonical POSIX delete 关闭顺序勘误

- 用户明确批准 D-022.11 canonical 删除顺序改为：同一已验证 canonical handle 执行 FileDispositionInfoEx `DELETE | POSIX` → 关闭该 handle → retained Records directory handle 确认 canonical 叶名从 visible namespace 消失 → 复核 Records directory identity。
- disposition 前仍必须在同一 canonical handle 完成 identity、owner/DACL、container/payload、recordId/taskId、允许终态和提交前 leaf mapping 复核；mutation 仍绑定同一已验证对象。
- 名称仍存在、目录枚举失败、目录 identity 变化或无法证明关闭/删除时进入 `RecoveryRequired`；不得报告删除成功。
- 禁止路径重试、按名称删除、重新打开后删除、File.Delete/DeleteFileW 或删除 replacement。
- FileDispositionInfoEx API/flags、class 65 rename、公开错误、owner/DACL、privilege、mutex、post-commit、temp cleanup、auxiliary security 与其他 D-022.11 规则全部保持不变。
- 下一步同步八份权威文档、运行一致性与残留检查，再调用只读 `stage_director`；不得直接恢复 coder。
- 八份权威文档已完成 canonical 删除顺序勘误同步。实际检查：strict UTF-8 8/8、delete order marker 8/8、Markdown 围栏平衡、尾随空白 0、禁用词 0、active conflict 0、`git diff --check -- .` 退出码 0、TEMP 残留 0。
- 下一步调用项目级只读 `stage_director`；仅在 `READY` 后恢复 coder。

## 2026-07-21 — CP8 第 2–4/6 修复轮完成与 reviewer PASS

- canonical 删除顺序文档同步后，项目级只读 `stage_director` 返回 `READY`，只读 planner 返回 `PROCEED`；根线程恢复第 2/6 修复轮。
- 第 2/6 修复轮完成 user-mode `NtSetInformationFile(FileRenameInformationEx = 65)`、retained Records directory FILE_ID 枚举、SYSTEM owner/精确 protected DACL writer/reader、持续 old/temp/canonical handles，以及 canonical `FileDispositionInfoEx DELETE|POSIX → close → retained directory leaf absence → directory identity`。路径型 move/replace/delete fallback 保持禁止。
- focused 初次发现三个直接回归：旧共享句柄测试在 `OpenExisting` 阶段失败、AlreadyClean 删除仍绑定旧 `Applied` record、canonical security 错误被折叠为 identity mismatch。修复后使用精确 delete failure injection、删除绑定已提交 `CleanupPending` record、canonical 保留 D-022.11 精确 security error；管道旧断言同步为 `FSL_E_RECOVERY_FILE_DELETE_FAILED`。
- reviewer 首次复查确认原 TOCTOU 与 owner/DACL 两个 `HIGH` 已关闭，但报告 temp handle 创建后取消/异常可绕过清理。第 3/6 修复轮从 `CreateTemporary` 成功起建立异常安全所有权，统一证明同 handle delete、关闭、retained directory 叶名消失和目录 identity；任一步无法证明均永久阻断写入并返回 `FSL_E_RECOVERY_TEMP_CLEANUP_FAILED`。
- reviewer 第二次复查确认 temp 生命周期 `HIGH` 已关闭，但报告 rename 已提交后取消/异常可绕过 post-commit failure。第 4/6 修复轮把 committed 后验证固定使用 `CancellationToken.None`，committed 状态任何异常统一返回 `FSL_E_RECOVERY_FILE_POST_COMMIT_VERIFICATION_FAILED`；canonical 保留，不执行 temp cleanup，transaction 保留磁盘 `Prepared` 恢复责任且不错误登记内存 registry。
- coder 曾尝试创建嵌套只读 review agent；根线程按项目编排规则立即禁止后续递归 agent 调用，该子任务已中断，未修改工作区，也未作为审查或阶段门证据。最终 reviewer 由根线程独立调用。
- reviewer 最终 `PASS`，无 `BLOCKER` 或 `HIGH`。句柄 TOCTOU、writer/reader owner/DACL、temp 生命周期和 post-commit 副作用分类四项均关闭。保留两个既有非阻断 `MEDIUM`：真实 CLI 参数错误未输出 `FSL_E_INVALID_ARGUMENTS`；wait-hint heartbeat 与顶层 `FSL_E_INTERNAL` 映射未实现。
- 根线程最终实际验证：`dotnet restore FolderSessionLock.sln` 退出 0；`dotnet build FolderSessionLock.sln -c Release --no-restore` 0 warning、0 error；`dotnet test FolderSessionLock.sln -c Release --no-restore -m:1` 为 Core 153/153、App 340/340、Windows 103/103，总计 596/596、failed 0、skipped 0；`dotnet format FolderSessionLock.sln --verify-no-changes --no-restore`、`git diff --check -- .` 均退出 0。
- 静态与清理检查：production recovery 禁止 API 匹配 0；rename 仅保留 class 65，`SetFileInformationByHandle` 仅用于 `FileDispositionInfoEx`；C# lone LF 文件 0；工作区恢复构件 0、TestResults 0、`%TEMP%\FolderSessionLock.Tests` 目录/文件 0、相关进程 0，`FolderSessionLockRecovery` 服务不存在。
- 当前机器仍为 `AGREELIN`。未执行 SCM、LocalSystem、UAC、注销、重启、ProgramData/ProgramFiles owner/DACL、真实 service SID ACL、签名、SACL、Audit File System、Security 日志或 `FSL-STAGE4-VM` 场景；不得据此标记阶段 4 完成。
- 下一步仅调用项目级只读 `stage_director`。若 `READY`，根线程立即读取并执行完整 `NEXT_STAGE_GOAL_PAYLOAD`；若 `BLOCKED`，只报告新的可验证阻塞。

## 2026-07-21 — CP8 状态文档同步阻塞解除

- CP8 最终 reviewer `PASS` 与 596/596 验证记录后，项目级只读 `stage_director` 返回 `BLOCKED`。唯一阻塞不是产品实现或测试，而是 `docs/REQUIREMENTS.md`、`docs/ARCHITECTURE.md`、`docs/SECURITY.md`、`PLAN.md` 与 `TASKS.md` 仍保留 platform 9/9、store 12/13 和“等待恢复 coder”的旧状态。
- 状态同步为：CP1–CP8 当前 `AGREELIN` 允许范围完成；CP8 reviewer 最终 `PASS`，无 `BLOCKER` 或 `HIGH`；最终 Core 153/153、App 340/340、Windows 103/103，总计 596/596，0 failed、0 skipped，Release build 0 warning、0 error，format、diff、静态扫描和清理通过。
- 紧邻未开始 checkpoint 唯一标明为 CP9“同账户 consent elevation；另一管理员账户凭据确定拒绝”。CP9 尚未开始，不预先实现。
- `FSL-STAGE4-VM` 的 SCM、LocalSystem、ProgramData/ProgramFiles ACL、真实 service SID、UAC、重启、签名和 D-026 证据仍未执行；本次状态同步不把 `AGREELIN` 证据扩张为 VM 通过，阶段 4仍不得完成。
- 下一步运行八份权威文档一致性与 `git diff --check -- .`，然后重新调用只读 `stage_director`。

## 2026-07-22 — CP9 D-029 consent elevation 与生产生命周期最终合同

- 已读取用户附件 `C:\Users\lingl\.codex\attachments\72a30c6b-5335-4765-bba4-1c9908072728\pasted-text.txt`。用户明确批准 `D-029：同账户 consent elevation 与 consent-broker 生产生命周期`，解除上一轮阶段门发现的四项新阻塞。
- 身份错误分层固定为 UI launcher、elevated bootstrap、connected Pipe handshake。`FSL_E_ACCOUNT_SID_MISMATCH` 只保留为 D-027 handshake诊断；bootstrap Account SID不同exit20。UI只把这两条跨账户路径映射为 `FSL_E_CROSS_ACCOUNT_ELEVATION_NOT_SUPPORTED`，不得转换Logon/Session/PID/identity/Pipe/unauthorized错误。
- UI在UAC前从自身process token取得Account SID、唯一Logon SID、Session ID，并取得PID与creation FILETIME。CLI精确增加 `--client-process-id`、`--client-process-creation-filetime`；SID不进入CLI。Broker在创建Pipe前重开UI process/token，重新读取身份并执行PID creation、Account、Session比较。
- bootstrap exit 21/22分别映射 `FSL_E_CLIENT_IDENTITY_UNAVAILABLE`/`FSL_E_CLIENT_PROCESS_MISMATCH`。只有bootstrap全部成功后才以可信UI Logon SID与Broker Account SID构建protected Pipe DACL并设置 `PIPE_REJECT_REMOTE_CLIENTS`。
- production Broker path只由 `SHGetKnownFolderPath(FOLDERID_ProgramFiles)` 解析固定安装文件；UAC前执行D-023 install directory、普通文件、non-reparse、final path、目录归属与identity验证。CP9不声称完成CP10 Authenticode。
- production launcher固定使用 `ShellExecuteExW(runas)`、`SW_HIDE`、`SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI | SEE_MASK_UNICODE`、专用参数encoder与非空process handle。ERROR_CANCELLED与其他launch failure分别映射新稳定错误。
- Broker连接等待15 seconds；UI Pipe/process race等待20 seconds。连接前timeout可TerminateProcess exit29并等待5 seconds；无法证明清理时 `FSL_E_BROKER_PROCESS_CLEANUP_FAILED` 覆盖connect timeout。Pipe连接后UI永远不得TerminateProcess。
- consent-broker exit code关闭集合固定为0、2、20–29，不改变recovery-once。应用failure response可以exit0；Cleanup failure优先27，response write failure且Cleanup成功为26；合法CommandResponse优先于后续process exit。
- 每个consent-broker只允许一个listener、连接、四帧握手和应用请求。CreateLock成功响应后Broker继续运行到Expiration Cleanup；UI断开不提前解除确定Active task，未知副作用进入RecoveryRequired。
- production composition必须包含D-029列出的Windows identity/path/ACL、recovery file/security/readiness、Replay、frame/protocol/execution、task/scheduler/lifecycle、logging与clock依赖；禁止AllowAll/fake identity/fake readiness/in-memory recovery/test cleanup hook/test path/debug Broker path，缺依赖fail closed exit28。
- 新增稳定错误：`FSL_E_BROKER_PATH_UNTRUSTED`、`FSL_E_ELEVATION_CANCELLED`、`FSL_E_ELEVATION_LAUNCH_FAILED`、`FSL_E_BROKER_LAUNCH_CONTRACT_INVALID`、`FSL_E_PIPE_INITIALIZATION_FAILED`、`FSL_E_BROKER_CONNECT_TIMEOUT`、`FSL_E_BROKER_EXITED_EARLY`、`FSL_E_BROKER_PROCESS_CLEANUP_FAILED`。field均null；仅elevation cancelled与connect timeout retryable=true。
- 八份权威文档已同步D-029并改写旧四参数CLI、身份错误混层、cwd/bin Broker path、ProcessStartInfo runas、连接后终止Broker、应用失败直接非零退出与第二连接/请求等冲突规则。
- 本轮文档同步未运行真实UAC、管理员凭据UI、跨账户提升、elevated Broker、Program Files安装、签名、服务、LocalSystem、注销、重启、SACL、Audit File System或Security日志。当前机器仍为`AGREELIN`；真实场景只允许`FSL-STAGE4-VM`。
- CP9产品代码尚未开始。下一步先运行strict UTF-8、Markdown围栏、尾空白、禁用词、旧冲突、D-029必备标识符、`git diff --check -- .`与残留检查；通过后调用项目级只读`stage_director`。只有`READY`后才串行planner → coder → reviewer。

## 2026-07-22 — CP9 gate READY 与 planner 新合同阻塞

- D-029 文档同步后的 strict UTF-8、Markdown 围栏、尾随空白、禁用词、旧 consent CLI、D-029 覆盖、TEMP/恢复构件和 `git diff --check -- .` 检查通过。项目级只读 `stage_director` 返回 `READY`，下发阶段 4 CP9 `NEXT_STAGE_GOAL_PAYLOAD`；`FSL-STAGE4-VM` 特权证据继续只阻止阶段 4 整体完成，不阻止 `AGREELIN` 非特权 CP9 工作。
- 根线程按 payload 调用只读 planner。planner 从当前代码确认 D-029 已批准范围外仍有三个精确生产合同缺口，因此结论为 `BLOCKED`，未生成 coder checkpoint，未启动 coder，未修改 CP9 产品代码。
- 阻塞 1：D-024.2 只定义 `RecoveryReadinessSnapshot`、`IRecoveryReadinessPublisher` 与 `IRecoveryReadinessReader`，production 仍使用 `UnavailableRecoveryReadinessReader`/`UnavailableRecoveryReadinessPublisher`。缺少跨 recovery-service、普通 UI 与 elevated Broker 的固定存储或 IPC 标识符、owner/DACL、脱敏读取边界、序列化与原子性、缺失/损坏/过期/service instance 变化/停止行为和生命周期清理合同。
- 阻塞 2：`FR-003` 禁止隐藏时长默认值；`BrokerCompositionRoot.CreateRuntime` 仍要求 `repositoryRoot`、`installationRoot`、`synchronizationRoots`、`LockDurationPolicy`，`BrokerLifecycleController.RunSchedulerAsync` 仍要求 `pollingInterval`，D-029 固定 CLI 不传这些值。缺少 production Minimum/Maximum、scheduler 模型或固定间隔、repository root、synchronization roots 与受信配置源及读取失败行为。
- 阻塞 3：D-029 bootstrap 要求先建立 protected logger，production composition 要求真实 `ILoggerFactory`；当前 Broker 没有 production provider，现有文档只固定 `%ProgramData%\FolderSessionLock\Logs\` 路径。缺少 provider/目标、Logs 目录及文件 owner/DACL、创建与验证主体、格式、轮换、大小上限、清理、初始化失败 exit 28 和 recovery-service/consent-broker 共享规则。
- 上述三项属于新的用户安全与产品决定。根线程不得自行选择 readiness 信任边界、生产时长、scheduler 间隔、路径来源或日志 ACL。取得决定并同步八份权威文档后，必须重新调用只读 `stage_director`；新的 `READY` 前禁止启动 coder。

## 2026-07-22 — CP9 D-030 三项生产合同最终批准

- 已完整读取用户附件 `C:\Users\lingl\.codex\attachments\b97f857e-51b0-43d8-bf4e-e7691bbc7a0b\pasted-text.txt` 共1852行。用户批准 `D-030：跨进程 Recovery Readiness、生产路径策略与受保护日志`，精确解除上一轮planner报告的全部三个合同阻塞。
- 跨进程readiness唯一使用ProgramData Known Folder下受保护machine snapshot，不新增公共Pipe。唯一publisher为`FolderSessionLockRecovery`服务；UI、consent-broker、recovery-once只读。固定Readiness目录/canonical/temp、SYSTEM owner、四ACE protected DACL、Users只读边界和`Global\FolderSessionLock.RecoveryReadiness.v1` mutex。
- snapshot固定strict UTF-8 without BOM、1..16384 bytes、十二字段schema1、四状态矩阵、service instance/sequence、10-second heartbeat、30-second validity与5-second future tolerance。publish/read/delete使用retained handles、owner/DACL/identity/content复核、FlushFileBuffers、class65相对原子replace和FileDispositionInfoEx；内部十个readiness错误对CreateLock统一映射`FSL_E_RECOVERY_BLOCKING`。
- production `LockDurationPolicy`固定60000..86400000ms；每consent-broker至多一个Active task、一个scheduler、一个串行loop，使用monotonic remaining与最大30-second分段重算。UI断开不取消Active；scheduler error只进protected logger，Cleanup first-task error保持优先。
- repository只按retained target handle逐级检查`.git|.hg|.svn`；synchronization只按`CfGetSyncRootInfoByHandle`和可信initiating UI token的`FOLDERID_SkyDrive`。环境变量、cwd、PATH、CLI/用户roots、注册表和第三方配置不得作为信任来源；任何indeterminate fail closed。
- production logger唯一为`ProtectedJsonLinesLoggerProvider`。ProgramData `Logs\v1`三个模式目录和文件owner SYSTEM、protected DACL只允许SYSTEM/Administrators/service SID FullControl，普通用户不可列出或读取。每进程独立十四字段JSONL、LF/无BOM/4096-byte行、每事件flush、8MiB或UTC跨日rotation、14days、每模式32关闭文件、全局256MiB与安全artifact规则固定。
- 新错误`FSL_E_PROTECTED_LOGGER_UNAVAILABLE`固定message `The protected diagnostic logger could not be initialized.`、retryable false、field null。consent-broker strict CLI后、Pipe前初始化失败exit28；运行中已有副作用先完成lifecycle/Cleanup，exit27优先；合法response不被后续exit28改写。service logger失败阻止Running或触发RecoveryBlocked受控停止；recovery-once使用既有exit15。
- 八份权威文档已按职责同步D-030；D-024.2旧八字段snapshot、FR-003待后续确认范围、未定义readiness时效、可配置repository/synchronization roots、generic logger和未固定scheduler条款已改写。同步阶段未执行真实UAC、SCM、LocalSystem、ProgramData ACL、service SID、Cloud Files/OneDrive或VM操作。
- 文档验证通过：strict UTF-8 8/8，BOM/NUL 0，Markdown围栏全部平衡，尾随空白0；八份文件均包含D-030、受保护machine snapshot、86400000和`ProtectedJsonLinesLoggerProvider`；八组主动冲突短语匹配0，禁用词0；`%TEMP%\FolderSessionLock.Tests`与工作区恢复/TestResults构件0；`git diff --check -- .`退出0。
- 下一步只调用项目级只读`stage_director`；新的`READY`前不启动coder。
- 项目级只读`stage_director`复核后返回`READY`：D-030已精确解除machine readiness、生产duration/scheduler/path classification和protected logger三项阻塞；CP8的596/596、Release build与reviewer`PASS`证据保持有效；CP9产品代码尚未开始。`FSL-STAGE4-VM`专属真实UAC、ProgramData ACL、service SID/LocalSystem、SCM、Cloud Files/OneDrive、跨用户readiness、生产并发日志、重启stale、签名和D-026证据继续只阻止阶段4整体完成。
- 根线程已读取完整CP9`NEXT_STAGE_GOAL_PAYLOAD`，下一步严格串行调用只读planner；planner完成前不启动coder。
- 只读planner重新核对D-029/D-030与实际源码、测试后返回`PROCEED`，未发现新合同阻塞。实施顺序固定为：共享合同/CLI → machine readiness → path classification/fixed duration/single scheduler → protected logger → UI identity/readiness/path/UAC/race client → Broker pre-Pipe bootstrap/host/exit → production composition/Program → 完整矩阵与状态证据。
- planner明确要求保持App只引用Core、复用现有class65/disposition和identity/recovery安全组件、所有新Win32逻辑置于可注入platform后、每checkpoint运行focused tests、最终完整Release build/serial tests/format/diff/static/residual checks。下一步调用唯一coder；coder不得递归调用agents或进入CP10/阶段5/阶段6。

## 2026-07-22 — CP9 当前 AGREELIN 实现与完整验证

- 唯一 coder 完成 D-029/D-030 当前环境范围：UAC 前 initiating UI token/PID/creation FILETIME、固定 Program Files Broker path、`ShellExecuteExW(runas)` 与连接 race、Broker pre-Pipe bootstrap、单连接 consent production lifecycle、machine readiness store、安全 repository/Cloud Files/SkyDrive 分类、60000..86400000ms production duration、单 scheduler、三模式 protected JSONL logger 与 production composition。
- recovery-service 与 recovery-once 生产入口改为 `WindowsProtectedLoggerFactory` 和 `WindowsRecoveryReadinessStore.CreateProduction`；protected logger 启动失败 fail closed，service 运行中永久失败发布 blocking readiness并请求受控停止，recovery-once 使用 exit 15 与 `FSL_E_PROTECTED_LOGGER_UNAVAILABLE` 结构化摘要。
- 新增 readiness canonical/temp 文件安全测试，验证 SYSTEM owner、protected four-ACE DACL、Users read-only masks、link count、owner/DACL/inheritance/ACE/mask 失败、temp-only security apply、restore privilege 与 revert failure。新增 production Program 静态组合、recovery-once structured output、repository marker/ancestor indeterminate fail-closed 测试。
- focused 实际结果：`RecoveryReadinessFileSecurity|RecoveryServiceOrchestrator|ProductionConsentBrokerPipeRunner` 26/26；扩展 `ProcessBoundaryTests` 集合 37/37；Windows repository/synchronization 集合 11/11。
- 首次全量串行测试结果为 Core 174/174、App 433/440、Windows 114/114，App 7 个失败均可单独复现。根因分别为测试 readiness snapshot 使用已过期固定时间、BrokerCommandProcessor 测试使用不存在的 `C:\Data\Locked`、以及 recovery cleanup 对已添加 Deny ACE 的目录重新请求 `FileReadData` 导致 `FSL_E_PATH_ACCESS_DENIED`。修复为 gate fixture 使用当前 UTC、测试创建 TEMP target、最终目录 handle 仅请求 `FileReadAttributes | READ_CONTROL | WRITE_DAC`；七项回归 focused 7/7。
- 最终实际命令：`dotnet restore FolderSessionLock.sln` 退出 0；`dotnet build FolderSessionLock.sln -c Release --no-restore` 0 warning、0 error；`dotnet test FolderSessionLock.sln -c Release --no-restore -m:1` 为 Core 174/174、App 440/440、Windows 114/114，总计 728/728，failed 0、skipped 0；`dotnet format FolderSessionLock.sln --verify-no-changes --no-restore` 与 `git diff --check -- .` 均退出 0。
- 静态检查：Broker Recovery 产品代码禁止 path replace/move/delete/security API 匹配 0；production Program/composition 的 unavailable/always-ready/in-memory/Console/Debug/Null provider 匹配 0；repository/synchronization 非权威环境变量、cwd、PATH、CLI root 来源匹配 0；Windows Task Scheduler/Timer API匹配 0。
- 残留检查：`%TEMP%\FolderSessionLock.Tests` 条目 0；FolderSessionLock 产品进程 0；bin/obj 外 `.fslr`、`.fsrr`、readiness temp/canonical 与 JSONL 构件 0；`FolderSessionLockRecovery` 服务不存在。未执行真实 UAC、SCM、LocalSystem、ProgramData/ProgramFiles owner/DACL、service SID、Cloud Files/OneDrive、签名、跨账户凭据、注销、重启、SACL 或审计操作。
- 2026-07-23 用户最终关闭上述两项精确证据：exit 2 固定为 `FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / false / null且禁止公开 CLI、参数、路径、命令行、Win32或异常细节；原始 `STATUS_CLOUD_FILE_NOT_UNDER_SYNC_ROOT` 为 `0xC000CF13` / `-1073688813`，转换 HRESULT 为 `0xD000CF13` / `-805253357`。实际 `CfGetSyncRootInfoByHandle` wrapper返回HRESULT，因此产品只比较转换值和Win32 not-under-sync-root HRESULT，不比较原始NTSTATUS、不掩码，未知失败fail closed。
- coder 已实现exit 2 mapper并新增精确公开对象测试；Cloud Files判断删除NTSTATUS掩码与`RtlNtStatusToDosError`路径，新增原始NTSTATUS、HRESULT转换和实际HRESULT关闭集合独立测试。focused App mapper 13/13、Windows synchronization 7/7通过。真实系统与VM-only证据继续仅允许`FSL-STAGE4-VM`。

## 2026-07-23 — CP9 最后两项精确值完成

- exit 2 产品映射已固定为 `FSL_E_BROKER_LAUNCH_CONTRACT_INVALID` / `The elevated broker launch request is invalid.` / retryable false / field null。mapper只返回该固定对象，不包含CLI、参数、路径、命令行、Win32或异常细节。
- `CfGetSyncRootInfoByHandle` P/Invoke实际返回`int hresult`。产品固定常量：原始NTSTATUS `unchecked((int)0xC000CF13)` / `-1073688813`，转换HRESULT `unchecked((int)0xD000CF13)` / `-805253357`；运行时判断只接受Win32 not-under-sync-root HRESULT与转换HRESULT，原始NTSTATUS和未知HRESULT均fail closed为`FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE`。旧FacilityNtBit掩码和`RtlNtStatusToDosError`已删除。
- 八份权威文档8/8同步固定message、原始NTSTATUS、转换HRESULT、实际wrapper比较类型与禁止泄露/混用/掩码规则。原“message未定义”“权威数值缺失”当前阻塞已删除；历史日志仍保留当时的阻塞发现过程。
- focused实际结果：`BrokerConnectionRaceTests` 13/13、`WindowsSynchronizationPathClassifierTests` 7/7，0 failed、0 skipped。
- 完整实际验证：`dotnet restore FolderSessionLock.sln`退出0；`dotnet build FolderSessionLock.sln -c Release --no-restore`为0 warning、0 error；`dotnet test FolderSessionLock.sln -c Release --no-restore -m:1`为Core174/174、App441/441、Windows117/117，总计732/732、failed0、skipped0；`dotnet format FolderSessionLock.sln --verify-no-changes --no-restore`退出0。
- 文档与静态验证：八份权威文档strict UTF-8 8/8、BOM/NUL 0、固定message/`0xC000CF13`/`0xD000CF13`覆盖8/8、Markdown围栏全部平衡、尾随空白0、主动旧缺口短语0；产品旧`FacilityNtBit`/`RtlNtStatusToDosError`逻辑匹配0；`git diff --check -- .`退出0。
- 残留检查：`%TEMP%\FolderSessionLock.Tests`条目0、FolderSessionLock/testhost/vstest进程0、bin/obj外recovery/readiness/logger构件0、TestResults目录0，`FolderSessionLockRecovery`服务不存在。
- 未执行真实UAC、SCM、LocalSystem、ProgramData/ProgramFiles ACL、service SID、Cloud Files/OneDrive、签名、跨账户凭据、注销、重启、SACL、审计或VM操作。下一步由根线程调用只读reviewer或`stage_director`，coder不进入CP10。

## 2026-07-23 — CP9 reviewer 首轮与第 1/6 修复轮

- reviewer 首轮结论为 `FAIL`，共 6 个 `HIGH`：SCM dispatcher/status wrapper 缺失；protected log retention 未接入首次创建、rotation 与运行维护；scheduler/Cleanup 合同诊断未通过真实 protected JSONL provider；consent-broker 永久 logger failure 未稳定映射退出；repository ancestor 验证仍有名称重开 TOCTOU；SkyDrive HRESULT 使用低 16 位判断。
- 第 1/6 修复轮已关闭并验证其中 5 项：新增 `WindowsRecoveryServiceHost`、SCM dispatcher/control handler/status reporter 与 D-024.2 状态映射；logger 在首次文件和每次 rotation 前执行 retention/hard-limit，并由 recovery-service 每 24 小时维护且失败进入 blocking readiness/受控 Stop；consent-broker 固定 Cleanup/合法响应/logger failure 的退出优先级；repository 分类保留 volume-root 与全部 ancestor handles并验证逐级 FILE_ID binding；SkyDrive低16位解释已删除，当时“所有负HRESULT fail closed”是不存在完整值未获批准时的临时规则，现已由修复轮2/6最终合同替换。
- Cleanup protected diagnostics 已通过真实 provider：稳定顺序遍历全部适用任务，逐条记录首个和后续 task error、受保护 `taskId`，最终记录完整遍历与 `RecoveryRequired`，公开 event 使用固定 catalog message且不包含异常类型、stack或敏感错误文本。consent-broker logger permanent failure 的无副作用、有副作用后 Cleanup、Cleanup failure优先和合法响应优先四种路径均有自动测试。
- scheduler protected logging 当时仍有唯一新合同阻塞。`LockTaskScheduler` 与 `BrokerLifecycleController` 当时使用的 `lock_task_scheduler.loop.exception` 是已废弃旧值；D-030 protected JSONL schema 在 `ProtectedJsonLinesLoggerProvider.ValidateContext` 中只允许 `FSL_E_*` 或 `lock_task.*`。真实 provider 因已废弃旧值永久 fail closed且不写 JSONL，无法同时满足“保留当时错误码”“D-030关闭集合”“通过真实 provider持久化”三项要求。
- 新增 `ProtectedLifecycleDiagnosticsTests.SchedulerErrorCodeOutsideD030Schema_PermanentlyFailsTheRealProviderWithoutWritingJsonLines` 作为精确阻塞证据；测试确认 scheduler failure 后 provider `IsPermanentlyFailed == true`、JSONL为空、Flush为0。按根线程指令未改名、未扩大允许集合、未为 scheduler 创建兼容分支。
- focused实际结果：Windows repository/SkyDrive 21/21；App SCM/retention/consent logger/Cleanup/process boundary 54/54；Core coordinator lifecycle/scheduler 18/18；全部 failed0、skipped0。
- 完整实际验证：`dotnet restore FolderSessionLock.sln`退出0；`dotnet build FolderSessionLock.sln -c Release --no-restore`为0 warning、0 error；`dotnet test FolderSessionLock.sln -c Release --no-restore -m:1`为Core174/174、App460/460、Windows124/124，总计758/758、failed0、skipped0；`dotnet format FolderSessionLock.sln --verify-no-changes --no-restore`与`git diff --check -- .`退出0。
- 静态检查：recovery-service产品入口存在`WindowsRecoveryServiceHost`/dispatcher且不含`Console.CancelKeyPress`或Null status reporter；production logging区域Console/Debug/EventLog匹配0；SkyDrive低16位mask匹配0；repository retained root-relative open与identity binding标识存在；recovery产品路径型replace/move/delete/security API匹配0。
- 清理复核：`%TEMP%\FolderSessionLock.Tests`目录存在但条目0；FolderSessionLock/testhost/vstest进程0；bin/obj外`.fslr`、`.fsrr`、`recovery-readiness.v1.json`、JSONL与TestResults构件0；`FolderSessionLockRecovery`服务不存在。未执行真实UAC、SCM状态变更、LocalSystem、ProgramData/ProgramFiles ACL、service SID、Cloud Files/OneDrive、签名、跨账户凭据、注销、重启、SACL、审计或VM操作。
- CP9不进入最终reviewer复审、CP10、阶段5或阶段6，直到用户给出 scheduler `errorCode` 与D-030关闭集合冲突的精确权威决定。

## 2026-07-23 — CP9 scheduler protected logging 最终合同

- 用户确认 `lock_task_scheduler.loop.exception` 为已废弃旧值，生产统一使用 `lock_task.scheduler.loop.exception`；固定英文message精确为`The lock task scheduler loop terminated unexpectedly.`。该合同仅表示`LockTaskScheduler`生产loop未预期的非取消异常。
- protected logger固定`component = Scheduler`、`level = Error`、精确code/message。预期token已取消的`OperationCanceledException`不记录；lifecycle stop、Cleanup failure、task状态转换、已有更具体错误和logger failure不得复用该code/message。
- 该错误只写受保护内部日志，不进入公开响应，不覆盖Cleanup first-task error，不阻止Cleanup。内部日志不得包含异常message、`ToString()`、stack、内部类型、路径、SID、HRESULT或Win32 message。
- 用户授权同步八份权威文档、scheduler实现/测试、protected logger schema/catalog/tests与静态扫描。完成完整验证前不进入最终reviewer复审、CP10、阶段5或阶段6。
- 实现完成：`LockTaskScheduler.RunAsync`仅在非取消异常时返回新code与固定message；预期token取消返回success且logger条目0。`BrokerLifecycleController`仅在scheduler结果code精确等于新值时以`Error`写`SchedulerStopped` protected catalog event；lifecycle自身捕获的异常、Cleanup failure和其他更具体scheduler错误不复用该合同。
- protected catalog的`SchedulerStopped`固定message已改为精确英文值。真实JSONL测试确认`level = Error`、`component = Scheduler`、精确code/message、provider保持健康且无敏感异常内容；并确认scheduler失败与Cleanup失败并存时对外仍返回稳定顺序第一个Cleanup task error。
- schema测试确认新值可写入，已废弃旧值使provider永久fail closed且不写JSONL。production `src`已废弃旧值匹配0；活动七份合同文档旧值匹配0；tests只保留1处旧值作为拒绝证据；DEVLOG两处历史旧值均标注“已废弃旧值”。
- focused实际结果：Core `LockTaskSchedulerTests` 9/9；App protected provider/lifecycle/runner 36/36；failed0、skipped0。完整验证：restore退出0；Release build0 warning、0 error；全量串行Core174/174、App462/462、Windows124/124，总计760/760、failed0、skipped0；format与`git diff --check -- .`退出0。
- 八份权威文档strict UTF-8 8/8、BOM/NUL 0、尾空白0、Markdown围栏全部平衡、新合同覆盖8/8、禁用词0。scheduler敏感日志模式匹配0；production新code仅出现在scheduler归一与lifecycle精确匹配两处，固定message仅出现在scheduler error与protected catalog两处。
- 清理复核：`%TEMP%\FolderSessionLock.Tests`目录存在但条目0；FolderSessionLock/testhost/vstest进程0；bin/obj外`.fslr`、`.fsrr`、`recovery-readiness.v1.json`、JSONL与TestResults构件0；`FolderSessionLockRecovery`服务不存在。未执行真实UAC、SCM状态变更、LocalSystem、ProgramData/ProgramFiles ACL、service SID、Cloud Files/OneDrive、签名、跨账户凭据、注销、重启、SACL、审计或VM操作。
- CP9第1/6修复轮已具备交回同一reviewer复审的实现与验证证据；coder停止，不进入CP10、阶段5或阶段6。

## 2026-07-23 — CP9 reviewer修复轮2/6 SkyDrive Known Folder最终合同

- 用户最终批准完整`SHGetKnownFolderPath(FOLDERID_SkyDrive, KF_FLAG_DEFAULT = 0, initiatingUserToken, out path)`合同；禁止CREATE、DONT_VERIFY、DEFAULT_PATH。调用前path为null；失败返回非null native pointer也必须`CoTaskMemFree`。
- 完整HRESULT `0x80070002`/`-2147024894`精确表示当前用户SkyDrive实例或目标叶项不存在；`0x80070003`/`-2147024893`精确表示父路径链不存在；两者返回`Exists=false, Path=null`并允许继续。
- “未注册”只通过创建`IKnownFolderManager`并要求`GetFolderIds == S_OK`后按GUID二进制精确查找；集合不含SkyDrive返回内部原因`KnownFolderNotRegistered`并允许继续。不得使用字符串、显示名、canonical name或从SH HRESULT猜测；GetFolderIds任何非S_OK fail closed。
- 只有注册缺失、`0x80070002`、`0x80070003`三个场景允许`Exists=false`。`0x80070057`、`0x80004005`、`0x80070005`、`0x80070006`、`0x8007052E`、`0x80070520`、`0x80070522`及其他HRESULT全部返回`FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE`。禁止低16位、HRESULT_CODE、facility mask、raw Win32 2/3、NTSTATUS、wrapper重编号或E_INVALIDARG未注册解释。
- S_OK必须path非null非空，复制受控string并释放pointer，然后执行持续handle、拒绝reparse、final path、DirectoryIdentity和Same/Descendant比较。八份权威文档已同步；实现与验证完成前不进入reviewer复审、CP10、阶段5或阶段6。
- 实现完成：`WindowsSynchronizationPathPlatform`先调用新COM wrapper取得注册GUID数组；GetFolderIds非S_OK、count/pointer不一致或COM异常统一fail closed，数组pointer在finally释放，COM对象最终释放。注册集合用`Guid`值直接比较，不转换字符串、显示名或canonical name。
- 注册缺失精确返回`Exists=false, Path=null, Reason=KnownFolderNotRegistered`且不取得token、不调用SH。注册存在后固定folderId SkyDrive、flags0和initiating token；本地pointer先置0，SH返回后无论HRESULT均在finally释放非null pointer。
- lookup解释只允许S_OK有效绝对路径、完整`0x80070002`和完整`0x80070003`。S_OK null/empty、E_INVALIDARG/E_FAIL/access denied/invalid handle/logon failure/no logon session/privilege not held、raw2/3、低16位伪装和其他HRESULT统一`FSL_E_SYNCHRONIZATION_CLASSIFICATION_UNAVAILABLE`。
- focused Windows同步分类测试28/28通过；覆盖GetFolderIds不含/失败/含后调用顺序、GUID/flags/pointer初值、S_OK有效/null/empty、两个not-found、完整拒绝集合、raw/伪装值、失败非null pointer释放以及禁止CREATE/DONT_VERIFY/DEFAULT_PATH/HRESULT_CODE/mask源码扫描。
- 完整验证：restore退出0；Release build0 warning、0 error；全量串行Core174/174、App462/462、Windows140/140，总计776/776、failed0、skipped0；format与`git diff --check -- .`退出0。
- 静态与文档检查：产品禁止flags/mask/E_INVALIDARG未注册模式0；COM、GetFolderIds、flags0、pointer释放、两个完整HRESULT必备标识存在；八份权威文档strict UTF-8 8/8、BOM/NUL0、尾空白0、围栏平衡、两个HRESULT与GetFolderIds覆盖8/8、禁用词0、活动冲突0。
- 清理复核：`%TEMP%\FolderSessionLock.Tests`目录存在但条目0；FolderSessionLock/testhost/vstest进程0；bin/obj外recovery/readiness/JSONL/TestResults构件0；`FolderSessionLockRecovery`服务不存在。未执行真实OneDrive、SCM、UAC、ACL、LocalSystem、签名、注销、重启、SACL、审计或VM操作。
- CP9第2/6修复轮已具备交回同一reviewer复查的实现与验证证据；coder停止，不进入CP10、阶段5或阶段6。

## 2026-07-23 — CP9 最终 reviewer PASS 与 STATE 同步

- 同一 reviewer 完成 CP9 最终只读复审并输出 `PASS`，无 `BLOCKER` 或 `HIGH`。第 1/6 修复轮关闭 SCM dispatcher/status wrapper、protected-log retention 与运行维护、consent-broker 永久 logger failure、repository ancestor TOCTOU、scheduler/Cleanup protected diagnostics；scheduler 固定为 `lock_task.scheduler.loop.exception` / `The lock task scheduler loop terminated unexpectedly.`。第 2/6 修复轮关闭 SkyDrive Known Folder 注册与 HRESULT 合同，落实 `IKnownFolderManager::GetFolderIds`、GUID 二进制注册检查、`KnownFolderNotRegistered`、flags 0、所有非 null native pointer 释放，以及仅允许完整 `0x80070002` 与 `0x80070003` 表示路径不存在。
- 最终验证证据：Core 174/174、App 462/462、Windows 140/140，总计 776/776、failed 0、skipped 0；Release build 0 warning、0 error；format、diff、文档一致性、静态禁止模式扫描和清理检查全部通过。
- 当前机器仍为 `AGREELIN`。未执行真实 UAC、SCM 系统变更、LocalSystem、ProgramData/ProgramFiles ACL、真实 service SID ACL、真实 OneDrive/Cloud Files 系统场景、签名、注销/重启或 `FSL-STAGE4-VM` 验证；这些证据不得记录为通过。
- CP9 当前 `AGREELIN` 允许范围完成，但阶段 4 仍未完成。CP10、阶段 5 与阶段 6 均未开始。
- 下一步由根线程调用项目级只读 `stage_director`；本次 STATE 同步不调用 planner、coder 或 reviewer，不进入后续 checkpoint。

## 2026-07-23 — CP9 最终状态文档残留修正

- 项目级只读 `stage_director` 核验发现 `docs/REQUIREMENTS.md`、`docs/ARCHITECTURE.md` 与 `docs/SECURITY.md` 的当前状态仍保留 732/732 和“等待 CP9 reviewer”，与已记录的最终 reviewer `PASS`、无 `BLOCKER` 或 `HIGH` 及 776/776 验证证据冲突。
- 本次仅修正上述三个当前状态入口及 `docs/REQUIREMENTS.md` 的决策状态段：统一为 CP1–CP9 当前 `AGREELIN` 允许范围完成、Core 174/174、App 462/462、Windows 140/140、总计 776/776、CP10 未开始且仅允许在 `FSL-STAGE4-VM` 执行。DEVLOG 中 732/732 的历史验证事实保持不变。
- 阶段 4 仍未完成；真实 UAC、SCM 系统变更、LocalSystem、ProgramData/ProgramFiles ACL、真实 service SID ACL、真实 OneDrive/Cloud Files、签名、注销/重启与 D-026 证据仍未执行。本次未修改产品代码或测试，未运行 build/test，未进入 CP10、阶段 5 或阶段 6。
- 下一步由根线程再次调用项目级只读 `stage_director`。

## 2026-07-25 — CP10 R5 WAL partial-write recovery

- `FileCopyAtomic` now freezes a transaction-derived same-directory temp name,
  proves target/temp absence before Begin, and durably binds the target parent
  and source identity/ACL before temp creation.
- The production writer uses explicit write-through chunks with
  `AfterTempCreate`, `DuringTempWrite`, `AfterTempFlush`, and `AfterRename`
  boundaries. Recovery deletes a partial temp only when the source and parent
  bindings remain exact and the temp is an ordinary, non-reparse, single-link,
  safe-owner/DACL exact source prefix no longer than the frozen source.
- Windows PowerShell 5.1 parent-side `Process.Kill` tests passed all eight
  positive boundaries, second-reconcile idempotence, nine post-Intent rejection
  cases, and three pre-Begin rejection cases. Six PowerShell parsers and the
  repository-integrity behavior suite also passed.
- This is a trusted-controller/executor recovery contract. It does not claim a
  boundary against malicious same-user code, administrators, LocalSystem,
  snapshot rollback, an external witness, or anti-rollback.

## 2026-07-27 — CP10-SCOPE-LOCAL-SINGLE-ADMIN coder implementation

- Adopted D-031 `LOCAL_SINGLE_USER_ADMINISTRATOR_ONLY` and the trusted
  single-user Stage 4 executor model. Creating `FSL-Standard` or `FSL-Admin`,
  separate-administrator credential elevation, dual-account evidence, and
  blocking Stage 5 solely for absent dual-account evidence are
  `CANCELLED / NOT REQUIRED`.
- Reconciled the authority documents, Stage 4 controller, Broker
  Authenticode verifier, evidence contract, and directly affected tests.
  D-026 evidence is schema v2 with exact top-level and scenario fields.
- The explicit unsigned mode is selected only when `publisherThumbprint` is
  null or exactly empty. It performs no platform signature calls in the App
  verifier. Whitespace and malformed non-empty values fail closed. A valid
  40-hex publisher pin retains the existing signed, exact-pin, fail-closed
  path.
- The Stage 4 controller no longer exposes certificate creation. Its current
  unsigned release verification requires all six first-party PE files to
  report Authenticode `NotSigned` with a null signer certificate, records a
  SHA-256 for each file, and preserves the remaining build, test, publish,
  install, recovery, ACL, and evidence gates.
- Coder verification passed: PowerShell parsing for the Stage 4 entry point,
  module, and behavior tests; the complete Stage 4 tooling behavior suite;
  Broker verifier tests 22/22; Release build with 0 warnings and 0 errors;
  App tests excluding the privileged Stage4Vm category 490/490, failed 0,
  skipped 0; and `dotnet format --verify-no-changes`.
- No account or certificate was created. No UAC, SCM, LocalSystem, ACL,
  signing, restart, logoff, VMware, push, or other system mutation was
  performed. Full root verification and reviewer disposition remain pending.

## 2026-07-27 — CP10 unsigned controller contract repair

- Root verification returned three focused contract gaps: the public
  Authenticode command/state names were still signature-oriented, the current
  controller still exposed optional publisher/signing inputs and a signed
  branch, and cleanup evidence did not explicitly bind certificate residue to
  zero.
- The public command is now `VerifyAuthenticode`; the current function and
  produced transition are `Invoke-FslVerifyAuthenticode` and
  `AuthenticodePolicyVerified`. The legacy transition token remains accepted
  only by state parsing and uninstall/cleanup compatibility and is never
  produced by a current command.
- `Invoke-Stage4.ps1` and `Invoke-FslStage4Command` expose neither
  `PublisherThumbprint` nor `SigningCertificateThumbprint`. Publish always
  embeds an exact empty `BrokerPublisherThumbprint`; Publish,
  VerifyAuthenticode, Install, and Verify have no reachable signed or SignTool
  branch and require all six first-party PE files to be `NotSigned` with a
  null signer. The App runtime verifier's separately tested valid-pin
  fail-closed capability remains unchanged for a future runtime configuration.
- Cleanup now writes exact `CertificatesRemaining=0`.
  `FinalizeEvidence` requires exactly one such zero line, and direct behavior
  tests reject a nonzero replacement.
- Repair verification passed: PowerShell parser 3/3; focused Stage4 Slice4;
  complete Stage4 Slice All; Release build with 0 warnings and 0 errors;
  Broker Authenticode verifier 22/22, failed 0, skipped 0; public-controller
  static contract, strict UTF-8/Markdown, `git diff --check`, and
  `dotnet format --verify-no-changes`.
- Final residue was zero for related product/test processes, repository dotnet
  processes, recovery service, `FSL-Standard`/`FSL-Admin`, Stage4 certificates,
  and `%TEMP%\FolderSessionLock.Tests` entries. Release, Program Files, and
  ProgramData product roots were absent. No UAC, SCM, LocalSystem, ACL,
  certificate, signing, restart, logoff, VMware, push, or other system
  mutation was performed. Root re-verification and reviewer remain pending.

## 2026-07-27 — CP10 reviewer HIGH repair: signing scope and frozen evidence

- Reviewer returned two `HIGH` findings. PLAN and ACCEPTANCE still contained
  unqualified signed-Broker gates that could be applied to the D-031 local
  scope, and Finalize accepted any uppercase 64-hex SHA-256 value reported by
  `signature-verification.txt`.
- The future Stage 7 public/enterprise/signing checkpoint is now explicitly
  inactive until a separate product decision activates it. Missing signing
  credentials, certificates, or a signing pipeline cannot block the D-031
  local unsigned Stage 4 or Stage 5 entry. Actual conflicting current wording
  was reconciled in PLAN, ACCEPTANCE, D-015, SECURITY, REQUIREMENTS,
  ARCHITECTURE, and historical TASKS; historical execution facts remain.
- Finalize now passes protected state `ReleaseRoot` and
  `ReleaseDescriptorSha256` to unsigned evidence validation. Validation
  re-reads the frozen descriptor, revalidates its metadata, exact file set and
  payload hashes, requires the exact ordered six-PE set, computes each actual
  frozen PE SHA-256, and requires the evidence record to match exactly.
- The direct behavior fixture creates a frozen release and proves the valid
  evidence path, non-null-signer rejection, and rejection of a different but
  otherwise valid uppercase 64-hex hash. It also verifies that Finalize wires
  both protected state values into the gate.
- Verification passed: PowerShell parser 3/3; focused Stage4 Slice4; complete
  Stage4 Slice All; Release build with 0 warnings and 0 errors; Broker
  Authenticode verifier 22/22, failed 0, skipped 0; frozen-binding and signing
  scope scans; strict UTF-8/Markdown; `git diff --check`; and
  `dotnet format --verify-no-changes`.
- Final residue was zero for related product/test and repository dotnet
  processes, recovery service, test accounts, Stage4 certificates, and
  `%TEMP%\FolderSessionLock.Tests` entries. Release, Program Files, and
  ProgramData product roots were absent. No account, UAC, SCM, LocalSystem,
  ACL, certificate, signing, restart, logoff, VMware, push, or other system
  mutation was performed. Root re-verification and reviewer remain pending.

## 2026-07-29 — CP10 tracked formal-launcher bundle generator reviewer PASS

- Formal Attempt002 remains permanently consumed and must not be replayed.
  Its frozen outer command line was 233 characters because PowerShell
  single-quoted `\"` produced literal backslashes, while the contract used
  real quotes and was 229 characters. The read-only exit-68 forensics
  checkpoint passed reviewer with no findings.
- Added the tracked, deterministic, non-executing
  `FolderSessionLock.Stage4.FormalLauncherBundle` generator/validator with
  exactly two public commands. It canonicalizes the outer, observer, contract,
  hashes, exact file sets, ACLs, recovery bindings, 22 ordered predicates,
  durable one-shot latch semantics, Windows argv encoding, token proof, Git
  object validation, and strict zlib/DEFLATE framing.
- The frozen outer process flags are intentionally
  `CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW` (`0x09000000`). The rendered
  outer uses the independent official constants, the contract binds both the
  ordered symbolic set and numeric value, and the observer verifies them
  before any latch write or RunAs. A native non-elevated Job Object test proved
  both permitted breakaway and fail-closed `ERROR_ACCESS_DENIED` behavior when
  breakaway is forbidden, with one attempt, no fallback, and zero residue.
- Root verification passed the PowerShell 5.1 suite with 205/205 cases and
  267/267 assertions, the C# bridge and native Job Object tests, Release build
  with 0 warnings and 0 errors, format verification, and `git diff --check`.
  The non-environment-dependent regression set passed 806/806 with 0 failed
  and 0 skipped.
- The unfiltered suite truthfully remained Core 174/174, App 493/500, and
  Windows 140/141: 807 passed, 8 failed, 0 skipped. All eight failures are
  still-open formal-install or pending-restart VM gates; they are not recorded
  as passed and continue to block final Stage 4 completion.
- The final reviewer returned Standards PASS, Spec/Security PASS, Overall
  PASS, and `BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`. Attempt002, WAL, state,
  anchors, evidence, the empty Program Files installation directory, and the
  frozen Release remained unchanged. No Attempt003, fresh formal object, UAC,
  RunAs, SCM, LocalSystem, restart, logoff, or VMware operation was authorized
  or executed.

## 2026-07-30 — CP10 post-freeze recovery-authority documentation synchronization

- Preserved all earlier dated verification facts. The reviewed active
  recovery-authority capability baseline is now commit
  `aa60c1c6cea2ea05648824acb10f5f3ec2342549`, tree
  `9b97428f3988c962e7d4b6899d3521f9cd3b7fc1`; final reviewer result is `PASS`
  with `BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`.
- Active verification is RAB 218/305, Formal 229/299, Stage 4 tooling 7/7,
  and non-environment-dependent 807/807. The unfiltered result remains
  truthful: Core 174/174, App 494/501, Windows 140/141, total 808 passed,
  8 environment failures, 0 skipped. Release build is 0 warnings/0
  errors; format, four PowerShell parsers, commit diff, and exact public
  exports passed.
- The public current-HEAD context gate is unchanged and an old frozen
  `ReleaseRoot` still exits 2. The private verified adapter binds runId,
  current machine, `cp10-vm-transfer`, execution/recovery commits and trees,
  state, and internally derived paths, then executes the repository and
  mutation gates. The elevated wrapper calls the verified resolver, private
  adapter, and reconciler exactly once; it has no controller/install,
  retry, fallback, or second execution path.
- Frozen execution remains commit
  `3170d89cfd6066ba494170826cd43626d83c6789`, tree
  `6bee7c4db4c9adde0612aa7c67467a331d20263e`, state sequence 6 /
  `InstallStarted`, and WAL 4. Authenticated current pre-recovery external
  anchors are latest/previous generations 11/10; generations 14/13 are only
  the future successful-recovery postcondition. Recovery remains 3
  directories/8 files and Release remains 22 files. The Program Files
  installation directory is empty and the ProgramData product root is absent.
- This was documentation-only preparation. No Formal source, Attempt003, new
  latch, UAC, RunAs, reconciler, SCM, LocalSystem, restart, logoff, or other
  system execution was created or performed.
- Remaining order is strict: documentation commit-freeze; final RAB exact-two
  plus FLB exact-three preparation; the one-shot observer/UAC; only after
  successful recovery, a separately authorized fresh restart; then remaining
  D-026 and Release work. VM, D-026, restart/logoff, Release, and Stage 4
  completion remain unchecked.

## 2026-07-30 — CP10 frozen external-anchor documentation correction

- Reviewer returned `FAIL` for documentation commit
  `a287d8b198398c7b9d1c3841a1653dab6f34d174`, with no `BLOCKER` and one
  unique `HIGH`: all seven current-state summaries reported latest generation
  12 and previous generation 11, while authenticated external-anchor evidence
  records current pre-recovery latest/previous generations 11/10.
- Read-only DPAPI/HMAC validation authenticated latest `anchor-1.json` as
  generation 11 bound to current WAL length 22920 and SHA-256
  `C299D1FE85E542603BABF5DB4B38796343CD8158817853F9E1D53EEFD15CEF69`.
  It authenticated previous `anchor-0.json` as generation 10 bound to the
  prior WAL length 22182 and SHA-256
  `C535582A1B2545681CAE5A681BC6B7010D5D21E78F7DAE303EBED279EC43B7FF`.
- The correction changes only the seven authoritative documentation files
  and now distinguishes current pre-recovery generations 11/10 from future
  successful-recovery postcondition generations 14/13. Independent root
  verification, reviewer disposition, and a new commit-freeze remain pending;
  all later execution, restart, D-026, Release, and Stage 4 gates remain
  incomplete.

## 2026-07-31 — CP10 Attempt003 pre-latch fail-closed and renderer repair

- The Round 5 Git profile/fingerprint closure, worktree EOL binder repair, and
  formal preparation completed at commit
  `91fdc5c613134d29cb3e6b8b020032dab999e543`, tree
  `0e294ae92e7fcc0bb367b21a433a328778f976c3`. The generated recovery authority
  `install-wal-rollback-2` remained exact-two and the formal launcher bundle
  `install-wal-rollback-launch-observer-3` remained exact-three. Their public
  validators independently returned `Valid=True`, `Errors=0`.
- After all UAC prestate gates passed, the fixed outer launcher SHA-256
  `EE8C17BBD0D524C8FB67BAB61342EE0ABA7AB8FCAC77C370DB3C81280DF41F6C`
  was invoked exactly once. It returned 0 and created the intended hidden
  observer process. Windows PowerShell event records bind observer PID 7968 to
  the exact `launch-observer.ps1` command line at
  `2026-07-31T01:00:48.5142847-07:00`; the engine stopped at
  `2026-07-31T01:00:48.9973203-07:00`.
- The observer exited before creating `launch-attempt.jsonl`. No UAC prompt,
  RunAs, reconciler, or product process occurred. The state, journal, four-line
  WAL and external anchor hashes remained exact; the bound Program Files
  directory remained empty, ProgramData remained absent, and product
  process/service/registry/pipe residue remained zero. Attempt003 is retired
  after its single outer invocation and must not be relaunched even though no
  durable latch was created.
- Root verification found the deterministic pre-latch defect at generated
  observer line 20: `$fixedTrackedClean = True`. The renderer used
  `[string][bool]`, producing a .NET Boolean word without the PowerShell `$`
  prefix. Under `Set-StrictMode`, `True` was treated as a command before the
  observer entered its top-level `try/catch`, explaining the immediate stop and
  absent latch. The embedded C# compiler, token proof, contract self-hash,
  bundle/source identities, and ACLs independently passed.
- The first reviewer result was `FAIL`,
  `BLOCKER/HIGH/MEDIUM/LOW = 0/1/0/0`. The focused repair now renders explicit
  PowerShell `$true` or `$false` literals and executes the generated preamble in
  the existing FLB regression suite. RED reproduced the preamble failure;
  GREEN passed `STAGE4_FORMAL_LAUNCHER_BUNDLE_PASS Cases=242 Assertions=312`.
  The focused C# bridge passed 1/1 with 0 failed and 0 skipped; both PowerShell
  parsers, `dotnet format --verify-no-changes`, `git diff --check`, and TEMP,
  testhost, service and product residue checks passed.
- Independent root rendering proved both Boolean branches execute as
  `System.Boolean` and contain no bare `True`/`False` line. The final reviewer
  returned `PASS`, `BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`. The two-file repair was
  committed as `99d75e09a7ab6ddf6bfc122fe671f74418efa3ae`, tree
  `c983b894e238f9bd61b9cb751b13a8a236552b3a`; no push was performed for this
  commit.
- Because recovery-toolchain commit/tree binding changed, RAB-2, FLB-3 and
  Attempt003 are evidence only and cannot authorize another launch. The next
  formal checkpoint must create and validate new immutable RAB-3 and
  FLB-4/Attempt004 objects before any further UAC activity. WAL rollback,
  anchors 14/13, installation, restart, D-026, Release and final Stage 4 remain
  incomplete.

## 2026-07-31 — CP10 Attempt004 pre-latch ACL-null repair

- From clean commit `4d17ff48807e079dbc94b7dd22efc0bd9a936329`, tree
  `18a4b3f5405d009cf975bc0d1c9b6d2a0bcb1afb`, the formal preparation created
  immutable RAB-3 `install-wal-rollback-3` and FLB-4
  `install-wal-rollback-launch-observer-4`. Their public validators returned
  `Valid=True`, `Errors=0`. The RAB canonical SHA-256 was
  `AD2FBEB12E9BE891E37D54BAD4FE57981C2BFC1E31C02043F40FCF963F09C364`;
  the FLB canonical SHA-256 was
  `A1247F792ED99CA99EFA7434C5785F309E6445E5B24D6F512BA7888954949DB8`.
- Independent root verification intentionally executed only the generated
  observer definitions and the complete `Assert-FormalPreLatch` path; it did
  not execute the observer top level, latch creation, outer launcher, or
  RunAs. The actual generated helper failed closed with exit 70 and
  `Bound object ACL drifted.` before any system mutation. Attempt004 outer was
  never invoked and its `launch-attempt.jsonl` latch was never created.
- The failure was deterministic. `Assert-CurrentRoot` passes `$null` for the
  optional expected SDDL, but the generated `Assert-Identity` declared that
  parameter as `[AllowNull()][string]`. Windows PowerShell 5.1 converted the
  null argument to an empty string, so the intended null guard became true and
  compared the actual SDDL with `''`. The public validator rendered and
  canonicalized the script but did not execute this generated helper path.
- The focused repair changes only the generated observer parameter to
  `[AllowNull()][object]`, preserves the explicit string comparison for a
  non-null SDDL, and extends the existing FLB regression assertion to execute
  the actual generated `Assert-CurrentRoot`. RED reproduced the null-ACL
  failure. GREEN passed
  `STAGE4_FORMAL_LAUNCHER_BUNDLE_PASS Cases=242 Assertions=312`; the focused
  C# bridge passed 1/1 with 0 failed and 0 skipped. Both PowerShell parsers,
  `dotnet format --verify-no-changes`, `git diff --check`, and test/product
  process and service residue checks passed.
- Independent root verification confirmed the generated parameter is
  `System.Object`, the real generated current-root helper accepts the bound
  null expected SDDL, Attempt004 latch remains absent, and no UAC, RunAs,
  reconciler, WAL, state, journal, anchor, Program Files, service, process, or
  pipe mutation occurred. Reviewer returned `PASS`,
  `BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`. The two-file repair was committed as
  `a5b1517c7977e70c0aade82c451d25a50e598c92`, tree
  `c0d19099abc0bccb767c2d4375ef2f99e20bd572`.
- RAB-3, FLB-4, and Attempt004 are retained only as failure evidence and must
  not authorize a later launch after the toolchain commit changed. The next
  formal checkpoint must generate and independently validate immutable RAB-4
  and FLB-5/Attempt005, including the complete generated pre-latch runtime
  diagnostic, before any UAC activity. WAL rollback, anchors 14/13,
  installation, restart, D-026, Release, and final Stage 4 remain incomplete.

## 2026-07-31 — CP10 Attempt005 opaque recovery gate-binding repair

- From clean commit `b2bd99c2ca94e512af5ac57d733eddc4075a0202`, tree
  `b10c1190c624155073c5a670d7e646ef23c9187d`, formal preparation created
  immutable RAB-4 `install-wal-rollback-4` and FLB-5
  `install-wal-rollback-launch-observer-5` exactly once. Both public validators
  returned `Valid=True`, `Errors=0`. Their canonical SHA-256 values were
  `10A4E34FF4791330AE978F4A07E838AB07210CE76F8334CD97EC6EB424266483`
  and `6C67F8EE8FDC34EB9238F8E3BCCB280FE3884D2D12FB45CF75A39226977552A2`.
- Root verification isolated and executed the complete actual generated
  `Assert-FormalPreLatch` runtime path without executing the observer top
  level, outer launcher, latch creation, or RunAs. It failed closed at exit 70
  with `Opaque recovery bindings drifted.` Attempt005 outer was never invoked
  and `launch-attempt.jsonl` was never created.
- The four-field comparison was minimized deterministically. Execution-state,
  recovery-toolchain, and repository authority hashes matched. Only the gate
  binding differed: RAB validator opaque authority returned
  `54814C482F0EE54AC4112826895ADBBE830D58963AE916FD0435359BEE512D5B`,
  while the FLB manifest contained
  `AA64692C7AA921784EA074E5859ADC903CDB1B781CEC0C0AD63820B5C91BC1E9`.
  The former is the RAB contract binding-manifest authority hash; the latter is
  the separately valid hash of the ordered actual gate-map content.
- The FLB contract builder had used
  `Authority.source.recoveryGateMapSha256` for both meanings. The generated
  observer first compares the manifest field with the RAB opaque authority and
  later independently recomputes the actual map hash against the authority
  source. The builder therefore made its own two runtime predicates mutually
  inconsistent.
- The repair reads the already length/SHA-bound RAB contract with strict UTF-8,
  validates the opaque gate hash shape, writes that authority value to the FLB
  binding manifest, and leaves the actual gate-map content hash unchanged in
  `authority.source`. A new cross-module regression case requires the generated
  FLB manifest value to equal the RAB public validator's opaque authority.
  RED reproduced the exact mismatch; GREEN passed
  `STAGE4_FORMAL_LAUNCHER_BUNDLE_PASS Cases=243 Assertions=313`. The focused C#
  bridge passed 1/1 with 0 failed and 0 skipped; both PowerShell parsers,
  `dotnet format --verify-no-changes`, `git diff --check`, and debug, TEMP,
  process, service, and latch residue checks passed.
- Reviewer returned `PASS`, `BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`. The focused
  two-file repair was committed as
  `f9df12b51274fac5e41dae82f4f2bef4fa7c3393`, tree
  `c152bee60ee7e61eb300f83e0e3dbdb4a19f0be9`. No UAC, RunAs, reconciler,
  WAL, state, journal, anchor, Program Files, ProgramData, service, process, or
  pipe mutation occurred.
- RAB-4, FLB-5, and Attempt005 are retained only as failure evidence and must
  not authorize a later launch after the toolchain commit changed. The next
  formal checkpoint must generate and independently validate immutable RAB-5
  and FLB-6/Attempt006, including the complete generated pre-latch runtime
  diagnostic, before any UAC activity. WAL rollback, anchors 14/13,
  installation, restart, D-026, Release, and final Stage 4 remain incomplete.

## 2026-07-31 — CP10 Attempt006 generated gate property-index repair

- From clean commit `60b3cb4dc5f6477eb756d7c433340a949204fd3b`, tree
  `0b47fe2f18164e5050da522889dc738611336f33`, formal preparation created
  immutable RAB-5 `install-wal-rollback-5` and FLB-6
  `install-wal-rollback-launch-observer-6` exactly once. Both public validators
  returned `Valid=True`, `Errors=0`. Their canonical SHA-256 values were
  `6F30FF6A2602C3547C664C9352E94A4C1AD66C62A3F9AD7D962FBB1CB1DE2482`
  and `BC23307ADB80784C263175E5F8E7077D42A74F9923722F1BF162EAB222A84928`.
- Root verification isolated the actual generated definitions and executed the
  complete `Assert-FormalPreLatch` path without executing the top level, outer
  launcher, latch creation, or RunAs. The observer passed the repaired opaque
  recovery binding and then stopped before recovery gate 1 with StrictMode
  `PropertyNotFoundStrict`: property `Name` could not be found.
- The generated loop used
  `$gates[$i].PSObject.Properties[0].Name`. In Windows PowerShell 5.1, the
  `PSMemberInfoIntegratingCollection` index expression selected a property
  named `0` instead of first materializing the collection as an array. It
  returned null, and StrictMode rejected the following `.Name` access.
  Attempt006 outer was never invoked and `launch-attempt.jsonl` was never
  created.
- The focused regression extracts the actual generated gate-loop text and
  executes it with the same RAB validator opaque gates and generated FLB
  contract. RED reproduced the generated runtime failure. The repair now
  materializes `$gateProperties=@($gates[$i].PSObject.Properties)` before
  reading elements 0 and 1. Gate count, exact property order, integer type,
  sequential gate ID/exit code, duplicate exit-code detection, and the final
  ordered map hash are unchanged.
- GREEN passed
  `STAGE4_FORMAL_LAUNCHER_BUNDLE_PASS Cases=243 Assertions=313`; the focused C#
  bridge passed 1/1 with 0 failed and 0 skipped. Both PowerShell parsers,
  `dotnet format --verify-no-changes`, `git diff --check`, the two-file boundary,
  and test/product process, service, and latch residue checks passed. Reviewer
  returned `PASS`, `BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`.
- The two-file repair was committed as
  `90705bc4587229b100a3dfdc689d3600d94469fe`, tree
  `31cfbb5e2ec83f7e2b951b06241ba76ea309b399`. No UAC, RunAs, reconciler, WAL,
  state, journal, anchor, Program Files, ProgramData, service, process, or pipe
  mutation occurred.
- RAB-5, FLB-6, and Attempt006 are retained only as failure evidence and must
  not authorize a later launch after the toolchain commit changed. The next
  formal checkpoint must generate and independently validate immutable RAB-6
  and FLB-7/Attempt007, including the complete generated pre-latch runtime
  diagnostic, before any UAC activity. WAL rollback, anchors 14/13,
  installation, restart, D-026, Release, and final Stage 4 remain incomplete.
