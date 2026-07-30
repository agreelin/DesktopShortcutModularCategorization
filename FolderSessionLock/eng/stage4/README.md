# FolderSessionLock Stage 4 VM controller

This controller is the repository-side implementation of the CP10/D-025/D-026
workflow. It must be run only in the disposable `FSL-STAGE4-VM`. It never
restores, creates, or deletes VMware snapshots and it never accepts arbitrary
service names, installation roots, ProgramData roots, pipe names, ACLs, or
commands.

The D-031 supported deployment is
`LOCAL_SINGLE_USER_ADMINISTRATOR_ONLY`. Do not create `FSL-Standard`,
`FSL-Admin`, any other dedicated Windows test account, or a test signing
certificate. Use only the current local administrator account and same-account
UAC consent.

## Current frozen recovery authority

The reviewed capability baseline is commit
`aa60c1c6cea2ea05648824acb10f5f3ec2342549`, tree
`9b97428f3988c962e7d4b6899d3521f9cd3b7fc1`. Reviewer result is `PASS` with
`BLOCKER/HIGH/MEDIUM/LOW = 0/0/0/0`. Validation passed RAB 218/305, Formal
229/299, Stage 4 tooling 7/7, and non-environment-dependent 807/807. The
unfiltered suite is Core 174/174, App 494/501, Windows 140/141: 808 passed,
8 environment failures, and 0 skipped. Build is 0 warnings/0 errors;
format, parser, diff, and exact exports pass.

The public current-HEAD context gate is unchanged. Supplying the old frozen
`ReleaseRoot` to the public context path exits 2. Recovery uses a private seam:
the RAB validator produces verified dual authority; the private adapter binds
runId, current machine, `cp10-vm-transfer`, execution and recovery commits and
trees, state, and independently derived repository/evidence/install/
ProgramData/external-anchor paths. It then runs the repository and mutation
gates. Authority paths are comparison evidence, never selectors. The elevated
wrapper calls the verified resolver, private adapter, and reconciler exactly
once. It has no public controller/install, retry, fallback, or second execution
path.

The current frozen execution is commit
`3170d89cfd6066ba494170826cd43626d83c6789`, tree
`6bee7c4db4c9adde0612aa7c67467a331d20263e`, with state sequence 6 /
`InstallStarted` and WAL 4. Authenticated current pre-recovery external anchors
are latest/previous generations 11/10; generations 14/13 are only the future
successful-recovery postcondition. Recovery has 3 directories/8 files and
Release has 22 files. `C:\Program Files\FolderSessionLock` exists and is empty;
`C:\ProgramData\FolderSessionLock` is absent. No final Formal source,
Attempt003, or new latch exists.

Operator order is strict:

1. Commit-freeze the synchronized documentation.
2. Prepare and validate the final RAB exact-two and FLB exact-three objects.
   Preparation must not execute the outer, observer, RunAs, UAC, reconciler, or
   any system mutation.
3. Execute the single one-shot observer/UAC using only those final objects.
4. Only after recovery succeeds, request fresh authorization for restart.
5. Complete the remaining D-026 and unsigned Release gates.

Do not modify the repository or restart/log off Windows after final object
generation. Generation is not execution, recovery success is not restart
authorization, and VM/D-026/restart/Release/Stage completion remain open until
their own evidence gates pass.

Run `Preflight` from a non-elevated Windows PowerShell. Its platform
attestation sources are read-only: the fixed 64-bit registry value
`HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled`,
native `Tbsi_GetDeviceInfo`, and the current WindowsPrincipal token. It requires
an `Int32` DWORD value of exactly `1`, a successful TPM 2.0 device descriptor,
and a non-administrator token. `Preflight` itself is not a read-only command: it
writes the new run's evidence, journal, state cache, independent state anchor,
and DPAPI/HMAC-protected external anchor. The raw registry/TBS fields are
preserved in `prestate.json`; readiness remains `DeferredUntilElevated` with
`SecureBootVerified=false`, `TpmPresentVerified=false`,
`TpmReadyVerified=false`, and a null `PlatformReadinessVerifiedUtc`.

Run `VerifyPlatformReadiness` later from an elevated Windows PowerShell:

```powershell
$runId = '20260725T120000Z-0123abcd'
.\eng\stage4\Invoke-Stage4.ps1 Preflight -RunId $runId
.\eng\stage4\Invoke-Stage4.ps1 VerifyPlatformReadiness -RunId $runId
```

