$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function New-WorkerStartInfo {
    param(
        [string]$Mode,
        [string]$CaseRoot,
        [string]$RunId,
        [string]$PauseBoundary,
        [string]$CaseType = 'Normal',
        [string]$RetireAnchor = 'Yes'
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName =
        "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $arguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        (Join-Path $PSScriptRoot 'Stage4WalWorker.ps1'),
        '-Mode', $Mode,
        '-CaseRoot', $CaseRoot,
        '-RunId', $RunId,
        '-CaseType', $CaseType,
        '-RetireAnchor', $RetireAnchor)
    if (-not [string]::IsNullOrWhiteSpace($PauseBoundary)) {
        $arguments += @('-PauseBoundary', $PauseBoundary)
    }
    $start.Arguments = (($arguments | ForEach-Object {
        '"' + ([string]$_).Replace('"', '\"') + '"'
    }) -join ' ')
    return $start
}

function Invoke-Worker {
    param(
        [string]$Mode,
        [string]$CaseRoot,
        [string]$RunId,
        [string]$PauseBoundary = '',
        [string]$CaseType = 'Normal',
        [string]$RetireAnchor = 'Yes'
    )

    $process = [Diagnostics.Process]::Start(
        (New-WorkerStartInfo `
            $Mode $CaseRoot $RunId $PauseBoundary $CaseType $RetireAnchor))
    if (-not $process.WaitForExit(60000)) {
        $process.Kill()
        throw 'A WAL worker did not exit within 60 seconds.'
    }
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $process.StandardOutput.ReadToEnd() +
            [Environment]::NewLine +
            $process.StandardError.ReadToEnd()
    }
}

function Stop-WorkerAtBoundary {
    param(
        [string]$CaseRoot,
        [string]$RunId,
        [string]$OperationId,
        [string]$Boundary
    )

    $pause = "$OperationId/$Boundary"
    $process = [Diagnostics.Process]::Start(
        (New-WorkerStartInfo `
            'Execute' $CaseRoot $RunId $pause 'Normal' 'Yes'))
    $safeOperation = $OperationId -replace '[^A-Za-z0-9_.-]', '_'
    $marker = Join-Path $CaseRoot (
        "boundaries\$safeOperation.$Boundary.marker")
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $marker) -and
        [DateTime]::UtcNow -lt $deadline -and
        -not $process.HasExited) {
        Start-Sleep -Milliseconds 25
    }
    if (-not (Test-Path -LiteralPath $marker)) {
        $output = ''
        if ($process.HasExited) {
            $output = $process.StandardOutput.ReadToEnd() +
                $process.StandardError.ReadToEnd()
        }
        if (-not $process.HasExited) {
            $process.Kill()
        }
        throw "Worker did not reach $pause. $output"
    }
    $process.Kill()
    [void]$process.WaitForExit(10000)
    return $process.ExitCode
}

function Get-CasePaths {
    param([string]$CaseRoot, [string]$RunId)

    $parent = Join-Path $CaseRoot 'object-parent'
    $target = Join-Path $parent 'durable-object.bin'
    $transactionId = 'CrossProcess-' + $RunId
    return [pscustomobject]@{
        Parent = $parent
        Source = (Join-Path $CaseRoot 'source.bin')
        Target = $target
        Temporary = Join-Path $parent (
            '.durable-object.bin.' + $transactionId + '.tmp')
    }
}

function Get-WalRecords {
    param([string]$CaseRoot)

    $wal = Join-Path $CaseRoot 'install-wal.jsonl'
    if (-not (Test-Path -LiteralPath $wal -PathType Leaf)) {
        return @()
    }
    return @([IO.File]::ReadAllLines($wal) |
        Where-Object { $_.Length -gt 0 } |
        ForEach-Object { $_ | ConvertFrom-Json })
}

function Assert-RollbackTerminal {
    param([string]$CaseRoot)

    $records = @(Get-WalRecords $CaseRoot)
    Assert-True (@($records | Where-Object {
        $_.phase -ceq 'Aborted'
    }).Count -eq 1) 'Rollback did not append exactly one Aborted terminal.'
    $intents = @($records | Where-Object { $_.phase -ceq 'Intent' })
    $rolled = @($records | Where-Object { $_.phase -ceq 'RolledBack' })
    Assert-True ($rolled.Count -eq $intents.Count) (
        'Rollback did not append one RolledBack per Intent.')
    Assert-True ($records[-1].phase -ceq 'Aborted') (
        'Aborted is not the final WAL record.')
}

function Assert-ReconcileRejected {
    param(
        [string]$CaseRoot,
        [string]$RunId,
        [string[]]$PreservedPaths,
        [string]$CaseName
    )

    $rejected = Invoke-Worker 'Reconcile' $CaseRoot $RunId
    Assert-True ($rejected.ExitCode -ne 0) (
        "$CaseName mutation was accepted: $($rejected.Output)")
    $records = @(Get-WalRecords $CaseRoot)
    Assert-True (@($records | Where-Object {
        $_.phase -in @('RolledBack', 'Aborted')
    }).Count -eq 0) "$CaseName produced a false rollback/Abort."
    foreach ($path in $PreservedPaths) {
        Assert-True (Test-Path -LiteralPath $path) (
            "$CaseName deleted an unproven object: $path")
    }
}

$outerRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'FolderSessionLock.Stage4.WalCrossProcess.' +
    [Guid]::NewGuid().ToString('D'))
[System.IO.Directory]::CreateDirectory($outerRoot) | Out-Null

try {
    $boundaries = @(
        [pscustomobject]@{
            Operation = 'transaction'; Boundary = 'AfterBegin'
        },
        [pscustomobject]@{
            Operation = 'DurableObject'; Boundary = 'AfterIntent'
        },
        [pscustomobject]@{
            Operation = 'DurableObject'; Boundary = 'AfterTempCreate'
        },
        [pscustomobject]@{
            Operation = 'DurableObject'; Boundary = 'DuringTempWrite'
        },
        [pscustomobject]@{
            Operation = 'DurableObject'; Boundary = 'AfterTempFlush'
        },
        [pscustomobject]@{
            Operation = 'DurableObject'; Boundary = 'AfterRename'
        },
        [pscustomobject]@{
            Operation = 'DurableObject'; Boundary = 'AfterApplied'
        },
        [pscustomobject]@{
            Operation = 'transaction'; Boundary = 'AfterCommit'
        })
    $index = 0
    foreach ($case in $boundaries) {
        $caseRoot = Join-Path $outerRoot ('Positive-' + $case.Boundary)
        $runId = '20260725T15{0:D2}00Z-{1}' -f (
            $index,
            ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
        $exitCode = Stop-WorkerAtBoundary `
            $caseRoot $runId $case.Operation $case.Boundary
        Assert-True ($exitCode -ne 0) (
            "Process.Kill did not terminate $($case.Boundary).")
        $paths = Get-CasePaths $caseRoot $runId
        if ($case.Boundary -ceq 'DuringTempWrite') {
            $partialLength = (Get-Item -LiteralPath $paths.Temporary).Length
            $sourceLength = (Get-Item -LiteralPath $paths.Source).Length
            Assert-True (
                $sourceLength -ge 8MB -and
                $partialLength -gt 0 -and
                $partialLength -lt $sourceLength) (
                'DuringTempWrite did not expose a durable strict prefix.')
        }
        $reconcile = Invoke-Worker `
            'Reconcile' $caseRoot $runId '' 'Normal' 'No'
        Assert-True ($reconcile.ExitCode -eq 0) (
            "Reconcile failed at $($case.Boundary): $($reconcile.Output)")
        if ($case.Boundary -ceq 'AfterCommit') {
            Assert-True (
                Test-Path -LiteralPath $paths.Target -PathType Leaf) (
                'Committed final file was rolled back.')
            $records = @(Get-WalRecords $caseRoot)
            Assert-True (@($records | Where-Object {
                $_.phase -ceq 'Committed'
            }).Count -eq 1) 'Committed case lost its terminal record.'
        }
        else {
            Assert-True (-not (Test-Path -LiteralPath $paths.Target)) (
                "Rollback left final file at $($case.Boundary).")
            Assert-RollbackTerminal $caseRoot
        }
        Assert-True (-not (Test-Path -LiteralPath $paths.Temporary)) (
            "Recovery left temporary file at $($case.Boundary).")
        $second = Invoke-Worker `
            'Reconcile' $caseRoot $runId '' 'Normal' 'Yes'
        Assert-True ($second.ExitCode -eq 0) (
            "Second reconcile was not idempotent: $($second.Output)")
        Assert-True (
            -not (Test-Path -LiteralPath (
                Join-Path $caseRoot 'protected-anchor'))) (
            "Recovery left protected anchor at $($case.Boundary).")
        $index++
    }

    $negativeCases = @(
        [pscustomobject]@{
            Name = 'ErrorPrefix'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                $stream = [IO.FileStream]::new(
                    $paths.Temporary,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::ReadWrite,
                    [IO.FileShare]::None)
                try {
                    $value = $stream.ReadByte()
                    $stream.Position = 0
                    $stream.WriteByte([byte](($value + 1) % 256))
                    $stream.Flush($true)
                }
                finally {
                    $stream.Dispose()
                }
                return @($paths.Temporary)
            }
        },
        [pscustomobject]@{
            Name = 'HardLink'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                $link = Join-Path $paths.Parent 'second-link.bin'
                New-Item -ItemType HardLink `
                    -Path $link -Target $paths.Temporary | Out-Null
                return @($paths.Temporary, $link)
            }
        },
        [pscustomobject]@{
            Name = 'Oversize'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                $stream = [IO.FileStream]::new(
                    $paths.Temporary,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $stream.SetLength(
                        (Get-Item -LiteralPath $paths.Source).Length + 1)
                    $stream.Flush($true)
                }
                finally {
                    $stream.Dispose()
                }
                return @($paths.Temporary)
            }
        },
        [pscustomobject]@{
            Name = 'TempAndFinal'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                [IO.File]::Copy($paths.Source, $paths.Target, $false)
                return @($paths.Temporary, $paths.Target)
            }
        },
        [pscustomobject]@{
            Name = 'PartialFinal'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                [IO.File]::Move($paths.Temporary, $paths.Target)
                return @($paths.Target)
            }
        },
        [pscustomobject]@{
            Name = 'SourceChanged'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                $stream = [IO.FileStream]::new(
                    $paths.Source,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::ReadWrite,
                    [IO.FileShare]::None)
                try {
                    $value = $stream.ReadByte()
                    $stream.Position = 0
                    $stream.WriteByte([byte](($value + 1) % 256))
                    $stream.Flush($true)
                }
                finally {
                    $stream.Dispose()
                }
                return @($paths.Temporary)
            }
        },
        [pscustomobject]@{
            Name = 'ParentReplacement'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                $displaced = $paths.Parent + '.displaced'
                [IO.Directory]::Move($paths.Parent, $displaced)
                [IO.Directory]::CreateDirectory($paths.Parent) | Out-Null
                $oldTemp = Join-Path $displaced (
                    [IO.Path]::GetFileName($paths.Temporary))
                [IO.File]::Copy($oldTemp, $paths.Temporary, $false)
                return @($paths.Temporary, $oldTemp)
            }
        },
        [pscustomobject]@{
            Name = 'UnsafeDacl'
            Boundary = 'DuringTempWrite'
            Mutate = {
                param($paths)
                $acl = Get-Acl -LiteralPath $paths.Temporary
                $identity = [Security.Principal.SecurityIdentifier]::new(
                    'S-1-5-21-111111111-222222222-333333333-4444')
                $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                    $identity,
                    [Security.AccessControl.FileSystemRights]::FullControl,
                    [Security.AccessControl.AccessControlType]::Allow)
                $acl.AddAccessRule($rule)
                Set-Acl -LiteralPath $paths.Temporary -AclObject $acl
                return @($paths.Temporary)
            }
        },
        [pscustomobject]@{
            Name = 'Reparse'
            Boundary = 'AfterTempCreate'
            Mutate = {
                param($paths)
                [IO.File]::Delete($paths.Temporary)
                $reparseTarget = Join-Path (
                    [IO.Path]::GetDirectoryName($paths.Parent)) 'junction-target'
                [IO.Directory]::CreateDirectory($reparseTarget) | Out-Null
                New-Item -ItemType Junction `
                    -Path $paths.Temporary -Target $reparseTarget | Out-Null
                return @($paths.Temporary)
            }
        })

    foreach ($negative in $negativeCases) {
        $caseRoot = Join-Path $outerRoot ('Negative-' + $negative.Name)
        $runId = '20260725T16{0:D2}00Z-{1}' -f (
            $index,
            ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
        [void](Stop-WorkerAtBoundary `
            $caseRoot $runId 'DurableObject' $negative.Boundary)
        $paths = Get-CasePaths $caseRoot $runId
        $preserved = @(& $negative.Mutate $paths)
        Assert-ReconcileRejected `
            $caseRoot $runId $preserved $negative.Name
        if ($negative.Name -ceq 'Reparse') {
            [IO.Directory]::Delete($paths.Temporary, $false)
        }
        $index++
    }

    foreach ($preBeginCase in @(
        'WrongTempName',
        'WrongTempParent',
        'PreexistingTemp')) {
        $caseRoot = Join-Path $outerRoot ('PreBegin-' + $preBeginCase)
        $runId = '20260725T17{0:D2}00Z-{1}' -f (
            $index,
            ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
        $result = Invoke-Worker `
            'Execute' $caseRoot $runId '' $preBeginCase 'Yes'
        Assert-True ($result.ExitCode -ne 0) (
            "$preBeginCase was accepted: $($result.Output)")
        $paths = Get-CasePaths $caseRoot $runId
        $expectedObject = switch ($preBeginCase) {
            'WrongTempName' {
                Join-Path $paths.Parent '.wrong-name.tmp'
            }
            'WrongTempParent' {
                Join-Path (Join-Path $caseRoot 'wrong-parent') (
                    [IO.Path]::GetFileName($paths.Temporary))
            }
            default {
                $paths.Temporary
            }
        }
        Assert-True (Test-Path -LiteralPath $expectedObject -PathType Leaf) (
            "$preBeginCase did not preserve its pre-Begin object.")
        Assert-True (@(Get-WalRecords $caseRoot).Count -eq 0) (
            "$preBeginCase wrote Begin or a false terminal record.")
        $index++
    }
}
finally {
    if (Test-Path -LiteralPath $outerRoot) {
        [System.IO.Directory]::Delete($outerRoot, $true)
    }
}

Write-Output 'STAGE4_WAL_CROSS_PROCESS_PASS'
