# FolderSessionLock Stage 4 工作区移交说明

更新时间：2026-07-31
执行环境：`FSL-STAGE4-VM`
项目范围：`LOCAL_SINGLE_USER_ADMINISTRATOR_ONLY`

## 1. 固定工作区

- Repository：`C:\FSL-Workspace\Repository`
- Project：`C:\FSL-Workspace\Repository\FolderSessionLock`
- Branch：`cp10-vm-transfer`
- 当前已提交代码 HEAD：`1a2f37ee827e32a28940773d96ea61615e51263c`
- 当前已提交 tree：`af6c93bcc4e27c70762bc58108c9a65aff8e959c`
- 固定 RunId：`20260727T144929Z-e5b6c040`
- 当前分支比 `github/cp10-vm-transfer` 领先 9 个本地提交；不得 push 或 force-push，除非用户再次明确授权。

恢复时必须以实际 `git status`、HEAD、tree 和外部 evidence 为准。不得 reset、clean、checkout 覆盖、删除 evidence 或恢复 VMware snapshot。

## 2. 已完成任务

### 2.1 CP9 基线

- Release build：0 warnings、0 errors。
- Core：174/174。
- App：462/462。
- Windows：140/140。
- 合计：776/776，0 failed，0 skipped。
- format、git diff、安全清理和系统残留检查通过。
- `FolderSessionLock-Stage4-Clean` snapshot attestation 已存在。

### 2.2 Stage 4 产品范围与主体实现

- 产品范围已冻结为本地单用户管理员。
- 不创建 `FSL-Standard`、`FSL-Admin` 或第二管理员账户。
- 支持当前管理员同账户 UAC consent；跨账户路径仅保留 fail-closed 自动测试。
- 本地 unsigned Release 被允许，Authenticode 必须如实记录为 `NotSigned`。
- 主 App、Broker、Named Pipe、ACL、recovery、readiness、WAL、SCM/LocalSystem 支撑、D-026 schema v2 与 Stage 4 formal tooling 已基本实现。
- D-026 威胁模型固定为 `TRUSTED_SINGLE_USER_STAGE4_EXECUTOR_MODEL`。

### 2.3 CP10 formal-preparation 修复

下列 generated pre-latch 缺陷均已确定根因、测试修复、root verification、reviewer PASS 并提交：

- generated PowerShell boolean literal。
- null ACL expectation coercion。
- opaque recovery gate binding。
- Windows PowerShell 5.1 property collection indexing。
- Git index cache `TREE` sibling ordering。
- RAB/observer Release fingerprint 算法不一致。

最新 Release fingerprint 修复：

- Commit：`1a2f37ee827e32a28940773d96ea61615e51263c`
- Tree：`af6c93bcc4e27c70762bc58108c9a65aff8e959c`
- FLB：248/248 cases，318/318 assertions。
- RAB：222/222 cases，309/309 assertions。
- Focused C# bridges：2/2，failed 0，skipped 0。
- Release build：0 warnings，0 errors。
- 三个 PowerShell 文件 parser errors：0。
- format、diff check、TEMP/process/service residue：通过。
- Reviewer：`PASS`，`BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`。

### 2.4 已退役的最新 formal artifacts

RAB-7、FLB-8 和 Attempt008 已永久退役，只能作为失败证据，不得修改、删除、重新生成或启动：

- RAB-7 root：`install-wal-rollback-7`
- RAB canonical：`9AA5EA45A1D677C42FAB0DDCD1E7DAB82311446AED1CB5B40E4E568762002505`
- RAB wrapper：`1A8A61CB7D7FD10853237349B0653907FA43D206C062960D31A36E4512161209`
- RAB contract：`F8A2DD8D1C30DFAD7186CAA902311EF5BCFCA8ED39159068779FB8E59FF58817`
- FLB-8 root：`install-wal-rollback-launch-observer-8`
- FLB canonical：`0EB2AD9B8C62D3D8FEC70B99F69EB7BBCA1BE95DD231BA2435578B36EACCF99E`
- FLB outer：`19121D260386E833941B53CDF8754F589911486EF96AF9FA07EFA86537D5348E`
- FLB observer：`F38011CF9267B6214DAE4EF3488481F69713583B788A9E1D8DA133878694F302`
- FLB contract：`57EE78670A58A07BB6BE396E80EE240EACF94FED34AA2FF35F3FDE38DEE59C60`
- Attempt：`CP10-IWRR-LAUNCH-ATTEMPT-008`
- Attempt008 latch：不存在。

