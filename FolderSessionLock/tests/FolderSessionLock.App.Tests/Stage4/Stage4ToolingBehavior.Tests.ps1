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

    $publisherModes = & $module {
        $unsignedNull = Resolve-FslPublisherMode $null 2
        $unsignedEmpty = Resolve-FslPublisherMode '' 2
        $pinned = Resolve-FslPublisherMode `
            '00112233445566778899aabbccddeeff00112233' 2
        $invalid = @()
        foreach ($value in @(' ', "`t", '001122', (
            '00112233445566778899AABBCCDDEEFF0011223Z'))) {
            try {
                [void](Resolve-FslPublisherMode $value 2)
                $invalid += -1
            }
            catch {
                $invalid += [int]$_.Exception.Data['FslStage4ExitCode']
            }
        }
        return [pscustomobject]@{
            UnsignedNull = $unsignedNull
            UnsignedEmpty = $unsignedEmpty
            Pinned = $pinned
            Invalid = $invalid
        }
    }
    Assert-True (
        $publisherModes.UnsignedNull.Mode -ceq 'UnsignedLocal' -and
        $null -eq $publisherModes.UnsignedNull.Pin -and
        $publisherModes.UnsignedEmpty.Mode -ceq 'UnsignedLocal' -and
        $null -eq $publisherModes.UnsignedEmpty.Pin -and
        $publisherModes.Pinned.Mode -ceq 'PinnedPublisher' -and
        $publisherModes.Pinned.Pin -ceq
            '00112233445566778899AABBCCDDEEFF00112233' -and
        @($publisherModes.Invalid | Where-Object { $_ -ne 2 }).Count -eq 0) (
        'Publisher mode did not distinguish null/empty, valid pin, and ' +
        'whitespace or malformed nonempty values.')

    $schemaRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        'FolderSessionLock.Stage4.Tooling.SchemaV2.' +
        [Guid]::NewGuid().ToString('D'))
    [System.IO.Directory]::CreateDirectory($schemaRoot) | Out-Null
    try {
        $schemaContract = & $module {
            param($Root)

            $scenario = [pscustomobject][ordered]@{
                scenarioId = 'same-account-consent'
                description = 'Same-account UAC consent'
                expectedResult = 'PASS'
                actualResult = 'PASS'
                result = 'PASS'
                evidenceFiles = @('commands.txt')
            }
            $top = [pscustomobject][ordered]@{
                schemaVersion = [int]2
                runId = '20260727T000000Z-0123abcd'
                sameAccountConsentPassed = $true
                preLoginRecoveryPassed = $true
                aclRestored = $true
                temporaryDirectoriesRemoved = $true
                recoveryRecordsRemoved = $true
                remainingRisks = @('Unsigned local release')
                scenarios = @($scenario)
            }
            Assert-FslExactJsonProperties $top @(
                'schemaVersion',
                'runId',
                'sameAccountConsentPassed',
                'preLoginRecoveryPassed',
                'aclRestored',
                'temporaryDirectoriesRemoved',
                'recoveryRecordsRemoved',
                'remainingRisks',
                'scenarios') 'Scenario evidence'
            Assert-FslExactJsonProperties $scenario @(
                'scenarioId',
                'description',
                'expectedResult',
                'actualResult',
                'result',
                'evidenceFiles') 'A scenario result'

            $evidencePath = Join-Path $Root 'signature-verification.txt'
            $builder = [System.Text.StringBuilder]::new()
            foreach ($name in $script:FirstPartyPortableExecutables) {
                [void]$builder.AppendLine("File=$name")
                [void]$builder.AppendLine('Status=NotSigned')
                [void]$builder.AppendLine('SignerThumbprint=null')
                [void]$builder.AppendLine(('SHA256=' + ('A' * 64)))
            }
            Write-FslUtf8NoBom $evidencePath $builder.ToString()
            Assert-FslUnsignedAuthenticodeEvidence $evidencePath

            Add-Member -InputObject $top -NotePropertyName extra -NotePropertyValue $true
            $extraRejected = $false
            try {
                Assert-FslExactJsonProperties $top @(
                    'schemaVersion',
                    'runId',
                    'sameAccountConsentPassed',
                    'preLoginRecoveryPassed',
                    'aclRestored',
                    'temporaryDirectoriesRemoved',
                    'recoveryRecordsRemoved',
                    'remainingRisks',
                    'scenarios') 'Scenario evidence'
            }
            catch {
                $extraRejected =
                    $_.Exception.Data['FslStage4ExitCode'] -eq 8
            }

            [System.IO.File]::WriteAllText(
                $evidencePath,
                [System.IO.File]::ReadAllText($evidencePath).
                    Replace('SignerThumbprint=null', 'SignerThumbprint=BAD'),
                [System.Text.UTF8Encoding]::new($false))
            $signedAliasRejected = $false
            try {
                Assert-FslUnsignedAuthenticodeEvidence $evidencePath
            }
            catch {
                $signedAliasRejected =
                    $_.Exception.Data['FslStage4ExitCode'] -eq 8
            }
            return [pscustomobject]@{
                ExtraRejected = $extraRejected
                SignedAliasRejected = $signedAliasRejected
            }
        } $schemaRoot
        Assert-True (
            $schemaContract.ExtraRejected -and
            $schemaContract.SignedAliasRejected) (
            'D-026 schema v2 or unsigned evidence accepted an extra field ' +
            'or non-null signer.')
    }
    finally {
        if (Test-Path -LiteralPath $schemaRoot) {
            [System.IO.Directory]::Delete($schemaRoot, $true)
        }
    }
}

