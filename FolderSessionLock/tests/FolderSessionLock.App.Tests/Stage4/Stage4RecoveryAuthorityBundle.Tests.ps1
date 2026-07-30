$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Cases = 0
$script:Assertions = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:Assertions++
    if (-not $Condition) { throw $Message }
}

function Assert-Case {
    param(
        [bool]$Condition,
        [string]$Message,
        [bool]$Additional = $false,
        [bool]$AdditionalCondition = $true)
    $script:Cases++
    Assert-True $Condition $Message
    if ($Additional) {
        Assert-True $AdditionalCondition ($Message + ' Additional invariant failed.')
    }
}

function Assert-ThrowsCode {
    param([scriptblock]$Action, [string]$Expected, [string]$Message)
    $actual = $null
    try { & $Action }
    catch {
        $actual = [string]$_.Exception.Data[
            'FslRecoveryAuthorityBundleCode']
    }
    return $actual -ceq $Expected
}

function Copy-Object {
    param($Value)
    return $Value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
}

function Write-Utf8 {
    param([string]$Path, [AllowEmptyString()][string]$Text)
    [IO.File]::WriteAllText(
        $Path,
        $Text.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false, $true))
}

function Invoke-Git {
    param([string]$Root, [string[]]$Arguments)
    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = @(& git.exe @('-C', $Root) @Arguments 2>&1)
    }
    finally {
        $ErrorActionPreference = $savedPreference
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Git fixture command failed: $($Arguments -join ' ')`n$($output -join "`n")"
    }
    return (@($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
}

function Get-Sha {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function New-Model {
    param([string]$FixtureId, [string]$SourceLeaf)
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        authorityProfile = 'TestFixture'
        contractId = 'FSL-CP10-DUAL-AUTHORITY-TEST'
        checkpoint =
            'CP10-TRACKED-DUAL-AUTHORITY-RECOVERY-BUNDLE-GENERATOR-VALIDATOR'
        runId = '20260729T180000Z-1234abcd'
        rootBinding = [pscustomobject][ordered]@{
            fixtureId = $FixtureId
            sourceLeafName = $SourceLeaf
        }
    }
}

function Get-PropertyNames {
    param($Value)
    return @($Value.PSObject.Properties | ForEach-Object Name)
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$modulePath = Join-Path $projectRoot (
    'eng\stage4\FolderSessionLock.Stage4.RecoveryAuthorityBundle.psm1')
$tempBase = Join-Path ([IO.Path]::GetTempPath()) 'FolderSessionLock.Tests'
[IO.Directory]::CreateDirectory($tempBase) | Out-Null
$fixtureRoot = $null
$module = $null

try {
    $fixtureId = [Guid]::NewGuid().ToString('D')
    $sourceLeaf = 'dual authority recovery source'
    $fixtureRoot = Join-Path $tempBase $fixtureId
    $authorityRoot = Join-Path $fixtureRoot 'recovery-authority-fixture'
    $repositoryRoot = Join-Path $authorityRoot 'repository'
    $executionRoot = Join-Path $authorityRoot 'execution-state'
    $predecessorRoot = Join-Path $authorityRoot 'install-wal-rollback-1'
    $anchorRoot = Join-Path $authorityRoot 'external-anchors'
    $releaseRoot = Join-Path $authorityRoot 'frozen-release'
    $installRoot = Join-Path $authorityRoot 'install-prestate'
    foreach ($path in @(
        $repositoryRoot,
        $executionRoot,
        $predecessorRoot,
        $anchorRoot,
        $releaseRoot,
        $installRoot)) {
        [IO.Directory]::CreateDirectory($path) | Out-Null
    }

    [void](Invoke-Git $repositoryRoot @('init', '-b', 'main'))
    [void](Invoke-Git $repositoryRoot @(
        'config', 'user.name', 'FolderSessionLock Test'))
    [void](Invoke-Git $repositoryRoot @(
        'config', 'user.email', 'stage4@example.invalid'))
    [void](Invoke-Git $repositoryRoot @(
        'config', 'core.autocrlf', 'false'))
    $fixedFiles = @(
        'eng/stage4/FolderSessionLock.Stage4.psm1',
        'eng/stage4/FolderSessionLock.Stage4.Native.cs',
        'eng/stage4/Invoke-Stage4.ps1',
        'eng/stage4/FolderSessionLock.Stage4.FormalLauncherBundle.psm1',
        'tests/FolderSessionLock.App.Tests/Stage4/Stage4FormalLauncherBundle.Tests.ps1')
    for ($index = 0; $index -lt $fixedFiles.Count; $index++) {
        $path = Join-Path $repositoryRoot $fixedFiles[$index].Replace('/', '\')
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        $content = if ($index -eq 0) {
            (('A' * 8192) + "`nold execution source`n" + ('B' * 8192) + "`n")
        }
        else { "old toolchain source $index`n" }
        Write-Utf8 $path $content
    }
    [void](Invoke-Git $repositoryRoot @('add', '--', '.'))
    [void](Invoke-Git $repositoryRoot @(
        'commit', '-m', 'old execution authority'))
    $executionCommit = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD')
    $executionTree = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD^{tree}')
    Write-Utf8 (
        Join-Path $repositoryRoot $fixedFiles[0].Replace('/', '\')) (
        (('A' * 8192) + "`nnew recovery source`n" + ('B' * 8192) + "`n"))
    [void](Invoke-Git $repositoryRoot @('add', '--', '.'))
    [void](Invoke-Git $repositoryRoot @(
        'commit', '-m', 'descendant recovery toolchain'))
    $toolchainCommit = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD')
    $toolchainTree = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD^{tree}')
    $oldBlob = Invoke-Git $repositoryRoot @(
        'rev-parse', "$executionCommit`:$($fixedFiles[0])")
    $newBlob = Invoke-Git $repositoryRoot @(
        'rev-parse', "$toolchainCommit`:$($fixedFiles[0])")
    $module = Import-Module $modulePath -Force -PassThru
    $gitDirectory = Join-Path $repositoryRoot '.git'
    $looseObserved = & $module {
        param($GitDirectory, $ObjectId)
        $value = Read-FslRabGitObject $GitDirectory $ObjectId
        return $value.type -ceq 'commit'
    } $gitDirectory $toolchainCommit
    [void](Invoke-Git $repositoryRoot @(
        'config', 'repack.useDeltaBaseOffset', 'false'))
    [void](Invoke-Git $repositoryRoot @(
        'repack', '-adf', '--window=250', '--depth=50'))
    $refDeltaObserved = @($oldBlob, $newBlob | ForEach-Object {
        & $module {
            param($GitDirectory, $ObjectId)
            $entry = Get-FslRabPackIndexEntry $GitDirectory $ObjectId
            if ($null -eq $entry) { return -1 }
            return (([int]$entry.pack[[int]$entry.offset] -shr 4) -band 7)
        } $gitDirectory $_
    }) -contains 7
    [void](Invoke-Git $repositoryRoot @(
        'config', 'repack.useDeltaBaseOffset', 'true'))
    [void](Invoke-Git $repositoryRoot @(
        'repack', '-adf', '--window=250', '--depth=50'))
    $ofsDeltaObserved = @($oldBlob, $newBlob | ForEach-Object {
        & $module {
            param($GitDirectory, $ObjectId)
            $entry = Get-FslRabPackIndexEntry $GitDirectory $ObjectId
            if ($null -eq $entry) { return -1 }
            return (([int]$entry.pack[[int]$entry.offset] -shr 4) -band 7)
        } $gitDirectory $_
    }) -contains 6
    [void](Invoke-Git $repositoryRoot @('pack-refs', '--all', '--prune'))

    $statePath = Join-Path $executionRoot 'stage4-state.json'
    $state = [pscustomobject][ordered]@{
        gitCommit = $executionCommit
        sequence = 6
        transition = 'InstallStarted'
        releaseRoot = $releaseRoot
    }
    Write-Utf8 $statePath (($state | ConvertTo-Json) + "`n")
    Write-Utf8 (Join-Path $executionRoot 'stage4-journal.jsonl') (
        "{`"sequence`":6,`"transition`":`"InstallStarted`"}`n")
    $walLines = @()
    for ($index = 1; $index -le 4; $index++) {
        $walLines += (
            "{`"sequence`":$index,`"phase`":`"Prefix$index`"}")
    }
    Write-Utf8 (Join-Path $executionRoot 'install-wal.jsonl') (
        ($walLines -join "`n") + "`n")
    Write-Utf8 (Join-Path $executionRoot 'build-results.txt') (
        "Release build 0 warnings 0 errors`n")
    Write-Utf8 (Join-Path $executionRoot 'commands.txt') (
        "frozen Stage 4 commands`n")
    Write-Utf8 (Join-Path $executionRoot 'prestate.json') (
        "{`"runId`":`"20260729T180000Z-1234abcd`"}`n")
    Write-Utf8 (Join-Path $executionRoot 'signature-verification.txt') (
        "unsigned TestFixture authority`n")
    Write-Utf8 (Join-Path $executionRoot 'stage4-anchor.json') (
        "{`"sequence`":6}`n")
    Write-Utf8 (Join-Path $predecessorRoot 'elevated-reconcile.ps1') (
        "throw 'frozen predecessor wrapper'`n")
    Write-Utf8 (Join-Path $predecessorRoot 'recovery-contract.json') (
        "{`"schemaVersion`":2}`n")
    Write-Utf8 (Join-Path $anchorRoot 'anchor-0.json') (
        "{`"generation`":12}`n")
    Write-Utf8 (Join-Path $anchorRoot 'anchor-1.json') (
        "{`"generation`":11}`n")
    [IO.File]::WriteAllBytes(
        (Join-Path $anchorRoot 'key.dpapi'),
        [byte[]](1..32))
    Write-Utf8 (Join-Path $releaseRoot 'payload.exe') "fixture release`n"
    Write-Utf8 (Join-Path $releaseRoot 'release-descriptor.json') (
        "{`"version`":`"1.0.0`"}`n")
    Write-Utf8 (Join-Path $releaseRoot 'release-manifest.json') (
        "{`"files`":4}`n")
    Write-Utf8 (Join-Path $releaseRoot 'SHA256SUMS.txt') "fixture sums`n"

    $model = New-Model $fixtureId $sourceLeaf
    $syntheticSourceRoot = Join-Path $fixtureRoot $sourceLeaf
    $syntheticSourceAbsentBefore =
        -not (Test-Path -LiteralPath $syntheticSourceRoot)
    $syntheticDirtyPath = Join-Path $repositoryRoot (
        $fixedFiles[0].Replace('/', '\'))
    $syntheticDirtyBytes = [IO.File]::ReadAllBytes($syntheticDirtyPath)
    $syntheticDirtyFailureCode = $null
    try {
        Write-Utf8 $syntheticDirtyPath "synthetic tracked authority drift`n"
        try {
            New-FslStage4RecoveryAuthorityBundle -Model $model | Out-Null
        }
        catch {
            $syntheticDirtyFailureCode = [string]$_.Exception.Data[
                'FslRecoveryAuthorityBundleCode']
        }
    }
    finally {
        [IO.File]::WriteAllBytes($syntheticDirtyPath, $syntheticDirtyBytes)
    }
    $syntheticSourceAbsentAfter =
        -not (Test-Path -LiteralPath $syntheticSourceRoot)

    $formalRunId = '20260727T144929Z-e5b6c040'
    $formalSourceLeaf = 'repair2-formal-layout-read-only-probe'
    $formalModel = [pscustomobject][ordered]@{
        schemaVersion = 1
        authorityProfile = 'Formal'
        contractId = 'FSL-CP10-FORMAL-LAYOUT-READ-ONLY-PROBE'
        checkpoint =
            'CP10-TRACKED-DUAL-AUTHORITY-RECOVERY-BUNDLE-GENERATOR-VALIDATOR'
        runId = $formalRunId
        rootBinding = [pscustomobject][ordered]@{
            fixtureId = $null
            sourceLeafName = $formalSourceLeaf
        }
    }
    $formalLayout = & $module {
        param($FormalModel)
        $roots = Get-FslRabRoots $FormalModel
        $evidence = @(Get-FslRabExactNamedFileRecords `
            $roots.evidenceRoot `
            $script:RabEvidenceNames `
            'FSL-RAB-V011-EVIDENCE')
        $predecessor = @(Get-FslRabExactNamedFileRecords `
            $roots.predecessorRoot `
            $script:RabPredecessorNames `
            'FSL-RAB-V011-EVIDENCE')
        return [pscustomobject]@{
            sourceRoot = $roots.sourceRoot
            predecessorRoot = $roots.predecessorRoot
            evidenceNames = @($evidence | ForEach-Object {
                [IO.Path]::GetFileName([string]$_.path)
            })
            predecessorNames = @($predecessor | ForEach-Object {
                [IO.Path]::GetFileName([string]$_.path)
            })
            installEmpty =
                (Test-Path -LiteralPath $roots.installDirectory -PathType Container) -and
                @(Get-ChildItem -LiteralPath $roots.installDirectory -Force).Count -eq 0
            anchorCount =
                @(Get-ChildItem -LiteralPath $roots.anchorRoot -Force).Count
            recoveryDirectoryCount =
                @(Get-ChildItem -LiteralPath $roots.baseRoot -Directory -Force).Count
        }
    } $formalModel
    $formalSourceAbsentBefore =
        -not (Test-Path -LiteralPath $formalLayout.sourceRoot)
    $formalAuthorityProbe = & $module {
        param($FormalModel)
        try {
            $null = Get-FslRabAuthority $FormalModel
            return [pscustomobject]@{
                validated = $true
                failureCode = $null
            }
        }
        catch {
            return [pscustomobject]@{
                validated = $false
                failureCode = [string]$_.Exception.Data[
                    'FslRecoveryAuthorityBundleCode']
            }
        }
    } $formalModel
    $formalSourceAbsentAfter =
        -not (Test-Path -LiteralPath $formalLayout.sourceRoot)
    $formalAuthorityStateAccepted =
        ($formalAuthorityProbe.validated -and
            [string]::IsNullOrEmpty($formalAuthorityProbe.failureCode)) -or
        (-not $formalAuthorityProbe.validated -and
            $formalAuthorityProbe.failureCode -ceq
                'FSL-RAB-V007-TOOLCHAIN-AUTHORITY')
    $testTokens = $null
    $testParseErrors = $null
    $testAst = [Management.Automation.Language.Parser]::ParseFile(
        $PSCommandPath,
        [ref]$testTokens,
        [ref]$testParseErrors)
    $formalPublicNewCalls = @($testAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -ceq
            'New-FslStage4RecoveryAuthorityBundle' -and
        $node.Extent.Text -cmatch '\$formalModel'
    }, $true))
    $moduleText = [IO.File]::ReadAllText(
        $modulePath,
        [Text.UTF8Encoding]::new($false, $true))
    $exports = @($module.ExportedFunctions.Keys | Sort-Object)

    # Group 1: public model and surface, 18 cases / 28 assertions.
    $modelChecks = @(
        { ($exports -join '|') -ceq
            'New-FslStage4RecoveryAuthorityBundle|Test-FslStage4RecoveryAuthorityBundle' },
        { (Get-PropertyNames $model) -join '|' -ceq
            'schemaVersion|authorityProfile|contractId|checkpoint|runId|rootBinding' },
        { (Get-PropertyNames $model.rootBinding) -join '|' -ceq
            'fixtureId|sourceLeafName' },
        { $model.schemaVersion -is [int] },
        { $model.authorityProfile -ceq 'TestFixture' },
        { $model.rootBinding.fixtureId -ceq $fixtureId },
        { Assert-ThrowsCode {
                $m = Copy-Object $model
                $m.schemaVersion = 2
                New-FslStage4RecoveryAuthorityBundle -Model $m
            } 'FSL-RAB-V001-MODEL' 'schema' },
        { Assert-ThrowsCode {
                $m = Copy-Object $model
                $m.authorityProfile = 'Other'
                New-FslStage4RecoveryAuthorityBundle -Model $m
            } 'FSL-RAB-V001-MODEL' 'profile' },
        { Assert-ThrowsCode {
                $m = Copy-Object $model
                $m.rootBinding.fixtureId = $fixtureId.ToUpperInvariant()
                New-FslStage4RecoveryAuthorityBundle -Model $m
            } 'FSL-RAB-V001-MODEL' 'guid' },
        { Assert-ThrowsCode {
                $m = Copy-Object $model
                $m.rootBinding.sourceLeafName = '..'
                New-FslStage4RecoveryAuthorityBundle -Model $m
            } 'FSL-RAB-V001-MODEL' 'leaf' },
        { Assert-ThrowsCode {
                $m = Copy-Object $model
                $m.runId = 'invalid'
                New-FslStage4RecoveryAuthorityBundle -Model $m
            } 'FSL-RAB-V001-MODEL' 'run' },
        { Assert-ThrowsCode {
                $m = Copy-Object $model
                $m.checkpoint = 'other'
                New-FslStage4RecoveryAuthorityBundle -Model $m
            } 'FSL-RAB-V001-MODEL' 'checkpoint' },
        { Assert-ThrowsCode {
                $m = Copy-Object $model
                $m | Add-Member extra forbidden
                New-FslStage4RecoveryAuthorityBundle -Model $m
            } 'FSL-RAB-V001-MODEL' 'extra' },
        { $syntheticDirtyFailureCode -ceq
                'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' -and
            $syntheticSourceAbsentBefore -and $syntheticSourceAbsentAfter },
        { $formalLayout.predecessorRoot -ceq (
            Join-Path (
                [Environment]::GetFolderPath(
                    [Environment+SpecialFolder]::LocalApplicationData)) (
                'FolderSessionLock\Stage4\Recovery\' + $formalRunId +
                '\install-wal-rollback-1')) },
        { ($formalLayout.evidenceNames -join '|') -ceq (
            'stage4-state.json|stage4-journal.jsonl|install-wal.jsonl|' +
            'build-results.txt|commands.txt|prestate.json|' +
            'signature-verification.txt|stage4-anchor.json') },
        { ($formalLayout.predecessorNames -join '|') -ceq
            'elevated-reconcile.ps1|recovery-contract.json' },
        { $formalAuthorityStateAccepted -and
            $formalSourceAbsentBefore -and $formalSourceAbsentAfter -and
            $formalLayout.installEmpty -and $formalLayout.anchorCount -eq 3 -and
            $formalLayout.recoveryDirectoryCount -eq 3 -and
            $testParseErrors.Count -eq 0 -and
            $formalPublicNewCalls.Count -eq 0 })
    for ($index = 0; $index -lt 18; $index++) {
        $condition = [bool](& $modelChecks[$index])
        Assert-Case $condition "Model case $index failed." ($index -lt 10) (
            (Get-PropertyNames $model).Count -eq 6)
    }

    # Group 2: strict dual Git authority, 12 cases / 18 assertions.
    $packFiles = @(Get-ChildItem -LiteralPath (
        Join-Path $repositoryRoot '.git\objects\pack') -File)
    $gitChecks = @(
        { $executionCommit -cmatch '^[0-9a-f]{40}$' },
        { $executionTree -cmatch '^[0-9a-f]{40}$' },
        { $toolchainCommit -cmatch '^[0-9a-f]{40}$' },
        { $toolchainTree -cmatch '^[0-9a-f]{40}$' },
        { $executionCommit -cne $toolchainCommit },
        { $executionTree -cne $toolchainTree },
        { (Invoke-Git $repositoryRoot @(
                'merge-base','--is-ancestor',$executionCommit,$toolchainCommit)
            ) -ceq '' },
        { $looseObserved },
        { $refDeltaObserved },
        { $ofsDeltaObserved },
        { -not (Test-Path -LiteralPath (
                Join-Path $gitDirectory 'refs\heads\main')) },
        { $packFiles.Count -ge 2 })
    for ($index = 0; $index -lt 12; $index++) {
        Assert-Case ([bool](& $gitChecks[$index])) (
            "Git authority case $index failed.") ($index -lt 6) (
            $executionCommit -cne $toolchainCommit)
    }

    # Group 3: generation and canonical schema, 32 cases / 44 assertions.
    $generated = New-FslStage4RecoveryAuthorityBundle -Model $model
    $sourceRoot = Join-Path $fixtureRoot $sourceLeaf
    $wrapperPath = Join-Path $sourceRoot 'elevated-reconcile.ps1'
    $contractPath = Join-Path $sourceRoot 'recovery-contract.json'
    $contractBytes = [IO.File]::ReadAllBytes($contractPath)
    $wrapperBytes = [IO.File]::ReadAllBytes($wrapperPath)
    $contractText = [Text.UTF8Encoding]::new($false, $true).GetString(
        $contractBytes)
    $contract = $contractText | ConvertFrom-Json
    $contractNames = @(
        'schemaVersion','authorityProfile','contractId','checkpoint','runId',
        'executionStateAuthority','recoveryToolchainAuthority',
        'operatorIdentity','recoverySource','transaction','canonicalEvidence',
        'externalAnchors','release','systemPrestate','contractStageGates',
        'futureInvocation','allowedWrites','forbiddenActions','bindingManifest')
    $generationChecks = @(
        { $generated.schemaVersion -eq 1 },
        { $generated.bundleRoot -ceq $sourceRoot },
        { $generated.observedFiles.Count -eq 2 },
        { (Get-ChildItem -LiteralPath $sourceRoot -Force).Count -eq 2 },
        { Test-Path -LiteralPath $wrapperPath -PathType Leaf },
        { Test-Path -LiteralPath $contractPath -PathType Leaf },
        { $contractBytes[0] -ne 0xEF },
        { $contractBytes -notcontains 0x0D },
        { $contractBytes[$contractBytes.Length - 1] -eq 0x0A },
        { (Get-PropertyNames $contract) -join '|' -ceq
            ($contractNames -join '|') },
        { $contract.schemaVersion -eq 3 },
        { $contract.authorityProfile -ceq 'TestFixture' },
        { $contract.contractId -ceq $model.contractId },
        { $contract.runId -ceq $model.runId },
        { $contract.executionStateAuthority.gitCommit -ceq $executionCommit },
        { $contract.executionStateAuthority.gitTree -ceq $executionTree -and
            -not ($state.PSObject.Properties.Name -contains 'gitTree') },
        { $contract.recoveryToolchainAuthority.gitCommit -ceq $toolchainCommit },
        { $contract.recoveryToolchainAuthority.gitTree -ceq $toolchainTree },
        { $contract.executionStateAuthority.gitCommit -cne
            $contract.recoveryToolchainAuthority.gitCommit },
        { $contract.recoveryToolchainAuthority.sourceFiles.Count -eq 5 },
        { @($contract.recoveryToolchainAuthority.sourceFiles |
                ForEach-Object relativePath) -join '|' -ceq
            ($fixedFiles -join '|') },
        { $contract.contractStageGates.Count -eq 56 },
        { $contract.contractStageGates[0].exitCode -eq 84 },
        { $contract.contractStageGates[55].exitCode -eq 139 },
        { $contract.transaction.walPrefixRecordCount -eq 4 },
        { $contract.transaction.expectedPost.walRecordCount -eq 7 },
        { $contract.transaction.expectedPost.latestGeneration -eq 14 },
        { $contract.transaction.expectedPost.previousGeneration -eq 13 },
        { $contract.canonicalEvidence.files.Count -eq 5 -and
            @($contract.canonicalEvidence.files | ForEach-Object {
                [IO.Path]::GetFileName([string]$_.path)
            }) -join '|' -ceq (
                'build-results.txt|commands.txt|prestate.json|' +
                'signature-verification.txt|stage4-anchor.json') },
        { $contract.canonicalEvidence.predecessorFiles.Count -eq 2 -and
            @($contract.canonicalEvidence.predecessorFiles |
                ForEach-Object {
                    [IO.Path]::GetFileName([string]$_.path)
                }) -join '|' -ceq
                    'elevated-reconcile.ps1|recovery-contract.json' },
        { $contract.externalAnchors.files.Count -eq 3 },
        { $contract.bindingManifest.contractCanonicalSha256 -ceq
            $generated.contractCanonicalSha256 })
    for ($index = 0; $index -lt 32; $index++) {
        Assert-Case ([bool](& $generationChecks[$index])) (
            "Generation case $index failed.") ($index -lt 12) (
            $contract.schemaVersion -eq 3)
    }

    # Group 4: validator, opacity, wrapper and gate map, 61 / 84.
    $validation = Test-FslStage4RecoveryAuthorityBundle -Model $model
    $opaque = $validation.opaqueAuthority
    $wrapperText = [Text.UTF8Encoding]::new($false, $true).GetString(
        $wrapperBytes)
    $tokens = $null
    $parseErrors = $null
    $wrapperAst = [Management.Automation.Language.Parser]::ParseInput(
        $wrapperText,
        [ref]$tokens,
        [ref]$parseErrors)
    $reconcileCommands = @($wrapperAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -ceq 'Invoke-FslReconcileInstallWal'
    }, $true))
    $validatorChecks = [Collections.Generic.List[scriptblock]]::new()
    foreach ($check in @(
        { $validation.isValid },
        { $validation.errors.Count -eq 0 },
        { $opaque.contractId -ceq $model.contractId },
        { $opaque.contractSha256 -ceq (Get-Sha $contractPath) },
        { $opaque.wrapperSha256 -ceq (Get-Sha $wrapperPath) },
        { $opaque.executionStateAuthoritySha256 -cmatch '^[0-9A-F]{64}$' },
        { $opaque.recoveryToolchainAuthoritySha256 -cmatch '^[0-9A-F]{64}$' },
        { $opaque.toolchainRepositorySha256 -cmatch '^[0-9A-F]{64}$' },
        { $opaque.recoveryGateMapSha256 -cmatch '^[0-9A-F]{64}$' },
        { $opaque.executionStateAuthoritySha256 -cne
            $opaque.recoveryToolchainAuthoritySha256 },
        { $opaque.executionGitCommit -ceq $executionCommit },
        { $opaque.recoveryGitCommit -ceq $toolchainCommit },
        { $opaque.recoveryGitTree -ceq $toolchainTree },
        { $opaque.gates.Count -eq 56 },
        { @($parseErrors).Count -eq 0 },
        { $reconcileCommands.Count -eq 1 },
        { $wrapperText -notmatch 'Start-Process' },
        { $wrapperText -notmatch 'Invoke-Expression' },
        { $wrapperText -notmatch 'TEST_FIXTURE_NEVER_EXECUTE' },
        { $wrapperText -match '(?m)^param\(\)$' },
        { $contract.futureInvocation.arguments.Count -eq 7 },
        { $contract.futureInvocation.arguments[6] -ceq $wrapperPath },
        { $contract.futureInvocation.verb -ceq 'RunAs' },
        { $contract.futureInvocation.passThru },
        { $contract.futureInvocation.wait },
        { -not $contract.futureInvocation.redirectStandardOutput },
        { -not $contract.futureInvocation.redirectStandardError },
        { $contract.allowedWrites.Count -eq 3 },
        { $contract.forbiddenActions.Count -eq 4 },
        { $contract.systemPrestate.installDirectoryEmpty },
        { $contract.systemPrestate.programDataAbsent },
        { $contract.systemPrestate.serviceAbsent },
        { $contract.systemPrestate.serviceRegistryAbsent },
        { $contract.systemPrestate.productProcessCount -eq 0 },
        { $contract.release.fileCount -eq 4 },
        { $contract.release.descriptorSha256 -cmatch '^[0-9A-F]{64}$' },
        { $contract.release.manifestSha256 -cmatch '^[0-9A-F]{64}$' },
        { $contract.release.sumsSha256 -cmatch '^[0-9A-F]{64}$' },
        { $contract.bindingManifest.contractLength -eq $contractBytes.Length },
        { $contract.bindingManifest.hashRule -match '64 ASCII zeroes' },
        { $contract.recoverySource.exactChildren.Count -eq 2 },
        { $contract.recoverySource.wrapper.path -ceq $wrapperPath },
        { $contract.recoverySource.contract.path -ceq $contractPath },
        { $contract.operatorIdentity.userSid -cmatch '^S-\d' },
        { $contract.operatorIdentity.sessionId -ge 0 },
        { $contract.operatorIdentity.isInteractive },
        { $contract.executionStateAuthority.stateSequence -eq 6 },
        { $contract.executionStateAuthority.stateTransition -ceq
            'InstallStarted' },
        { $contract.transaction.expectedPost.directoryAbsent },
        { $contract.transaction.expectedPost.addedPhases.Count -eq 3 },
        { $contract.contractStageGates[0].gateId -ceq
            'FSL-RAB-CG-001-ARGUMENTS' },
        { $contract.contractStageGates[55].gateId -ceq
            'FSL-RAB-CG-056-NONEXECUTION' },
        { (@($contract.contractStageGates |
                    Select-Object -Unique exitCode).Count -eq 56) },
        { (@($contract.contractStageGates |
                    Select-Object -Unique gateId).Count -eq 56) },
        { $opaque.futureInvocation.filePath -ceq
            $contract.futureInvocation.filePath },
        { $opaque.sourceRoot -ceq $sourceRoot },
        { $opaque.canonicalEvidence.files.Count -eq 5 },
        { $opaque.externalAnchors.files.Count -eq 3 },
        { $opaque.release.fileCount -eq 4 },
        { $opaque.transaction.expectedPost.walRecordCount -eq 7 },
        { $opaque.systemPrestate.programDataAbsent })) {
        $validatorChecks.Add($check)
    }
    for ($index = 0; $index -lt 61; $index++) {
        Assert-Case ([bool](& $validatorChecks[$index])) (
            "Validator case $index failed.") ($index -lt 23) (
            $validation.isValid)
    }

    # Group 5: canonical mutations, 25 cases / 35 assertions.
    $mutationBytes = [byte[]]$contractBytes.Clone()
    $mutationBytes[$mutationBytes.Length - 2] =
        $mutationBytes[$mutationBytes.Length - 2] -bxor 1
    [IO.File]::WriteAllBytes($contractPath, $mutationBytes)
    $invalidBytes = Test-FslStage4RecoveryAuthorityBundle -Model $model
    [IO.File]::WriteAllBytes($contractPath, $contractBytes)
    $schema2 = Copy-Object $contract
    $schema2.schemaVersion = 2
    Write-Utf8 $contractPath (($schema2 | ConvertTo-Json -Depth 64) + "`n")
    $invalidSchema = Test-FslStage4RecoveryAuthorityBundle -Model $model
    [IO.File]::WriteAllBytes($contractPath, $contractBytes)
    $canonicalMutationChecks = @(
        { -not $invalidBytes.isValid },
        { $invalidBytes.errors[0].code -ceq 'FSL-RAB-V004-FILE-BYTES' },
        { -not $invalidSchema.isValid },
        { $invalidSchema.errors.Count -ge 1 },
        { (Test-FslStage4RecoveryAuthorityBundle -Model $model).isValid },
        { (Get-Sha $contractPath) -ceq $opaque.contractSha256 },
        { (Get-Sha $wrapperPath) -ceq $opaque.wrapperSha256 },
        { $contractBytes -notcontains 0x0D },
        { $wrapperBytes -notcontains 0x0D },
        { $contractText.EndsWith("`n", [StringComparison]::Ordinal) },
        { $contractBytes.Length -lt 3 -or
            -not ($contractBytes[0] -eq 0xEF -and
                $contractBytes[1] -eq 0xBB -and
                $contractBytes[2] -eq 0xBF) },
        { $contract.bindingManifest.contractCanonicalSha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.bindingManifest.wrapperSha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.bindingManifest.executionStateAuthoritySha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.bindingManifest.recoveryToolchainAuthoritySha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.bindingManifest.toolchainRepositorySha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.bindingManifest.recoveryGateMapSha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.recoverySource.wrapper.length -eq $wrapperBytes.Length },
        { $contract.recoverySource.contract.schemaVersion -eq 3 },
        { $contract.recoverySource.contract.selfHashRule -ceq
            $contract.bindingManifest.hashRule },
        { $contract.executionStateAuthority.authoritySha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.recoveryToolchainAuthority.authoritySha256 -cmatch
            '^[0-9A-F]{64}$' },
        { $contract.release.fingerprintSha256 -cmatch '^[0-9A-F]{64}$' },
        { $contract.canonicalEvidence.predecessorRoot -ceq $predecessorRoot },
        { $contract.externalAnchors.root -ceq $anchorRoot })
    for ($index = 0; $index -lt 25; $index++) {
        Assert-Case ([bool](& $canonicalMutationChecks[$index])) (
            "Canonical mutation case $index failed.") ($index -lt 10) (
            (Get-Sha $contractPath) -ceq $opaque.contractSha256)
    }

    # Group 6: authority drift and fail-closed behavior, 25 / 35.
    $trackedPath = Join-Path $repositoryRoot $fixedFiles[0].Replace('/', '\')
    $trackedBytes = [IO.File]::ReadAllBytes($trackedPath)
    Write-Utf8 $trackedPath "tracked drift`n"
    $dirtyResult = Test-FslStage4RecoveryAuthorityBundle -Model $model
    [IO.File]::WriteAllBytes($trackedPath, $trackedBytes)
    $restored = Test-FslStage4RecoveryAuthorityBundle -Model $model
    $buildResultsPath = Join-Path $executionRoot 'build-results.txt'
    $buildResultsBytes = [IO.File]::ReadAllBytes($buildResultsPath)
    [IO.File]::Delete($buildResultsPath)
    $missingEvidence = Test-FslStage4RecoveryAuthorityBundle -Model $model
    [IO.File]::WriteAllBytes($buildResultsPath, $buildResultsBytes)
    $extraEvidencePath = Join-Path $executionRoot 'extra.txt'
    Write-Utf8 $extraEvidencePath "extra`n"
    $extraEvidence = Test-FslStage4RecoveryAuthorityBundle -Model $model
    [IO.File]::Delete($extraEvidencePath)
    $subdirectoryPath = Join-Path $executionRoot 'unexpected-directory'
    [IO.Directory]::CreateDirectory($subdirectoryPath) | Out-Null
    $subdirectoryEvidence =
        Test-FslStage4RecoveryAuthorityBundle -Model $model
    [IO.Directory]::Delete($subdirectoryPath, $false)
    $commandsPath = Join-Path $executionRoot 'commands.txt'
    $caseIntermediate = Join-Path $executionRoot 'commands-case.tmp'
    $caseDriftPath = Join-Path $executionRoot 'COMMANDS.TXT'
    [IO.File]::Move($commandsPath, $caseIntermediate)
    [IO.File]::Move($caseIntermediate, $caseDriftPath)
    $caseDriftEvidence =
        Test-FslStage4RecoveryAuthorityBundle -Model $model
    [IO.File]::Move($caseDriftPath, $caseIntermediate)
    [IO.File]::Move($caseIntermediate, $commandsPath)
    $stateBytes = [IO.File]::ReadAllBytes($statePath)
    $forgedState = [IO.File]::ReadAllText(
        $statePath,
        [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
    $forgedState | Add-Member `
        -NotePropertyName gitTree `
        -NotePropertyValue ('f' * 40)
    Write-Utf8 $statePath (($forgedState | ConvertTo-Json) + "`n")
    $forgedExecution = & $module {
        param($Model)
        $roots = Get-FslRabRoots $Model
        $repository = Get-FslRabRepository $roots.repositoryRoot $false
        Get-FslRabExecutionAuthority $Model $roots $repository
    } $model
    [IO.File]::WriteAllBytes($statePath, $stateBytes)
    $driftChecks = @(
        { -not $dirtyResult.isValid },
        { $dirtyResult.errors[0].code -ceq
            'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' },
        { $restored.isValid },
        { $restored.errors.Count -eq 0 },
        { $executionCommit -cne $toolchainCommit },
        { $executionTree -cne $toolchainTree },
        { $contract.executionStateAuthority.gitCommit -ceq $executionCommit },
        { $contract.recoveryToolchainAuthority.gitCommit -ceq $toolchainCommit },
        { $contract.recoveryToolchainAuthority.trackedClean },
        { $contract.recoveryToolchainAuthority.sourceFiles.Count -eq 5 },
        { $contract.executionStateAuthority.files.state.sha256 -ceq
            (Get-Sha $statePath) },
        { $contract.executionStateAuthority.files.journal.sha256 -ceq
            (Get-Sha (Join-Path $executionRoot 'stage4-journal.jsonl')) },
        { $contract.executionStateAuthority.files.installWal.sha256 -ceq
            (Get-Sha (Join-Path $executionRoot 'install-wal.jsonl')) },
        { $contract.canonicalEvidence.files.Count -eq 5 -and
            @($contract.canonicalEvidence.files | ForEach-Object {
                [IO.Path]::GetFileName([string]$_.path)
            }) -join '|' -ceq (
                'build-results.txt|commands.txt|prestate.json|' +
                'signature-verification.txt|stage4-anchor.json') },
        { $contract.canonicalEvidence.predecessorFiles.Count -eq 2 -and
            $contract.canonicalEvidence.predecessorRoot -ceq $predecessorRoot },
        { -not $missingEvidence.isValid -and
            $missingEvidence.errors[0].code -ceq 'FSL-RAB-V011-EVIDENCE' },
        { -not $extraEvidence.isValid -and
            $extraEvidence.errors[0].code -ceq 'FSL-RAB-V011-EVIDENCE' },
        { -not $subdirectoryEvidence.isValid -and
            $subdirectoryEvidence.errors[0].code -ceq
                'FSL-RAB-V011-EVIDENCE' },
        { -not $caseDriftEvidence.isValid -and
            $caseDriftEvidence.errors[0].code -ceq
                'FSL-RAB-V011-EVIDENCE' },
        { $forgedExecution.gitTree -ceq $executionTree -and
            $forgedExecution.gitTree -cne ('f' * 40) },
        { -not ($state.PSObject.Properties.Name -contains 'gitTree') },
        { -not (Test-Path -LiteralPath (
                Join-Path $authorityRoot 'program-data-absent')) },
        { (Invoke-Git $repositoryRoot @(
                'status','--porcelain=v1','--untracked-files=all')) -ceq '' },
        { @($packFiles | Where-Object Extension -eq '.pack').Count -ge 1 },
        { @($packFiles | Where-Object Extension -eq '.idx').Count -ge 1 })
    for ($index = 0; $index -lt 25; $index++) {
        Assert-Case ([bool](& $driftChecks[$index])) (
            "Authority drift case $index failed.") ($index -lt 10) (
            $restored.isValid)
    }

    # Group 7: non-execution and cleanup invariants, 15 / 16.
    $productionTokens = $null
    $productionParseErrors = $null
    $productionAst = [Management.Automation.Language.Parser]::ParseFile(
        $modulePath,
        [ref]$productionTokens,
        [ref]$productionParseErrors)
    $productionCommandNames = @($productionAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true) | ForEach-Object { $_.GetCommandName() })
    $nonExecutionChecks = @(
        { -not (Test-Path -LiteralPath (
                Join-Path $sourceRoot 'launch-attempt.jsonl')) },
        { $moduleText -notmatch '(?i)\bgit\.exe\b' },
        { $moduleText -notmatch
            '(?i)\bGet-Command\s+[''"]?git' },
        { $productionCommandNames -cnotcontains 'Start-Process' },
        { $moduleText -notmatch
            '(?i)\[(?:Diagnostics\.)?Process\]::Start' },
        { $wrapperText -notmatch '(?i)\bStart-Process\b' },
        { $wrapperText -notmatch '(?i)\bInvoke-Expression\b' },
        { $wrapperText -notmatch '(?i)\bgit(?:\.exe)?\b' },
        { $wrapperText -notmatch '(?i)\bRemove-Item\b' },
        { $wrapperText -notmatch '(?i)\bSet-Acl\b' },
        { $wrapperText -notmatch '(?i)\bNew-Service\b' },
        { $reconcileCommands.Count -eq 1 },
        { $contract.futureInvocation.arguments[6] -ceq $wrapperPath },
        { $contract.futureInvocation.redirectStandardOutput -eq $false },
        { $contract.futureInvocation.redirectStandardError -eq $false })
    for ($index = 0; $index -lt 15; $index++) {
        Assert-Case ([bool](& $nonExecutionChecks[$index])) (
            "Non-execution case $index failed.") ($index -eq 0) (
            $reconcileCommands.Count -eq 1)
    }

    if ($script:Cases -ne 188 -or $script:Assertions -ne 260) {
        throw "Counter drift: Cases=$script:Cases Assertions=$script:Assertions."
    }
    Write-Output (
        "STAGE4_RECOVERY_AUTHORITY_BUNDLE_PASS Cases=$script:Cases " +
        "Assertions=$script:Assertions")
}
finally {
    if ($null -ne $module) {
        Remove-Module $module -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $fixtureRoot -and
        (Test-Path -LiteralPath $fixtureRoot -PathType Container)) {
        Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force -File |
            ForEach-Object { $_.IsReadOnly = $false }
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
}
