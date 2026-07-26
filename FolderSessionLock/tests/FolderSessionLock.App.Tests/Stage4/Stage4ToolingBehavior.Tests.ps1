$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

$repository = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\..'))
$modulePath = Join-Path $repository 'eng\stage4\FolderSessionLock.Stage4.psm1'
$module = Import-Module $modulePath -Force -PassThru

& $module {
    Assert-True (Test-FslFullyQualifiedPath 'C:\Stage4\artifact.exe') (
        'A rooted Windows leaf path was rejected.')
    Assert-True (-not (Test-FslFullyQualifiedPath 'relative\artifact.exe')) (
        'A relative path was accepted.')
    Assert-True (-not (Test-FslFullyQualifiedPath 'C:\')) (
        'A drive root was accepted as a controlled leaf.')

    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'FolderSessionLock.Stage4.Tooling.' + [Guid]::NewGuid().ToString('D'))
    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
    try {
        $context = [pscustomobject]@{
            RunId = '20260725T120000Z-0123abcd'
            RepositoryRoot = $repository
            EvidenceRoot = $testRoot
            PrestatePath = (Join-Path $testRoot 'prestate.json')
            StatePath = (Join-Path $testRoot 'stage4-state.json')
            JournalPath = (Join-Path $testRoot 'stage4-journal.jsonl')
            AnchorPath = (Join-Path $testRoot 'stage4-anchor.json')
            InstallWalPath = (Join-Path $testRoot 'install-wal.jsonl')
            ExternalAnchorRoot = (Join-Path $testRoot 'external-anchor')
            ExternalAnchorKeyPath = (
                Join-Path $testRoot 'external-anchor\key.dpapi')
            ExternalAnchorSlot0Path = (
                Join-Path $testRoot 'external-anchor\anchor-0.json')
            ExternalAnchorSlot1Path = (
                Join-Path $testRoot 'external-anchor\anchor-1.json')
        }
        $branch = (& git.exe -C $repository branch --show-current | Out-String).Trim()
        $commit = (& git.exe -C $repository rev-parse HEAD | Out-String).Trim()
        Write-FslUtf8NoBom $context.PrestatePath (
            ([ordered]@{
                runId = $context.RunId
                machineName = [Environment]::MachineName
                branch = $branch
                gitCommit = $commit
            } | ConvertTo-Json) + [Environment]::NewLine)
        Initialize-FslExternalAnchor $context
        $state = [pscustomobject]@{
            schemaVersion = 1
            runId = $context.RunId
            machineName = [Environment]::MachineName
            branch = $branch
            gitCommit = $commit
            sequence = 0
            transition = $null
        }
        Write-FslState $context $state 'PreflightCaptured'
        $validated = Read-FslState $context
        Assert-True ($validated.transition -ceq 'PreflightCaptured') (
            'A valid journaled state was not accepted.')

        $tampered = [System.IO.File]::ReadAllText($context.StatePath).Replace(
            'PreflightCaptured',
            'PublishCompleted')
        Write-FslUtf8NoBom $context.StatePath $tampered
        $recovered = Read-FslState $context
        Assert-True ($recovered.transition -ceq 'PreflightCaptured') (
            'The disposable state cache was not rebuilt from the anchored journal.')

        $trxPath = Join-Path $testRoot 'test-results.trx'
        $validTrx = @'
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testId="1" testName="one" outcome="Passed" />
  </Results>
  <ResultSummary outcome="Completed">
    <Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="1" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
'@
        Write-FslUtf8NoBom $trxPath $validTrx
        $summary = Assert-FslCanonicalTrx $trxPath
        Assert-True ($summary.Total -eq 1 -and $summary.Skipped -eq 0) (
            'A valid canonical TRX was not accepted.')

        Write-FslUtf8NoBom $trxPath (
            $validTrx.Replace('executed="1" passed="1"', 'executed="0" passed="0"').
                Replace('notExecuted="0"', 'notExecuted="1"'))
        $skippedRejected = $false
        try {
            [void](Assert-FslCanonicalTrx $trxPath)
        }
        catch {
            $skippedRejected = $true
        }
        Assert-True $skippedRejected 'A TRX containing a skipped test was accepted.'
    }
    finally {
        if (Test-Path -LiteralPath $testRoot) {
            [System.IO.Directory]::Delete($testRoot, $true)
        }
    }
}

Write-Output 'STAGE4_TOOLING_BEHAVIOR_PASS'
