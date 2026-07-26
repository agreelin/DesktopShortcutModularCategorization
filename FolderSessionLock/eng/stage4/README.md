# FolderSessionLock Stage 4 VM controller

This controller is the repository-side implementation of the CP10/D-025/D-026
workflow. It must be run only in the disposable `FSL-STAGE4-VM`. It never
restores, creates, or deletes VMware snapshots and it never accepts arbitrary
service names, installation roots, ProgramData roots, pipe names, ACLs, or
commands.

Run `Preflight` from a non-elevated Windows PowerShell. It reads only the fixed
64-bit registry value
`HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled`,
native `Tbsi_GetDeviceInfo`, and the current WindowsPrincipal token. It requires
an `Int32` DWORD value of exactly `1`, a successful TPM 2.0 device descriptor,
and a non-administrator token. The raw registry/TBS fields are preserved in
`prestate.json`; readiness remains `DeferredUntilElevated` with all three
verification flags false and no verification timestamp.

Run `CreateTestCertificate` later from an elevated Windows PowerShell:

```powershell
$runId = '20260725T120000Z-0123abcd'
.\eng\stage4\Invoke-Stage4.ps1 Preflight -RunId $runId
.\eng\stage4\Invoke-Stage4.ps1 CreateTestCertificate -RunId $runId
```

Before any certificate mutation, `CreateTestCertificate` repeats the fixed base
gate, requires an administrator token, requires strict Boolean `true` from
`Confirm-SecureBootUEFI`, and requires strict Boolean `TpmPresent=true` and
`TpmReady=true` from `Get-Tpm`. It revalidates the anchored preflight evidence,
durably records `PlatformReadinessVerified`, then records
`CertificateCreating`. Every other command rejects
`DeferredUntilElevated` before entering its handler or performing a mutation.
Legacy anchored state with all five readiness properties absent is normalized
to deferred only in memory; partial or invalid readiness state is rejected.

`CreateTestCertificate` creates a seven-day, non-exportable, self-signed VM test
certificate. It is explicitly not a production certificate. The public
certificate may be trusted only inside the disposable VM. No PFX or private key
is written to the repository.

The supported commands, in order, are:

1. `Preflight`
2. `CreateTestCertificate`
3. `Publish`
4. `VerifySignature`
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
`BrokerPublisherThumbprint` MSBuild property. When a signing thumbprint is
provided (or this run created the VM test certificate), the fixed first-party PE
set is signed and verified with SHA-256:
`FolderSessionLock.App.exe`, `FolderSessionLock.App.dll`,
`FolderSessionLock.Broker.exe`, `FolderSessionLock.Broker.dll`,
`FolderSessionLock.Core.dll`, and `FolderSessionLock.Windows.dll`. The
controller rejects a missing or additional `FolderSessionLock.*` executable or
DLL. SignTool is accepted only from the x64 Windows Kits installation tree;
`PATH` is not a trust source. Acceptance uses native `WinVerifyTrust` plus the
approved Microsoft signing-key SPKI SHA-256 allowlist, never a certificate
Subject string. The executable and every path ancestor through the volume root
are opened by handle and bound to final path, non-reparse state, volume/file
identity, owner, and effective mutation ACL. The complete descriptor must be
unchanged after every SignTool invocation.

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
hashes. WAL bytes after the last protected slot are treated only as a crash tail
and truncated before parsing.

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
contains `schemaVersion`, `runId`, the five D-026 boolean results,
`remainingRisks`, and nonempty `scenarios`; each scenario supplies
`scenarioId`, `description`, `expectedResult`, `actualResult`, `result`, and
existing `evidenceFiles`. A `FAIL`, `BLOCKED`, false completion boolean, missing
evidence file, or malformed value prevents finalization. All generated JSON and
text is UTF-8 without BOM. After final state and command evidence are durable,
finalization retires the DPAPI key and both external-anchor slots and requires
the run-specific external anchor directory to be absent.

Exit codes are fixed:

| Code | Meaning |
| ---: | --- |
| 0 | Success |
| 2 | Invalid arguments |
| 3 | Environment or authorization gate |
| 4 | Pre-existing conflict |
| 5 | Signing or publisher verification |
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
certificate is removed from both `LocalMachine\My` and
`LocalMachine\TrustedPeople` and cleanup succeeds only after proving that the
run-specific subject is absent from both stores.

After preflight, repository changes are allowed only below the current RunId
evidence directory. Service deletion requires an exact structured SCM snapshot
in the stopped state. SignTool is also bound to its final path, non-reparse
ancestors, trusted owner/DACL, Microsoft signer, and unchanged file identity.
Reviewer evidence must contain exactly one uppercase `PASS` or `FAIL` token;
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