if ($Slice -in @('All', 'Slice5')) {
    foreach ($legacyCase in @(
        [pscustomobject]@{
            Name = 'AllMissing'
            Mode = 'AllMissing'
            ExpectedMutationCount = 0
            ExpectedExit = 0
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
                        PlatformReadinessStatus = 'VerifiedElevated'
                        SecureBootVerified = $true
                        TpmPresentVerified = $true
                        TpmReadyVerified = $false
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
                    -Command VerifyPlatformReadiness `
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
                TpmPresentVerified = $false
                TpmReadyVerified = $false
                PlatformReadinessVerifiedUtc = $null
            }
            Write-FslState `
                $script:FslOrderingContext $state 'PreflightCaptured'
            $exitCode = Invoke-FslStage4Command `
                -Command VerifyPlatformReadiness `
                -RunId $script:FslOrderingContext.RunId
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
            return [pscustomobject]@{
                ExitCode = $exitCode
                Events = @($script:FslOrderingEvents)
                MutationState = $script:FslOrderingMutationState
                Transitions = @($script:FslOrderingTransitions)
            }
        } $caseRoot $repository

        Assert-True ($result.ExitCode -eq 0) (
            'Elevated platform readiness verification did not succeed.')
        Assert-True (
            ($result.Events -join ',') -ceq
                'base,admin,confirm-secure-boot,get-tpm') (
            'VerifyPlatformReadiness platform verification order was invalid.')
        Assert-True (
            $result.MutationState.transition -ceq 'PlatformReadinessVerified' -and
            $result.MutationState.PlatformReadinessStatus -ceq
                'VerifiedElevated' -and
            $result.MutationState.SecureBootVerified -eq $true -and
            $result.MutationState.TpmPresentVerified -eq $true -and
            $result.MutationState.TpmReadyVerified -eq $true -and
            $result.MutationState.PlatformReadinessVerifiedUtc -is [string]) (
            'Platform readiness verification was not persisted.')
        $transitionTail = @($result.Transitions |
            Select-Object -Last 1) -join ','
        Assert-True (
            $transitionTail -ceq 'PlatformReadinessVerified') (
            'Platform readiness was not durably ordered before handoff.')
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
                    TpmPresentVerified = $false
                    TpmReadyVerified = $false
                    PlatformReadinessVerifiedUtc = $null
                }
                Write-FslState `
                    $script:FslFailureContext $state 'PreflightCaptured'
                $exitCode = Invoke-FslStage4Command `
                    -Command VerifyPlatformReadiness `
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
                "Readiness failure $failureCase did not fail before mutation.")
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

            $script:FslDispatchBranch =
                (& git.exe -C $Repository branch --show-current |
                Out-String).Trim()
            $script:FslDispatchCommit =
                (& git.exe -C $Repository rev-parse HEAD |
                Out-String).Trim()
            $script:FslDispatchHandlerEntries = 0
            $script:FslDispatchInvocations = 0
            $script:FslDispatchUseMutationReader = $false
            function Get-FslContext { return $script:FslDispatchContext }
            function Get-FslGitValue {
                param($Context, [string[]]$Arguments)

                $joined = $Arguments -join ' '
                if ($joined -ceq 'branch --show-current') {
                    return $script:FslDispatchBranch
                }
                if ($joined -ceq 'rev-parse HEAD') {
                    return $script:FslDispatchCommit
                }
                if ($joined -ceq 'status --porcelain=v1 --untracked-files=all') {
                    return ''
                }
                if ($joined -ceq 'rev-parse --git-dir') {
                    return (Join-Path $Repository '.git')
                }
                throw "Unexpected dispatch fixture git query: $joined"
            }
            function Assert-FslRepositoryGate {}
            function Assert-FslRepositoryMutationGate {}
            function Invoke-FslDispatchHandlerSentinel {
                if ($script:FslDispatchUseMutationReader) {
                    [void](Read-FslState $script:FslDispatchContext)
                }
                $script:FslDispatchHandlerEntries++
                Stop-FslStage4 3 'HANDLER_SENTINEL'
            }
            function Invoke-FslPublish {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslVerifySignature {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslInstall {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslVerify {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslPrepareContinuation {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslResume {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslUninstall {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslCleanup {
                Invoke-FslDispatchHandlerSentinel
            }
            function Invoke-FslFinalizeEvidence {
                Invoke-FslDispatchHandlerSentinel
            }
            function Get-FslDispatchSnapshot {
                param([string]$Path)

                $fullRoot = [System.IO.Path]::GetFullPath($Path).
                    TrimEnd('\')
                $rootInfo = [System.IO.DirectoryInfo]::new($fullRoot)
                if (-not $rootInfo.Exists -or
                    ($rootInfo.Attributes -band
                        [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Dispatch fixture root is missing or reparse-backed.'
                }
                $pending =
                    [Collections.Generic.Queue[System.IO.DirectoryInfo]]::new()
                $pending.Enqueue($rootInfo)
                $rows = [Collections.Generic.List[string]]::new()
                $leaves = [Collections.Generic.List[string]]::new()
                $separator = [char]0
                $rows.Add(('D{0}.{0}{1}' -f
                    $separator,
                    [int64]$rootInfo.Attributes))
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    while ($pending.Count -gt 0) {
                        $directory = $pending.Dequeue()
                        foreach ($item in
                            $directory.EnumerateFileSystemInfos()) {
                            if (($item.Attributes -band
                                    [System.IO.FileAttributes]::
                                    ReparsePoint) -ne 0) {
                                throw (
                                    'Dispatch fixture contains a reparse ' +
                                    'point: ' + $item.FullName)
                            }
                            $full = [System.IO.Path]::GetFullPath(
                                $item.FullName)
                            $relative =
                                $full.Substring($fullRoot.Length).TrimStart('\')
                            if ($item -is [System.IO.DirectoryInfo]) {
                                $rows.Add(('D{0}{1}{0}{2}' -f
                                    $separator,
                                    $relative,
                                    [int64]$item.Attributes))
                                $pending.Enqueue($item)
                            }
                            elseif ($item -is [System.IO.FileInfo]) {
                                $bytes =
                                    [System.IO.File]::ReadAllBytes($full)
                                $fileHash = [BitConverter]::ToString(
                                    $sha.ComputeHash($bytes)).Replace('-', '')
                                $rows.Add(
                                    ('F{0}{1}{0}{2}{0}{3}{0}{4}' -f
                                        $separator,
                                        $relative,
                                        [int64]$item.Attributes,
                                        [int64]$bytes.LongLength,
                                        $fileHash))
                                $leaves.Add($relative)
                            }
                            else {
                                throw (
                                    'Dispatch fixture contains an unknown ' +
                                    'entry: ' + $item.FullName)
                            }
                        }
                    }
                }
                finally {
                    $sha.Dispose()
                }
                $canonicalRows = $rows.ToArray()
                [Array]::Sort(
                    $canonicalRows,
                    [StringComparer]::Ordinal)
                $leafSet = $leaves.ToArray()
                [Array]::Sort($leafSet, [StringComparer]::Ordinal)
                return [pscustomobject]@{
                    CanonicalRows = $canonicalRows
                    LeafSet = $leafSet
                }
            }
            function Test-FslDispatchSnapshotEqual {
                param([psobject]$Left, [psobject]$Right)

                return $Left.CanonicalRows.Count -eq
                    $Right.CanonicalRows.Count -and
                    [Linq.Enumerable]::SequenceEqual(
                        [string[]]$Left.CanonicalRows,
                        [string[]]$Right.CanonicalRows)
            }
            function New-FslDispatchByteTemplate {
                param([string]$Path)

                $fullRoot = [System.IO.Path]::GetFullPath($Path).
                    TrimEnd('\')
                $rootInfo = [System.IO.DirectoryInfo]::new($fullRoot)
                if (-not $rootInfo.Exists -or
                    ($rootInfo.Attributes -band
                        [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Dispatch template root is missing or reparse-backed.'
                }
                $pending =
                    [Collections.Generic.Queue[System.IO.DirectoryInfo]]::new()
                $pending.Enqueue($rootInfo)
                $directories = [Collections.Generic.List[object]]::new()
                $files = [Collections.Generic.List[object]]::new()
                $directories.Add([pscustomobject]@{
                    RelativePath = '.'
                    Attributes = [int64]$rootInfo.Attributes
                })
                while ($pending.Count -gt 0) {
                    $directory = $pending.Dequeue()
                    foreach ($item in
                        $directory.EnumerateFileSystemInfos()) {
                        if (($item.Attributes -band
                                [System.IO.FileAttributes]::ReparsePoint) -ne
                            0) {
                            throw (
                                'Dispatch template contains a reparse point: ' +
                                $item.FullName)
                        }
                        $full = [System.IO.Path]::GetFullPath($item.FullName)
                        $relative =
                            $full.Substring($fullRoot.Length).TrimStart('\')
                        if ($item -is [System.IO.DirectoryInfo]) {
                            $directories.Add([pscustomobject]@{
                                RelativePath = $relative
                                Attributes = [int64]$item.Attributes
                            })
                            $pending.Enqueue($item)
                        }
                        elseif ($item -is [System.IO.FileInfo]) {
                            $files.Add([pscustomobject]@{
                                RelativePath = $relative
                                Attributes = [int64]$item.Attributes
                                Bytes = [System.IO.File]::ReadAllBytes($full)
                            })
                        }
                        else {
                            throw (
                                'Dispatch template contains an unknown entry: ' +
                                $item.FullName)
                        }
                    }
                }
                return [pscustomobject]@{
                    Root = $fullRoot
                    Directories = $directories.ToArray()
                    Files = $files.ToArray()
                }
            }
            function Restore-FslDispatchByteTemplate {
                param([psobject]$Template)

                $directoryMap =
                    [Collections.Generic.Dictionary[string, object]]::new(
                        [StringComparer]::Ordinal)
                foreach ($record in $Template.Directories) {
                    $directoryMap.Add($record.RelativePath, $record)
                }
                $fileMap =
                    [Collections.Generic.Dictionary[string, object]]::new(
                        [StringComparer]::Ordinal)
                foreach ($record in $Template.Files) {
                    $fileMap.Add($record.RelativePath, $record)
                }
                $rootInfo =
                    [System.IO.DirectoryInfo]::new([string]$Template.Root)
                if (-not $rootInfo.Exists -or
                    ($rootInfo.Attributes -band
                        [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw 'Dispatch template reset root is unsafe.'
                }
                $pending =
                    [Collections.Generic.Queue[System.IO.DirectoryInfo]]::new()
                $pending.Enqueue($rootInfo)
                while ($pending.Count -gt 0) {
                    $directory = $pending.Dequeue()
                    foreach ($item in
                        $directory.EnumerateFileSystemInfos()) {
                        if (($item.Attributes -band
                                [System.IO.FileAttributes]::ReparsePoint) -ne
                            0) {
                            throw (
                                'Dispatch template reset found a reparse ' +
                                'point: ' + $item.FullName)
                        }
                        $full = [System.IO.Path]::GetFullPath($item.FullName)
                        $relative = $full.Substring(
                            $Template.Root.Length).TrimStart('\')
                        if ($item -is [System.IO.DirectoryInfo]) {
                            if (-not $directoryMap.ContainsKey($relative)) {
                                throw (
                                    'Dispatch template reset found an unknown ' +
                                    'directory: ' + $item.FullName)
                            }
                            $pending.Enqueue($item)
                        }
                        elseif ($item -is [System.IO.FileInfo]) {
                            if (-not $fileMap.ContainsKey($relative)) {
                                throw (
                                    'Dispatch template reset found an unknown ' +
                                    'file: ' + $item.FullName)
                            }
                        }
                        else {
                            throw (
                                'Dispatch template reset found an unknown ' +
                                'entry: ' + $item.FullName)
                        }
                    }
                }
                foreach ($record in $Template.Directories) {
                    $path = if ($record.RelativePath -ceq '.') {
                        $Template.Root
                    }
                    else {
                        Join-Path $Template.Root $record.RelativePath
                    }
                    $info = [System.IO.DirectoryInfo]::new($path)
                    if (-not $info.Exists -or
                        ($info.Attributes -band
                            [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or
                        [int64]$info.Attributes -ne
                            [int64]$record.Attributes) {
                        throw 'Dispatch template directory identity changed.'
                    }
                }
                foreach ($record in $Template.Files) {
                    $path = Join-Path $Template.Root $record.RelativePath
                    if ([System.IO.Directory]::Exists($path)) {
                        throw 'Dispatch template file became a directory.'
                    }
                    [System.IO.File]::WriteAllBytes($path, $record.Bytes)
                    [System.IO.File]::SetAttributes(
                        $path,
                        [System.IO.FileAttributes]$record.Attributes)
                }
            }
            function New-FslDispatchFixture {
                param([string]$FixtureRoot, [string]$Mode)

                [System.IO.Directory]::CreateDirectory($FixtureRoot) |
                    Out-Null
                $context = [pscustomobject]@{
                    RunId = '20260725T185000Z-' +
                        [Guid]::NewGuid().ToString('N').Substring(0, 8)
                    RepositoryRoot = $Repository
                    EvidenceRoot = (Join-Path $FixtureRoot 'evidence')
                    ReleaseRoot = (Join-Path $FixtureRoot 'release')
                    InstallDirectory = (Join-Path $FixtureRoot 'install')
                    ProgramDataRoot = (Join-Path $FixtureRoot 'program-data')
                    BrokerPath = (Join-Path $FixtureRoot 'install\FolderSessionLock.Broker.exe')
                    PrestatePath = (Join-Path $FixtureRoot 'evidence\prestate.json')
                    StatePath = (Join-Path $FixtureRoot 'evidence\stage4-state.json')
                    JournalPath = (Join-Path $FixtureRoot 'evidence\stage4-journal.jsonl')
                    AnchorPath = (Join-Path $FixtureRoot 'evidence\stage4-anchor.json')
                    InstallWalPath = (Join-Path $FixtureRoot 'evidence\install-wal.jsonl')
                    CommandsPath = (Join-Path $FixtureRoot 'evidence\commands.txt')
                    ExternalAnchorRoot = (Join-Path $FixtureRoot 'external-anchor')
                    ExternalAnchorKeyPath = (Join-Path $FixtureRoot 'external-anchor\key.dpapi')
                    ExternalAnchorSlot0Path = (Join-Path $FixtureRoot 'external-anchor\anchor-0.json')
                    ExternalAnchorSlot1Path = (Join-Path $FixtureRoot 'external-anchor\anchor-1.json')
                }
                [System.IO.Directory]::CreateDirectory(
                    $context.EvidenceRoot) | Out-Null
                [System.IO.Directory]::CreateDirectory(
                    $context.ReleaseRoot) | Out-Null
                Write-FslUtf8NoBom $context.PrestatePath (
                    ([ordered]@{
                        runId = $context.RunId
                        machineName = [Environment]::MachineName
                        branch = $script:FslDispatchBranch
                        gitCommit = $script:FslDispatchCommit
                    } | ConvertTo-Json) + [Environment]::NewLine)
                Write-FslUtf8NoBom $context.CommandsPath "fixture`r`n"
                Write-FslUtf8NoBom $context.InstallWalPath "fixture-wal`r`n"
                Write-FslUtf8NoBom (
                    Join-Path $context.ReleaseRoot 'release.bin') 'release'
                Initialize-FslExternalAnchor $context
                $state = [pscustomobject]@{
                    schemaVersion = 1
                    runId = $context.RunId
                    machineName = [Environment]::MachineName
                    branch = $script:FslDispatchBranch
                    gitCommit = $script:FslDispatchCommit
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
                $properties = switch ($Mode) {
                    'Deferred' {
                        [ordered]@{
                            PlatformReadinessStatus =
                                'DeferredUntilElevated'
                            SecureBootVerified = $false
                            TpmPresentVerified = $false
                            TpmReadyVerified = $false
                            PlatformReadinessVerifiedUtc = $null
                        }
                    }
                    'Legacy' { [ordered]@{} }
                    'Partial' {
                        [ordered]@{
                            PlatformReadinessStatus =
                                'DeferredUntilElevated'
                        }
                    }
                    'OldVocabulary' {
                        [ordered]@{
                            PlatformReadinessStatus = 'Verified'
                            SecureBootVerified = $true
                            TpmNativeVerified = $true
                            TpmCmdletVerified = $true
                            PlatformReadinessVerifiedUtc =
                                '2026-07-25T18:50:00.0000000Z'
                        }
                    }
                    'MixedVerified' {
                        [ordered]@{
                            PlatformReadinessStatus = 'VerifiedElevated'
                            SecureBootVerified = $true
                            TpmPresentVerified = $true
                            TpmReadyVerified = $false
                            PlatformReadinessVerifiedUtc =
                                '2026-07-25T18:50:00.0000000Z'
                        }
                    }
                    'InvalidTimestamp' {
                        [ordered]@{
                            PlatformReadinessStatus = 'VerifiedElevated'
                            SecureBootVerified = $true
                            TpmPresentVerified = $true
                            TpmReadyVerified = $true
                            PlatformReadinessVerifiedUtc = 'not-a-time'
                        }
                    }
                    'Verified' {
                        [ordered]@{
                            PlatformReadinessStatus = 'VerifiedElevated'
                            SecureBootVerified = $true
                            TpmPresentVerified = $true
                            TpmReadyVerified = $true
                            PlatformReadinessVerifiedUtc =
                                '2026-07-25T18:50:00.0000000Z'
                        }
                    }
                }
                foreach ($property in $properties.GetEnumerator()) {
                    Add-Member -InputObject $state `
                        -NotePropertyName $property.Key `
                        -NotePropertyValue $property.Value
                }
                Write-FslState $context $state 'PreflightCaptured'
                if ($Mode -ceq 'Deferred') {
                    [System.IO.File]::AppendAllText(
                        $context.JournalPath,
                        '{"incomplete":',
                        [System.Text.UTF8Encoding]::new($false))
                }
                return $context
            }
            function Set-FslDispatchLatestSlotPayload {
                param(
                    [psobject]$Context,
                    [scriptblock]$Mutation
                )

                $slot = [System.IO.File]::ReadAllText(
                    $Context.ExternalAnchorSlot0Path) |
                    ConvertFrom-Json
                $payloadBytes =
                    [Convert]::FromBase64String([string]$slot.payload)
                $payload = [System.Text.UTF8Encoding]::new(
                    $false,
                    $true).GetString($payloadBytes) |
                    ConvertFrom-Json
                & $Mutation $payload
                $payloadBytes = [System.Text.UTF8Encoding]::new($false).
                    GetBytes(
                        ($payload | ConvertTo-Json -Compress -Depth 20))
                $key = Get-FslExternalAnchorKey $Context
                $slot.payload = [Convert]::ToBase64String($payloadBytes)
                $slot.hmacSha256 =
                    [FolderSessionLock.Stage4.Native]::HmacSha256(
                        $key,
                        $payloadBytes)
                Write-FslUtf8NoBom $Context.ExternalAnchorSlot0Path (
                    ($slot | ConvertTo-Json -Compress) +
                    [Environment]::NewLine)
            }
            function Set-FslDispatchSlotHmacInvalid {
                param([string]$Path)

                $slot =
                    [System.IO.File]::ReadAllText($Path) |
                    ConvertFrom-Json
                $replacement = if ($slot.hmacSha256.StartsWith(
                    '0',
                    [StringComparison]::Ordinal)) {
                    '1'
                }
                else {
                    '0'
                }
                $slot.hmacSha256 =
                    $replacement + $slot.hmacSha256.Substring(1)
                Write-FslUtf8NoBom $Path (
                    ($slot | ConvertTo-Json -Compress) +
                    [Environment]::NewLine)
            }
            function Invoke-FslDispatchProbe {
                param(
                    [string]$FixtureRoot,
                    [hashtable]$Arguments = @{ Command = 'Resume' },
                    [AllowNull()][psobject]$Baseline
                )

                if ($null -eq $Baseline) {
                    $Baseline = Get-FslDispatchSnapshot $FixtureRoot
                }
                $script:FslDispatchHandlerEntries = 0
                $parameters = @{
                    RunId = $script:FslDispatchContext.RunId
                }
                foreach ($key in $Arguments.Keys) {
                    $parameters[$key] = $Arguments[$key]
                }
                $writer = [System.IO.StringWriter]::new()
                $originalError = [Console]::Error
                [Console]::SetError($writer)
                try {
                    $script:FslDispatchInvocations++
                    $exitCode = Invoke-FslStage4Command @parameters
                }
                finally {
                    [Console]::SetError($originalError)
                }
                $after = Get-FslDispatchSnapshot $FixtureRoot
                return [pscustomobject]@{
                    ExitCode = $exitCode
                    Message = $writer.ToString().Trim()
                    HandlerEntries = $script:FslDispatchHandlerEntries
                    TreeUnchanged =
                        Test-FslDispatchSnapshotEqual $Baseline $after
                }
            }

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
            $matrix = @()
            $requiredLeaves = @(
                'evidence\commands.txt',
                'evidence\install-wal.jsonl',
                'evidence\prestate.json',
                'evidence\stage4-anchor.json',
                'evidence\stage4-journal.jsonl',
                'evidence\stage4-state.json',
                'external-anchor\anchor-0.json',
                'external-anchor\anchor-1.json',
                'external-anchor\key.dpapi',
                'release\release.bin')
            foreach ($mode in @(
                'Deferred',
                'Legacy',
                'Partial',
                'OldVocabulary',
                'MixedVerified',
                'InvalidTimestamp')) {
                $fixtureRoot = Join-Path $Root $mode
                $script:FslDispatchContext =
                    New-FslDispatchFixture $fixtureRoot $mode
                $baseline = Get-FslDispatchSnapshot $fixtureRoot
                foreach ($arguments in $commands) {
                    $parameters = @{
                        RunId = $script:FslDispatchContext.RunId
                    }
                    foreach ($key in $arguments.Keys) {
                        $parameters[$key] = $arguments[$key]
                    }
                    $writer = [System.IO.StringWriter]::new()
                    $originalError = [Console]::Error
                    [Console]::SetError($writer)
                    try {
                        $script:FslDispatchInvocations++
                        $exitCode = Invoke-FslStage4Command @parameters
                    }
                    finally {
                        [Console]::SetError($originalError)
                    }
                    $after = Get-FslDispatchSnapshot $fixtureRoot
                    $matrix += [pscustomobject]@{
                        Mode = $mode
                        Command = [string]$arguments.Command
                        ExitCode = $exitCode
                        Message = $writer.ToString().Trim()
                        TreeUnchanged =
                            Test-FslDispatchSnapshotEqual $baseline $after
                        RequiredLeavesPresent = @(
                            $requiredLeaves |
                            Where-Object {
                                $baseline.LeafSet -cnotcontains $_
                            }).Count -eq 0
                    }
                }
            }
            $matrixHandlerEntries = $script:FslDispatchHandlerEntries
            $hmacRoot = Join-Path $Root 'HmacTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $hmacRoot 'Verified'
            $slot = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.ExternalAnchorSlot0Path) |
                ConvertFrom-Json
            $replacement = if (
                $slot.hmacSha256.StartsWith(
                    '0',
                    [StringComparison]::Ordinal)) {
                '1'
            }
            else {
                '0'
            }
            $slot.hmacSha256 =
                $replacement + $slot.hmacSha256.Substring(1)
            Write-FslUtf8NoBom `
                $script:FslDispatchContext.ExternalAnchorSlot0Path (
                ($slot | ConvertTo-Json -Compress) +
                [Environment]::NewLine)
            $beforeHmac = Get-FslDispatchSnapshot $hmacRoot
            $script:FslDispatchHandlerEntries = 0
            $hmacWriter = [System.IO.StringWriter]::new()
            $originalError = [Console]::Error
            [Console]::SetError($hmacWriter)
            try {
                $script:FslDispatchInvocations++
                $hmacExit = Invoke-FslStage4Command `
                    -Command Resume `
                    -RunId $script:FslDispatchContext.RunId
            }
            finally {
                [Console]::SetError($originalError)
            }
            $afterHmac = Get-FslDispatchSnapshot $hmacRoot
            $hmacHandlerEntries = $script:FslDispatchHandlerEntries
            $generationRoot = Join-Path $Root 'GenerationTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $generationRoot 'Verified'
            $slot = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.ExternalAnchorSlot0Path) |
                ConvertFrom-Json
            $payloadBytes =
                [Convert]::FromBase64String([string]$slot.payload)
            $payload = [System.Text.UTF8Encoding]::new(
                $false,
                $true).GetString($payloadBytes) |
                ConvertFrom-Json
            $payload.generation = 4
            $payloadBytes = [System.Text.UTF8Encoding]::new($false).
                GetBytes(($payload | ConvertTo-Json -Compress -Depth 20))
            $key = Get-FslExternalAnchorKey $script:FslDispatchContext
            $slot.payload = [Convert]::ToBase64String($payloadBytes)
            $slot.hmacSha256 =
                [FolderSessionLock.Stage4.Native]::HmacSha256(
                    $key,
                    $payloadBytes)
            Write-FslUtf8NoBom `
                $script:FslDispatchContext.ExternalAnchorSlot0Path (
                ($slot | ConvertTo-Json -Compress) +
                [Environment]::NewLine)
            $beforeGeneration =
                Get-FslDispatchSnapshot $generationRoot
            $script:FslDispatchHandlerEntries = 0
            $generationWriter = [System.IO.StringWriter]::new()
            $originalError = [Console]::Error
            [Console]::SetError($generationWriter)
            try {
                $script:FslDispatchInvocations++
                $generationExit = Invoke-FslStage4Command `
                    -Command Resume `
                    -RunId $script:FslDispatchContext.RunId
            }
            finally {
                [Console]::SetError($originalError)
            }
            $afterGeneration =
                Get-FslDispatchSnapshot $generationRoot
            $generationHandlerEntries = $script:FslDispatchHandlerEntries
            $bindingRoot = Join-Path $Root 'BindingTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $bindingRoot 'Verified'
            Set-FslDispatchLatestSlotPayload `
                $script:FslDispatchContext {
                param($Payload)
                $Payload.binding.state.length =
                    [int64]$Payload.binding.state.length + 1L
            }
            $bindingResult = Invoke-FslDispatchProbe $bindingRoot
            $slotBindingRoot = Join-Path $Root 'SlotBindingTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $slotBindingRoot 'Verified'
            $slot = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.ExternalAnchorSlot1Path) |
                ConvertFrom-Json
            $payloadBytes =
                [Convert]::FromBase64String([string]$slot.payload)
            $payload = [System.Text.UTF8Encoding]::new(
                $false,
                $true).GetString($payloadBytes) |
                ConvertFrom-Json
            $payload.binding.runId = 'tampered'
            $payloadBytes = [System.Text.UTF8Encoding]::new($false).
                GetBytes(($payload | ConvertTo-Json -Compress -Depth 20))
            $key = Get-FslExternalAnchorKey $script:FslDispatchContext
            $slot.payload = [Convert]::ToBase64String($payloadBytes)
            $slot.hmacSha256 =
                [FolderSessionLock.Stage4.Native]::HmacSha256(
                    $key,
                    $payloadBytes)
            Write-FslUtf8NoBom `
                $script:FslDispatchContext.ExternalAnchorSlot1Path (
                ($slot | ConvertTo-Json -Compress) +
                [Environment]::NewLine)
            $slotBindingResult =
                Invoke-FslDispatchProbe $slotBindingRoot
            $cacheRoot = Join-Path $Root 'CacheTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $cacheRoot 'Deferred'
            [System.IO.File]::AppendAllText(
                $script:FslDispatchContext.StatePath,
                'tamper',
                [System.Text.UTF8Encoding]::new($false))
            $cacheResult = Invoke-FslDispatchProbe $cacheRoot
            $walRoot = Join-Path $Root 'WalTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $walRoot 'Deferred'
            [System.IO.File]::AppendAllText(
                $script:FslDispatchContext.InstallWalPath,
                'tamper',
                [System.Text.UTF8Encoding]::new($false))
            $walResult = Invoke-FslDispatchProbe $walRoot
            $completeTailRoot = Join-Path $Root 'CompleteTail'
            $script:FslDispatchContext =
                New-FslDispatchFixture $completeTailRoot 'Verified'
            [System.IO.File]::AppendAllText(
                $script:FslDispatchContext.JournalPath,
                "{}`n",
                [System.Text.UTF8Encoding]::new($false))
            $completeTailResult =
                Invoke-FslDispatchProbe $completeTailRoot
            $incompleteTailRoot = Join-Path $Root 'IncompleteTail'
            $script:FslDispatchContext =
                New-FslDispatchFixture $incompleteTailRoot 'Verified'
            $expectedJournal = [System.IO.File]::ReadAllBytes(
                $script:FslDispatchContext.JournalPath)
            $incompleteStateBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.StatePath) `
                -Algorithm SHA256).Hash
            $incompleteWalBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.InstallWalPath) `
                -Algorithm SHA256).Hash
            [System.IO.File]::AppendAllText(
                $script:FslDispatchContext.JournalPath,
                '{"incomplete":',
                [System.Text.UTF8Encoding]::new($false))
            $script:FslDispatchUseMutationReader = $true
            $incompleteTailResult =
                Invoke-FslDispatchProbe $incompleteTailRoot
            $script:FslDispatchUseMutationReader = $false
            $incompleteTailRepaired = [Linq.Enumerable]::SequenceEqual(
                [byte[]]$expectedJournal,
                [byte[]][System.IO.File]::ReadAllBytes(
                    $script:FslDispatchContext.JournalPath))
            $incompleteOtherBytesUnchanged = (
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.StatePath) `
                    -Algorithm SHA256).Hash -ceq $incompleteStateBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.InstallWalPath) `
                    -Algorithm SHA256).Hash -ceq $incompleteWalBefore)
            $keyRoot = Join-Path $Root 'DpapiKeyTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $keyRoot 'Verified'
            $keyBytes = [System.IO.File]::ReadAllBytes(
                $script:FslDispatchContext.ExternalAnchorKeyPath)
            $keyBytes[0] = $keyBytes[0] -bxor 1
            [System.IO.File]::WriteAllBytes(
                $script:FslDispatchContext.ExternalAnchorKeyPath,
                $keyBytes)
            $keyResult = Invoke-FslDispatchProbe $keyRoot
            $journalRoot = Join-Path $Root 'JournalPrefixTamper'
            $script:FslDispatchContext =
                New-FslDispatchFixture $journalRoot 'Verified'
            $journalBytes = [System.IO.File]::ReadAllBytes(
                $script:FslDispatchContext.JournalPath)
            $journalBytes[0] = $journalBytes[0] -bxor 1
            [System.IO.File]::WriteAllBytes(
                $script:FslDispatchContext.JournalPath,
                $journalBytes)
            $journalResult = Invoke-FslDispatchProbe $journalRoot
            $cacheMissingRoot = Join-Path $Root 'VerifiedCacheMissing'
            $script:FslDispatchContext =
                New-FslDispatchFixture $cacheMissingRoot 'Verified'
            $expectedCache = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.StatePath)
            [System.IO.File]::Delete(
                $script:FslDispatchContext.StatePath)
            $journalBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.JournalPath) `
                -Algorithm SHA256).Hash
            $walBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.InstallWalPath) `
                -Algorithm SHA256).Hash
            $script:FslDispatchUseMutationReader = $true
            $cacheMissingResult =
                Invoke-FslDispatchProbe $cacheMissingRoot
            $script:FslDispatchUseMutationReader = $false
            $cacheMissingRestored =
                (Test-Path -LiteralPath (
                    $script:FslDispatchContext.StatePath) -PathType Leaf) -and
                [System.IO.File]::ReadAllText(
                    $script:FslDispatchContext.StatePath) -ceq $expectedCache
            $cacheMissingOtherBytesUnchanged = (
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.JournalPath) `
                    -Algorithm SHA256).Hash -ceq $journalBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.InstallWalPath) `
                    -Algorithm SHA256).Hash -ceq $walBefore)
            $f2sVerifiedRoot = Join-Path $Root 'F2SVerified'
            $f2sVerifiedContext =
                New-FslDispatchFixture $f2sVerifiedRoot 'Verified'
            $f2sVerifiedTemplate =
                New-FslDispatchByteTemplate $f2sVerifiedRoot
            $olderMissingRoot = $f2sVerifiedRoot
            $script:FslDispatchContext = $f2sVerifiedContext
            $expectedCache = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.StatePath)
            [System.IO.File]::Delete(
                $script:FslDispatchContext.ExternalAnchorSlot1Path)
            [System.IO.File]::Delete(
                $script:FslDispatchContext.StatePath)
            $journalBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.JournalPath) `
                -Algorithm SHA256).Hash
            $walBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.InstallWalPath) `
                -Algorithm SHA256).Hash
            $script:FslDispatchUseMutationReader = $true
            $olderMissingResult =
                Invoke-FslDispatchProbe $olderMissingRoot
            $script:FslDispatchUseMutationReader = $false
            $olderMissingRestored =
                (Test-Path -LiteralPath (
                    $script:FslDispatchContext.StatePath) -PathType Leaf) -and
                [System.IO.File]::ReadAllText(
                    $script:FslDispatchContext.StatePath) -ceq $expectedCache
            $olderMissingOtherBytesUnchanged = (
                -not (Test-Path -LiteralPath (
                    $script:FslDispatchContext.ExternalAnchorSlot1Path)) -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.JournalPath) `
                    -Algorithm SHA256).Hash -ceq $journalBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.InstallWalPath) `
                    -Algorithm SHA256).Hash -ceq $walBefore)
            Restore-FslDispatchByteTemplate $f2sVerifiedTemplate
            $olderTornRoot = $f2sVerifiedRoot
            $script:FslDispatchContext = $f2sVerifiedContext
            $expectedCache = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.StatePath)
            Write-FslUtf8NoBom `
                $script:FslDispatchContext.ExternalAnchorSlot1Path '{"torn":'
            Write-FslUtf8NoBom `
                $script:FslDispatchContext.StatePath '{"torn":'
            $olderSlotBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.ExternalAnchorSlot1Path) `
                -Algorithm SHA256).Hash
            $journalBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.JournalPath) `
                -Algorithm SHA256).Hash
            $walBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.InstallWalPath) `
                -Algorithm SHA256).Hash
            $script:FslDispatchUseMutationReader = $true
            $olderTornResult = Invoke-FslDispatchProbe $olderTornRoot
            $script:FslDispatchUseMutationReader = $false
            $olderTornRestored =
                [System.IO.File]::ReadAllText(
                    $script:FslDispatchContext.StatePath) -ceq
                    $expectedCache
            $olderTornOtherBytesUnchanged = (
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.ExternalAnchorSlot1Path) `
                    -Algorithm SHA256).Hash -ceq $olderSlotBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.JournalPath) `
                    -Algorithm SHA256).Hash -ceq $journalBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.InstallWalPath) `
                    -Algorithm SHA256).Hash -ceq $walBefore)
            Restore-FslDispatchByteTemplate $f2sVerifiedTemplate
            $olderHmacRoot = $f2sVerifiedRoot
            $script:FslDispatchContext = $f2sVerifiedContext
            $expectedCache = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.StatePath)
            Set-FslDispatchSlotHmacInvalid `
                $script:FslDispatchContext.ExternalAnchorSlot1Path
            [System.IO.File]::Delete(
                $script:FslDispatchContext.StatePath)
            $olderSlotBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.ExternalAnchorSlot1Path) `
                -Algorithm SHA256).Hash
            $journalBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.JournalPath) `
                -Algorithm SHA256).Hash
            $walBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.InstallWalPath) `
                -Algorithm SHA256).Hash
            $script:FslDispatchUseMutationReader = $true
            $olderHmacResult = Invoke-FslDispatchProbe $olderHmacRoot
            $script:FslDispatchUseMutationReader = $false
            $olderHmacRestored =
                (Test-Path -LiteralPath (
                    $script:FslDispatchContext.StatePath) -PathType Leaf) -and
                [System.IO.File]::ReadAllText(
                    $script:FslDispatchContext.StatePath) -ceq $expectedCache
            $olderHmacOtherBytesUnchanged = (
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.ExternalAnchorSlot1Path) `
                    -Algorithm SHA256).Hash -ceq $olderSlotBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.JournalPath) `
                    -Algorithm SHA256).Hash -ceq $journalBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.InstallWalPath) `
                    -Algorithm SHA256).Hash -ceq $walBefore)
            Restore-FslDispatchByteTemplate $f2sVerifiedTemplate
            $advancedHmacRoot = $f2sVerifiedRoot
            $script:FslDispatchContext = $f2sVerifiedContext
            $advancedState = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.StatePath) |
                ConvertFrom-Json
            Write-FslState `
                $script:FslDispatchContext `
                $advancedState `
                'PreflightCaptured'
            $slot1Outer = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.ExternalAnchorSlot1Path) |
                ConvertFrom-Json
            $slot1Payload = [System.Text.UTF8Encoding]::new(
                $false,
                $true).GetString(
                    [Convert]::FromBase64String(
                        [string]$slot1Outer.payload)) |
                ConvertFrom-Json
            $slot0Outer = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.ExternalAnchorSlot0Path) |
                ConvertFrom-Json
            $slot0Payload = [System.Text.UTF8Encoding]::new(
                $false,
                $true).GetString(
                    [Convert]::FromBase64String(
                        [string]$slot0Outer.payload)) |
                ConvertFrom-Json
            $advancedJournalLength = [int64](Get-Item -LiteralPath (
                $script:FslDispatchContext.JournalPath)).Length
            $advancedSetupCorrect = (
                [int64]$slot1Payload.generation -eq 3L -and
                [int64]$slot0Payload.generation -eq 2L -and
                [int64]$slot1Payload.binding.journal.length -eq
                    $advancedJournalLength -and
                [int64]$slot0Payload.binding.journal.length -lt
                    $advancedJournalLength)
            Set-FslDispatchSlotHmacInvalid `
                $script:FslDispatchContext.ExternalAnchorSlot1Path
            $advancedHmacResult =
                Invoke-FslDispatchProbe $advancedHmacRoot
            $f2sDeferredRoot = Join-Path $Root 'F2SDeferred'
            $f2sDeferredContext =
                New-FslDispatchFixture $f2sDeferredRoot 'Deferred'
            $f2sDeferredTemplate =
                New-FslDispatchByteTemplate $f2sDeferredRoot
            $deferredOlderMissingRoot = $f2sDeferredRoot
            $script:FslDispatchContext = $f2sDeferredContext
            [System.IO.File]::Delete(
                $script:FslDispatchContext.ExternalAnchorSlot1Path)
            $deferredOlderMissingResult =
                Invoke-FslDispatchProbe $deferredOlderMissingRoot
            Restore-FslDispatchByteTemplate $f2sDeferredTemplate
            $deferredOlderTornRoot = $f2sDeferredRoot
            $script:FslDispatchContext = $f2sDeferredContext
            Write-FslUtf8NoBom `
                $script:FslDispatchContext.ExternalAnchorSlot1Path '{"torn":'
            $deferredOlderTornResult =
                Invoke-FslDispatchProbe $deferredOlderTornRoot
            Restore-FslDispatchByteTemplate $f2sDeferredTemplate
            $deferredOlderHmacRoot = $f2sDeferredRoot
            $script:FslDispatchContext = $f2sDeferredContext
            Set-FslDispatchSlotHmacInvalid `
                $script:FslDispatchContext.ExternalAnchorSlot1Path
            $deferredOlderHmacResult =
                Invoke-FslDispatchProbe $deferredOlderHmacRoot
            $cacheTornRoot = Join-Path $Root 'VerifiedCacheTorn'
            $script:FslDispatchContext =
                New-FslDispatchFixture $cacheTornRoot 'Verified'
            $expectedCache = [System.IO.File]::ReadAllText(
                $script:FslDispatchContext.StatePath)
            Write-FslUtf8NoBom $script:FslDispatchContext.StatePath (
                '{"torn":')
            $journalBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.JournalPath) `
                -Algorithm SHA256).Hash
            $walBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.InstallWalPath) `
                -Algorithm SHA256).Hash
            $script:FslDispatchUseMutationReader = $true
            $cacheTornResult =
                Invoke-FslDispatchProbe $cacheTornRoot
            $script:FslDispatchUseMutationReader = $false
            $cacheTornRestored =
                [System.IO.File]::ReadAllText(
                    $script:FslDispatchContext.StatePath) -ceq
                    $expectedCache
            $cacheTornOtherBytesUnchanged = (
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.JournalPath) `
                    -Algorithm SHA256).Hash -ceq $journalBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.InstallWalPath) `
                    -Algorithm SHA256).Hash -ceq $walBefore)
            $walTailRoot = Join-Path $Root 'VerifiedWalTail'
            $script:FslDispatchContext =
                New-FslDispatchFixture $walTailRoot 'Verified'
            $expectedWal = [System.IO.File]::ReadAllBytes(
                $script:FslDispatchContext.InstallWalPath)
            $stateBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.StatePath) `
                -Algorithm SHA256).Hash
            $journalBefore = (Get-FileHash -LiteralPath (
                $script:FslDispatchContext.JournalPath) `
                -Algorithm SHA256).Hash
            [System.IO.File]::AppendAllText(
                $script:FslDispatchContext.InstallWalPath,
                'recoverable-tail',
                [System.Text.UTF8Encoding]::new($false))
            $script:FslDispatchUseMutationReader = $true
            $walTailResult = Invoke-FslDispatchProbe $walTailRoot
            $script:FslDispatchUseMutationReader = $false
            $walTailRepaired = [Linq.Enumerable]::SequenceEqual(
                [byte[]]$expectedWal,
                [byte[]][System.IO.File]::ReadAllBytes(
                    $script:FslDispatchContext.InstallWalPath))
            $walTailOtherBytesUnchanged = (
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.StatePath) `
                    -Algorithm SHA256).Hash -ceq $stateBefore -and
                (Get-FileHash -LiteralPath (
                    $script:FslDispatchContext.JournalPath) `
                    -Algorithm SHA256).Hash -ceq $journalBefore)
            $walTruncatedRoot = Join-Path $Root 'VerifiedWalTruncated'
            $script:FslDispatchContext =
                New-FslDispatchFixture $walTruncatedRoot 'Verified'
            $walBytes = [System.IO.File]::ReadAllBytes(
                $script:FslDispatchContext.InstallWalPath)
            [System.IO.File]::WriteAllBytes(
                $script:FslDispatchContext.InstallWalPath,
                $walBytes[0..($walBytes.Length - 2)])
            $script:FslDispatchUseMutationReader = $true
            $walTruncatedResult =
                Invoke-FslDispatchProbe $walTruncatedRoot
            $script:FslDispatchUseMutationReader = $false
            $walPrefixRoot = Join-Path $Root 'VerifiedWalPrefix'
            $script:FslDispatchContext =
                New-FslDispatchFixture $walPrefixRoot 'Verified'
            $walBytes = [System.IO.File]::ReadAllBytes(
                $script:FslDispatchContext.InstallWalPath)
            $walBytes[0] = $walBytes[0] -bxor 1
            [System.IO.File]::WriteAllBytes(
                $script:FslDispatchContext.InstallWalPath,
                $walBytes)
            $script:FslDispatchUseMutationReader = $true
            $walPrefixResult = Invoke-FslDispatchProbe $walPrefixRoot
            $script:FslDispatchUseMutationReader = $false
            $deferredIntegrity = @()
            $deferredCases = @(
                [pscustomobject]@{
                    Name = 'CacheMissing'
                    FatalWal = $false
                    Mutation = {
                        param($Context)
                        [System.IO.File]::Delete($Context.StatePath)
                    }
                },
                [pscustomobject]@{
                    Name = 'CacheTorn'
                    FatalWal = $false
                    Mutation = {
                        param($Context)
                        Write-FslUtf8NoBom $Context.StatePath '{"torn":'
                    }
                },
                [pscustomobject]@{
                    Name = 'WalTail'
                    FatalWal = $false
                    Mutation = {
                        param($Context)
                        [System.IO.File]::AppendAllText(
                            $Context.InstallWalPath,
                            'recoverable-tail',
                            [System.Text.UTF8Encoding]::new($false))
                    }
                },
                [pscustomobject]@{
                    Name = 'JournalTail'
                    FatalWal = $false
                    Mutation = { param($Context) }
                },
                [pscustomobject]@{
                    Name = 'WalTruncated'
                    FatalWal = $true
                    Mutation = {
                        param($Context)
                        $bytes = [System.IO.File]::ReadAllBytes(
                            $Context.InstallWalPath)
                        [System.IO.File]::WriteAllBytes(
                            $Context.InstallWalPath,
                            $bytes[0..($bytes.Length - 2)])
                    }
                },
                [pscustomobject]@{
                    Name = 'WalPrefix'
                    FatalWal = $true
                    Mutation = {
                        param($Context)
                        $bytes = [System.IO.File]::ReadAllBytes(
                            $Context.InstallWalPath)
                        $bytes[0] = $bytes[0] -bxor 1
                        [System.IO.File]::WriteAllBytes(
                            $Context.InstallWalPath,
                            $bytes)
                    }
                })
            foreach ($case in $deferredCases) {
                $fixtureRoot =
                    Join-Path $Root ('Deferred' + $case.Name)
                $script:FslDispatchContext =
                    New-FslDispatchFixture $fixtureRoot 'Deferred'
                & $case.Mutation $script:FslDispatchContext
                $baseline = Get-FslDispatchSnapshot $fixtureRoot
                foreach ($arguments in $commands) {
                    $probe = Invoke-FslDispatchProbe `
                        $fixtureRoot $arguments $baseline
                    $deferredIntegrity += [pscustomobject]@{
                        Case = $case.Name
                        Command = [string]$arguments.Command
                        FatalWal = $case.FatalWal
                        ExitCode = $probe.ExitCode
                        Message = $probe.Message
                        HandlerEntries = $probe.HandlerEntries
                        TreeUnchanged = $probe.TreeUnchanged
                    }
                }
            }
            return [pscustomobject]@{
                DispatcherInvocations = $script:FslDispatchInvocations
                Matrix = @($matrix)
                HandlerEntries = $matrixHandlerEntries
                Hmac = [pscustomobject]@{
                    ExitCode = $hmacExit
                    Message = $hmacWriter.ToString().Trim()
                    HandlerEntries = $hmacHandlerEntries
                    TreeUnchanged = Test-FslDispatchSnapshotEqual `
                        $beforeHmac $afterHmac
                }
                Generation = [pscustomobject]@{
                    ExitCode = $generationExit
                    Message = $generationWriter.ToString().Trim()
                    HandlerEntries = $generationHandlerEntries
                    TreeUnchanged = Test-FslDispatchSnapshotEqual `
                        $beforeGeneration $afterGeneration
                }
                Binding = $bindingResult
                SlotBinding = $slotBindingResult
                Cache = $cacheResult
                Wal = $walResult
                CompleteTail = $completeTailResult
                IncompleteTail = [pscustomobject]@{
                    Probe = $incompleteTailResult
                    Repaired = $incompleteTailRepaired
                    OtherBytesUnchanged =
                        $incompleteOtherBytesUnchanged
                }
                DpapiKey = $keyResult
                JournalPrefix = $journalResult
                VerifiedCacheMissing = [pscustomobject]@{
                    Probe = $cacheMissingResult
                    Restored = $cacheMissingRestored
                    OtherBytesUnchanged =
                        $cacheMissingOtherBytesUnchanged
                }
                OlderMissingCurrentVerified = [pscustomobject]@{
                    Probe = $olderMissingResult
                    Restored = $olderMissingRestored
                    OtherBytesUnchanged =
                        $olderMissingOtherBytesUnchanged
                }
                OlderTornCurrentVerified = [pscustomobject]@{
                    Probe = $olderTornResult
                    Restored = $olderTornRestored
                    OtherBytesUnchanged =
                        $olderTornOtherBytesUnchanged
                }
                OlderHmacCurrentVerified = [pscustomobject]@{
                    Probe = $olderHmacResult
                    Restored = $olderHmacRestored
                    OtherBytesUnchanged =
                        $olderHmacOtherBytesUnchanged
                }
                AdvancedLatestHmacOlderStale = [pscustomobject]@{
                    SetupCorrect = $advancedSetupCorrect
                    Probe = $advancedHmacResult
                }
                DeferredOlderMissing = $deferredOlderMissingResult
                DeferredOlderTorn = $deferredOlderTornResult
                DeferredOlderHmac = $deferredOlderHmacResult
                VerifiedCacheTorn = [pscustomobject]@{
                    Probe = $cacheTornResult
                    Restored = $cacheTornRestored
                    OtherBytesUnchanged =
                        $cacheTornOtherBytesUnchanged
                }
                VerifiedWalTail = [pscustomobject]@{
                    Probe = $walTailResult
                    Repaired = $walTailRepaired
                    OtherBytesUnchanged =
                        $walTailOtherBytesUnchanged
                }
                VerifiedWalTruncated = $walTruncatedResult
                VerifiedWalPrefix = $walPrefixResult
                DeferredIntegrity = @($deferredIntegrity)
            }
        } $caseRoot $repository

        $badDeferredResults = @($result.Matrix | Where-Object {
                $_.ExitCode -ne 8 -or
                $_.Message -cne
                    'Platform readiness is deferred until elevation.' -or
                -not $_.TreeUnchanged -or
                -not $_.RequiredLeavesPresent
            })
        if ($badDeferredResults.Count -gt 0) {
            Write-Output ($badDeferredResults |
                ConvertTo-Json -Compress -Depth 6)
        }
        Assert-True (
            $result.DispatcherInvocations -eq 142) (
            'The dispatcher coverage count changed from 142 invocations.')
        Assert-True (
            $result.Matrix.Count -eq 60 -and
            $badDeferredResults.Count -eq 0) (
            'A deferred dispatcher fixture changed bytes or returned ' +
            'a non-readiness result.')
        Assert-True (
            $result.HandlerEntries -eq 0) (
            'A deferred command reached a handler boundary.')
        Assert-True (
            $result.Hmac.ExitCode -eq 3 -and
            $result.Hmac.Message -ceq 'HANDLER_SENTINEL' -and
            $result.Hmac.HandlerEntries -eq 1 -and
            $result.Hmac.TreeUnchanged) (
            'A fresh surviving slot did not tolerate latest HMAC damage.')
        Assert-True (
            $result.Generation.ExitCode -eq 8 -and
            $result.Generation.HandlerEntries -eq 0 -and
            $result.Generation.TreeUnchanged) (
            'Generation tampering reached a handler or changed fixture bytes.')
        Assert-True (
            $result.Binding.ExitCode -eq 8 -and
            $result.Binding.HandlerEntries -eq 0 -and
            $result.Binding.TreeUnchanged) (
            'Binding tampering reached a handler or changed fixture bytes.')
        Assert-True (
            $result.SlotBinding.ExitCode -eq 8 -and
            $result.SlotBinding.HandlerEntries -eq 0 -and
            $result.SlotBinding.TreeUnchanged) (
            'An older slot binding tamper reached a handler or changed bytes.')
        Assert-True (
            $result.Cache.ExitCode -eq 8 -and
            $result.Cache.HandlerEntries -eq 0 -and
            $result.Cache.TreeUnchanged) (
            'Cache tampering reached a handler or changed fixture bytes.')
        Assert-True (
            $result.Wal.ExitCode -eq 8 -and
            $result.Wal.HandlerEntries -eq 0 -and
            $result.Wal.TreeUnchanged) (
            'WAL tampering reached a handler or changed fixture bytes.')
        Assert-True (
            $result.CompleteTail.ExitCode -eq 8 -and
            $result.CompleteTail.HandlerEntries -eq 0 -and
            $result.CompleteTail.TreeUnchanged) (
            'A complete unanchored tail was accepted or changed.')
        Assert-True (
            $result.IncompleteTail.Probe.ExitCode -eq 3 -and
            $result.IncompleteTail.Probe.Message -ceq
                'HANDLER_SENTINEL' -and
            $result.IncompleteTail.Probe.HandlerEntries -eq 1 -and
            $result.IncompleteTail.Repaired -and
            $result.IncompleteTail.OtherBytesUnchanged) (
            'A verified incomplete journal tail was not repaired at handoff.')
        Assert-True (
            $result.DpapiKey.ExitCode -eq 8 -and
            $result.DpapiKey.HandlerEntries -eq 0 -and
            $result.DpapiKey.TreeUnchanged) (
            'A DPAPI key tamper reached a handler or changed fixture bytes.')
        Assert-True (
            $result.JournalPrefix.ExitCode -eq 8 -and
            $result.JournalPrefix.HandlerEntries -eq 0 -and
            $result.JournalPrefix.TreeUnchanged) (
            'An anchored journal tamper reached a handler or changed bytes.')
        Assert-True (
            $result.VerifiedCacheMissing.Probe.ExitCode -eq 3 -and
            $result.VerifiedCacheMissing.Probe.Message -ceq
                'HANDLER_SENTINEL' -and
            $result.VerifiedCacheMissing.Probe.HandlerEntries -eq 1 -and
            $result.VerifiedCacheMissing.Restored -and
            $result.VerifiedCacheMissing.OtherBytesUnchanged) (
            'A verified missing cache was not repaired before handoff.')
        Assert-True (
            $result.OlderMissingCurrentVerified.Probe.ExitCode -eq 3 -and
            $result.OlderMissingCurrentVerified.Probe.Message -ceq
                'HANDLER_SENTINEL' -and
            $result.OlderMissingCurrentVerified.Probe.HandlerEntries -eq 1 -and
            $result.OlderMissingCurrentVerified.Restored -and
            $result.OlderMissingCurrentVerified.OtherBytesUnchanged) (
            'A current verified slot did not survive an older missing slot.')
        Assert-True (
            $result.OlderTornCurrentVerified.Probe.ExitCode -eq 3 -and
            $result.OlderTornCurrentVerified.Probe.Message -ceq
                'HANDLER_SENTINEL' -and
            $result.OlderTornCurrentVerified.Probe.HandlerEntries -eq 1 -and
            $result.OlderTornCurrentVerified.Restored -and
            $result.OlderTornCurrentVerified.OtherBytesUnchanged) (
            'A current verified slot did not survive an older torn slot.')
        Assert-True (
            $result.OlderHmacCurrentVerified.Probe.ExitCode -eq 3 -and
            $result.OlderHmacCurrentVerified.Probe.Message -ceq
                'HANDLER_SENTINEL' -and
            $result.OlderHmacCurrentVerified.Probe.HandlerEntries -eq 1 -and
            $result.OlderHmacCurrentVerified.Restored -and
            $result.OlderHmacCurrentVerified.OtherBytesUnchanged) (
            'A current verified slot did not survive an older HMAC failure.')
        Assert-True (
            $result.AdvancedLatestHmacOlderStale.SetupCorrect -and
            $result.AdvancedLatestHmacOlderStale.Probe.ExitCode -eq 8 -and
            $result.AdvancedLatestHmacOlderStale.Probe.HandlerEntries -eq 0 -and
            $result.AdvancedLatestHmacOlderStale.Probe.TreeUnchanged) (
            'A stale surviving slot was accepted after latest HMAC damage.')
        Assert-True (
            $result.DeferredOlderMissing.ExitCode -eq 8 -and
            $result.DeferredOlderMissing.Message -ceq
                'Platform readiness is deferred until elevation.' -and
            $result.DeferredOlderMissing.HandlerEntries -eq 0 -and
            $result.DeferredOlderMissing.TreeUnchanged) (
            'Deferred readiness changed bytes with an older missing slot.')
        Assert-True (
            $result.DeferredOlderTorn.ExitCode -eq 8 -and
            $result.DeferredOlderTorn.Message -ceq
                'Platform readiness is deferred until elevation.' -and
            $result.DeferredOlderTorn.HandlerEntries -eq 0 -and
            $result.DeferredOlderTorn.TreeUnchanged) (
            'Deferred readiness changed bytes with an older torn slot.')
        Assert-True (
            $result.DeferredOlderHmac.ExitCode -eq 8 -and
            $result.DeferredOlderHmac.Message -ceq
                'Platform readiness is deferred until elevation.' -and
            $result.DeferredOlderHmac.HandlerEntries -eq 0 -and
            $result.DeferredOlderHmac.TreeUnchanged) (
            'Deferred readiness changed bytes with an older HMAC failure.')
        Assert-True (
            $result.VerifiedCacheTorn.Probe.ExitCode -eq 3 -and
            $result.VerifiedCacheTorn.Probe.Message -ceq
                'HANDLER_SENTINEL' -and
            $result.VerifiedCacheTorn.Probe.HandlerEntries -eq 1 -and
            $result.VerifiedCacheTorn.Restored -and
            $result.VerifiedCacheTorn.OtherBytesUnchanged) (
            'A verified torn cache was not repaired from journal authority.')
        Assert-True (
            $result.VerifiedWalTail.Probe.ExitCode -eq 3 -and
            $result.VerifiedWalTail.Probe.Message -ceq
                'HANDLER_SENTINEL' -and
            $result.VerifiedWalTail.Probe.HandlerEntries -eq 1 -and
            $result.VerifiedWalTail.Repaired -and
            $result.VerifiedWalTail.OtherBytesUnchanged) (
            'A verified recoverable WAL tail was not repaired at handoff.')
        Assert-True (
            $result.VerifiedWalTruncated.ExitCode -eq 8 -and
            $result.VerifiedWalTruncated.HandlerEntries -eq 0 -and
            $result.VerifiedWalTruncated.TreeUnchanged) (
            'A truncated WAL reached a handler or changed fixture bytes.')
        Assert-True (
            $result.VerifiedWalPrefix.ExitCode -eq 8 -and
            $result.VerifiedWalPrefix.HandlerEntries -eq 0 -and
            $result.VerifiedWalPrefix.TreeUnchanged) (
            'A WAL prefix tamper reached a handler or changed fixture bytes.')
        $badDeferredIntegrity = @(
            $result.DeferredIntegrity | Where-Object {
                $_.ExitCode -ne 8 -or
                $_.HandlerEntries -ne 0 -or
                -not $_.TreeUnchanged -or
                ($_.FatalWal -and
                    $_.Message -cne
                        'Protected platform readiness WAL is invalid.') -or
                (-not $_.FatalWal -and
                    $_.Message -cne
                        'Platform readiness is deferred until elevation.')
            })
        if ($badDeferredIntegrity.Count -gt 0) {
            Write-Output ($badDeferredIntegrity |
                ConvertTo-Json -Compress -Depth 5)
        }
        Assert-True (
            $result.DeferredIntegrity.Count -eq 60 -and
            $badDeferredIntegrity.Count -eq 0) (
            'A deferred integrity case wrote bytes, reached a handler, or ' +
            'returned the wrong readiness result across the 10 commands.')
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
            $result.State.TpmPresentVerified -is [bool] -and
            -not $result.State.TpmPresentVerified -and
            $result.State.TpmReadyVerified -is [bool] -and
            -not $result.State.TpmReadyVerified -and
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