`VerifyPlatformReadiness` repeats the fixed base gate, requires an administrator
token, requires strict Boolean `true` from
`Confirm-SecureBootUEFI`, and requires strict Boolean `TpmPresent=true` and
`TpmReady=true` from `Get-Tpm`. It revalidates the anchored preflight evidence,
then durably records `PlatformReadinessVerified`. It does not create, import,
trust, export, or delete a certificate. Every later command rejects
`DeferredUntilElevated` before entering its handler or performing a mutation.
Only the complete `VerifiedElevated` tuple with all three verification flags
true and a round-trip verification timestamp can enter those handlers.
The dispatcher readiness check is itself completely read-only: it validates
both protected external anchor slots, their generation and bindings, the
anchored journal prefix and hash chain, the state anchor, and the WAL binding.
One authoritative snapshot takes its state only from the latest valid HMAC slot
and the internally anchored journal prefix. It classifies the rebuildable cache
as exact, missing, or mismatched and the WAL as exact, recoverable tail, or a
fatal missing/truncated/prefix-mismatch condition. The dispatcher and mutation
reader consume that same snapshot; the readiness check never repairs,
reconciles, truncates, or deletes. Deferred readiness therefore leaves a
missing or torn cache, a recoverable WAL tail, and an incomplete journal tail
byte-for-byte unchanged. A complete unanchored journal record is rejected.
Legacy anchored state with all five readiness properties absent is normalized
to deferred only in memory; partial or invalid readiness state is rejected.

The supported commands, in order, are:

1. `Preflight`
2. `VerifyPlatformReadiness`
3. `Publish`
4. `VerifyAuthenticode`
5. `Install`
6. `Verify`
7. `PrepareLogout` or `PrepareRestart`
8. `Resume`
9. `Uninstall`
10. `Cleanup`
11. `FinalizeEvidence`

`Publish` creates a Release, `win-x64`, framework-dependent, multi-file package
outside the repository. App and Broker are staged separately and merged only
after collision hashes agree. The App receives the non-secret
`BrokerPublisherThumbprint` MSBuild property with an exact empty value. The
public controller exposes no publisher-pin or signing-certificate parameter
and has no signed execution branch. The fixed first-party PE set is verified as actual
`NotSigned` with a null signer and recorded with SHA-256:
`FolderSessionLock.App.exe`, `FolderSessionLock.App.dll`,
`FolderSessionLock.Broker.exe`, `FolderSessionLock.Broker.dll`,
`FolderSessionLock.Core.dll`, and `FolderSessionLock.Windows.dll`. The
controller rejects a missing or additional `FolderSessionLock.*` executable or
DLL. The current controller never invokes SignTool. The App runtime verifier
retains its separately tested valid-pin fail-closed capability for a future
runtime configuration, but Stage 4 cannot select it. The
executable and every path ancestor through the volume root are opened by handle
and bound to final path, non-reparse state, volume/file identity, owner, and
effective mutation ACL.

The controller persists every legal transition in an append-only, write-through
JSONL hash-chain journal plus an independently replaced anchor. `state.json` is
only a rebuildable cache. Each command revalidates run ID, machine, branch,
commit, sequence, previous-entry hash, and anchored journal length. A missing or
torn cache and an incomplete torn tail are recoverable; truncation, anchor
mismatch, hash-chain tampering, and a complete unanchored record are rejected.
An additional anchor outside the repository is stored below the current user's
LocalApplicationData. A random HMAC key is protected with current-user DPAPI;
alternating write-through slots bind machine/run/repository/branch/commit and
the exact prestate, journal, WAL, state cache, and internal-anchor lengths and
hashes. After `VerifiedElevated`, the mutation reader repairs a missing or torn
cache from authoritative journal state, removes an incomplete journal tail, and
truncates WAL bytes after the protected prefix before handler state mutation.
A missing or shortened protected WAL, or any protected-prefix mismatch, fails
closed without entering a handler or changing bytes. Cache bytes never replace
the authoritative journal state.

`release-descriptor.json` freezes the exact case-sensitive release file set and
binds the manifest, checksums, payload lengths, and SHA-256 values. Verification
and installation never regenerate frozen metadata. Schema 3 freezes the complete
ordered primitive plan and plan hash before `Begin`. Every copied file uses a
transaction-derived same-directory temp name. Before the temp is created, its
absence and the target-parent/source identities and ACLs are durably bound by
`Intent`. The copy is an explicit write-through loop followed by `Flush(true)`,
proof, and a non-overwrite rename. Install, uninstall, and cleanup use the same
durable adapter. Recovery rolls an uncommitted install back in reverse order;
destructive uninstall/cleanup transactions only continue an exactly identified
deletion. Unknown operation kinds and replacement objects fail closed.

