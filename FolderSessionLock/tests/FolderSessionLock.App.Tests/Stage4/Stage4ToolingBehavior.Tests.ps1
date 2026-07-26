param(
    [ValidateSet(
        'All',
        'Slice1',
        'Slice2',
        'Slice3',
        'Slice4',
        'Slice5',
        'Slice6',
        'Slice7',
        'Slice8')]
    [string]$Slice = 'All'
)

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

$defaultVersionContext = & $module {
    Get-FslContext -RunId '20260726T042251Z-69c0fac4'
}
$expectedDefaultCommit = (
    & git.exe -C $repository rev-parse HEAD | Out-String).Trim()
$expectedDefaultReleaseRoot =
    Join-Path 'C:\FSL-Release\1.0.0' $expectedDefaultCommit
Assert-True (
    [System.IO.Path]::GetFullPath($defaultVersionContext.ReleaseRoot) -ceq
    [System.IO.Path]::GetFullPath($expectedDefaultReleaseRoot)) (
    'A project without an explicit Version did not use version 1.0.0.')

function Invoke-PreflightToolingCase {
    param(
        [Parameter(Mandatory = $true)][System.Management.Automation.PSModuleInfo]
        $Module,
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Root,
        [AllowNull()][psobject]$SecureBoot,
        [AllowNull()][psobject]$NativeTpm,
        [AllowNull()]$IsElevated,
        [bool]$ThrowSecureBoot = $false,
        [bool]$ThrowNativeTpm = $false,
        [bool]$ThrowToken = $false
    )

    return & $Module {
        param(
            $Root,
            $Repository,
            $SecureBoot,
            $NativeTpm,
            $IsElevated,
            $ThrowSecureBoot,
            $ThrowNativeTpm,
            $ThrowToken)

        $branch = (& git.exe -C $Repository branch --show-current |
            Out-String).Trim()
        $commit = (& git.exe -C $Repository rev-parse HEAD |
            Out-String).Trim()
        $script:FslCaseContext = [pscustomobject]@{
            RunId = '20260725T181000Z-' +
                [Guid]::NewGuid().ToString('N').Substring(0, 8)
            RepositoryRoot = $Repository
            EvidenceRoot = (Join-Path $Root 'evidence')
            ReleaseRoot = (Join-Path $Root 'release')
            InstallDirectory = (Join-Path $Root 'install')
            ProgramDataRoot = (Join-Path $Root 'program-data')
            BrokerPath = (Join-Path $Root 'install\FolderSessionLock.Broker.exe')
            PrestatePath = (Join-Path $Root 'evidence\prestate.json')
            StatePath = (Join-Path $Root 'evidence\stage4-state.json')
            JournalPath = (Join-Path $Root 'evidence\stage4-journal.jsonl')
            AnchorPath = (Join-Path $Root 'evidence\stage4-anchor.json')
            InstallWalPath = (Join-Path $Root 'evidence\install-wal.jsonl')
            CommandsPath = (Join-Path $Root 'evidence\commands.txt')
            ExternalAnchorRoot = (Join-Path $Root 'external-anchor')
            ExternalAnchorKeyPath = (
                Join-Path $Root 'external-anchor\key.dpapi')
            ExternalAnchorSlot0Path = (
                Join-Path $Root 'external-anchor\anchor-0.json')
            ExternalAnchorSlot1Path = (
                Join-Path $Root 'external-anchor\anchor-1.json')
        }
        $script:FslCaseBranch = $branch
        $script:FslCaseCommit = $commit
        $script:FslCaseSecureBoot = $SecureBoot
        $script:FslCaseNativeTpm = $NativeTpm
        $script:FslCaseIsElevated = $IsElevated
        $script:FslCaseThrowSecureBoot = $ThrowSecureBoot
        $script:FslCaseThrowNativeTpm = $ThrowNativeTpm
        $script:FslCaseThrowToken = $ThrowToken
        function Get-FslContext {
            return $script:FslCaseContext
        }
        function Get-FslGitValue {
            param($Context, [string[]]$Arguments)
            $joined = $Arguments -join ' '
            if ($joined -ceq
                'status --porcelain=v1 --untracked-files=all') {
                return ''
            }
            if ($joined -ceq 'branch --show-current') {
                return $script:FslCaseBranch
            }
            if ($joined -ceq 'rev-parse HEAD') {
                return $script:FslCaseCommit
            }
            if ($joined -ceq 'rev-parse --git-dir') {
                return (Join-Path $Repository '.git')
            }
            throw "Unexpected test git query: $joined"
        }
        function Get-CimInstance {
            param([string]$ClassName, [string]$Filter)
            if ($ClassName -ceq 'Win32_OperatingSystem') {
                return [pscustomobject]@{
                    Caption = 'Microsoft Windows 11 Pro'
                    Version = '10.0.26100'
                    BuildNumber = '26100'
                }
            }
            if ($ClassName -ceq 'Win32_ComputerSystem') {
                return [pscustomobject]@{ Name = 'FSL-STAGE4-VM' }
            }
            throw "Unexpected CIM class: $ClassName"
        }
        function Test-FslServiceExists { return $false }
        function Get-Process { return @() }
        function Get-FslSecureBootRegistryEvidence {
            if ($script:FslCaseThrowSecureBoot) {
                throw 'secure boot read failed'
            }
            return $script:FslCaseSecureBoot
        }
        function Get-FslNativeTpmDeviceInfo {
            if ($script:FslCaseThrowNativeTpm) {
                throw 'native TPM read failed'
            }
            return $script:FslCaseNativeTpm
        }
        function Test-FslCurrentTokenAdministrator {
            if ($script:FslCaseThrowToken) {
                throw 'token read failed'
            }
            return $script:FslCaseIsElevated
        }

        $exitCode = Invoke-FslStage4Command `
            -Command Preflight `
            -RunId $script:FslCaseContext.RunId
        return [pscustomobject]@{
            ExitCode = $exitCode
            EvidenceExists =
                Test-Path -LiteralPath $script:FslCaseContext.EvidenceRoot
            AnchorExists =
                Test-Path -LiteralPath $script:FslCaseContext.ExternalAnchorRoot
        }
    } $Root $Repository $SecureBoot $NativeTpm $IsElevated `
        $ThrowSecureBoot $ThrowNativeTpm $ThrowToken
}

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

if ($Slice -in @('All', 'Slice2')) {
    $validSecureBoot = [pscustomobject][ordered]@{
        path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
        name = 'UEFISecureBootEnabled'
        kind = 'DWord'
        valueType = 'System.Int32'
        rawValue = 1
    }
    $validNativeTpm = [pscustomobject][ordered]@{
        result = 0
        structVersion = 1
        tpmVersion = 2
        tpmInterfaceType = 1
        tpmImpRevision = 138
    }
    $secureBootCases = @(
        [pscustomobject]@{
            Name = 'Zero'
            Value = [pscustomobject][ordered]@{
                path = $validSecureBoot.path
                name = $validSecureBoot.name
                kind = 'DWord'
                valueType = 'System.Int32'
                rawValue = 0
            }
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'Missing'
            Value = $null
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'WrongKind'
            Value = [pscustomobject][ordered]@{
                path = $validSecureBoot.path
                name = $validSecureBoot.name
                kind = 'String'
                valueType = 'System.String'
                rawValue = '1'
            }
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'WrongType'
            Value = [pscustomobject][ordered]@{
                path = $validSecureBoot.path
                name = $validSecureBoot.name
                kind = 'DWord'
                valueType = 'System.UInt32'
                rawValue = [uint32]1
            }
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'ReadError'
            Value = $validSecureBoot
            Throw = $true
        })
    foreach ($case in $secureBootCases) {
        $caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
            'FolderSessionLock.Stage4.Tooling.SecureBoot.' +
            $case.Name + '.' + [Guid]::NewGuid().ToString('D'))
        [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
        try {
            $result = Invoke-PreflightToolingCase `
                $module $repository $caseRoot $case.Value `
                $validNativeTpm $false $case.Throw
            Assert-True ($result.ExitCode -eq 3) (
                "Secure Boot $($case.Name) did not fail closed.")
            Assert-True (
                -not $result.EvidenceExists -and
                -not $result.AnchorExists) (
                "Secure Boot $($case.Name) wrote deferred evidence.")
        }
        finally {
            if (Test-Path -LiteralPath $caseRoot) {
                [System.IO.Directory]::Delete($caseRoot, $true)
            }
        }
    }
}

if ($Slice -in @('All', 'Slice3')) {
    $validSecureBoot = [pscustomobject][ordered]@{
        path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
        name = 'UEFISecureBootEnabled'
        kind = 'DWord'
        valueType = 'System.Int32'
        rawValue = 1
    }
    $validNativeTpm = [pscustomobject][ordered]@{
        result = 0
        structVersion = 1
        tpmVersion = 2
        tpmInterfaceType = 1
        tpmImpRevision = 138
    }
    $nativeCases = @(
        [pscustomobject]@{
            Name = 'Missing'; Value = $null; Throw = $false
        },
        [pscustomobject]@{
            Name = 'ResultError'
            Value = [pscustomobject][ordered]@{
                result = 2150121473
                structVersion = 1
                tpmVersion = 2
                tpmInterfaceType = 1
                tpmImpRevision = 138
            }
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'StructVersion'
            Value = [pscustomobject][ordered]@{
                result = 0
                structVersion = 2
                tpmVersion = 2
                tpmInterfaceType = 1
                tpmImpRevision = 138
            }
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'TpmVersion'
            Value = [pscustomobject][ordered]@{
                result = 0
                structVersion = 1
                tpmVersion = 1
                tpmInterfaceType = 1
                tpmImpRevision = 138
            }
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'Interface'
            Value = [pscustomobject][ordered]@{
                result = 0
                structVersion = 1
                tpmVersion = 2
                tpmInterfaceType = 0
                tpmImpRevision = 138
            }
            Throw = $false
        },
        [pscustomobject]@{
            Name = 'ReadError'; Value = $validNativeTpm; Throw = $true
        })
    foreach ($case in $nativeCases) {
        $caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
            'FolderSessionLock.Stage4.Tooling.NativeTpm.' +
            $case.Name + '.' + [Guid]::NewGuid().ToString('D'))
        [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
        try {
            $result = Invoke-PreflightToolingCase `
                $module $repository $caseRoot $validSecureBoot `
                $case.Value $false $false $case.Throw
            Assert-True ($result.ExitCode -eq 3) (
                "Native TPM $($case.Name) did not fail closed.")
            Assert-True (
                -not $result.EvidenceExists -and
                -not $result.AnchorExists) (
                "Native TPM $($case.Name) wrote deferred evidence.")
        }
        finally {
            if (Test-Path -LiteralPath $caseRoot) {
                [System.IO.Directory]::Delete($caseRoot, $true)
            }
        }
    }
    $revisionRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'FolderSessionLock.Stage4.Tooling.NativeTpm.ZeroRevision.' +
        [Guid]::NewGuid().ToString('D'))
    [System.IO.Directory]::CreateDirectory($revisionRoot) | Out-Null
    try {
        $zeroRevision = [pscustomobject][ordered]@{
            result = 0
            structVersion = 1
            tpmVersion = 2
            tpmInterfaceType = 1
            tpmImpRevision = 0
        }
        $result = Invoke-PreflightToolingCase `
            $module $repository $revisionRoot $validSecureBoot `
            $zeroRevision $false
        Assert-True (
            $result.ExitCode -eq 0 -and
            $result.EvidenceExists -and
            $result.AnchorExists) (
            'A valid raw TPM implementation revision of zero was rejected: ' +
            ($result | ConvertTo-Json -Compress))
    }
    finally {
        if (Test-Path -LiteralPath $revisionRoot) {
            [System.IO.Directory]::Delete($revisionRoot, $true)
        }
    }
}

if ($Slice -in @('All', 'Slice4')) {
    $validSecureBoot = [pscustomobject][ordered]@{
        path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
        name = 'UEFISecureBootEnabled'
        kind = 'DWord'
        valueType = 'System.Int32'
        rawValue = 1
    }
    $validNativeTpm = [pscustomobject][ordered]@{
        result = 0
        structVersion = 1
        tpmVersion = 2
        tpmInterfaceType = 1
        tpmImpRevision = 138
    }
    $tokenCases = @(
        [pscustomobject]@{
            Name = 'Elevated'; Value = $true; Throw = $false
        },
        [pscustomobject]@{
            Name = 'Missing'; Value = $null; Throw = $false
        },
        [pscustomobject]@{
            Name = 'WrongType'; Value = 'False'; Throw = $false
        },
        [pscustomobject]@{
            Name = 'ReadError'; Value = $false; Throw = $true
        })
    foreach ($case in $tokenCases) {
        $caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
            'FolderSessionLock.Stage4.Tooling.Token.' +
            $case.Name + '.' + [Guid]::NewGuid().ToString('D'))
        [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
        try {
            $result = Invoke-PreflightToolingCase `
                $module $repository $caseRoot $validSecureBoot `
                $validNativeTpm $case.Value $false $false $case.Throw
            Assert-True ($result.ExitCode -eq 3) (
                "Token $($case.Name) did not fail closed.")
            Assert-True (
                -not $result.EvidenceExists -and
                -not $result.AnchorExists) (
                "Token $($case.Name) wrote deferred evidence.")
        }
        finally {
            if (Test-Path -LiteralPath $caseRoot) {
                [System.IO.Directory]::Delete($caseRoot, $true)
            }
        }
    }
}

if ($Slice -in @('All', 'Slice5')) {
    foreach ($legacyCase in @(
        [pscustomobject]@{
            Name = 'AllMissing'
            Mode = 'AllMissing'
            ExpectedMutationCount = 1
            ExpectedExit = 5
        },
        [pscustomobject]@{
            Name = 'Partial'
            Mode = 'Partial'
            ExpectedMutationCount = 0
            ExpectedExit = 8
        },
        [pscustomobject]@{
            Name = 'Invalid'
            Mode = 'Invalid'
            ExpectedMutationCount = 0
            ExpectedExit = 8
        })) {
        $caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
            'FolderSessionLock.Stage4.Tooling.LegacyState.' +
            $legacyCase.Name + '.' + [Guid]::NewGuid().ToString('D'))
        [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
        try {
            $result = & $module {
                param($Root, $Repository, $Mode)

                $branch = (& git.exe -C $Repository branch --show-current |
                    Out-String).Trim()
                $commit = (& git.exe -C $Repository rev-parse HEAD |
                    Out-String).Trim()
                $script:FslLegacyContext = [pscustomobject]@{
                    RunId = '20260725T182000Z-' +
                        [Guid]::NewGuid().ToString('N').Substring(0, 8)
                    RepositoryRoot = $Repository
                    EvidenceRoot = (Join-Path $Root 'evidence')
                    ReleaseRoot = (Join-Path $Root 'release')
                    InstallDirectory = (Join-Path $Root 'install')
                    ProgramDataRoot = (Join-Path $Root 'program-data')
                    BrokerPath = (
                        Join-Path $Root 'install\FolderSessionLock.Broker.exe')
                    PrestatePath = (Join-Path $Root 'evidence\prestate.json')
                    StatePath = (
                        Join-Path $Root 'evidence\stage4-state.json')
                    JournalPath = (
                        Join-Path $Root 'evidence\stage4-journal.jsonl')
                    AnchorPath = (
                        Join-Path $Root 'evidence\stage4-anchor.json')
                    InstallWalPath = (
                        Join-Path $Root 'evidence\install-wal.jsonl')
                    CommandsPath = (
                        Join-Path $Root 'evidence\commands.txt')
                    ExternalAnchorRoot = (Join-Path $Root 'external-anchor')
                    ExternalAnchorKeyPath = (
                        Join-Path $Root 'external-anchor\key.dpapi')
                    ExternalAnchorSlot0Path = (
                        Join-Path $Root 'external-anchor\anchor-0.json')
                    ExternalAnchorSlot1Path = (
                        Join-Path $Root 'external-anchor\anchor-1.json')
                }
                $script:FslLegacyMutationCount = 0
                function Get-FslContext { return $script:FslLegacyContext }
                function Assert-FslMachineGate {}
                function Assert-FslAdministrator {}
                function Assert-FslRepositoryGate {}
                function Assert-FslRepositoryMutationGate {}
                function Confirm-SecureBootUEFI { return $true }
                function Get-Tpm {
                    return [pscustomobject]@{
                        TpmPresent = $true
                        TpmReady = $true
                    }
                }
                function New-SelfSignedCertificate {
                    $script:FslLegacyMutationCount++
                    throw 'TEST_CERTIFICATE_MUTATION_BOUNDARY'
                }
                function Get-ChildItem {
                    return @()
                }

                [System.IO.Directory]::CreateDirectory(
                    $script:FslLegacyContext.EvidenceRoot) | Out-Null
                Write-FslUtf8NoBom `
                    $script:FslLegacyContext.PrestatePath (
                        ([ordered]@{
                            runId = $script:FslLegacyContext.RunId
                            machineName = [Environment]::MachineName
                            branch = $branch
                            gitCommit = $commit
                            secureBootRegistry = [ordered]@{
                                path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
                                name = 'UEFISecureBootEnabled'
                                kind = 'DWord'
                                valueType = 'System.Int32'
                                rawValue = 1
                            }
                            tbsDeviceInfo = [ordered]@{
                                result = 0
                                structVersion = 1
                                tpmVersion = 2
                                tpmInterfaceType = 1
                                tpmImpRevision = 138
                            }
                            isElevated = $false
                        } | ConvertTo-Json) + [Environment]::NewLine)
                Initialize-FslExternalAnchor $script:FslLegacyContext
                $state = [pscustomobject]@{
                    schemaVersion = 1
                    runId = $script:FslLegacyContext.RunId
                    machineName = [Environment]::MachineName
                    branch = $branch
                    gitCommit = $commit
                    secureBootRegistry = [ordered]@{
                        path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
                        name = 'UEFISecureBootEnabled'
                        kind = 'DWord'
                        valueType = 'System.Int32'
                        rawValue = 1
                    }
                    tbsDeviceInfo = [ordered]@{
                        result = 0
                        structVersion = 1
                        tpmVersion = 2
                        tpmInterfaceType = 1
                        tpmImpRevision = 138
                    }
                    isElevated = $false
                    sequence = 0
                    transition = $null
                    CreatedCertificateThumbprint = $null
                    TrustedCertificateThumbprint = $null
                    ReleaseRoot = $null
                    ReleaseDescriptorSha256 = $null
                    InstallStarted = $false
                    Installed = $false
                    ServiceCreated = $false
                    InstallProof = $null
                    Continuation = $null
                }
                if ($Mode -ceq 'Partial') {
                    Add-Member -InputObject $state `
                        -NotePropertyName PlatformReadinessStatus `
                        -NotePropertyValue 'DeferredUntilElevated'
                }
                elseif ($Mode -ceq 'Invalid') {
                    foreach ($property in ([ordered]@{
                        PlatformReadinessStatus = 'Verified'
                        SecureBootVerified = $true
                        TpmNativeVerified = $true
                        TpmCmdletVerified = $false
                        PlatformReadinessVerifiedUtc = 'not-a-time'
                    }).GetEnumerator()) {
                        Add-Member -InputObject $state `
                            -NotePropertyName $property.Key `
                            -NotePropertyValue $property.Value
                    }
                }
                Write-FslState `
                    $script:FslLegacyContext $state 'PreflightCaptured'
                $exitCode = Invoke-FslStage4Command `
                    -Command CreateTestCertificate `
                    -RunId $script:FslLegacyContext.RunId
                return [pscustomobject]@{
                    ExitCode = $exitCode
                    MutationCount = $script:FslLegacyMutationCount
                }
            } $caseRoot $repository $legacyCase.Mode

            Assert-True (
                $result.ExitCode -eq $legacyCase.ExpectedExit -and
                $result.MutationCount -eq
                    $legacyCase.ExpectedMutationCount) (
                "Legacy state $($legacyCase.Name) normalization was invalid.")
        }
        finally {
            if (Test-Path -LiteralPath $caseRoot) {
                [System.IO.Directory]::Delete($caseRoot, $true)
            }
        }
    }
}

if ($Slice -in @('All', 'Slice6')) {
    $caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'FolderSessionLock.Stage4.Tooling.CreateOrdering.' +
        [Guid]::NewGuid().ToString('D'))
    [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    try {
        $result = & $module {
            param($Root, $Repository)

            $branch = (& git.exe -C $Repository branch --show-current |
                Out-String).Trim()
            $commit = (& git.exe -C $Repository rev-parse HEAD |
                Out-String).Trim()
            $script:FslOrderingContext = [pscustomobject]@{
                RunId = '20260725T183000Z-' +
                    [Guid]::NewGuid().ToString('N').Substring(0, 8)
                RepositoryRoot = $Repository
                EvidenceRoot = (Join-Path $Root 'evidence')
                ReleaseRoot = (Join-Path $Root 'release')
                InstallDirectory = (Join-Path $Root 'install')
                ProgramDataRoot = (Join-Path $Root 'program-data')
                BrokerPath = (
                    Join-Path $Root 'install\FolderSessionLock.Broker.exe')
                PrestatePath = (Join-Path $Root 'evidence\prestate.json')
                StatePath = (Join-Path $Root 'evidence\stage4-state.json')
                JournalPath = (
                    Join-Path $Root 'evidence\stage4-journal.jsonl')
                AnchorPath = (Join-Path $Root 'evidence\stage4-anchor.json')
                InstallWalPath = (
                    Join-Path $Root 'evidence\install-wal.jsonl')
                CommandsPath = (Join-Path $Root 'evidence\commands.txt')
                ExternalAnchorRoot = (Join-Path $Root 'external-anchor')
                ExternalAnchorKeyPath = (
                    Join-Path $Root 'external-anchor\key.dpapi')
                ExternalAnchorSlot0Path = (
                    Join-Path $Root 'external-anchor\anchor-0.json')
                ExternalAnchorSlot1Path = (
                    Join-Path $Root 'external-anchor\anchor-1.json')
            }
            $script:FslOrderingEvents = @()
            $script:FslOrderingMutationState = $null
            $script:FslOrderingTransitions = @()
            function Get-FslContext { return $script:FslOrderingContext }
            function Assert-FslRepositoryGate {}
            function Assert-FslRepositoryMutationGate {}
            function Assert-FslMachineGate {
                $script:FslOrderingEvents += 'base'
            }
            function Assert-FslAdministrator {
                $script:FslOrderingEvents += 'admin'
            }
            function Confirm-SecureBootUEFI {
                $script:FslOrderingEvents += 'confirm-secure-boot'
                return $true
            }
            function Get-Tpm {
                $script:FslOrderingEvents += 'get-tpm'
                return [pscustomobject]@{
                    TpmPresent = $true
                    TpmReady = $true
                }
            }
            function New-SelfSignedCertificate {
                $script:FslOrderingEvents += 'certificate-mutation'
                $script:FslOrderingMutationState =
                    Get-Content `
                        -LiteralPath $script:FslOrderingContext.StatePath `
                        -Raw | ConvertFrom-Json
                $script:FslOrderingTransitions = @(
                    [IO.File]::ReadAllLines(
                        $script:FslOrderingContext.JournalPath) |
                        ForEach-Object {
                            ($_ | ConvertFrom-Json).transition
                        })
                throw 'TEST_CERTIFICATE_MUTATION_BOUNDARY'
            }
            function Get-ChildItem { return @() }

            [System.IO.Directory]::CreateDirectory(
                $script:FslOrderingContext.EvidenceRoot) | Out-Null
            Write-FslUtf8NoBom $script:FslOrderingContext.PrestatePath (
                ([ordered]@{
                    runId = $script:FslOrderingContext.RunId
                    machineName = [Environment]::MachineName
                    branch = $branch
                    gitCommit = $commit
                    secureBootRegistry = [ordered]@{
                        path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
                        name = 'UEFISecureBootEnabled'
                        kind = 'DWord'
                        valueType = 'System.Int32'
                        rawValue = 1
                    }
                    tbsDeviceInfo = [ordered]@{
                        result = 0
                        structVersion = 1
                        tpmVersion = 2
                        tpmInterfaceType = 1
                        tpmImpRevision = 138
                    }
                    isElevated = $false
                } | ConvertTo-Json) + [Environment]::NewLine)
            Initialize-FslExternalAnchor $script:FslOrderingContext
            $state = [pscustomobject]@{
                schemaVersion = 1
                runId = $script:FslOrderingContext.RunId
                machineName = [Environment]::MachineName
                branch = $branch
                gitCommit = $commit
                sequence = 0
                transition = $null
                CreatedCertificateThumbprint = $null
                TrustedCertificateThumbprint = $null
                ReleaseRoot = $null
                ReleaseDescriptorSha256 = $null
                InstallStarted = $false
                Installed = $false
                ServiceCreated = $false
                InstallProof = $null
                Continuation = $null
                PlatformReadinessStatus = 'DeferredUntilElevated'
                SecureBootVerified = $false
                TpmNativeVerified = $false
                TpmCmdletVerified = $false
                PlatformReadinessVerifiedUtc = $null
            }
            Write-FslState `
                $script:FslOrderingContext $state 'PreflightCaptured'
            $exitCode = Invoke-FslStage4Command `
                -Command CreateTestCertificate `
                -RunId $script:FslOrderingContext.RunId
            return [pscustomobject]@{
                ExitCode = $exitCode
                Events = @($script:FslOrderingEvents)
                MutationState = $script:FslOrderingMutationState
                Transitions = @($script:FslOrderingTransitions)
            }
        } $caseRoot $repository

        Assert-True ($result.ExitCode -eq 5) (
            'The isolated certificate mutation boundary did not stop Create.')
        Assert-True (
            ($result.Events -join ',') -ceq
                'base,admin,confirm-secure-boot,get-tpm,' +
                'certificate-mutation') (
            'CreateTestCertificate platform verification order was invalid.')
        Assert-True (
            $result.MutationState.transition -ceq 'CertificateCreating' -and
            $result.MutationState.PlatformReadinessStatus -ceq 'Verified' -and
            $result.MutationState.SecureBootVerified -eq $true -and
            $result.MutationState.TpmNativeVerified -eq $true -and
            $result.MutationState.TpmCmdletVerified -eq $true -and
            $result.MutationState.PlatformReadinessVerifiedUtc -is [string]) (
            'Certificate mutation preceded persisted readiness verification.')
        $transitionTail = @($result.Transitions |
            Select-Object -Last 2) -join ','
        Assert-True (
            $transitionTail -ceq
                'PlatformReadinessVerified,CertificateCreating') (
            'Readiness and certificate transitions were not durably ordered.')
    }
    finally {
        if (Test-Path -LiteralPath $caseRoot) {
            [System.IO.Directory]::Delete($caseRoot, $true)
        }
    }
}

