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
$stage4ModulePath = Join-Path $projectRoot (
    'eng\stage4\FolderSessionLock.Stage4.psm1')
$tempBase = Join-Path ([IO.Path]::GetTempPath()) 'FolderSessionLock.Tests'
[IO.Directory]::CreateDirectory($tempBase) | Out-Null
$fixtureRoot = $null
$module = $null
$stage4Module = $null

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

    [void](Invoke-Git $repositoryRoot @(
        'init', '-b', 'cp10-vm-transfer'))
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
    $productionStage4Text = [IO.File]::ReadAllText(
        $stage4ModulePath,
        [Text.UTF8Encoding]::new($false, $true)).TrimEnd("`r", "`n")
    $productionNativeText = [IO.File]::ReadAllText(
        (Join-Path $projectRoot (
            'eng\stage4\FolderSessionLock.Stage4.Native.cs')),
        [Text.UTF8Encoding]::new($false, $true))
    for ($index = 0; $index -lt $fixedFiles.Count; $index++) {
        $path = Join-Path $repositoryRoot $fixedFiles[$index].Replace('/', '\')
        [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
        $content = if ($index -eq 0) {
            $productionStage4Text + "`n# " + ('A' * 8192) +
                "`n# old execution source`n# " + ('B' * 8192) + "`n"
        }
        elseif ($index -eq 1) {
            $productionNativeText
        }
        else { "old toolchain source $index`n" }
        Write-Utf8 $path $content
    }
    $fixtureRecoveryModulePath = Join-Path $repositoryRoot (
        'eng\stage4\FolderSessionLock.Stage4.RecoveryAuthorityBundle.psm1')
    Write-Utf8 $fixtureRecoveryModulePath ([IO.File]::ReadAllText(
        $modulePath,
        [Text.UTF8Encoding]::new($false, $true)))
    $fixtureProjectPath = Join-Path $repositoryRoot (
        'src\FolderSessionLock.App\FolderSessionLock.App.csproj')
    [IO.Directory]::CreateDirectory(
        (Split-Path -Parent $fixtureProjectPath)) | Out-Null
    Write-Utf8 $fixtureProjectPath (
        "<Project Sdk=`"Microsoft.NET.Sdk`">`n" +
        "</Project>`n")
    Write-Utf8 (Join-Path $repositoryRoot 'FolderSessionLock.sln') (
        "Microsoft Visual Studio Solution File, Format Version 12.00`n")
    [void](Invoke-Git $repositoryRoot @('add', '--', '.'))
    [void](Invoke-Git $repositoryRoot @(
        'commit', '-m', 'old execution authority'))
    $executionCommit = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD')
    $executionTree = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD^{tree}')
    Write-Utf8 (
        Join-Path $repositoryRoot $fixedFiles[0].Replace('/', '\')) (
        $productionStage4Text + "`n# " + ('A' * 8192) +
            "`n# new recovery source`n# " + ('B' * 8192) + "`n")
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
        runId = '20260729T180000Z-1234abcd'
        machineName = [Environment]::MachineName
        branch = 'cp10-vm-transfer'
        gitCommit = $executionCommit
        sequence = 6
        transition = 'InstallStarted'
        releaseRoot = Join-Path (
            Join-Path 'C:\FSL-Release' '1.0.0') $executionCommit
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
    $getFormalBaseSnapshot = {
        param([string]$Path)

        $requestedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        $base = Get-Item -LiteralPath $requestedPath -Force
        $childNames = [string[]]@(
            Get-ChildItem -LiteralPath $requestedPath -Directory -Force |
                ForEach-Object { [string]$_.Name })
        [Array]::Sort($childNames, [StringComparer]::Ordinal)
        $children = @(
            foreach ($name in $childNames) {
                $child = Get-Item -LiteralPath (
                    Join-Path $requestedPath $name) -Force
                [pscustomobject][ordered]@{
                    name = [string]$child.Name
                    fullPath = [IO.Path]::GetFullPath(
                        [string]$child.FullName).TrimEnd('\')
                    creationTicks = [int64]$child.CreationTimeUtc.Ticks
                    attributes = [int]$child.Attributes
                }
            })
        return [pscustomobject][ordered]@{
            requestedPath = $requestedPath
            fullPath = [IO.Path]::GetFullPath(
                [string]$base.FullName).TrimEnd('\')
            name = [string]$base.Name
            parentFullPath = [IO.Path]::GetFullPath(
                [string]$base.Parent.FullName).TrimEnd('\')
            creationTicks = [int64]$base.CreationTimeUtc.Ticks
            attributes = [int]$base.Attributes
            ordinary = (
                $base -is [IO.DirectoryInfo] -and
                ($base.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -eq 0)
            children = $children
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
            baseRoot = $roots.baseRoot
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
        }
    } $formalModel
    $formalBaseBefore =
        & $getFormalBaseSnapshot $formalLayout.baseRoot
    $formalSourceAbsentBefore =
        -not (Test-Path -LiteralPath $formalLayout.sourceRoot) -and
        @($formalBaseBefore.children | Where-Object {
            $_.name -ceq $formalSourceLeaf }).Count -eq 0
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
    $formalBaseAfter =
        & $getFormalBaseSnapshot $formalLayout.baseRoot
    $formalSourceAbsentAfter =
        -not (Test-Path -LiteralPath $formalLayout.sourceRoot) -and
        @($formalBaseAfter.children | Where-Object {
            $_.name -ceq $formalSourceLeaf }).Count -eq 0
    $formalBaseUnchanged =
        $formalBaseBefore.ordinary -and
        $formalBaseAfter.ordinary -and
        $formalBaseBefore.requestedPath -ceq $formalBaseBefore.fullPath -and
        $formalBaseAfter.requestedPath -ceq $formalBaseAfter.fullPath -and
        $formalBaseBefore.fullPath -ceq $formalBaseAfter.fullPath -and
        $formalBaseBefore.name -ceq $formalBaseAfter.name -and
        $formalBaseBefore.parentFullPath -ceq
            $formalBaseAfter.parentFullPath -and
        $formalBaseBefore.creationTicks -eq
            $formalBaseAfter.creationTicks -and
        $formalBaseBefore.attributes -eq $formalBaseAfter.attributes -and
        @($formalBaseBefore.children).Count -eq
            @($formalBaseAfter.children).Count
    if ($formalBaseUnchanged) {
        for ($index = 0;
            $index -lt @($formalBaseBefore.children).Count;
            $index++) {
            $before = $formalBaseBefore.children[$index]
            $after = $formalBaseAfter.children[$index]
            $expectedPath = [IO.Path]::GetFullPath((
                Join-Path $formalBaseBefore.fullPath $before.name)).TrimEnd('\')
            if ($before.name -cne $after.name -or
                $before.fullPath -cne $after.fullPath -or
                $before.fullPath -cne $expectedPath -or
                $before.creationTicks -ne $after.creationTicks -or
                $before.attributes -ne $after.attributes) {
                $formalBaseUnchanged = $false
                break
            }
        }
    }
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
            $formalBaseUnchanged -and
            $formalLayout.installEmpty -and $formalLayout.anchorCount -eq 3 -and
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
                Join-Path $gitDirectory 'refs\heads\cp10-vm-transfer')) },
        { $packFiles.Count -ge 2 })
    for ($index = 0; $index -lt 12; $index++) {
        Assert-Case ([bool](& $gitChecks[$index])) (
            "Git authority case $index failed.") ($index -lt 6) (
            $executionCommit -cne $toolchainCommit)
    }

    $crlfTrackedPath = Join-Path $repositoryRoot (
        $fixedFiles[2].Replace('/', '\'))
    $crlfOriginalBytes = [IO.File]::ReadAllBytes($crlfTrackedPath)
    $crlfCleanAccepted = $false
    $crlfFailureCode = $null
    $crlfStatus = $null
    $crlfGeneratedCount = -1
    $crlfValidationValid = $false
    $crlfValidationErrors = -1
    $rootAttributesOracleDirty = $false
    $rootAttributesRejected = $false
    $rawConversionSourceRejected = $false
    $unsafeEnvironmentRejected = $false
    $worktreeConfigRejected = $false
    $nestedAttributesRejected = $false
    $customGlobalAttributesRejected = $false
    $dangerousRoundtripRejected = $false
    $dangerousBigFileRejected = $false
    $harmlessProfileAccepted = $false
    $homeOverridesXdgAccepted = $false
    $localOverridesHomeAccepted = $false
    $xdgOverridesSystemExact = $false
    $lfsQuartetAccepted = $false
    $partialLfsRejected = $false
    $commandAutocrlfRejected = $false
    $commandRoutingRejected = $false
    $commandIncompleteRejected = $false
    $commandBoundRejected = $false
    $namedRoutingOverridesRejected = $false
    $noncanonicalEnvironmentRejected = $false
    $profileLocalPath = $null
    $profileLocalBytes = $null
    $unsafeEnvironmentOriginal =
        [Environment]::GetEnvironmentVariable(
            'GIT_CONFIG_NOSYSTEM',
            [EnvironmentVariableTarget]::Process)
    $rootAttributesPath = Join-Path $repositoryRoot '.gitattributes'
    $nestedAttributesPath = Join-Path $repositoryRoot 'eng\.gitattributes'
    $worktreeConfigPath = Join-Path $gitDirectory 'config.worktree'
    try {
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'true'))
        $crlfText = [Text.UTF8Encoding]::new(
            $false,
            $true).GetString($crlfOriginalBytes).Replace(
                "`r`n",
                "`n").Replace(
                    "`n",
                    "`r`n")
        [IO.File]::WriteAllText(
            $crlfTrackedPath,
            $crlfText,
            [Text.UTF8Encoding]::new($false, $true))
        $crlfModel = New-Model $fixtureId 'crlf clean recovery source'
        $crlfGenerated =
            New-FslStage4RecoveryAuthorityBundle -Model $crlfModel
        $crlfValidation =
            Test-FslStage4RecoveryAuthorityBundle -Model $crlfModel
        $crlfStatus = Invoke-Git $repositoryRoot @(
            'status',
            '--porcelain=v1',
            '--untracked-files=all')
        $crlfGeneratedCount = $crlfGenerated.observedFiles.Count
        $crlfValidationValid = $crlfValidation.isValid
        $crlfValidationErrors = $crlfValidation.errors.Count
        $crlfCleanAccepted =
            $crlfGeneratedCount -eq 2 -and
            $crlfValidationValid -and
            $crlfValidationErrors -eq 0

        Write-Utf8 $rootAttributesPath "* -text`n"
        $savedPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            [void](& git.exe -C $repositoryRoot diff --quiet -- (
                $fixedFiles[2]) 2>&1)
            $rootAttributesOracleDirty = $LASTEXITCODE -eq 1
            if ($LASTEXITCODE -notin @(0, 1)) {
                throw 'The external Git conversion oracle failed.'
            }
        }
        finally {
            $ErrorActionPreference = $savedPreference
        }
        $rootAttributesRejected = Assert-ThrowsCode {
            New-FslStage4RecoveryAuthorityBundle -Model (
                New-Model $fixtureId 'root attributes exploit') |
                Out-Null
        } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' 'root attributes exploit'

        [IO.File]::WriteAllBytes($crlfTrackedPath, $crlfOriginalBytes)
        Write-Utf8 $rootAttributesPath (
            '*.ps1 filter=reviewer working-tree-encoding=UTF-8' + "`n")
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'false'))
        [void](Invoke-Git $repositoryRoot @(
            'config', 'filter.reviewer.clean', 'reviewer-filter'))
        $rawConversionSourceRejected = Assert-ThrowsCode {
            New-FslStage4RecoveryAuthorityBundle -Model (
                New-Model $fixtureId 'raw oid conversion source exploit') |
                Out-Null
        } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'raw oid conversion source exploit')

        [IO.File]::Delete($rootAttributesPath)
        [void](Invoke-Git $repositoryRoot @(
            'config', '--unset-all', 'filter.reviewer.clean'))
        [Environment]::SetEnvironmentVariable(
            'GIT_CONFIG_NOSYSTEM',
            '1',
            [EnvironmentVariableTarget]::Process)
        try {
            $unsafeEnvironmentRejected = Assert-ThrowsCode {
                New-FslStage4RecoveryAuthorityBundle -Model (
                    New-Model $fixtureId 'unsafe environment exploit') |
                    Out-Null
            } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'unsafe environment exploit')
        }
        finally {
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_NOSYSTEM',
                $unsafeEnvironmentOriginal,
                [EnvironmentVariableTarget]::Process)
        }

        Write-Utf8 $worktreeConfigPath (
            "[core]`n" +
            "    autocrlf = false`n")
        $worktreeConfigRejected = Assert-ThrowsCode {
            New-FslStage4RecoveryAuthorityBundle -Model (
                New-Model $fixtureId 'worktree config exploit') |
                Out-Null
        } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' 'worktree config exploit'
        [IO.File]::Delete($worktreeConfigPath)

        Write-Utf8 $nestedAttributesPath "* -text`n"
        $nestedAttributesRejected = Assert-ThrowsCode {
            New-FslStage4RecoveryAuthorityBundle -Model (
                New-Model $fixtureId 'nested attributes exploit') |
                Out-Null
        } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'nested attributes exploit')
        [IO.File]::Delete($nestedAttributesPath)

        [void](Invoke-Git $repositoryRoot @(
            'config',
            'core.attributesfile',
            'C:/synthetic-global-attributes'))
        $customGlobalAttributesRejected = Assert-ThrowsCode {
            New-FslStage4RecoveryAuthorityBundle -Model (
                New-Model $fixtureId 'custom global attributes exploit') |
                Out-Null
        } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'custom global attributes exploit')
        [void](Invoke-Git $repositoryRoot @(
            'config', '--unset-all', 'core.attributesfile'))

        foreach ($dangerousKey in @(
            'core.checkRoundtripEncoding',
            'core.bigFileThreshold')) {
            [void](Invoke-Git $repositoryRoot @(
                'config', $dangerousKey, 'synthetic-danger'))
            $dangerousLeaf = 'round4 dangerous ' + $dangerousKey
            $dangerousRejected = Assert-ThrowsCode {
                New-FslStage4RecoveryAuthorityBundle -Model (
                    New-Model $fixtureId $dangerousLeaf) |
                    Out-Null
            } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' $dangerousKey
            if ($dangerousKey -ceq 'core.checkRoundtripEncoding') {
                $dangerousRoundtripRejected = $dangerousRejected
            }
            else {
                $dangerousBigFileRejected = $dangerousRejected
            }
            $dangerousRoot = Join-Path $fixtureRoot $dangerousLeaf
            if (Test-Path -LiteralPath $dangerousRoot) {
                Remove-Item -LiteralPath $dangerousRoot -Recurse -Force
            }
            [void](Invoke-Git $repositoryRoot @(
                'config', '--unset-all', $dangerousKey))
        }

        $profileHome = Join-Path $fixtureRoot 'round4-home'
        $profileXdg = Join-Path $fixtureRoot 'round4-xdg'
        [IO.Directory]::CreateDirectory(
            (Join-Path $profileXdg 'git')) | Out-Null
        [IO.Directory]::CreateDirectory($profileHome) | Out-Null
        Write-Utf8 (Join-Path $profileXdg 'git\config') (
            "[core]`n" +
            "    autocrlf = false`n" +
            "[user]`n" +
            "    name = harmless xdg fixture`n")
        Write-Utf8 (Join-Path $profileHome '.gitconfig') (
            "[core]`n" +
            "    autocrlf = true`n" +
            "[diff `"harmless`"]`n" +
            "    command = inert fixture`n")
        $profileEnvironmentNames = @(
            'HOME',
            'XDG_CONFIG_HOME',
            'GIT_FSL_HARMLESS',
            'GIT_CONFIG_COUNT',
            'GIT_CONFIG_KEY_0',
            'GIT_CONFIG_VALUE_0',
            'ProgramFiles',
            'USERPROFILE')
        $profileEnvironment = @{}
        foreach ($name in $profileEnvironmentNames) {
            $profileEnvironment[$name] =
                [Environment]::GetEnvironmentVariable(
                    $name,
                    [EnvironmentVariableTarget]::Process)
        }
        try {
            [Environment]::SetEnvironmentVariable(
                'HOME',
                $profileHome,
                [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                'XDG_CONFIG_HOME',
                $profileXdg,
                [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                'GIT_FSL_HARMLESS',
                'round4-fixture',
                [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_COUNT',
                '1',
                [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_KEY_0',
                'safe.directory',
                [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_VALUE_0',
                $repositoryRoot,
                [EnvironmentVariableTarget]::Process)
            [void](Invoke-Git $repositoryRoot @(
                'config', 'core.autocrlf', 'true'))
            [IO.File]::WriteAllText(
                $crlfTrackedPath,
                ([Text.UTF8Encoding]::new(
                    $false,
                    $true).GetString($crlfOriginalBytes).Replace(
                        "`r`n",
                        "`n").Replace(
                            "`n",
                            "`r`n")),
                [Text.UTF8Encoding]::new($false, $true))
            $harmlessLeaf = 'round4 harmless profile'
            $harmlessModel = New-Model $fixtureId $harmlessLeaf
            $harmlessGenerated =
                New-FslStage4RecoveryAuthorityBundle -Model $harmlessModel
            $harmlessValidation =
                Test-FslStage4RecoveryAuthorityBundle -Model $harmlessModel
            $harmlessProfileAccepted =
                $harmlessGenerated.observedFiles.Count -eq 2 -and
                $harmlessValidation.isValid -and
                $harmlessValidation.errors.Count -eq 0

            $profileLocalPath = Join-Path $gitDirectory 'config'
            $profileLocalBytes =
                [IO.File]::ReadAllBytes($profileLocalPath)
            $profileProbe = {
                param(
                    [string]$Leaf,
                    [AllowNull()][string]$XdgText,
                    [AllowNull()][string]$HomeText,
                    [string]$LocalText,
                    [bool]$Crlf)
                $xdgPath = Join-Path $profileXdg 'git\config'
                $homePath = Join-Path $profileHome '.gitconfig'
                if ($null -eq $XdgText) {
                    if (Test-Path -LiteralPath $xdgPath) {
                        [IO.File]::Delete($xdgPath)
                    }
                }
                else { Write-Utf8 $xdgPath $XdgText }
                if ($null -eq $HomeText) {
                    if (Test-Path -LiteralPath $homePath) {
                        [IO.File]::Delete($homePath)
                    }
                }
                else { Write-Utf8 $homePath $HomeText }
                Write-Utf8 $profileLocalPath $LocalText
                if ($Crlf) {
                    [IO.File]::WriteAllText(
                        $crlfTrackedPath,
                        ([Text.UTF8Encoding]::new(
                            $false,
                            $true).GetString($crlfOriginalBytes).Replace(
                                "`r`n",
                                "`n").Replace(
                                    "`n",
                                    "`r`n")),
                        [Text.UTF8Encoding]::new($false, $true))
                }
                else {
                    [IO.File]::WriteAllBytes(
                        $crlfTrackedPath,
                        $crlfOriginalBytes)
                }
                $accepted = $false
                $code = $null
                try {
                    $probeModel = New-Model $fixtureId $Leaf
                    $probeGenerated =
                        New-FslStage4RecoveryAuthorityBundle `
                            -Model $probeModel
                    $probeValidation =
                        Test-FslStage4RecoveryAuthorityBundle `
                            -Model $probeModel
                    $accepted =
                        $probeGenerated.observedFiles.Count -eq 2 -and
                        $probeValidation.isValid -and
                        $probeValidation.errors.Count -eq 0
                }
                catch {
                    $code = [string]$_.Exception.Data[
                        'FslRecoveryAuthorityBundleCode']
                }
                finally {
                    $probeRoot = Join-Path $fixtureRoot $Leaf
                    if (Test-Path -LiteralPath $probeRoot) {
                        Remove-Item -LiteralPath $probeRoot -Recurse -Force
                    }
                }
                return [pscustomobject]@{
                    accepted = $accepted
                    code = $code
                }
            }

            $homePrecedence = & $profileProbe `
                'round4 home precedence' `
                ("[core]`n    autocrlf = false`n") `
                ("[core]`n    autocrlf = true`n" +
                    "[credential]`n    helper = harmless`n") `
                ("[user]`n    email = harmless@example.invalid`n") `
                $true
            $homeOverridesXdgAccepted = $homePrecedence.accepted

            $localPrecedence = & $profileProbe `
                'round4 local precedence' `
                ("[core]`n    autocrlf = true`n") `
                ("[core]`n    autocrlf = false`n") `
                ("[core]`n    autocrlf = true`n" +
                    "[diff `"harmless`"]`n    command = inert`n") `
                $true
            $localOverridesHomeAccepted = $localPrecedence.accepted

            $xdgLf = & $profileProbe `
                'round4 xdg false lf' `
                ("[core]`n    autocrlf = false`n") `
                ("[user]`n    name = harmless`n") `
                ("[credential]`n    helper = harmless`n") `
                $false
            $xdgCrlf = & $profileProbe `
                'round4 xdg false crlf' `
                ("[core]`n    autocrlf = false`n") `
                ("[user]`n    name = harmless`n") `
                ("[credential]`n    helper = harmless`n") `
                $true
            $xdgOverridesSystemExact =
                $xdgLf.accepted -and
                -not $xdgCrlf.accepted -and
                $xdgCrlf.code -ceq
                    'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'

            $lfsText =
                "[filter `"lfs`"]`n" +
                "    clean = git-lfs clean -- %f`n" +
                "    smudge = git-lfs smudge -- %f`n" +
                "    process = git-lfs filter-process`n" +
                "    required = true`n" +
                "[core]`n" +
                "    autocrlf = true`n"
            $lfsXdgProbe = & $profileProbe `
                'round4 lfs quartet xdg' `
                $lfsText `
                ("[user]`n    name = harmless`n") `
                ("[credential]`n    helper = harmless`n") `
                $true
            $lfsHomeProbe = & $profileProbe `
                'round4 lfs quartet home' `
                ("[user]`n    name = harmless`n") `
                $lfsText `
                ("[credential]`n    helper = harmless`n") `
                $true
            $lfsLocalProbe = & $profileProbe `
                'round4 lfs quartet local' `
                ("[user]`n    name = harmless`n") `
                ("[credential]`n    helper = harmless`n") `
                $lfsText `
                $true
            $lfsQuartetAccepted =
                $lfsXdgProbe.accepted -and
                $lfsHomeProbe.accepted -and
                $lfsLocalProbe.accepted
            $round6LfsExactDuplicateProbe = & $profileProbe `
                'round6 lfs exact duplicate' `
                $null `
                $null `
                ("[filter `"lfs`"]`n" +
                    "    clean = git-lfs clean -- %f`n" +
                    "    smudge = git-lfs smudge -- %f`n" +
                    "    process = git-lfs filter-process`n" +
                    "    required = true`n" +
                    "    clean = git-lfs clean -- %f`n" +
                    "[core]`n    autocrlf = true`n") `
                $true
            $round6LfsCaseDuplicateProbe = & $profileProbe `
                'round6 lfs case duplicate' `
                $null `
                $null `
                ("[filter `"lfs`"]`n" +
                    "    clean = git-lfs clean -- %f`n" +
                    "    smudge = git-lfs smudge -- %f`n" +
                    "    process = git-lfs filter-process`n" +
                    "    required = true`n" +
                    "    ClEaN = git-lfs clean -- %f`n" +
                    "[core]`n    autocrlf = true`n") `
                $true
            $round6LfsWrongDuplicateProbe = & $profileProbe `
                'round6 lfs wrong duplicate' `
                $null `
                $null `
                ("[filter `"lfs`"]`n" +
                    "    clean = git-lfs clean -- %f`n" +
                    "    smudge = git-lfs smudge -- %f`n" +
                    "    process = git-lfs filter-process`n" +
                    "    required = true`n" +
                    "    clean = git-lfs clean -- %x`n" +
                    "[core]`n    autocrlf = true`n") `
                $true
            $round6LfsDuplicateGrammarClosure =
                $round6LfsExactDuplicateProbe.accepted -and
                $round6LfsCaseDuplicateProbe.accepted -and
                -not $round6LfsWrongDuplicateProbe.accepted -and
                $round6LfsWrongDuplicateProbe.code -ceq
                    'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
            $round5DuplicateImplicitProbe = & $profileProbe `
                'round5 duplicate implicit autocrlf' `
                ("[user]`n    name = harmless`n") `
                ("[credential]`n    helper = harmless`n") `
                ("[core]`n    autocrlf = false`n" +
                    "    autocrlf`n") `
                $true
            $round5DuplicateOrders = $true
            foreach ($duplicateVector in @(
                [pscustomobject]@{
                    text = "[core]`n    autocrlf = true`n" +
                        "    autocrlf = false`n"
                    crlf = $false
                },
                [pscustomobject]@{
                    text = "[core]`n    autocrlf = true`n" +
                        "    autocrlf = true`n"
                    crlf = $true
                },
                [pscustomobject]@{
                    text = "[core]`n    autocrlf = false`n" +
                        "    autocrlf = false`n"
                    crlf = $false
                })) {
                $duplicateProbe = & $profileProbe `
                    ('round5 duplicate order ' +
                        [Guid]::NewGuid().ToString('N')) `
                    $null `
                    $null `
                    $duplicateVector.text `
                    $duplicateVector.crlf
                $round5DuplicateOrders =
                    $round5DuplicateOrders -and $duplicateProbe.accepted
            }
            $round5QuotedGrammarProbe = & $profileProbe `
                'round5 quoted grammar' `
                ("[user `"quoted`"]`n" +
                    "    name = `"line\nvalue`" # comment`n") `
                ("[diff `"harmless`"]`n" +
                    "    command = first\`n        second`n") `
                ("[core]`n    autocrlf = `"true`" ; comment`n") `
                $true
            $round5InvalidOccurrenceProbe = & $profileProbe `
                'round5 invalid duplicate autocrlf' `
                $null `
                $null `
                ("[core]`n    autocrlf = maybe`n" +
                    "    autocrlf = true`n") `
                $true
            $round5MalformedGrammarRejected = $true
            foreach ($malformedText in @(
                "[core`n    autocrlf = true`n",
                "[core]`n    autocrlf = `"true`n",
                "[diff]`n    command = true\q`n",
                "[core]`n    autocrlf = true\\",
                "autocrlf = true`n")) {
                $malformedProbe = & $profileProbe `
                    ('round5 malformed ' + [Guid]::NewGuid().ToString('N')) `
                    $null `
                    $null `
                    $malformedText `
                    $false
                $round5MalformedGrammarRejected =
                    $round5MalformedGrammarRejected -and
                    -not $malformedProbe.accepted -and
                    $malformedProbe.code -ceq
                        'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
            }
            $round5GrammarClosure =
                $round5DuplicateImplicitProbe.accepted -and
                $round5DuplicateOrders -and
                $round5QuotedGrammarProbe.accepted -and
                -not $round5InvalidOccurrenceProbe.accepted -and
                $round5InvalidOccurrenceProbe.code -ceq
                    'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' -and
                $round5MalformedGrammarRejected
            $partialLfsProbe = & $profileProbe `
                'round4 partial lfs' `
                ("[user]`n    name = harmless`n") `
                ("[filter `"lfs`"]`n" +
                    "    clean = git-lfs clean -- %f`n") `
                ("[core]`n    autocrlf = false`n") `
                $false
            $partialLfsRejected =
                -not $partialLfsProbe.accepted -and
                $partialLfsProbe.code -ceq
                    'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'

            foreach ($commandKey in @(
                'core.autocrlf',
                'core.attributesfile')) {
                [Environment]::SetEnvironmentVariable(
                    'GIT_CONFIG_KEY_0',
                    $commandKey,
                    [EnvironmentVariableTarget]::Process)
                $commandProbe = & $profileProbe `
                    ('round4 command ' + $commandKey) `
                    ("[user]`n    name = harmless`n") `
                    ("[credential]`n    helper = harmless`n") `
                    ("[core]`n    autocrlf = false`n") `
                    $false
                $commandRejected =
                    -not $commandProbe.accepted -and
                    $commandProbe.code -ceq
                        'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
                if ($commandKey -ceq 'core.autocrlf') {
                    $commandAutocrlfRejected = $commandRejected
                }
                else { $commandRoutingRejected = $commandRejected }
            }
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_KEY_0',
                'SaFe.DiReCtOrY',
                [EnvironmentVariableTarget]::Process)
            $mixedSafeCommandProbe = & $profileProbe `
                'round5 mixed safe.directory' `
                $null `
                $null `
                ("[core]`n    autocrlf = false`n") `
                $false
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_KEY_0',
                'CoRe.AuToCrLf',
                [EnvironmentVariableTarget]::Process)
            $mixedDangerousCommandProbe = & $profileProbe `
                'round5 mixed dangerous command' `
                $null `
                $null `
                ("[core]`n    autocrlf = false`n") `
                $false
            $round5CommandCaseClosure =
                $mixedSafeCommandProbe.accepted -and
                -not $mixedDangerousCommandProbe.accepted -and
                $mixedDangerousCommandProbe.code -ceq
                    'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_KEY_0',
                'safe.directory',
                [EnvironmentVariableTarget]::Process)

            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_VALUE_0',
                $null,
                [EnvironmentVariableTarget]::Process)
            $incompleteProbe = & $profileProbe `
                'round4 incomplete command config' `
                $null `
                $null `
                ("[core]`n    autocrlf = false`n") `
                $false
            $commandIncompleteRejected =
                -not $incompleteProbe.accepted -and
                $incompleteProbe.code -ceq
                    'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_VALUE_0',
                $repositoryRoot,
                [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_COUNT',
                '65',
                [EnvironmentVariableTarget]::Process)
            $boundProbe = & $profileProbe `
                'round4 bounded command config' `
                $null `
                $null `
                ("[core]`n    autocrlf = false`n") `
                $false
            $commandBoundRejected =
                -not $boundProbe.accepted -and
                $boundProbe.code -ceq
                    'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
            [Environment]::SetEnvironmentVariable(
                'GIT_CONFIG_COUNT',
                '1',
                [EnvironmentVariableTarget]::Process)

            $namedRoutingOverridesRejected = $true
            foreach ($routingName in @(
                'GIT_CONFIG_SYSTEM',
                'GIT_CONFIG_GLOBAL',
                'GIT_CONFIG_NOSYSTEM',
                'GIT_CONFIG_PARAMETERS',
                'GIT_ATTR_NOSYSTEM',
                'GIT_DIR',
                'GIT_WORK_TREE',
                'GIT_COMMON_DIR',
                'GIT_INDEX_FILE',
                'GIT_OBJECT_DIRECTORY',
                'GIT_ALTERNATE_OBJECT_DIRECTORIES',
                'GIT_QUARANTINE_PATH',
                'GIT_NAMESPACE',
                'GIT_SHALLOW_FILE',
                'GIT_GRAFT_FILE',
                'GIT_NO_REPLACE_OBJECTS',
                'GIT_REPLACE_REF_BASE',
                'GIT_CONFIG_UNKNOWN')) {
                $routingOriginal =
                    [Environment]::GetEnvironmentVariable(
                        $routingName,
                        [EnvironmentVariableTarget]::Process)
                try {
                    [Environment]::SetEnvironmentVariable(
                        $routingName,
                        'round4-routing-override',
                        [EnvironmentVariableTarget]::Process)
                    $routingProbe = & $profileProbe `
                        ('round4 routing ' + $routingName) `
                        $null `
                        $null `
                        ("[core]`n    autocrlf = false`n") `
                        $false
                    $namedRoutingOverridesRejected =
                        $namedRoutingOverridesRejected -and
                        -not $routingProbe.accepted -and
                        $routingProbe.code -ceq
                            'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
                }
                finally {
                    [Environment]::SetEnvironmentVariable(
                        $routingName,
                        $routingOriginal,
                        [EnvironmentVariableTarget]::Process)
                }
            }

            $noncanonicalEnvironmentRejected = $true
            foreach ($canonicalName in @(
                'ProgramFiles',
                'USERPROFILE',
                'HOME')) {
                $canonicalOriginal =
                    [Environment]::GetEnvironmentVariable(
                        $canonicalName,
                        [EnvironmentVariableTarget]::Process)
                try {
                    [Environment]::SetEnvironmentVariable(
                        $canonicalName,
                        (Join-Path $canonicalOriginal '.'),
                        [EnvironmentVariableTarget]::Process)
                    $noncanonicalProbe = & $profileProbe `
                        ('round4 noncanonical ' + $canonicalName) `
                        $null `
                        $null `
                        ("[core]`n    autocrlf = false`n") `
                        $false
                    $noncanonicalEnvironmentRejected =
                        $noncanonicalEnvironmentRejected -and
                        -not $noncanonicalProbe.accepted -and
                        $noncanonicalProbe.code -ceq
                            'FSL-RAB-V007-TOOLCHAIN-AUTHORITY'
                }
                finally {
                    [Environment]::SetEnvironmentVariable(
                        $canonicalName,
                        $canonicalOriginal,
                        [EnvironmentVariableTarget]::Process)
                }
            }
            $programW6432Original =
                [Environment]::GetEnvironmentVariable(
                    'ProgramW6432',
                    [EnvironmentVariableTarget]::Process)
            try {
                $round5ProgramW6432Closure = $true
                foreach ($programW6432Vector in @(
                    [pscustomobject]@{
                        value = $null
                        accepted = $true
                    },
                    [pscustomobject]@{
                        value = [Environment]::GetEnvironmentVariable(
                            'ProgramFiles',
                            [EnvironmentVariableTarget]::Process)
                        accepted = $true
                    },
                    [pscustomobject]@{
                        value = '.'
                        accepted = $false
                    },
                    [pscustomobject]@{
                        value = (Join-Path $fixtureRoot 'missing-program-w6432')
                        accepted = $false
                    },
                    [pscustomobject]@{
                        value = $env:SystemRoot
                        accepted = $false
                    })) {
                    [Environment]::SetEnvironmentVariable(
                        'ProgramW6432',
                        $programW6432Vector.value,
                        [EnvironmentVariableTarget]::Process)
                    $programW6432Probe = & $profileProbe `
                        ('round5 ProgramW6432 ' +
                            [Guid]::NewGuid().ToString('N')) `
                        $null `
                        $null `
                        ("[core]`n    autocrlf = false`n") `
                        $false
                    $round5ProgramW6432Closure =
                        $round5ProgramW6432Closure -and
                        ($programW6432Probe.accepted -eq
                            $programW6432Vector.accepted)
                }
            }
            finally {
                [Environment]::SetEnvironmentVariable(
                    'ProgramW6432',
                    $programW6432Original,
                    [EnvironmentVariableTarget]::Process)
            }
            [IO.File]::WriteAllBytes(
                $profileLocalPath,
                $profileLocalBytes)
        }
        finally {
            [IO.File]::WriteAllBytes($crlfTrackedPath, $crlfOriginalBytes)
            foreach ($name in $profileEnvironmentNames) {
                [Environment]::SetEnvironmentVariable(
                    $name,
                    $profileEnvironment[$name],
                    [EnvironmentVariableTarget]::Process)
            }
            $harmlessRoot = Join-Path $fixtureRoot 'round4 harmless profile'
            if (Test-Path -LiteralPath $harmlessRoot) {
                Remove-Item -LiteralPath $harmlessRoot -Recurse -Force
            }
            [void](Invoke-Git $repositoryRoot @(
                'config', 'core.autocrlf', 'false'))
        }
    }
    catch {
        $crlfFailureCode = [string]$_.Exception.Data[
            'FslRecoveryAuthorityBundleCode']
    }
    finally {
        [IO.File]::WriteAllBytes($crlfTrackedPath, $crlfOriginalBytes)
        if ($null -ne $profileLocalBytes -and
            -not [string]::IsNullOrEmpty($profileLocalPath)) {
            [IO.File]::WriteAllBytes(
                $profileLocalPath,
                $profileLocalBytes)
        }
        if (Test-Path -LiteralPath $rootAttributesPath) {
            [IO.File]::Delete($rootAttributesPath)
        }
        foreach ($temporaryPath in @(
            $nestedAttributesPath,
            $worktreeConfigPath)) {
            if (Test-Path -LiteralPath $temporaryPath) {
                [IO.File]::Delete($temporaryPath)
            }
        }
        [Environment]::SetEnvironmentVariable(
            'GIT_CONFIG_NOSYSTEM',
            $unsafeEnvironmentOriginal,
            [EnvironmentVariableTarget]::Process)
        $savedPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            [void](& git.exe -C $repositoryRoot config --unset-all (
                'filter.reviewer.clean') 2>&1)
            [void](& git.exe -C $repositoryRoot config --unset-all (
                'core.attributesfile') 2>&1)
        }
        finally {
            $ErrorActionPreference = $savedPreference
        }
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'false'))
    }
    Assert-Case (
        $crlfCleanAccepted -and
        $rootAttributesOracleDirty -and
        $rootAttributesRejected -and
        $rawConversionSourceRejected -and
        $unsafeEnvironmentRejected -and
        $worktreeConfigRejected -and
        $nestedAttributesRejected -and
        $customGlobalAttributesRejected -and
        $dangerousRoundtripRejected -and
        $dangerousBigFileRejected -and
        $harmlessProfileAccepted -and
        $homeOverridesXdgAccepted -and
        $localOverridesHomeAccepted -and
        $xdgOverridesSystemExact -and
        $lfsQuartetAccepted -and
        $partialLfsRejected -and
        $commandAutocrlfRejected -and
        $commandRoutingRejected -and
        $commandIncompleteRejected -and
        $commandBoundRejected -and
        $namedRoutingOverridesRejected -and
        $noncanonicalEnvironmentRejected -and
        $round6LfsDuplicateGrammarClosure -and
        $round5GrammarClosure -and
        $round5CommandCaseClosure -and
        $round5ProgramW6432Closure) (
        'A semantically clean core.autocrlf=true CRLF worktree was rejected: ' +
        "code=$crlfFailureCode status=$crlfStatus " +
        "generated=$crlfGeneratedCount valid=$crlfValidationValid " +
        "errors=$crlfValidationErrors oracleDirty=$rootAttributesOracleDirty " +
        "rootAttributesRejected=$rootAttributesRejected " +
        "rawSourceRejected=$rawConversionSourceRejected " +
        "environmentRejected=$unsafeEnvironmentRejected " +
        "worktreeConfigRejected=$worktreeConfigRejected " +
        "nestedAttributesRejected=$nestedAttributesRejected " +
        "customGlobalRejected=$customGlobalAttributesRejected " +
        "roundtripRejected=$dangerousRoundtripRejected " +
        "bigFileRejected=$dangerousBigFileRejected " +
        "harmlessProfileAccepted=$harmlessProfileAccepted " +
        "homePrecedence=$homeOverridesXdgAccepted " +
        "localPrecedence=$localOverridesHomeAccepted " +
        "xdgPrecedence=$xdgOverridesSystemExact " +
        "lfsQuartet=$lfsQuartetAccepted partialLfs=$partialLfsRejected " +
        "commandAutocrlf=$commandAutocrlfRejected " +
        "commandRouting=$commandRoutingRejected " +
        "commandIncomplete=$commandIncompleteRejected " +
        "commandBound=$commandBoundRejected " +
        "namedRouting=$namedRoutingOverridesRejected " +
        "canonicalEnvironment=$noncanonicalEnvironmentRejected " +
        "round6LfsDuplicates=$round6LfsDuplicateGrammarClosure " +
        "round6Exact=$($round6LfsExactDuplicateProbe.accepted) " +
        "round6Case=$($round6LfsCaseDuplicateProbe.accepted) " +
        "round6WrongAccepted=$($round6LfsWrongDuplicateProbe.accepted) " +
        "round6WrongCode=$($round6LfsWrongDuplicateProbe.code) " +
        "round5Grammar=$round5GrammarClosure " +
        "round5Command=$round5CommandCaseClosure " +
        "round5ProgramW6432=$round5ProgramW6432Closure")

    $customEolRejected = $false
    try {
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'true'))
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.eol', 'lf'))
        [IO.File]::WriteAllText(
            $crlfTrackedPath,
            ([Text.UTF8Encoding]::new(
                $false,
                $true).GetString($crlfOriginalBytes).Replace(
                    "`r`n",
                    "`n").Replace(
                        "`n",
                        "`r`n")),
            [Text.UTF8Encoding]::new($false, $true))
        $customEolModel =
            New-Model $fixtureId 'custom eol recovery source'
        $customEolRejected = Assert-ThrowsCode {
            New-FslStage4RecoveryAuthorityBundle -Model $customEolModel |
                Out-Null
        } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' 'custom eol'
    }
    finally {
        [IO.File]::WriteAllBytes($crlfTrackedPath, $crlfOriginalBytes)
        [void](Invoke-Git $repositoryRoot @(
            'config', '--unset-all', 'core.eol'))
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'false'))
    }
    Assert-Case $customEolRejected (
        'A custom core.eol profile was accepted for CRLF canonicalization.')

    $customAttributesPath = Join-Path $gitDirectory 'info\attributes'
    $customAttributesRejected = $false
    try {
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'true'))
        Write-Utf8 $customAttributesPath "* text eol=lf`n"
        [IO.File]::WriteAllText(
            $crlfTrackedPath,
            ([Text.UTF8Encoding]::new(
                $false,
                $true).GetString($crlfOriginalBytes).Replace(
                    "`r`n",
                    "`n").Replace(
                        "`n",
                        "`r`n")),
            [Text.UTF8Encoding]::new($false, $true))
        $customAttributesModel =
            New-Model $fixtureId 'custom attributes recovery source'
        $customAttributesRejected = Assert-ThrowsCode {
            New-FslStage4RecoveryAuthorityBundle `
                -Model $customAttributesModel |
                Out-Null
        } 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' 'custom attributes'
    }
    finally {
        [IO.File]::WriteAllBytes($crlfTrackedPath, $crlfOriginalBytes)
        if (Test-Path -LiteralPath $customAttributesPath) {
            [IO.File]::Delete($customAttributesPath)
        }
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'false'))
    }
    Assert-Case $customAttributesRejected (
        'A custom repository attributes profile was accepted.')

    $systemProfileAccepted = $false
    $systemProfileFailureCode = $null
    try {
        [void](Invoke-Git $repositoryRoot @(
            'config', '--unset-all', 'core.autocrlf'))
        [IO.File]::WriteAllText(
            $crlfTrackedPath,
            ([Text.UTF8Encoding]::new(
                $false,
                $true).GetString($crlfOriginalBytes).Replace(
                    "`r`n",
                    "`n").Replace(
                        "`n",
                        "`r`n")),
            [Text.UTF8Encoding]::new($false, $true))
        $systemProfileModel =
            New-Model $fixtureId 'system profile recovery source'
        $systemProfileGenerated =
            New-FslStage4RecoveryAuthorityBundle -Model $systemProfileModel
        $systemProfileValidation =
            Test-FslStage4RecoveryAuthorityBundle -Model $systemProfileModel
        $systemProfileAccepted =
            $systemProfileGenerated.observedFiles.Count -eq 2 -and
            $systemProfileValidation.isValid -and
            $systemProfileValidation.errors.Count -eq 0
    }
    catch {
        $systemProfileFailureCode = [string]$_.Exception.Data[
            'FslRecoveryAuthorityBundleCode']
    }
    finally {
        [IO.File]::WriteAllBytes($crlfTrackedPath, $crlfOriginalBytes)
        [void](Invoke-Git $repositoryRoot @(
            'config', 'core.autocrlf', 'false'))
    }
    Assert-Case $systemProfileAccepted (
        'The safe standard system autocrlf profile was rejected: ' +
        $systemProfileFailureCode)

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

    # Group 8: verified recovery context seam, 30 / 45.
    $verifiedAuthority = & $module {
        param($Model)
        Resolve-FslRabVerifiedRecoveryAuthority $Model
    } $model
    $fixtureStage4ModulePath = Join-Path $repositoryRoot (
        'eng\stage4\FolderSessionLock.Stage4.psm1')
    $fixtureRecoveryModulePath = Join-Path $repositoryRoot (
        'eng\stage4\FolderSessionLock.Stage4.RecoveryAuthorityBundle.psm1')
    $stage4Module = Import-Module $fixtureStage4ModulePath -Force -PassThru
    $frozenContext = & $stage4Module {
        param($Authority, $ApprovedCommit)
        $script:ApprovedCommit = $ApprovedCommit
        Get-FslFrozenRecoveryContext $Authority
    } $verifiedAuthority $executionCommit
    $forbiddenMutationPath = Join-Path $repositoryRoot (
        'forbidden-recovery-mutation.txt')
    $mutationGateCode = $null
    try {
        Write-Utf8 $forbiddenMutationPath "forbidden mutation`n"
        $mutationGateCode = & $stage4Module {
            param($Authority)
            try {
                Get-FslFrozenRecoveryContext $Authority | Out-Null
                return $null
            }
            catch {
                return [int]$_.Exception.Data['FslStage4ExitCode']
            }
        } $verifiedAuthority
    }
    finally {
        [IO.File]::Delete($forbiddenMutationPath)
    }
    $publicOldReleaseCode = & $stage4Module {
        param($RunId, $OldReleaseRoot)
        try {
            Get-FslContext $RunId $OldReleaseRoot | Out-Null
            return $null
        }
        catch {
            return [int]$_.Exception.Data['FslStage4ExitCode']
        }
    } $model.runId (
        Join-Path (Join-Path 'C:\FSL-Release' '1.0.0') $executionCommit)
    $invokeTamperedContext = {
        param($Authority)
        return & $stage4Module {
            param($Value)
            try {
                Get-FslFrozenRecoveryContext $Value | Out-Null
                return $null
            }
            catch {
                return [int]$_.Exception.Data['FslStage4ExitCode']
            }
        } $Authority
    }
    $tamperedAuthorities = [Collections.Generic.List[object]]::new()
    $tampered = Copy-Object $verifiedAuthority
    $tampered | Add-Member unexpectedProperty forbidden
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.repositoryRoot = Join-Path $fixtureRoot 'wrong-repository'
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.evidenceRoot = Join-Path $fixtureRoot 'wrong-evidence'
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.installDirectory = Join-Path $fixtureRoot 'wrong-install'
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.programDataRoot = Join-Path $fixtureRoot 'wrong-program-data'
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.externalAnchorRoot = Join-Path $fixtureRoot 'wrong-anchors'
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.state.ReleaseRoot = Join-Path $fixtureRoot 'wrong-release'
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.recoveryGitCommit = 'f' * 40
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.recoveryGitTree = 'f' * 40
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.state.sequence = 5
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.state.machineName = 'WRONG-STAGE4-MACHINE'
    $tamperedAuthorities.Add($tampered)
    $tampered = Copy-Object $verifiedAuthority
    $tampered.state.branch = 'wrong-stage4-branch'
    $tamperedAuthorities.Add($tampered)
    $tamperCodes = @($tamperedAuthorities | ForEach-Object {
        & $invokeTamperedContext $_
    })
    $wrapperCommands = @($wrapperAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true))
    $resolverCommands = @($wrapperCommands | Where-Object {
        $_.GetCommandName() -ceq
            'Resolve-FslRabVerifiedRecoveryAuthority'
    })
    $contextCommands = @($wrapperCommands | Where-Object {
        $_.GetCommandName() -ceq 'Get-FslFrozenRecoveryContext'
    })
    $importCommands = @($wrapperCommands | Where-Object {
        $_.GetCommandName() -ceq 'Import-Module'
    })
    $forbiddenWrapperCommands = @($wrapperCommands | Where-Object {
        $_.GetCommandName() -cin @(
            'Get-FslContext',
            'Invoke-FslStage4Command',
            'Invoke-FslStage4',
            'Invoke-FslInstall',
            'Start-Process',
            'Invoke-Expression')
    })
    $verifiedNames = @(
        'schemaVersion','authorityKind','runId','repositoryRoot','evidenceRoot',
        'installDirectory','programDataRoot','externalAnchorRoot',
        'executionGitCommit','executionGitTree','recoveryGitCommit',
        'recoveryGitTree','state')
    $expectedEvidenceRoot = Join-Path $repositoryRoot (
        Join-Path 'docs\evidence\stage-4' $model.runId)
    $expectedInstallDirectory = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::ProgramFiles)) 'FolderSessionLock'
    $expectedProgramDataRoot = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::CommonApplicationData)) (
            'FolderSessionLock')
    $expectedAnchorRoot = Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) (
            Join-Path 'FolderSessionLock\Stage4\Anchors' $model.runId)
    $expectedReleaseRoot = Join-Path (
        Join-Path 'C:\FSL-Release' '1.0.0') $executionCommit
    $liveProductionCommit = Invoke-Git $projectRoot @('rev-parse', 'HEAD')
    $liveProductionTree = Invoke-Git $projectRoot @(
        'rev-parse', 'HEAD^{tree}')
    $stage4ModuleText = [IO.File]::ReadAllText(
        $stage4ModulePath,
        [Text.UTF8Encoding]::new($false, $true))
    $combinedProductionText = $moduleText + "`n" + $stage4ModuleText
    $seamChecks = @(
        { (Get-PropertyNames $verifiedAuthority) -join '|' -ceq
            ($verifiedNames -join '|') },
        { $verifiedAuthority.schemaVersion -eq 1 -and
            $verifiedAuthority.authorityKind -ceq
                'FolderSessionLock.Stage4.VerifiedFrozenRecoveryAuthority.v1' },
        { $verifiedAuthority.runId -ceq $model.runId },
        { $verifiedAuthority.repositoryRoot -ceq $repositoryRoot },
        { $verifiedAuthority.evidenceRoot -ceq $expectedEvidenceRoot },
        { $verifiedAuthority.installDirectory -ceq
                $expectedInstallDirectory -and
            $verifiedAuthority.programDataRoot -ceq
                $expectedProgramDataRoot -and
            $verifiedAuthority.externalAnchorRoot -ceq $expectedAnchorRoot },
        { $verifiedAuthority.executionGitCommit -ceq $executionCommit -and
            $verifiedAuthority.executionGitTree -ceq $executionTree },
        { $verifiedAuthority.recoveryGitCommit -ceq $toolchainCommit -and
            $verifiedAuthority.recoveryGitTree -ceq $toolchainTree },
        { $verifiedAuthority.state.runId -ceq $model.runId -and
            $verifiedAuthority.state.machineName -ceq
                [Environment]::MachineName -and
            $verifiedAuthority.state.branch -ceq 'cp10-vm-transfer' -and
            $verifiedAuthority.state.gitCommit -ceq $executionCommit -and
            $verifiedAuthority.state.ReleaseRoot -ceq $expectedReleaseRoot },
        { $frozenContext.RunId -ceq $model.runId -and
            $frozenContext.RepositoryRoot -ceq $repositoryRoot },
        { $frozenContext.EvidenceRoot -ceq $expectedEvidenceRoot -and
            $frozenContext.InstallDirectory -ceq
                $expectedInstallDirectory },
        { $frozenContext.ProgramDataRoot -ceq $expectedProgramDataRoot -and
            $frozenContext.ExternalAnchorRoot -ceq $expectedAnchorRoot },
        { [string]::Equals(
            [IO.Path]::GetFullPath([string]$frozenContext.ReleaseRoot),
            [IO.Path]::GetFullPath($expectedReleaseRoot),
            [StringComparison]::OrdinalIgnoreCase) },
        { $frozenContext.ReleaseRoot -notmatch
            [regex]::Escape($toolchainCommit) },
        { $publicOldReleaseCode -eq 2 },
        { $resolverCommands.Count -eq 1 },
        { $contextCommands.Count -eq 1 },
        { $reconcileCommands.Count -eq 1 },
        { $importCommands.Count -eq 2 },
        { @($wrapperCommands | Where-Object {
                $_.GetCommandName() -ceq 'Get-FslContext'
            }).Count -eq 0 },
        { $forbiddenWrapperCommands.Count -eq 0 },
        { $null -ne $wrapperAst.ParamBlock -and
            $wrapperAst.ParamBlock.Parameters.Count -eq 0 },
        { $wrapperText -notmatch '(?i)\b(?:fallback|retry)\b' },
        { $wrapperText -match [regex]::Escape($fixtureStage4ModulePath) -and
            $wrapperText -match [regex]::Escape(
                $fixtureRecoveryModulePath) },
        { $wrapperText -notmatch
            '(?i)\b(?:ReadAllText|ConvertFrom-Json)\b' },
        { ($exports -join '|') -ceq
            'New-FslStage4RecoveryAuthorityBundle|Test-FslStage4RecoveryAuthorityBundle' },
        { (@($stage4Module.ExportedFunctions.Keys) -join '|') -ceq
            'Invoke-FslStage4Command' },
        { $combinedProductionText -notmatch
                [regex]::Escape($liveProductionCommit) -and
            $combinedProductionText -notmatch
                [regex]::Escape($liveProductionTree) },
        { $tamperCodes.Count -eq 12 -and
            @($tamperCodes | Where-Object { $_ -ne 8 }).Count -eq 0 },
        { $mutationGateCode -eq 3 -and
            $stage4ModuleText -match
                '(?s)function Get-FslFrozenRecoveryContext.+Assert-FslRepositoryGate\s+\$context.+Assert-FslRepositoryMutationGate\s+\$context' -and
            (Get-ChildItem -LiteralPath $sourceRoot -Force).Count -eq 2 -and
            -not (Test-Path -LiteralPath (
                Join-Path $sourceRoot 'launch-attempt.jsonl')) })
    for ($index = 0; $index -lt 30; $index++) {
        Assert-Case ([bool](& $seamChecks[$index])) (
            "Recovery context seam case $index failed; tamper codes: " +
            ($tamperCodes -join ',') + '.') ($index -lt 15) (
            $validation.isValid)
    }

    if ($script:Cases -ne 222 -or $script:Assertions -ne 309) {
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
    if ($null -ne $stage4Module) {
        Remove-Module $stage4Module -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $fixtureRoot -and
        (Test-Path -LiteralPath $fixtureRoot -PathType Container)) {
        Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force -File |
            ForEach-Object { $_.IsReadOnly = $false }
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
}