`Verify` runs the three test assemblies in one `dotnet vstest` invocation and
uses its direct TRX as the canonical `test-results.trx`. Validation requires one
counter set, `executed = passed = total`, zero failed/not-executed/error/
timeout/aborted, exactly `total` unit-test results, and every outcome `Passed`.
No synthetic merge or rewritten test count is permitted.

`PrepareLogout` and `PrepareRestart` only validate and persist continuation
state. They do not log out or restart Windows. The target must be an existing
`%TEMP%\FolderSessionLock.Tests\<Guid>` directory. The human/root controller
remains responsible for authorizing the actual logout or restart.

`FinalizeEvidence` refuses to invent missing results. Every D-026 required file
must already exist and be nonempty; the aggregate TRX must prove zero failed and
zero not-executed tests; and an external reviewer verdict containing an explicit
`PASS` or `FAIL` line must be supplied. The root verifier must also provide
`scenario-results.json` from the actual VM commands and human observations. It
uses schema v2 and exact top-level fields: `schemaVersion`, `runId`,
`sameAccountConsentPassed`, `preLoginRecoveryPassed`, `aclRestored`,
`temporaryDirectoriesRemoved`, `recoveryRecordsRemoved`,
`remainingRisks`, and nonempty `scenarios`; each scenario supplies
`scenarioId`, `description`, `expectedResult`, `actualResult`, `result`, and
existing `evidenceFiles`. A `FAIL`, `BLOCKED`, false completion boolean, missing
evidence file, or malformed value prevents finalization. All generated JSON and
text is UTF-8 without BOM. After final state and command evidence are durable,
finalization retires the DPAPI key and both external-anchor slots and requires
the run-specific external anchor directory to be absent.

Finalization also re-reads the frozen release through the protected state's
`ReleaseRoot` and `ReleaseDescriptorSha256`, revalidates its exact file set and
payload hashes, requires the exact six first-party PE files, and compares every
ordered `signature-verification.txt` SHA-256 with the actual frozen PE. A
different value is rejected even when it is a well-formed uppercase 64-hex hash.

Exit codes are fixed:

| Code | Meaning |
| ---: | --- |
| 0 | Success |
| 2 | Invalid arguments |
| 3 | Environment or authorization gate |
| 4 | Pre-existing conflict |
| 5 | Authenticode policy verification |
| 6 | Installation or ACL |
| 7 | SCM or service |
| 8 | Validation or evidence |
| 9 | Cleanup refused or incomplete |

The fixed service is `FolderSessionLockRecovery`; its binary is
`"%ProgramFiles%\FolderSessionLock\FolderSessionLock.Broker.exe" --mode
recovery-service`. The controller refuses cleanup if pre-state or current
identity indicates that a service, directory, certificate, or other object was
not created by the current run. It also binds cleanup to the recorded final
path, NTFS file ID, SHA-256, release-manifest hash, and exact protected ACL.
Unknown files, reparse points, replacements, identity drift, or ACL drift stop
cleanup; product directories are never recursively deleted. A VM test
current run creates no certificate. Cleanup and final residue checks prove that
the run-specific subject is absent from `LocalMachine\My` and
`LocalMachine\TrustedPeople`; unknown pre-existing certificates are never
deleted. `cleanup-results.txt` records `CertificatesRemaining=0`, and
`FinalizeEvidence` requires that exact line.

After preflight, repository changes are allowed only below the current RunId
evidence directory. Service deletion requires an exact structured SCM snapshot
in the stopped state. Dormant signing-tool trust helpers are not reachable from
the fixed unsigned public controller. Reviewer evidence must contain exactly
one uppercase `PASS` or `FAIL` token;
only `PASS` can finalize.

The non-privileged WAL contract uses real Windows PowerShell 5.1 worker
processes and parent-side `Process.Kill`. Every worker follows the production
plan/executor/reconcile path with an 8 MiB-or-larger payload for the
mid-write case.

| Kill boundary | Required reconciliation result |
| --- | --- |
| `AfterBegin`, `AfterIntent` | No final/temp; `Aborted` only after every Intent is `RolledBack` |
| `AfterTempCreate`, `DuringTempWrite` | Delete only an ordinary, single-link, non-reparse source prefix under the bound parent |
| `AfterTempFlush`, `AfterRename`, `AfterApplied` | Remove the exactly proven temp/final and finish rollback |
| `AfterCommit` | Keep the exactly proven final |

Every successful reconciliation is run a second time to prove idempotence.
Negative cases cover wrong temp name/parent, pre-existing temp, wrong prefix,
changed source, replaced parent, reparse object, hard link, unsafe DACL,
oversize temp, temp plus final, and partial final. Each negative object must be
preserved, and no false `RolledBack` or `Aborted` record may be appended.