Attempt008 只执行了 generated definitions-only pre-latch diagnostic。它在任何 observer top-level、outer、latch、RunAs 或 UAC 前以 exit 74 / `Release fingerprint drifted.` fail-closed。Release 22-file exact set没有漂移；缺陷是 RAB canonical record fingerprint 与旧 observer `Name|Length|Hash` 文本算法不一致，现已由上述 commit 修复。

## 3. 当前安全状态

- State：sequence 6 / `InstallStarted`。
- WAL：4 records，length 22920。
- WAL SHA-256：`C299D1FE85E542603BABF5DB4B38796343CD8158817853F9E1D53EEFD15CEF69`。
- State SHA-256：`553113C430F645EB911F4BDA3E8FF38FD27D541E0177645A123D20F5CEE17270`。
- Journal SHA-256：`FD1376C84C0588D0D9971F95719138DD570577F79883E7B143DC321F0D9397E8`。
- 当前 authenticated anchor generations：11/10。
- 成功 recovery 后预期 generations：14/13；当前不得提前声称完成。
- `C:\Program Files\FolderSessionLock`：存在、ordinary/non-reparse、为空。
- `C:\ProgramData\FolderSessionLock`：不存在。
- 产品进程、服务、测试 TEMP residue：0。
- Attempt008 latch：不存在。
- CBS RebootPending：true；Windows Update RebootRequired：false。
- Pending restart 尚未完成；当前 checkpoint 未授权重启。
- PendingFileRenameOperations 中仅保留已知无关 Edge 临时项；不得把它误报为产品成功或擅自清除。

## 4. 当前工作树断点

当前正在完成 Attempt008 修复后的文档 commit-freeze：

- `TASKS.md`：已更新但尚未 reviewer/commit。
- `DEVLOG.md`：已更新但尚未 reviewer/commit。
- `STAGE4-WORKSPACE-HANDOFF.md`：本移交文件，需纳入同一文档 checkpoint 的 root verification 与 reviewer。

如果恢复时实际工作树已经 clean 或 HEAD 已变化，不得回退到这里。应读取最新 `git log -5`、`git status` 和这三份文档，按实际最新断点继续。

## 5. 未完成任务

1. 完成当前三文档 checkpoint 的严格 UTF-8/CRLF、diff、事实、hash 和 residue root verification。
2. 取得文档 reviewer `PASS 0/0/0/0` 并创建本地 commit；不得 push。
3. Fresh stage_director 分配并生成：
   - RAB-8：`install-wal-rollback-8`
   - RAB contract：`FSL-CP10-INSTALL-WAL-ROLLBACK-RECOVERY-8`
   - FLB-9：`install-wal-rollback-launch-observer-9`
   - FLB contract：`FSL-CP10-INSTALL-WAL-ROLLBACK-LAUNCH-OBSERVER-9`
   - Attempt009：`CP10-IWRR-LAUNCH-ATTEMPT-009`
4. 对 RAB-8/FLB-9 执行 public/private validator、identity/ACL/hash 与 actual generated definitions-only pre-latch probe；不得执行 top-level/outer/UAC。
5. Formal preparation reviewer PASS 后，fresh stage_director 才可授权 Attempt009 唯一 one-shot observer/UAC。
6. UAC 后完成 WAL 5–7、rollback recovery、anchor rotation 14/13，并只删除已验证为空且安全的安装目录；不得执行第二次 Install。
7. 完成真实 VM 的 Broker、Named Pipe、SCM、LocalSystem、recovery/readiness、Program Files/ProgramData、ACL、D-026 验证。
8. 如权威 checkpoint 仍要求 restart/logoff，先保存 continuation state，再取得本次明确授权；不得复用历史重启授权。
9. 建立最终安装/环境状态，使当前 8 个 environment gates 全部真实通过，不得 skip 或修改断言绕过。
10. 完成最终完整回归：0 failed、0 skipped、Release build 0 warnings/errors、format/diff/residue 通过。
11. 发布仓库外正式 win-x64 Release，包含主 UI、Broker、Recovery/Service 必需 executable、依赖、`release-manifest.json`、`SHA256SUMS.txt`、`README-RUN.txt`。
12. 从仓库外目录完成真实 smoke test，记录 executable path、PID、command line、exit code、版本、SHA-256、UAC/service/recovery结果和 cleanup。
13. 最终 root verification 与 reviewer PASS 后，才可宣布 `FINAL STAGE4 DELIVERY COMPLETE`。

## 6. 下一步开发执行顺序

严格保持每个 checkpoint：

`stage_director → planner → coder/executor → root verification → reviewer`

任何时刻只允许一个 agent 和一个写入者。建议恢复后依次执行：

