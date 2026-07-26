param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Execute', 'Reconcile')]
    [string]$Mode,
    [Parameter(Mandatory = $true)][string]$CaseRoot,
    [Parameter(Mandatory = $true)][string]$RunId,
    [string]$PauseBoundary,
    [ValidateSet(
        'Normal',
        'WrongTempName',
        'WrongTempParent',
        'PreexistingTemp')]
    [string]$CaseType = 'Normal',
    [ValidateSet('Yes', 'No')]
    [string]$RetireAnchor = 'Yes'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\..'))
$module = Import-Module (
    Join-Path $repository 'eng\stage4\FolderSessionLock.Stage4.psm1') `
    -Force `
    -PassThru

& $module {
    param(
        $Mode,
        $CaseRoot,
        $RunId,
        $PauseBoundary,
        $CaseType,
        $RetireAnchor,
        $Repository)

    $anchorRoot = Join-Path $CaseRoot 'protected-anchor'
    $context = [pscustomobject]@{
        RunId = $RunId
        RepositoryRoot = $Repository
        EvidenceRoot = $CaseRoot
        PrestatePath = (Join-Path $CaseRoot 'prestate.json')
        StatePath = (Join-Path $CaseRoot 'stage4-state.json')
        JournalPath = (Join-Path $CaseRoot 'stage4-journal.jsonl')
        AnchorPath = (Join-Path $CaseRoot 'stage4-anchor.json')
        InstallWalPath = (Join-Path $CaseRoot 'install-wal.jsonl')
        CommandsPath = (Join-Path $CaseRoot 'commands.txt')
        ExternalAnchorRoot = $anchorRoot
        ExternalAnchorKeyPath = (Join-Path $anchorRoot 'key.dpapi')
        ExternalAnchorSlot0Path = (Join-Path $anchorRoot 'anchor-0.json')
        ExternalAnchorSlot1Path = (Join-Path $anchorRoot 'anchor-1.json')
        WalBoundaryDirectory = (Join-Path $CaseRoot 'boundaries')
        WalPauseBoundary = $PauseBoundary
    }
    $source = Join-Path $CaseRoot 'source.bin'
    $targetParent = Join-Path $CaseRoot 'object-parent'
    $target = Join-Path $targetParent 'durable-object.bin'

    if ($Mode -ceq 'Execute') {
        [System.IO.Directory]::CreateDirectory($CaseRoot) | Out-Null
        $branch = (& git.exe -C $Repository branch --show-current |
            Out-String).Trim()
        $commit = (& git.exe -C $Repository rev-parse HEAD |
            Out-String).Trim()
        Write-FslUtf8NoBom $context.PrestatePath (
            ([ordered]@{
                runId = $RunId
                machineName = [Environment]::MachineName
                branch = $branch
                gitCommit = $commit
            } | ConvertTo-Json) + [Environment]::NewLine)
        Initialize-FslExternalAnchor $context
        $state = [pscustomobject]@{
            schemaVersion = 1
            runId = $RunId
            machineName = [Environment]::MachineName
            branch = $branch
            gitCommit = $commit
            sequence = 0
            transition = $null
        }
        Write-FslState $context $state 'PreflightCaptured'
        [System.IO.Directory]::CreateDirectory($targetParent) | Out-Null
        $bytes = [byte[]]::new(8MB + 257)
        $random = [Security.Cryptography.RNGCryptoServiceProvider]::new()
        try {
            $random.GetBytes($bytes)
        }
        finally {
            $random.Dispose()
        }
        [FolderSessionLock.Stage4.Native]::AtomicWrite($source, $bytes)
        $transactionId = 'CrossProcess-' + $RunId
        $temporary = Get-FslDeterministicTemporaryPath `
            $target $transactionId
        if ($CaseType -ceq 'WrongTempName') {
            $temporary = Join-Path $targetParent '.wrong-name.tmp'
        }
        elseif ($CaseType -ceq 'WrongTempParent') {
            $wrongParent = Join-Path $CaseRoot 'wrong-parent'
            [System.IO.Directory]::CreateDirectory($wrongParent) | Out-Null
            $temporary = Join-Path $wrongParent (
                [System.IO.Path]::GetFileName(
                    (Get-FslDeterministicTemporaryPath `
                        $target $transactionId)))
        }
        elseif ($CaseType -ceq 'PreexistingTemp') {
            [System.IO.File]::WriteAllText(
                $temporary,
                'preexisting object',
                [System.Text.UTF8Encoding]::new($false))
        }
        if ($CaseType -in @('WrongTempName', 'WrongTempParent')) {
            [System.IO.File]::WriteAllText(
                $temporary,
                'incorrect planned temporary object',
                [System.Text.UTF8Encoding]::new($false))
        }
        $plan = @(New-FslDurablePlan @(
            [pscustomobject]@{
                operationId = 'DurableObject'
                kind = 'FileCopyAtomic'
                target = $target
                desired = [pscustomobject][ordered]@{
                    source = $source
                    targetParent = $targetParent
                    temporaryPath = $temporary
                    length = $bytes.LongLength
                    sha256 = Get-FslSha256 $bytes
                }
            }))
        [void](Start-FslDurableTransaction `
            $context $transactionId 'Rollback' 'WalTest' $plan)
        Publish-FslWalBoundary $context 'transaction' 'AfterBegin'
        Invoke-FslExecuteDurablePlan $context $transactionId
        Complete-FslDurableTransaction $context $transactionId
        Publish-FslWalBoundary $context 'transaction' 'AfterCommit'
        Write-Output 'STAGE4_WAL_WORKER_COMMITTED'
        return
    }

    $state = Read-FslState $context
    Invoke-FslReconcileInstallWal $context $state
    if ($RetireAnchor -ceq 'Yes') {
        Remove-FslExternalAnchor $context
        if (Test-Path -LiteralPath $context.ExternalAnchorRoot) {
            throw 'Cross-process WAL reconciliation left its external anchor.'
        }
    }
    Write-Output 'STAGE4_WAL_WORKER_RECONCILED'
} $Mode $CaseRoot $RunId $PauseBoundary $CaseType $RetireAnchor $repository