if ($Slice -in @('All', 'Slice7')) {
    $failureCases = @(
        'BaseError',
        'AdminError',
        'ConfirmFalse',
        'ConfirmNull',
        'ConfirmWrongType',
        'ConfirmError',
        'TpmMissing',
        'TpmPresentFalse',
        'TpmReadyFalse',
        'TpmPresentWrongType',
        'TpmReadyWrongType',
        'TpmError')
    foreach ($failureCase in $failureCases) {
        $caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
            'FolderSessionLock.Stage4.Tooling.CreateFailure.' +
            $failureCase + '.' + [Guid]::NewGuid().ToString('D'))
        [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
        try {
            $result = & $module {
                param($Root, $Repository, $FailureCase)

                $branch = (& git.exe -C $Repository branch --show-current |
                    Out-String).Trim()
                $commit = (& git.exe -C $Repository rev-parse HEAD |
                    Out-String).Trim()
                $script:FslFailureCase = $FailureCase
                $script:FslFailureMutationCount = 0
                $script:FslFailureContext = [pscustomobject]@{
                    RunId = '20260725T184000Z-' +
                        [Guid]::NewGuid().ToString('N').Substring(0, 8)
                    RepositoryRoot = $Repository
                    EvidenceRoot = (Join-Path $Root 'evidence')
                    ReleaseRoot = (Join-Path $Root 'release')
                    InstallDirectory = (Join-Path $Root 'install')
                    ProgramDataRoot = (Join-Path $Root 'program-data')
                    BrokerPath = (
                        Join-Path $Root 'install\FolderSessionLock.Broker.exe')
                    PrestatePath = (Join-Path $Root 'evidence\prestate.json')
                    StatePath = (
                        Join-Path $Root 'evidence\stage4-state.json')
                    JournalPath = (
                        Join-Path $Root 'evidence\stage4-journal.jsonl')
                    AnchorPath = (
                        Join-Path $Root 'evidence\stage4-anchor.json')
                    InstallWalPath = (
                        Join-Path $Root 'evidence\install-wal.jsonl')
                    CommandsPath = (
                        Join-Path $Root 'evidence\commands.txt')
                    ExternalAnchorRoot = (Join-Path $Root 'external-anchor')
                    ExternalAnchorKeyPath = (
                        Join-Path $Root 'external-anchor\key.dpapi')
                    ExternalAnchorSlot0Path = (
                        Join-Path $Root 'external-anchor\anchor-0.json')
                    ExternalAnchorSlot1Path = (
                        Join-Path $Root 'external-anchor\anchor-1.json')
                }
                function Get-FslContext { return $script:FslFailureContext }
                function Assert-FslRepositoryGate {}
                function Assert-FslRepositoryMutationGate {}
                function Assert-FslMachineGate {
                    if ($script:FslFailureCase -ceq 'BaseError') {
                        throw 'base read error'
                    }
                }
                function Assert-FslAdministrator {
                    if ($script:FslFailureCase -ceq 'AdminError') {
                        throw 'administrator token error'
                    }
                }
                function Confirm-SecureBootUEFI {
                    switch ($script:FslFailureCase) {
                        'ConfirmFalse' { return $false }
                        'ConfirmNull' { return $null }
                        'ConfirmWrongType' { return 1 }
                        'ConfirmError' { throw 'confirm read error' }
                        default { return $true }
                    }
                }
                function Get-Tpm {
                    switch ($script:FslFailureCase) {
                        'TpmMissing' { return $null }
                        'TpmPresentFalse' {
                            return [pscustomobject]@{
                                TpmPresent = $false
                                TpmReady = $true
                            }
                        }
                        'TpmReadyFalse' {
                            return [pscustomobject]@{
                                TpmPresent = $true
                                TpmReady = $false
                            }
                        }
                        'TpmPresentWrongType' {
                            return [pscustomobject]@{
                                TpmPresent = 1
                                TpmReady = $true
                            }
                        }
                        'TpmReadyWrongType' {
                            return [pscustomobject]@{
                                TpmPresent = $true
                                TpmReady = 'True'
                            }
                        }
                        'TpmError' { throw 'TPM cmdlet error' }
                        default {
                            return [pscustomobject]@{
                                TpmPresent = $true
                                TpmReady = $true
                            }
                        }
                    }
                }
                function New-SelfSignedCertificate {
                    $script:FslFailureMutationCount++
                    throw 'certificate mutation must not be reached'
                }

                [System.IO.Directory]::CreateDirectory(
                    $script:FslFailureContext.EvidenceRoot) | Out-Null
                Write-FslUtf8NoBom $script:FslFailureContext.PrestatePath (
                    ([ordered]@{
                        runId = $script:FslFailureContext.RunId
                        machineName = [Environment]::MachineName
                        branch = $branch
                        gitCommit = $commit
                    } | ConvertTo-Json) + [Environment]::NewLine)
                Initialize-FslExternalAnchor $script:FslFailureContext
                $state = [pscustomobject]@{
                    schemaVersion = 1
                    runId = $script:FslFailureContext.RunId
                    machineName = [Environment]::MachineName
                    branch = $branch
                    gitCommit = $commit
                    sequence = 0
                    transition = $null
                    CreatedCertificateThumbprint = $null
                    TrustedCertificateThumbprint = $null
                    ReleaseRoot = $null
                    ReleaseDescriptorSha256 = $null
                    InstallStarted = $false
                    Installed = $false
                    ServiceCreated = $false
                    InstallProof = $null
                    Continuation = $null
                    PlatformReadinessStatus = 'DeferredUntilElevated'
                    SecureBootVerified = $false
                    TpmNativeVerified = $false
                    TpmCmdletVerified = $false
                    PlatformReadinessVerifiedUtc = $null
                }
                Write-FslState `
                    $script:FslFailureContext $state 'PreflightCaptured'
                $exitCode = Invoke-FslStage4Command `
                    -Command CreateTestCertificate `
                    -RunId $script:FslFailureContext.RunId
                $after = Get-Content `
                    -LiteralPath $script:FslFailureContext.StatePath `
                    -Raw | ConvertFrom-Json
                return [pscustomobject]@{
                    ExitCode = $exitCode
                    MutationCount = $script:FslFailureMutationCount
                    Transition = $after.transition
                }
            } $caseRoot $repository $failureCase

            Assert-True (
                $result.ExitCode -eq 3 -and
                $result.MutationCount -eq 0 -and
                $result.Transition -ceq 'PreflightCaptured') (
                "Create failure $failureCase did not fail before mutation.")
        }
        finally {
            if (Test-Path -LiteralPath $caseRoot) {
                [System.IO.Directory]::Delete($caseRoot, $true)
            }
        }
    }
}

if ($Slice -in @('All', 'Slice8')) {
    $caseRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'FolderSessionLock.Stage4.Tooling.DeferredDispatch.' +
        [Guid]::NewGuid().ToString('D'))
    [System.IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    try {
        $result = & $module {
            param($Root, $Repository)

            $branch = (& git.exe -C $Repository branch --show-current |
                Out-String).Trim()
            $commit = (& git.exe -C $Repository rev-parse HEAD |
                Out-String).Trim()
            $script:FslDispatchHandlerEntries = 0
            $script:FslDispatchContext = [pscustomobject]@{
                RunId = '20260725T185000Z-' +
                    [Guid]::NewGuid().ToString('N').Substring(0, 8)
                RepositoryRoot = $Repository
                EvidenceRoot = (Join-Path $Root 'evidence')
                ReleaseRoot = (Join-Path $Root 'release')
                InstallDirectory = (Join-Path $Root 'install')
                ProgramDataRoot = (Join-Path $Root 'program-data')
                BrokerPath = (
                    Join-Path $Root 'install\FolderSessionLock.Broker.exe')
                PrestatePath = (Join-Path $Root 'evidence\prestate.json')
                StatePath = (Join-Path $Root 'evidence\stage4-state.json')
                JournalPath = (
                    Join-Path $Root 'evidence\stage4-journal.jsonl')
                AnchorPath = (Join-Path $Root 'evidence\stage4-anchor.json')
                InstallWalPath = (
                    Join-Path $Root 'evidence\install-wal.jsonl')
                CommandsPath = (Join-Path $Root 'evidence\commands.txt')
                ExternalAnchorRoot = (Join-Path $Root 'external-anchor')
                ExternalAnchorKeyPath = (
                    Join-Path $Root 'external-anchor\key.dpapi')
                ExternalAnchorSlot0Path = (
                    Join-Path $Root 'external-anchor\anchor-0.json')
                ExternalAnchorSlot1Path = (
                    Join-Path $Root 'external-anchor\anchor-1.json')
            }
            function Get-FslContext { return $script:FslDispatchContext }
            function Assert-FslRepositoryGate {}
            function Assert-FslRepositoryMutationGate {}
            function Assert-FslMachineGate {
                $script:FslDispatchHandlerEntries++
                Stop-FslStage4 3 'HANDLER_BOUNDARY_REACHED'
            }

            [System.IO.Directory]::CreateDirectory(
                $script:FslDispatchContext.EvidenceRoot) | Out-Null
            Write-FslUtf8NoBom $script:FslDispatchContext.PrestatePath (
                ([ordered]@{
                    runId = $script:FslDispatchContext.RunId
                    machineName = [Environment]::MachineName
                    branch = $branch
                    gitCommit = $commit
                } | ConvertTo-Json) + [Environment]::NewLine)
            Initialize-FslExternalAnchor $script:FslDispatchContext
            $state = [pscustomobject]@{
                schemaVersion = 1
                runId = $script:FslDispatchContext.RunId
                machineName = [Environment]::MachineName
                branch = $branch
                gitCommit = $commit
                sequence = 0
                transition = $null
                CreatedCertificateThumbprint = $null
                TrustedCertificateThumbprint = $null
                ReleaseRoot = $null
                ReleaseDescriptorSha256 = $null
                InstallStarted = $false
                Installed = $false
                ServiceCreated = $false
                InstallProof = $null
                Continuation = $null
                PlatformReadinessStatus = 'DeferredUntilElevated'
                SecureBootVerified = $false
                TpmNativeVerified = $false
                TpmCmdletVerified = $false
                PlatformReadinessVerifiedUtc = $null
            }
            Write-FslState `
                $script:FslDispatchContext $state 'PreflightCaptured'
            $pin = '00112233445566778899AABBCCDDEEFF00112233'
            $commands = @(
                @{ Command = 'Publish'; PublisherThumbprint = $pin },
                @{ Command = 'VerifySignature'; PublisherThumbprint = $pin },
                @{ Command = 'Install'; PublisherThumbprint = $pin },
                @{ Command = 'Verify'; PublisherThumbprint = $pin },
                @{
                    Command = 'PrepareLogout'
                    ScenarioId = 'deferred'
                    TestTarget = (Join-Path $Root 'target')
                },
                @{
                    Command = 'PrepareRestart'
                    ScenarioId = 'deferred'
                    TestTarget = (Join-Path $Root 'target')
                },
                @{ Command = 'Resume' },
                @{ Command = 'Uninstall' },
                @{ Command = 'Cleanup' },
                @{
                    Command = 'FinalizeEvidence'
                    ReviewerVerdictPath = (Join-Path $Root 'reviewer.txt')
                })
            $exitCodes = @()
            foreach ($arguments in $commands) {
                $parameters = @{
                    RunId = $script:FslDispatchContext.RunId
                }
                foreach ($key in $arguments.Keys) {
                    $parameters[$key] = $arguments[$key]
                }
                $exitCodes += Invoke-FslStage4Command @parameters
            }
            $after = Get-Content `
                -LiteralPath $script:FslDispatchContext.StatePath `
                -Raw | ConvertFrom-Json
            return [pscustomobject]@{
                ExitCodes = @($exitCodes)
                HandlerEntries = $script:FslDispatchHandlerEntries
                Sequence = [int]$after.sequence
                InstallWalExists =
                    Test-Path -LiteralPath (
                        $script:FslDispatchContext.InstallWalPath)
                ReleaseExists =
                    Test-Path -LiteralPath (
                        $script:FslDispatchContext.ReleaseRoot)
            }
        } $caseRoot $repository

        Assert-True (
            @($result.ExitCodes | Where-Object { $_ -ne 8 }).Count -eq 0) (
            'A deferred command returned a non-readiness failure.')
        Assert-True (
            $result.HandlerEntries -eq 0 -and
            $result.Sequence -eq 1 -and
            -not $result.InstallWalExists -and
            -not $result.ReleaseExists) (
            'A deferred command reached a handler or mutation boundary.')
    }
    finally {
        if (Test-Path -LiteralPath $caseRoot) {
            [System.IO.Directory]::Delete($caseRoot, $true)
        }
    }
}

if ($Slice -in @('All', 'Slice1')) {
    $sliceRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'FolderSessionLock.Stage4.Tooling.PreflightDeferred.' +
        [Guid]::NewGuid().ToString('D'))
    [System.IO.Directory]::CreateDirectory($sliceRoot) | Out-Null
    try {
        $result = & $module {
            param($Root, $Repository)

            $branch = (& git.exe -C $Repository branch --show-current |
                Out-String).Trim()
            $commit = (& git.exe -C $Repository rev-parse HEAD |
                Out-String).Trim()
            $script:FslToolingTestContext = [pscustomobject]@{
                RunId = '20260725T180000Z-0123abcd'
                RepositoryRoot = $Repository
                EvidenceRoot = (Join-Path $Root 'evidence')
                ReleaseRoot = (Join-Path $Root 'release')
                InstallDirectory = (Join-Path $Root 'install')
                ProgramDataRoot = (Join-Path $Root 'program-data')
                BrokerPath = (Join-Path $Root 'install\FolderSessionLock.Broker.exe')
                PrestatePath = (Join-Path $Root 'evidence\prestate.json')
                StatePath = (Join-Path $Root 'evidence\stage4-state.json')
                JournalPath = (Join-Path $Root 'evidence\stage4-journal.jsonl')
                AnchorPath = (Join-Path $Root 'evidence\stage4-anchor.json')
                InstallWalPath = (Join-Path $Root 'evidence\install-wal.jsonl')
                CommandsPath = (Join-Path $Root 'evidence\commands.txt')
                ExternalAnchorRoot = (Join-Path $Root 'external-anchor')
                ExternalAnchorKeyPath = (
                    Join-Path $Root 'external-anchor\key.dpapi')
                ExternalAnchorSlot0Path = (
                    Join-Path $Root 'external-anchor\anchor-0.json')
                ExternalAnchorSlot1Path = (
                    Join-Path $Root 'external-anchor\anchor-1.json')
            }
            $script:FslToolingTestBranch = $branch
            $script:FslToolingTestCommit = $commit
            function Get-FslContext {
                return $script:FslToolingTestContext
            }
            function Get-FslGitValue {
                param($Context, [string[]]$Arguments)
                $joined = $Arguments -join ' '
                if ($joined -ceq 'status --porcelain=v1 --untracked-files=all') {
                    return ''
                }
                if ($joined -ceq 'branch --show-current') {
                    return $script:FslToolingTestBranch
                }
                if ($joined -ceq 'rev-parse HEAD') {
                    return $script:FslToolingTestCommit
                }
                if ($joined -ceq 'rev-parse --git-dir') {
                    return (Join-Path $Repository '.git')
                }
                throw "Unexpected test git query: $joined"
            }
            function Get-CimInstance {
                param([string]$ClassName, [string]$Filter)
                if ($ClassName -ceq 'Win32_OperatingSystem') {
                    return [pscustomobject]@{
                        Caption = 'Microsoft Windows 11 Pro'
                        Version = '10.0.26100'
                        BuildNumber = '26100'
                    }
                }
                if ($ClassName -ceq 'Win32_ComputerSystem') {
                    return [pscustomobject]@{ Name = 'FSL-STAGE4-VM' }
                }
                throw "Unexpected CIM class: $ClassName"
            }
            function Confirm-SecureBootUEFI { return $true }
            function Get-Tpm {
                return [pscustomobject]@{
                    TpmPresent = $true
                    TpmReady = $true
                }
            }
            function Test-FslServiceExists { return $false }
            function Get-Process { return @() }
            function Get-FslSecureBootRegistryEvidence {
                return [pscustomobject][ordered]@{
                    path =
                        'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
                    name = 'UEFISecureBootEnabled'
                    kind = 'DWord'
                    valueType = 'System.Int32'
                    rawValue = 1
                }
            }
            function Get-FslNativeTpmDeviceInfo {
                return [pscustomobject][ordered]@{
                    result = 0
                    structVersion = 1
                    tpmVersion = 2
                    tpmInterfaceType = 1
                    tpmImpRevision = 138
                }
            }
            function Test-FslCurrentTokenAdministrator { return $false }

            $exitCode = Invoke-FslStage4Command `
                -Command Preflight `
                -RunId $script:FslToolingTestContext.RunId
            $prestate = Get-Content `
                -LiteralPath $script:FslToolingTestContext.PrestatePath `
                -Raw | ConvertFrom-Json
            $state = Get-Content `
                -LiteralPath $script:FslToolingTestContext.StatePath `
                -Raw | ConvertFrom-Json
            return [pscustomobject]@{
                ExitCode = $exitCode
                Prestate = $prestate
                State = $state
            }
        } $sliceRoot $repository

        Assert-True ($result.ExitCode -eq 0) (
            'Non-elevated Preflight did not succeed.')
        Assert-True (
            $result.State.PlatformReadinessStatus -ceq
                'DeferredUntilElevated' -and
            $result.State.SecureBootVerified -is [bool] -and
            -not $result.State.SecureBootVerified -and
            $result.State.TpmNativeVerified -is [bool] -and
            -not $result.State.TpmNativeVerified -and
            $result.State.TpmCmdletVerified -is [bool] -and
            -not $result.State.TpmCmdletVerified -and
            $null -eq $result.State.PlatformReadinessVerifiedUtc) (
            'Preflight did not persist the exact deferred readiness state.')
        Assert-True (
            $result.Prestate.secureBootRegistry.kind -ceq 'DWord' -and
            $result.Prestate.secureBootRegistry.valueType -ceq
                'System.Int32' -and
            $result.Prestate.secureBootRegistry.rawValue -eq 1 -and
            $result.Prestate.tbsDeviceInfo.result -eq 0 -and
            $result.Prestate.tbsDeviceInfo.structVersion -eq 1 -and
            $result.Prestate.tbsDeviceInfo.tpmVersion -eq 2 -and
            $result.Prestate.isElevated -is [bool] -and
            -not $result.Prestate.isElevated) (
            'Preflight did not record exact raw platform evidence.')
    }
    finally {
        if (Test-Path -LiteralPath $sliceRoot) {
            [System.IO.Directory]::Delete($sliceRoot, $true)
        }
    }
}

Write-Output 'STAGE4_TOOLING_BEHAVIOR_PASS'