1. 只读确认 computer、branch、HEAD/tree、status、三文件 diff、RunId、RAB7/FLB8 hashes、latch 和 frozen state。
2. 完成当前文档 root verification/reviewer/commit。
3. Fresh stage_director 建立 RAB8/FLB9 formal-preparation checkpoint。
4. Planner 重建 exact ordered models；sole executor 各调用一次 `New-*`。
5. Root 只执行 validators 与 definitions-only pre-latch probe。
6. Reviewer PASS 后另开 Attempt009 UAC checkpoint。
7. 遇到安全桌面时只输出 `STAGE4 USER ACTION REQUIRED` 并等待用户点击“是”或取消；不得自动点击。
8. 持续推进 WAL/recovery/VM matrix/final tests/release/smoke/final review。

普通编译、测试、parser、format 或可定位实现缺陷应自动修复，不得因此请求用户继续。仅 UAC、凭据、真实签名材料、未授权 restart/logoff、VMware 宿主操作、未知数据删除、无法由权威文件决定的产品决策或连续三轮完全相同无新证据失败才暂停。

## 7. VM 重启后的恢复 Prompt

将以下内容作为 VM 重启后新的 Codex Prompt。若重启前已经产生更新提交，Prompt 要求以实际状态继续，不得 reset 到记录中的旧 HEAD。

```text
恢复 FolderSessionLock Stage 4 Goal。

运行环境：
- Computer: FSL-STAGE4-VM
- Repository: C:\FSL-Workspace\Repository
- Project: C:\FSL-Workspace\Repository\FolderSessionLock
- Branch: cp10-vm-transfer
- RunId: 20260727T144929Z-e5b6c040

开始前完整读取：
1. C:\Users\FSL-STAGE4-VME\.codex\attachments\f450e9ec-fab9-4bd2-8af1-c67f2c49f264\goal-objective.md
2. C:\FSL-Workspace\Repository\FolderSessionLock\STAGE4-WORKSPACE-HANDOFF.md
3. AGENTS.md、TASKS.md、DEVLOG.md 及八份 Stage 4 权威文档。

仅执行只读恢复门：确认 computer、branch、实际 HEAD/tree、git status/diff、最近 5 个 commit、RunId、state/WAL/journal/anchors、RAB7/FLB8 hashes、Attempt008 latch、Program Files/ProgramData、产品进程/服务/TEMP residue、CBS/WU/PFRO。

不得 reset、clean、checkout 覆盖、删除 evidence、恢复 VMware snapshot、重新生成旧 RAB/FLB/attempt、push 或 force-push。以实际工作树为权威；如果 HEAD 已超过 1a2f37ee827e32a28940773d96ea61615e51263c，不得回退。

已知安全事实：RAB7/FLB8/Attempt008 永久退役；Attempt008 outer/top-level/latch/UAC/RunAs 未执行；state sequence 6 / InstallStarted、WAL 4、anchors 11/10 保持冻结。最新已审核代码修复为 commit 1a2f37ee827e32a28940773d96ea61615e51263c，tree af6c93bcc4e27c70762bc58108c9a65aff8e959c。

从实际最新断点继续。若当前三文档 diff 尚未提交，先完成 STAGE4-WORKSPACE-HANDOFF.md、TASKS.md、DEVLOG.md 的 root verification → reviewer → 本地 commit；若已提交且 worktree clean，则 fresh stage_director 规划 RAB8/FLB9/Attempt009 formal preparation。

后续严格串行：stage_director → planner → sole coder/executor → root verification → reviewer。任何时刻只允许一个 agent/写入者，不得并行。普通技术失败自动诊断修复；只有 UAC 安全桌面、凭据、真实签名材料、当前 checkpoint 未授权的 restart/logoff、VMware 宿主操作、未知数据删除、权威文档无法决定的产品决策或三轮完全相同无新证据失败才暂停。

成功前不要结束 Goal。最终必须完成真实 UAC/Broker/SCM/LocalSystem/recovery/readiness/ACL/D-026/pending-restart、0 failed/0 skipped、仓库外 Release publish/smoke、cleanup 和最终 reviewer PASS。
```

## 8. 重启前保存要求

若后续 checkpoint 明确需要重启或注销，执行前必须：

1. 确保工作树不存在未提交的脆弱实现状态；文档断点已落盘。
2. 记录实际 HEAD/tree、RunId、state/WAL/anchor hashes 和下一命令。
3. 验证没有 active observer、Broker、testhost、service 或 writable handle。
4. 验证没有尚未持久化的 latch、临时 ACL 或 unknown Program Files/ProgramData 内容。
5. 输出 `STAGE4 USER ACTION REQUIRED` 并取得该 checkpoint 的 fresh restart/logoff 授权。
6. 重启后先执行 POST-RESTART gate，不能把“系统成功启动”自动当作产品验证通过。
