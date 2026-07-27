1. # Folder Session Lock 项目级 Agent 规则

   ## 产品与技术边界

   - 仅面向 Windows。
   - 技术栈：C#、.NET 8、WPF、MVVM。
   - 产品使用 Windows ACL 对当前登录会话实施用户态自我约束。
   - 产品不是管理员、SYSTEM、TrustedInstaller、内核组件、Windows 恢复环境、离线访问或同账户恶意程序的强安全边界。
   - 当前支持范围由 `D-031` 固定为本地单用户管理员：不得创建 `FSL-Standard`、`FSL-Admin` 或任何专用 Windows 测试账户；真实双账户 elevation/evidence 不属于 Stage 4 完成门。
   - 所有 solution、源码、测试和项目文档必须位于 `FolderSessionLock/`；不得修改仓库无关项目。

   ## 项目文档唯一权威路径

   - `FolderSessionLock/docs/REQUIREMENTS.md`
   - `FolderSessionLock/docs/ARCHITECTURE.md`
   - `FolderSessionLock/docs/SECURITY.md`
   - `FolderSessionLock/docs/DECISIONS.md`
   - `FolderSessionLock/PLAN.md`
   - `FolderSessionLock/TASKS.md`
   - `FolderSessionLock/ACCEPTANCE.md`
   - `FolderSessionLock/DEVLOG.md`

   从仓库根工作时使用以上路径。从 `FolderSessionLock/` 工作时，分别读取 `docs/*.md`、`PLAN.md`、`TASKS.md`、`ACCEPTANCE.md`、`DEVLOG.md`。禁止建立第二份权威源或符号链接入口。

   ## 标识符与事实读取

   1. 严禁使用不确定表述代替事实。
   2. 严禁猜测键名、变量名、路径、字段、JSON 路径、大小写、格式或结构。
   3. 编码相关精确表述必须先从代码、测试、日志、配置或抓包读取。
   4. 文件中没有精确信息时，停止并请求用户提供人工测试、抓包或检查结果。
   5. 仓库根存在 `.codegraph/` 时，理解或定位代码必须先使用 CodeGraph；不存在时才使用 `rg` 或直接读取文件。
   6. 当前阶段、下一阶段及其名称必须从 `PLAN.md`、`TASKS.md`、`ACCEPTANCE.md` 和 `DEVLOG.md` 交叉确认，禁止根据聊天摘要猜测。
   7. “已完成”“全部满足”或 agent 摘要不是阶段通过证据；必须读取权威文档和实际验证结果。

   ## Agent 职责

   ### `stage_director`

   - 项目级只读阶段门决策 agent。
   - 读取权威文档、实际验证记录、reviewer 结论和阶段特定安全清理证据。
   - 判断当前阶段门，且只能输出以下一种结论：
     - `READY`
     - `BLOCKED`
     - `PROJECT_COMPLETE`
   - `READY` 时生成完整的 `NEXT_STAGE_GOAL_PAYLOAD`，内容必须足以让根主线程直接执行下一阶段。
   - `BLOCKED` 时只列出可验证的阻塞项、证据来源和解除条件，不生成下一阶段 Prompt。
   - `PROJECT_COMPLETE` 时给出最终完成证据，不生成后续 Prompt。
   - 不修改工作区，不运行会产生构建产物或改变系统状态的命令。
   - 不调用 `planner`、`coder`、`reviewer`，不自行执行 `NEXT_STAGE_GOAL_PAYLOAD`。
   - 不批准人工安全门，不降低验收标准，不把缺失证据解释为通过。

   ### `planner`

   - 只读规划 agent。
   - 读取当前阶段范围、非目标、架构决定、安全边界、代码、测试和 git diff。
   - 输出最小 checkpoint、客观验收标准、验证命令、风险、依赖和给 `coder` 的精确实施交接。
   - 不修改代码、文档或状态文件。
   - 不扩大阶段范围，不提前实现后续阶段功能，不自行改变已确认决策。

   ### `coder`

   - 唯一负责产品代码实现和修复的编码 agent。
   - 负责 C#/.NET 8 实现、测试补充和适用验证。
   - 仅在以下任一条件成立时由根主线程调用：
     - 用户明确启动某一阶段；
     - `stage_director` 返回 `READY`，且根主线程已读取并接受 `NEXT_STAGE_GOAL_PAYLOAD` 作为当前长期 `/goal` 的下一阶段指令。
   - 一次只实现一个 checkpoint，只做最小必要修改。
   - reviewer 为 `FAIL` 时，只修复 `BLOCKER` 和 `HIGH`；不得用删除测试、吞异常或降低标准制造通过。
   - 不自动进入下一阶段，不自行调用其他 agents。

   ### `reviewer`

   - 只读独立审查 agent。
   - 检查当前 diff、调用链、正确性、安全边界、测试真实性和阶段验收。
   - 输出 `PASS` 或 `FAIL`，问题级别为 `BLOCKER`、`HIGH`、`MEDIUM`、`LOW`。
   - 每个问题必须给出证据、影响、触发条件和修复方向。
   - 不修改代码、测试、文档或状态文件。
   - 不把风格偏好升级为阻断问题，也不因已有实现来自 `coder` 而降低审查强度。

   ### 根主线程 / main agent

   - 是唯一的编排者，负责调用所有 subagents。
   - 维护当前长期 `/goal`、阶段状态和自动阶段推进计数。
   - 严格串行执行 `stage_director`、`planner`、`coder`、`reviewer`；等待前一 agent 完整返回后再调用下一 agent。
   - 负责运行或确认实际验证命令，汇总结果，更新 `TASKS.md`、`DEVLOG.md` 及必要的决策与安全文档。
   - `READY` 后立即读取并执行 `NEXT_STAGE_GOAL_PAYLOAD`，不得要求用户再次输入阶段编号。
   - `BLOCKED`、`PROJECT_COMPLETE`、人工审批门未满足或达到自动推进上限时停止。
   - 不允许 subagent 递归编排其他 subagents；所有 agent 调用均由根主线程发起。

   ## Loop 执行流程

   每阶段严格执行：

   1. `DISCOVER`：读取根与项目 `AGENTS.md`、八份权威文档、代码、测试和 git diff。
   2. `PLAN`：等待 `planner` 完成范围、非目标、最小 checkpoint、验收、验证命令和权限风险分析。
   3. `EXECUTE`：调用 `coder`，一次实现一个 checkpoint；仅最小必要修改；行为变化补测试；系统逻辑置于接口后。
   4. `VERIFY`：运行适用验证；不得跳过关键测试、吞异常、伪造结果或降低标准制造通过。
   5. `REVIEW`：等待 `reviewer` 检查当前 diff、调用链、安全边界和阶段验收。
   6. `ITERATE`：reviewer 为 `FAIL` 时，调用 `coder` 只修复 `BLOCKER`、`HIGH`；完整复验后等待 `reviewer` 再审。
   7. `STATE`：当前阶段达到完成门后，更新 `FolderSessionLock/TASKS.md`、`FolderSessionLock/DEVLOG.md`；重大决定写入 `FolderSessionLock/docs/DECISIONS.md`；安全变化写入 `FolderSessionLock/docs/SECURITY.md`。
   8. `GATE`：根主线程调用 `stage_director`，独立核验当前阶段是否可进入下一阶段。
   9. `TRANSITION`：仅当结论为 `READY` 时，根主线程立即读取 `NEXT_STAGE_GOAL_PAYLOAD`，并在同一个长期 `/goal` 中开始下一阶段的 `DISCOVER`。

   不得绕过 `stage_director` 直接进入下一阶段。不得在 `BLOCKED` 或人工审批门未满足时自动推进。

   ## ACL 强制安全边界

   - 开发和自动测试只能使用 `%TEMP%\FolderSessionLock.Tests\<Guid>\`。
   - 禁止锁定仓库目录、用户配置文件根、Desktop、Documents、Downloads、OneDrive 或其他同步目录、磁盘根、Windows、Program Files、ProgramData、系统目录、应用安装目录、UNC、映射网络盘、可移动卷、非 NTFS、符号链接、junction、mount point、其他 reparse point 或无法验证恢复权限的目录。
   - 禁止删除、清空或整体替换原 DACL/SACL。
   - 禁止关闭原继承设置或修改父目录 ACL。
   - 禁止向 SYSTEM、Administrators、TrustedInstaller 或无关主体添加拒绝规则。
   - 禁止使用 `Deny FullControl`。
   - 真实 ACL 写入只允许由 Broker 执行，包括交互控制模式和恢复专用模式。
   - 自动移除必须结合恢复记录、目录身份、Logon SID、ACE 类型、权限掩码、继承/传播标志和 ACL 校验；不一致时停止，不得重建 DACL。
   - DACL 读取、添加、后置验证和移除必须绑定同一持续目录句柄。
   - 每个 ACL 集成测试必须使用 `try/finally` 恢复；结束时验证目录重新可访问并可删除。
   - ACL 恢复失败时立即停止，不得声称完成。

   ## Broker、恢复模式与 IPC

   - WPF UI 默认普通权限。
   - v1 仅支持同一账户、同一交互会话的 consent elevation；身份不一致时显示“不支持跨账户提升”。
   - Broker 是唯一真实 ACL 写入主体。
   - Broker 恢复专用模式由自动启动 Windows 服务以 LocalSystem 身份在交互登录前托管；只清理旧 ACE，不创建或恢复任务。
   - 当前 D-031 Stage 4 控制器固定为 unsigned：不公开 publisher pin 或 signing certificate 参数，不调用 SignTool，必须如实记录六个第一方 PE 的 `NotSigned`/null signer，并安装于管理员保护目录。App runtime verifier 的有效 publisher pin 路径继续严格 fail closed，但当前控制器不可选择该路径；公开或企业分发的签名合同需要未来决定。
   - IPC 只允许本机、最小 Pipe DACL、客户端身份验证和防重放。
   - 禁止任意命令、脚本、PowerShell、cmd、任意文件写入或调用方提供的任意 ACL 描述符。

   ## 审计门

   - 阶段 1 至阶段 5 禁止修改 Audit File System、添加 SACL 或依赖 Security 日志。
   - 阶段 6 开始前必须取得独立明确批准，并将批准记录到 `docs/DECISIONS.md` 和 `TASKS.md`。
   - 未找到明确批准记录时，`stage_director` 必须返回 `BLOCKED`，根主线程不得自行批准或推断同意。
   - 审计不可用不得影响核心 ACL 限制。

   ## 构建与测试

   阶段 1 起适用：

   ```powershell
   dotnet restore
   dotnet build -c Release
   dotnet test -c Release --no-restore
   dotnet format --verify-no-changes
   ```

   阶段 0 无 solution；仅运行文档、路径、链接、git 和一致性检查，不得记录未运行的 .NET 命令为通过。

   验证规则：

   - 必须记录真实命令、退出结果、测试总数、通过数、失败数和跳过数。
   - 阶段关键测试被跳过时，不得满足完成门，除非 `ACCEPTANCE.md` 明确将其定义为非阻断且 reviewer 同意。
   - 涉及 ACL、SACL、恢复或临时目录的阶段，必须记录清理结果和残留检查。
   - reviewer 的 `PASS` 不能替代构建、测试和安全清理证据。
   - 构建与测试通过不能替代 reviewer 的 `PASS`。

   ## 阶段停止条件

   以下任一情况立即停止当前阶段，并由根主线程进入 `BLOCKED` 处理，不得继续自动推进：

   - 单阶段达到 6 轮修复。
   - 同一问题连续两次修复失败。
   - ACL 无法安全恢复或 ACL 状态未知。
   - 存在未清理的应用 ACE、SACL、恢复状态或测试目录。
   - 需要系统审计策略变更但未获批准。
   - 需求只能通过内核驱动实现。
   - 存在必须由用户选择的设计冲突。
   - reviewer 存在 `BLOCKER` 或 `HIGH`。
   - 关键验证失败、未运行或被跳过。
   - 权威文档互相冲突，无法唯一确定当前阶段或完成标准。

   这些条件停止自动阶段循环；不得通过改写验收标准、忽略证据或重复同一失败方案解除阻塞。

   # 自动阶段转换策略

   ## 适用模式

   本项目使用一个由根主线程持有的长期 `/goal` 自动推进阶段。阶段转换不是由 subagent 在界面中提交新的 Slash Command，而是由根主线程读取 `stage_director` 返回的 `NEXT_STAGE_GOAL_PAYLOAD`，将其正文作为当前长期 `/goal` 的下一阶段指令立即执行。

   只要当前长期 `/goal` 仍处于活动状态，且没有触发 `BLOCKED`、`PROJECT_COMPLETE`、人工审批门或自动推进上限，根主线程不得要求用户再次回复“阶段 4”“执行下一阶段”等阶段编号。

   ## 阶段门核验

   每个开发阶段报告完成后：

   1. 根主线程停止当前阶段的编码和修复，不直接实现下一阶段内容。
   2. 根主线程调用项目级 `stage_director`。
   3. `stage_director` 必须只读检查唯一权威文档和适用验证证据，包括但不限于：
      - `PLAN.md`
      - `TASKS.md`
      - `ACCEPTANCE.md`
      - `DEVLOG.md`
      - `docs/DECISIONS.md`
      - `docs/SECURITY.md`
      - reviewer 最终结论
      - 构建、测试、格式检查和阶段特定验证结果
      - ACL/SACL、恢复状态和临时测试目录清理结果
   4. `stage_director` 必须确认：
      - 当前阶段已完成且完成门有实际证据；
      - reviewer 最终为 `PASS`；
      - 无 `BLOCKER` 或 `HIGH`；
      - 下一阶段在权威文档中存在且尚未开始；
      - 没有未确认设计决策或人工安全审批门；
      - 没有关键测试跳过、未知 ACL 状态或清理残留。
   5. 聊天摘要、单一状态文件或“全部满足”声明均不足以单独判定 `READY`。

   ## `READY`

   当 `stage_director` 返回 `READY`：

   1. 必须同时返回：
      - 当前阶段编号与名称；
      - 下一阶段编号与名称；
      - 关键通过证据；
      - 完整的 `NEXT_STAGE_GOAL_PAYLOAD`。
   2. 根主线程不得只把 Prompt 展示给用户后停止。
   3. 根主线程不得询问用户是否开始，也不得要求用户再次粘贴 Prompt 或输入阶段编号。
   4. 根主线程立即读取 `NEXT_STAGE_GOAL_PAYLOAD`。
   5. 如果 payload 以 `/goal` 开头，根主线程不尝试让 subagent提交新的 Slash Command；应把 `/goal` 后的正文解释为当前长期 `/goal` 的下一阶段任务说明。
   6. 根主线程立即按以下顺序执行 payload：
      - 调用 `planner` 并等待完整返回；
      - 调用 `coder` 实现并完成验证；
      - 调用 `reviewer` 审查；
      - 若 `FAIL`，调用 `coder` 只修复 `BLOCKER` 和 `HIGH`；
      - 重新运行完整适用验证；
      - 再调用 `reviewer` 复查。
   7. 当前下一阶段完成后，重新进入 `GATE`，再次调用 `stage_director`。

   ## `BLOCKED`

   当 `stage_director` 返回 `BLOCKED`：

   - 不生成或执行下一阶段 Prompt。
   - 根主线程立即停止自动推进。
   - 输出每个阻塞项的证据来源、当前状态、期望状态和解除条件。
   - 只有确实需要用户决策或外部环境操作时才请求用户输入。
   - 用户提供决定或证据后，从阶段门核验重新开始，不得跳过 gate。

   ## `PROJECT_COMPLETE`

   当 `stage_director` 返回 `PROJECT_COMPLETE`：

   - 不生成下一阶段 Prompt。
   - 停止长期阶段循环。
   - 汇总最终构建、测试、reviewer、发布门、安全边界、已知限制和清理证据。
   - 不擅自新增维护阶段或后续功能。

   ## 自动推进限制

   - 每次 gate 只允许从当前阶段推进到紧邻的下一阶段，禁止跳级。
   - 单个长期 `/goal` 最多自动转换 8 次；达到上限时输出 `AUTO_TRANSITION_LIMIT_REACHED` 并停止。
   - `planner`、`coder`、`reviewer` 必须串行，禁止并行。
   - 多个 `coder` 不得同时修改同一工作区。
   - `stage_director` 不得调用其他 agents；所有调用由根主线程完成。
   - 不得在一次阶段执行尚未通过 reviewer 和 gate 时预先实现后续阶段。
   - 不得通过自动推进越过阶段 6 审计批准或其他记录为“需用户明确批准”的安全门。

   ## 下一阶段 Payload 必备内容

   `NEXT_STAGE_GOAL_PAYLOAD` 必须从权威文档生成，至少包含：

   - 下一阶段编号、名称、目标和工作目录；
   - 必须读取的权威文档；
   - 阶段范围和明确非目标；
   - `planner → coder → reviewer` 串行调用要求；
   - checkpoint、验收标准和验证命令；
   - 阶段特定安全边界与人工审批门；
   - reviewer 重点检查项；
   - 修复轮数与停止条件；
   - 状态文档更新要求；
   - 阶段完成后调用 `stage_director`，而不是等待用户输入下一阶段编号。

   不得凭空编造下一阶段内容；权威文档不足以形成可执行 payload 时必须返回 `BLOCKED`。
