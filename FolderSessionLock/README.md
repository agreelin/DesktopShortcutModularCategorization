# Folder Session Lock

Windows-only folder session restriction application. Stage 3 adds Windows Logon SID discovery, strict fixed-NTFS path validation, stable directory identity, handle-bound DACL editing, and an in-process ACL lock service to the stage 2 domain model and scheduler. Elevation, IPC, persistence, startup recovery, and access auditing are not implemented.

## Structure

```text
src/
  FolderSessionLock.App/       WPF shell, MVVM, startup, DI, logging
  FolderSessionLock.Core/      Domain models, state machine, task manager, coordinator, scheduler
  FolderSessionLock.Windows/   Windows identity, path validation, and ACL engine
  FolderSessionLock.Broker/    Process skeleton; no elevation or IPC
tests/
  FolderSessionLock.Core.Tests/
  FolderSessionLock.App.Tests/
  FolderSessionLock.Windows.Tests/
```

Dependency direction:

```text
App -> Core
Windows -> Core
Broker -> Core + Windows
Tests -> corresponding product project
```

`FolderSessionLock.Core` does not reference WPF, Windows UI, Broker, or Windows ACL APIs. App references Core only and does not own scheduler or lock-removal lifecycle.

## Prerequisites

- Windows
- .NET 8 SDK with Windows Desktop support

## Build and test

Run from this directory:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release --no-restore
dotnet format --verify-no-changes
```

Launch the minimal WPF shell:

```powershell
dotnet run --project src/FolderSessionLock.App/FolderSessionLock.App.csproj -c Release
```

The Windows test infrastructure creates unique directories only under `%TEMP%\FolderSessionLock.Tests\<Guid>`. It does not accept an external directory path and reports cleanup failure by throwing an exception.

## Implemented behavior and stage 3 safety boundary

- Real DACL integration tests run only in auto-created `%TEMP%\FolderSessionLock.Tests\<Guid>` fixed-NTFS directories.
- The deny mask is `0x000101FF`; it excludes `ReadPermissions`, `ChangePermissions`, `TakeOwnership`, and `Synchronize`.
- DACL read, add, verification, rollback, and removal use one continuously held directory handle.
- Original ACE bytes, order, inheritance state, unrelated SIDs, and parent ACL are preserved; path replacement tests prove replacement ACLs are not changed.
- Windows tests call the Windows implementation directly. Broker does not yet compose or expose the real ACL service; App still references Core only.
- No SACL writes, audit policy changes, or Security log access.
- No Named Pipe or arbitrary command dispatch.
- No UAC elevation or administrator requirement.
- No access to real user or system directories.
- No persistent recovery record or startup recovery service; unexpected process termination recovery remains a stage 4 requirement.
- Duration bounds must be supplied through an explicit `LockDurationPolicy`; Core has no hidden production default.
- UTC timestamps are display data; monotonic elapsed time controls expiry.
- Expiration removal is exactly-once per task under concurrent scans.
- UI and Window lifecycles do not cancel or remove active tasks.

Project requirements, architecture, security rules, decisions, plan, tasks, acceptance criteria, and development log are the authoritative documents in this directory.
