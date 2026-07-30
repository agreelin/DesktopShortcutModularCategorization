$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:Cases = 0
$script:Assertions = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:Assertions++
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param([AllowNull()]$Actual, [AllowNull()]$Expected, [string]$Message)
    Assert-True ($Actual -ceq $Expected) (
        "$Message Actual=<$Actual>; Expected=<$Expected>.")
}

function Assert-CodeSet {
    param($Result, [string[]]$Expected, [string]$Message)
    $script:Cases++
    $actual = @($Result.errors | ForEach-Object { $_.code })
    Assert-Equal ($actual -join '|') ($Expected -join '|') $Message
}

function Assert-ThrowsCode {
    param([scriptblock]$Action, [string]$Expected, [string]$Message)
    $script:Cases++
    $actual = $null
    try { & $Action }
    catch {
        $actual = [string]$_.Exception.Data['FslFormalLauncherBundleCode']
    }
    Assert-Equal $actual $Expected $Message
}

function Copy-Object {
    param($Value)
    return $Value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
}

function Write-Utf8 {
    param([string]$Path, [AllowEmptyString()][string]$Text)
    [IO.File]::WriteAllText(
        $Path,
        $Text,
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
        throw (
            "Git fixture command failed: $($Arguments -join ' ')`n" +
            ($output -join "`n"))
    }
    return (@($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
}

function Get-Sha {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-TextSha {
    param([string]$Text)
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha.ComputeHash($bytes)).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Get-Sha1Bytes {
    param([byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA1]::Create()
    try { return ,$sha.ComputeHash($Bytes) }
    finally { $sha.Dispose() }
}

function Join-Bytes {
    param([object[]]$Parts)
    $length = 0
    foreach ($part in $Parts) { $length += ([byte[]]$part).Length }
    $result = [byte[]]::new($length)
    $offset = 0
    foreach ($part in $Parts) {
        $bytes = [byte[]]$part
        [Array]::Copy($bytes, 0, $result, $offset, $bytes.Length)
        $offset += $bytes.Length
    }
    return ,$result
}

function Get-GitObjectIdBytes {
    param([string]$Type, [byte[]]$Content)
    $header = [Text.Encoding]::ASCII.GetBytes(
        $Type + ' ' + $Content.Length + [char]0)
    return ,(Get-Sha1Bytes (Join-Bytes @($header, $Content)))
}

function Convert-HexBytes {
    param([string]$Hex)
    $bytes = [byte[]]::new($Hex.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte(
            $Hex.Substring($index * 2, 2),
            16)
    }
    return ,$bytes
}

function Convert-BytesHex {
    param([byte[]]$Bytes)
    return [BitConverter]::ToString($Bytes).Replace('-', '').ToLowerInvariant()
}

function Set-U16Be {
    param([byte[]]$Bytes, [int]$Offset, [uint16]$Value)
    $Bytes[$Offset] = [byte](($Value -shr 8) -band 0xFF)
    $Bytes[$Offset + 1] = [byte]($Value -band 0xFF)
}

function Set-U32Be {
    param([byte[]]$Bytes, [int]$Offset, [uint32]$Value)
    $Bytes[$Offset] = [byte](($Value -shr 24) -band 0xFF)
    $Bytes[$Offset + 1] = [byte](($Value -shr 16) -band 0xFF)
    $Bytes[$Offset + 2] = [byte](($Value -shr 8) -band 0xFF)
    $Bytes[$Offset + 3] = [byte]($Value -band 0xFF)
}

function New-TestGitIndex {
    param([object[]]$Entries, [object[]]$Extensions)
    $parts = [Collections.Generic.List[object]]::new()
    $header = [byte[]]::new(12)
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('DIRC'), $header, 4)
    Set-U32Be $header 4 2
    Set-U32Be $header 8 $Entries.Count
    [void]$parts.Add($header)
    foreach ($entry in $Entries) {
        $pathBytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(
            [string]$entry.path)
        $length = 62 + $pathBytes.Length + 1
        $paddedLength = ($length + 7) -band (-bnot 7)
        $bytes = [byte[]]::new($paddedLength)
        Set-U32Be $bytes 24 ([uint32]$entry.mode)
        $oid = [byte[]]$entry.oid
        [Array]::Copy($oid, 0, $bytes, 40, 20)
        $flags = [uint16](
            [Math]::Min($pathBytes.Length, 0x0FFF) -bor
            [uint16]$entry.flags)
        Set-U16Be $bytes 60 $flags
        [Array]::Copy($pathBytes, 0, $bytes, 62, $pathBytes.Length)
        [void]$parts.Add($bytes)
    }
    foreach ($extension in $Extensions) {
        $payload = [byte[]]$extension.payload
        $record = [byte[]]::new(8 + $payload.Length)
        [Array]::Copy(
            [Text.Encoding]::ASCII.GetBytes([string]$extension.signature),
            $record,
            4)
        Set-U32Be $record 4 $payload.Length
        [Array]::Copy($payload, 0, $record, 8, $payload.Length)
        [void]$parts.Add($record)
    }
    $body = Join-Bytes @($parts)
    return ,(Join-Bytes @($body, (Get-Sha1Bytes $body)))
}

function Update-TestIndexChecksum {
    param([byte[]]$Bytes)
    $checksumOffset = $Bytes.Length - 20
    $body = [byte[]]::new($checksumOffset)
    [Array]::Copy($Bytes, 0, $body, 0, $checksumOffset)
    $checksum = Get-Sha1Bytes $body
    [Array]::Copy($checksum, 0, $Bytes, $checksumOffset, 20)
}

function New-TestZlib {
    param([byte[]]$Uncompressed)
    $output = [IO.MemoryStream]::new()
    $output.WriteByte(0x78)
    $output.WriteByte(0x9C)
    $deflate = [IO.Compression.DeflateStream]::new(
        $output,
        [IO.Compression.CompressionMode]::Compress,
        $true)
    try { $deflate.Write($Uncompressed, 0, $Uncompressed.Length) }
    finally { $deflate.Dispose() }
    [uint32]$a = 1
    [uint32]$b = 0
    foreach ($value in $Uncompressed) {
        $a = [uint32](($a + $value) % 65521)
        $b = [uint32](($b + $a) % 65521)
    }
    $adler = [byte[]]::new(4)
    Set-U32Be $adler 0 ([uint32](($b -shl 16) -bor $a))
    $output.Write($adler, 0, 4)
    $bytes = $output.ToArray()
    $output.Dispose()
    return ,$bytes
}

function New-TestStoredZlib {
    param([byte[]]$Uncompressed)
    if ($Uncompressed.Length -gt 65535) {
        throw 'The stored test payload is too large.'
    }
    $raw = [byte[]]::new(5 + $Uncompressed.Length)
    $raw[0] = 1
    $length = [uint16]$Uncompressed.Length
    $complement = [uint16]($length -bxor 0xFFFF)
    $raw[1] = [byte]($length -band 0xFF)
    $raw[2] = [byte](($length -shr 8) -band 0xFF)
    $raw[3] = [byte]($complement -band 0xFF)
    $raw[4] = [byte](($complement -shr 8) -band 0xFF)
    [Array]::Copy($Uncompressed, 0, $raw, 5, $Uncompressed.Length)
    [uint32]$a = 1
    [uint32]$b = 0
    foreach ($value in $Uncompressed) {
        $a = [uint32](($a + $value) % 65521)
        $b = [uint32](($b + $a) % 65521)
    }
    $adler = [byte[]]::new(4)
    Set-U32Be $adler 0 ([uint32](($b -shl 16) -bor $a))
    return ,(Join-Bytes @([byte[]]@(0x78, 0x01), $raw, $adler))
}

function Write-TestLooseObject {
    param(
        [string]$GitDirectory,
        [string]$Type,
        [byte[]]$Content)
    $header = [Text.Encoding]::ASCII.GetBytes(
        $Type + ' ' + $Content.Length + [char]0)
    $uncompressed = Join-Bytes @($header, $Content)
    $oid = Convert-BytesHex (Get-Sha1Bytes $uncompressed)
    $bytes = New-TestZlib $uncompressed
    $directory = Join-Path (
        Join-Path $GitDirectory 'objects') $oid.Substring(0, 2)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllBytes(
        (Join-Path $directory $oid.Substring(2)),
        $bytes)
    return $oid
}

function New-TestAcl {
    param([string]$UserSid, [bool]$Directory)
    $sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
    $group = [Security.Principal.SecurityIdentifier]::new(
        $sid.AccountDomainSid.Value + '-513')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administrators = [Security.Principal.SecurityIdentifier]::new(
        'S-1-5-32-544')
    $security = if ($Directory) {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    else { [Security.AccessControl.FileSecurity]::new() }
    $security.SetOwner($sid)
    $security.SetGroup($group)
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = if ($Directory) {
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($principal in @($system, $administrators, $sid)) {
        [void]$security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $principal,
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

function Set-TestAcl {
    param([string]$Path, [string]$UserSid, [bool]$Directory)
    $security = New-TestAcl $UserSid $Directory
    if ($Directory) {
        [IO.Directory]::SetAccessControl($Path, $security)
    }
    else { [IO.File]::SetAccessControl($Path, $security) }
}

function Get-Sddl {
    param([string]$Path, [bool]$Directory)
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor
        [Security.AccessControl.AccessControlSections]::Access
    $security = if ($Directory) {
        [IO.Directory]::GetAccessControl($Path, $sections)
    }
    else { [IO.File]::GetAccessControl($Path, $sections) }
    return $security.GetSecurityDescriptorSddlForm($sections)
}

function Get-FileRecord {
    param([string]$Path)
    return [pscustomobject][ordered]@{
        path = [IO.Path]::GetFullPath($Path)
        length = (Get-Item -LiteralPath $Path).Length
        sha256 = Get-Sha $Path
    }
}

function New-PublicModel {
    param(
        [string]$FixtureId,
        [string]$SourceLeaf,
        [string]$BundleLeaf,
        [string]$RecoveryContractId,
        [string]$RecoveryHash)
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        authorityProfile = 'TestFixture'
        contractId = 'FSL-CP10-FORMAL-LAUNCHER-BUNDLE-TEST'
        checkpoint = 'CP10-TRACKED-FORMAL-LAUNCHER-BUNDLE-GENERATOR-VALIDATOR'
        attemptId = 'CP10-FLB-TEST-001'
        runId = '20260729T120000Z-1234abcd'
        rootBinding = [pscustomobject][ordered]@{
            fixtureId = $FixtureId
            sourceLeafName = $SourceLeaf
            bundleLeafName = $BundleLeaf
        }
        recoveryAuthority = [pscustomobject][ordered]@{
            contractId = $RecoveryContractId
            contractSha256 = $RecoveryHash
        }
    }
}

function Set-RecoveryContract {
    param(
        [string]$Path,
        $Contract,
        [string]$UserSid,
        $Model)
    Write-Utf8 $Path (($Contract | ConvertTo-Json -Depth 32) + "`n")
    Set-TestAcl $Path $UserSid $false
    $Model.recoveryAuthority.contractSha256 = Get-Sha $Path
}

function Restore-Bytes {
    param([string]$Path, [byte[]]$Bytes)
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Write-RehashedBundleContract {
    param([string]$Path, $Contract, $Module)
    for ($iteration = 0; $iteration -lt 8; $iteration++) {
        $Contract.bindingManifest.contractCanonicalSha256 = '0' * 64
        $bytes = & $Module {
            param($Value)
            Get-FslFlbBytes (ConvertTo-FslFlbCanonicalJson $Value)
        } $Contract
        if ([int]$Contract.bindingManifest.contractLength -eq $bytes.Length) {
            break
        }
        $Contract.bindingManifest.contractLength = $bytes.Length
    }
    $Contract.bindingManifest.contractCanonicalSha256 = '0' * 64
    $zeroBytes = & $Module {
        param($Value)
        Get-FslFlbBytes (ConvertTo-FslFlbCanonicalJson $Value)
    } $Contract
    $Contract.bindingManifest.contractCanonicalSha256 = & $Module {
        param($Bytes)
        Get-FslFlbSha256Bytes $Bytes
    } $zeroBytes
    $actual = & $Module {
        param($Value)
        Get-FslFlbBytes (ConvertTo-FslFlbCanonicalJson $Value)
    } $Contract
    [IO.File]::WriteAllBytes($Path, $actual)
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$modulePath = Join-Path $projectRoot (
    'eng\stage4\FolderSessionLock.Stage4.FormalLauncherBundle.psm1')
$recoveryModulePath = Join-Path $projectRoot (
    'eng\stage4\FolderSessionLock.Stage4.RecoveryAuthorityBundle.psm1')
$nativePath = Join-Path $projectRoot (
    'eng\stage4\FolderSessionLock.Stage4.Native.cs')
$controllerPath = Join-Path $projectRoot 'eng\stage4\Invoke-Stage4.ps1'
$module = Import-Module $modulePath -Force -PassThru
$tempBase = Join-Path ([IO.Path]::GetTempPath()) 'FolderSessionLock.Tests'
[IO.Directory]::CreateDirectory($tempBase) | Out-Null
$fixtureRoot = $null

try {
    $fixtureId = [Guid]::NewGuid().ToString('D')
    $fixtureRoot = Join-Path $tempBase $fixtureId
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sourceLeaf = "source recovery's authority"
    $bundleLeaf = "bundle output's authority"
    $sourceRoot = Join-Path $fixtureRoot $sourceLeaf
    $bundleRoot = Join-Path $fixtureRoot $bundleLeaf
    $authorityRoot = Join-Path $fixtureRoot 'recovery-authority-fixture'
    $repositoryRoot = Join-Path $authorityRoot 'repository'
    $evidenceRoot = Join-Path $authorityRoot 'execution-state'
    $predecessorRoot = Join-Path $authorityRoot 'install-wal-rollback-1'
    $anchorRoot = Join-Path $authorityRoot 'external-anchors'
    $releaseRoot = Join-Path $authorityRoot 'frozen-release'
    $transactionRoot = Join-Path $authorityRoot 'install-prestate'
    foreach ($path in @(
        $repositoryRoot,
        $evidenceRoot,
        $predecessorRoot,
        $anchorRoot,
        $releaseRoot,
        $transactionRoot)) {
        [IO.Directory]::CreateDirectory($path) | Out-Null
    }

    # Strict process-free Git authority is exercised only against a synthetic
    # temporary repository. The real repository's .git is never written.
    $syntheticRoot = Join-Path $fixtureRoot 'synthetic-repository'
    $syntheticGit = Join-Path $syntheticRoot '.git'
    $syntheticRefDirectory = Join-Path $syntheticGit 'refs\heads'
    [IO.Directory]::CreateDirectory($syntheticRefDirectory) | Out-Null
    $alphaPath = Join-Path $syntheticRoot 'alpha.txt'
    $nestedRoot = Join-Path $syntheticRoot 'dir'
    [IO.Directory]::CreateDirectory($nestedRoot) | Out-Null
    $betaPath = Join-Path $nestedRoot 'beta.txt'
    Write-Utf8 $alphaPath "alpha`n"
    Write-Utf8 $betaPath "beta`n"
    $alphaContent = [IO.File]::ReadAllBytes($alphaPath)
    $betaContent = [IO.File]::ReadAllBytes($betaPath)
    $alphaOid = Get-GitObjectIdBytes 'blob' $alphaContent
    $betaOid = Get-GitObjectIdBytes 'blob' $betaContent
    $subtreeBody = Join-Bytes @(
        [Text.Encoding]::ASCII.GetBytes("100644 beta.txt`0"),
        $betaOid)
    $subtreeOid = Get-GitObjectIdBytes 'tree' $subtreeBody
    $rootTreeBody = Join-Bytes @(
        [Text.Encoding]::ASCII.GetBytes("100644 alpha.txt`0"),
        $alphaOid,
        [Text.Encoding]::ASCII.GetBytes("40000 dir`0"),
        $subtreeOid)
    $rootTreeOid = Get-GitObjectIdBytes 'tree' $rootTreeBody
    $rootTreeHex = Convert-BytesHex $rootTreeOid
    $gitEntries = @(
        [pscustomobject]@{
            path = 'alpha.txt'
            mode = [uint32]0x000081A4
            oid = $alphaOid
            flags = [uint16]0
        },
        [pscustomobject]@{
            path = 'dir/beta.txt'
            mode = [uint32]0x000081A4
            oid = $betaOid
            flags = [uint16]0
        })
    $cacheTreePayload = Join-Bytes @(
        [byte]0,
        [Text.Encoding]::ASCII.GetBytes("2 1`n"),
        $rootTreeOid,
        [Text.Encoding]::UTF8.GetBytes("dir`0"),
        [Text.Encoding]::ASCII.GetBytes("1 0`n"),
        $subtreeOid)
    $treeExtension = [pscustomobject]@{
        signature = 'TREE'
        payload = $cacheTreePayload
    }
    $validIndex = New-TestGitIndex $gitEntries @($treeExtension)
    $syntheticIndexPath = Join-Path $syntheticGit 'index'
    [IO.File]::WriteAllBytes($syntheticIndexPath, $validIndex)
    Write-Utf8 (Join-Path $syntheticGit 'HEAD') "ref: refs/heads/main`n"
    $commitContent = [Text.Encoding]::UTF8.GetBytes(
        "tree $rootTreeHex`nauthor Test <test@example.invalid> 0 +0000`n" +
        "committer Test <test@example.invalid> 0 +0000`n`nfixture`n")
    $commitOid = Write-TestLooseObject $syntheticGit 'commit' $commitContent
    Write-Utf8 (
        Join-Path $syntheticRefDirectory 'main') ($commitOid + "`n")
    $syntheticState = & $module {
        param($Root)
        Get-FslFlbRepositoryAtRoot $Root $Root
    } $syntheticRoot
    Assert-True $syntheticState.trackedClean (
        'The valid synthetic Git index/tree/HEAD authority failed.')
    Assert-Equal $syntheticState.tree $rootTreeHex (
        'The synthetic HEAD tree binding drifted.')

    $assertGitFalse = {
        param([byte[]]$IndexBytes, [string]$Message)
        [IO.File]::WriteAllBytes($syntheticIndexPath, $IndexBytes)
        $script:Cases++
        $clean = & $module {
            param($Root, $Git, $Tree)
            Test-FslFlbIndexClean $Root $Git $Tree
        } $syntheticRoot $syntheticGit $rootTreeHex
        Assert-True (-not $clean) $Message
        [IO.File]::WriteAllBytes($syntheticIndexPath, $validIndex)
    }

    Write-Utf8 $alphaPath "unstaged modification`n"
    & $assertGitFalse $validIndex (
        'An unstaged worktree modification was accepted.')
    Restore-Bytes $alphaPath $alphaContent
    $gammaPath = Join-Path $syntheticRoot 'gamma.txt'
    Write-Utf8 $gammaPath "gamma`n"
    $gammaEntry = [pscustomobject]@{
        path = 'gamma.txt'
        mode = [uint32]0x000081A4
        oid = Get-GitObjectIdBytes 'blob' (
            [IO.File]::ReadAllBytes($gammaPath))
        flags = [uint16]0
    }
    & $assertGitFalse (
        New-TestGitIndex @($gitEntries + $gammaEntry) @()) (
        'A staged add was accepted against the frozen HEAD tree.')
    [IO.File]::Delete($gammaPath)
    & $assertGitFalse (
        New-TestGitIndex @($gitEntries[0]) @()) (
        'A staged delete was accepted against the frozen HEAD tree.')
    foreach ($flagCase in @(
        [uint16]0x1000,
        [uint16]0x4000,
        [uint16]0x8000)) {
        $entries = Copy-Object $gitEntries
        $entries[0].flags = $flagCase
        & $assertGitFalse (
            New-TestGitIndex $entries @()) (
            "A stage/extended/assume-valid flag 0x$($flagCase.ToString('X4')) was accepted.")
    }
    $entries = Copy-Object $gitEntries
    $entries[0].mode = [uint32]0x0000A000
    & $assertGitFalse (
        New-TestGitIndex $entries @()) (
        'An invalid index mode was accepted.')
    $entries = Copy-Object $gitEntries
    $entries[0].oid = [byte[]]::new(20)
    & $assertGitFalse (
        New-TestGitIndex $entries @()) (
        'An all-zero index object ID was accepted.')
    & $assertGitFalse (
        New-TestGitIndex @($gitEntries[1], $gitEntries[0]) @()) (
        'An unsorted index was accepted.')
    & $assertGitFalse (
        New-TestGitIndex @($gitEntries[0], $gitEntries[0]) @()) (
        'A duplicate index path was accepted.')
    & $assertGitFalse (
        New-TestGitIndex @(
            [pscustomobject]@{
                path = '../escape.txt'
                mode = [uint32]0x000081A4
                oid = $alphaOid
                flags = [uint16]0
            }) @()) (
        'An escaping index path was accepted.')
    $badChecksum = [byte[]]$validIndex.Clone()
    $badChecksum[$badChecksum.Length - 1] =
        $badChecksum[$badChecksum.Length - 1] -bxor 1
    & $assertGitFalse $badChecksum (
        'A bad index trailing SHA-1 was accepted.')
    $badVersion = [byte[]]$validIndex.Clone()
    Set-U32Be $badVersion 4 3
    Update-TestIndexChecksum $badVersion
    & $assertGitFalse $badVersion (
        'An unsupported index version was accepted.')
    $badCount = [byte[]]$validIndex.Clone()
    Set-U32Be $badCount 8 999
    Update-TestIndexChecksum $badCount
    & $assertGitFalse $badCount (
        'An out-of-bounds index entry count was accepted.')
    $badUtf8 = [byte[]]$validIndex.Clone()
    $badUtf8[12 + 62] = 0xFF
    Update-TestIndexChecksum $badUtf8
    & $assertGitFalse $badUtf8 (
        'A non-strict-UTF8 index path was accepted.')
    & $assertGitFalse (
        New-TestGitIndex $gitEntries @(
            [pscustomobject]@{
                signature = 'UNTR'
                payload = [byte[]]@(1, 2, 3)
            })) (
        'An unknown index extension was accepted.')
    & $assertGitFalse (
        New-TestGitIndex $gitEntries @($treeExtension, $treeExtension)) (
        'A duplicate TREE extension was accepted.')
    & $assertGitFalse (
        New-TestGitIndex $gitEntries @(
            [pscustomobject]@{
                signature = 'TREE'
                payload = [Text.Encoding]::ASCII.GetBytes("bad`0x")
            })) (
        'A malformed TREE extension was accepted.')
    $semanticTreePayload = [byte[]]$cacheTreePayload.Clone()
    $semanticTreePayload[5] = $semanticTreePayload[5] -bxor 1
    & $assertGitFalse (
        New-TestGitIndex $gitEntries @(
            [pscustomobject]@{
                signature = 'TREE'
                payload = $semanticTreePayload
            })) (
        'A semantically drifted TREE cache OID was accepted.')
    $badPadding = [byte[]]$validIndex.Clone()
    $badPadding[159] = 1
    Update-TestIndexChecksum $badPadding
    & $assertGitFalse $badPadding (
        'A nonzero index entry padding byte was accepted.')
    & $assertGitFalse (
        $validIndex[0..($validIndex.Length - 2)]) (
        'A truncated index was accepted.')
    $syntheticHardLink = Join-Path $fixtureRoot 'synthetic-alpha-hardlink'
    New-Item -ItemType HardLink -Path $syntheticHardLink -Target $alphaPath |
        Out-Null
    & $assertGitFalse $validIndex (
        'A hard-linked tracked worktree file was accepted.')
    [IO.File]::Delete($syntheticHardLink)

    $packedRefs = Join-Path $syntheticGit 'packed-refs'
    $looseRef = Join-Path $syntheticRefDirectory 'main'
    $looseRefBytes = [IO.File]::ReadAllBytes($looseRef)
    [IO.File]::Delete($looseRef)
    Write-Utf8 $packedRefs ("$commitOid refs/heads/main`n")
    Assert-ThrowsCode {
        & $module {
            param($Root)
            Get-FslFlbRepositoryAtRoot $Root $Root
        } $syntheticRoot
    } 'FSL-FLB-V010-SOURCE-RECOVERY' (
        'A packed-only branch ref was accepted.')
    [IO.File]::Delete($packedRefs)
    Restore-Bytes $looseRef $looseRefBytes
    $commitObjectPath = Join-Path (
        Join-Path $syntheticGit (
            'objects\' + $commitOid.Substring(0, 2))) (
        $commitOid.Substring(2))
    $commitObjectBytes = [IO.File]::ReadAllBytes($commitObjectPath)
    [IO.File]::Delete($commitObjectPath)
    Assert-ThrowsCode {
        & $module {
            param($Root)
            Get-FslFlbRepositoryAtRoot $Root $Root
        } $syntheticRoot
    } 'FSL-FLB-V010-SOURCE-RECOVERY' (
        'A missing loose commit object was accepted.')
    Restore-Bytes $commitObjectPath $commitObjectBytes

    # The strict zlib/deflate scanner accepts stored, fixed, and dynamic
    # final-block streams and rejects envelope/end-boundary drift before
    # DeflateStream is allowed to decode a Git object.
    $fixedZlib = New-TestZlib (
        [Text.Encoding]::UTF8.GetBytes('abcde'))
    $dynamicZlib = New-TestZlib (
        [Text.Encoding]::UTF8.GetBytes(('abcde' * 1000)))
    $storedZlib = New-TestStoredZlib (
        [Text.Encoding]::UTF8.GetBytes('stored payload'))
    foreach ($positiveZlib in @(
        @($storedZlib, 0),
        @($fixedZlib, 1),
        @($dynamicZlib, 2))) {
        $script:Cases++
        Assert-True (
            (([int]$positiveZlib[0][2] -shr 1) -band 3) -eq
                [int]$positiveZlib[1] -and
            [FolderSessionLock.Stage4.FormalLauncherNative]::
                ValidateZlibEnvelope([byte[]]$positiveZlib[0])) (
            'A valid stored/fixed/dynamic zlib stream was rejected.')
    }
    $zlibMutations = [Collections.Generic.List[byte[]]]::new()
    $mutation = [byte[]]$commitObjectBytes.Clone()
    $mutation[0] = 0x79
    $zlibMutations.Add($mutation)
    $mutation = [byte[]]$commitObjectBytes.Clone()
    $mutation[1] = 0x20
    $zlibMutations.Add($mutation)
    $mutation = [byte[]]$commitObjectBytes.Clone()
    $mutation[0] = 0x88
    for ($flag = 0; $flag -le 255; $flag++) {
        if (($flag -band 0x20) -eq 0 -and
            (((0x88 -shl 8) -bor $flag) % 31) -eq 0) {
            $mutation[1] = [byte]$flag
            break
        }
    }
    $zlibMutations.Add($mutation)
    $mutation = [byte[]]$commitObjectBytes.Clone()
    $mutation[1] = $mutation[1] -bxor 1
    $zlibMutations.Add($mutation)
    $zlibMutations.Add([byte[]]$commitObjectBytes[
        0..($commitObjectBytes.Length - 2)])
    $mutation = [byte[]]$commitObjectBytes.Clone()
    $mutation[$mutation.Length - 1] =
        $mutation[$mutation.Length - 1] -bxor 1
    $zlibMutations.Add($mutation)
    $zlibMutations.Add((Join-Bytes @(
        [byte[]]$commitObjectBytes[0..($commitObjectBytes.Length - 5)],
        [byte[]]@(0),
        [byte[]]$commitObjectBytes[
            ($commitObjectBytes.Length - 4)..($commitObjectBytes.Length - 1)])))
    $zlibMutations.Add((Join-Bytes @(
        $commitObjectBytes,
        [byte[]]@(0))))
    $mutation = [byte[]]$commitObjectBytes.Clone()
    $mutation[2] = $mutation[2] -band 0xFE
    $zlibMutations.Add($mutation)
    $paddingMutation = [byte[]]$fixedZlib.Clone()
    $paddingMutation[$paddingMutation.Length - 5] =
        $paddingMutation[$paddingMutation.Length - 5] -bor 0x80
    $zlibMutations.Add($paddingMutation)
    $storedMutation = [byte[]]$storedZlib.Clone()
    $storedMutation[5] = $storedMutation[5] -bxor 1
    $zlibMutations.Add($storedMutation)
    $zlibMutationIndex = 0
    foreach ($badZlib in $zlibMutations) {
        $script:Cases++
        Assert-True (-not (
            [FolderSessionLock.Stage4.FormalLauncherNative]::
                ValidateZlibEnvelope($badZlib))) (
            "Malformed zlib mutation $zlibMutationIndex was accepted.")
        $zlibMutationIndex++
    }
    foreach ($badLooseObject in @($zlibMutations[0..8])) {
        [IO.File]::WriteAllBytes($commitObjectPath, $badLooseObject)
        Assert-ThrowsCode {
            & $module {
                param($Git, $Object)
                Read-FslFlbLooseObject $Git $Object 'commit'
            } $syntheticGit $commitOid
        } 'FSL-FLB-V010-SOURCE-RECOVERY' (
            'A malformed Git loose-object zlib envelope was decoded.')
    }
    Restore-Bytes $commitObjectPath $commitObjectBytes
    Write-Utf8 (Join-Path $syntheticGit 'HEAD') "ref: refs/heads/other`n"
    Assert-ThrowsCode {
        & $module {
            param($Root)
            Get-FslFlbRepositoryAtRoot $Root $Root
        } $syntheticRoot
    } 'FSL-FLB-V010-SOURCE-RECOVERY' (
        'A missing loose branch after HEAD drift was accepted.')
    Write-Utf8 (Join-Path $syntheticGit 'HEAD') "ref: refs/heads/main`n"

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
        Write-Utf8 $path ("old execution source $index`n")
    }
    [void](Invoke-Git $repositoryRoot @('add', '--', '.'))
    [void](Invoke-Git $repositoryRoot @(
        'commit', '-m', 'old execution authority'))
    $executionCommit = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD')
    $executionTree = Invoke-Git $repositoryRoot @('rev-parse', 'HEAD^{tree}')
    Write-Utf8 (
        Join-Path $repositoryRoot $fixedFiles[0].Replace('/', '\')) (
        "new recovery toolchain source`n")
    [void](Invoke-Git $repositoryRoot @('add', '--', '.'))
    [void](Invoke-Git $repositoryRoot @(
        'commit', '-m', 'descendant recovery toolchain'))

    $statePath = Join-Path $evidenceRoot 'stage4-state.json'
    Write-Utf8 $statePath (([pscustomobject][ordered]@{
        gitCommit = $executionCommit
        sequence = 6
        transition = 'InstallStarted'
        releaseRoot = $releaseRoot
    } | ConvertTo-Json) + "`n")
    Write-Utf8 (Join-Path $evidenceRoot 'stage4-journal.jsonl') (
        "{`"sequence`":6,`"transition`":`"InstallStarted`"}`n")
    $walLines = @()
    for ($index = 1; $index -le 4; $index++) {
        $walLines += "{`"sequence`":$index,`"phase`":`"Prefix$index`"}"
    }
    Write-Utf8 (Join-Path $evidenceRoot 'install-wal.jsonl') (
        ($walLines -join "`n") + "`n")
    Write-Utf8 (Join-Path $evidenceRoot 'build-results.txt') (
        "Release build 0 warnings 0 errors`n")
    Write-Utf8 (Join-Path $evidenceRoot 'commands.txt') (
        "frozen Stage 4 commands`n")
    Write-Utf8 (Join-Path $evidenceRoot 'prestate.json') (
        "{`"runId`":`"20260729T120000Z-1234abcd`"}`n")
    Write-Utf8 (Join-Path $evidenceRoot 'signature-verification.txt') (
        "unsigned TestFixture authority`n")
    Write-Utf8 (Join-Path $evidenceRoot 'stage4-anchor.json') (
        "{`"sequence`":6}`n")
    Write-Utf8 (Join-Path $predecessorRoot 'elevated-reconcile.ps1') (
        "throw 'frozen predecessor wrapper'`n")
    Write-Utf8 (Join-Path $predecessorRoot 'recovery-contract.json') (
        "{`"schemaVersion`":2}`n")
    $evidencePaths = @(
        Join-Path $evidenceRoot 'build-results.txt'
        Join-Path $evidenceRoot 'commands.txt'
        Join-Path $evidenceRoot 'prestate.json'
        Join-Path $evidenceRoot 'signature-verification.txt'
        Join-Path $evidenceRoot 'stage4-anchor.json')
    $anchorPaths = @(
        Join-Path $anchorRoot 'anchor-0.json'
        Join-Path $anchorRoot 'anchor-1.json'
        Join-Path $anchorRoot 'key.dpapi')
    Write-Utf8 $anchorPaths[0] "{`"generation`":12}`n"
    Write-Utf8 $anchorPaths[1] "{`"generation`":11}`n"
    [IO.File]::WriteAllBytes($anchorPaths[2], [byte[]](1..32))

    $releasePaths = @(
        Join-Path $releaseRoot 'payload.exe'
        Join-Path $releaseRoot 'release-descriptor.json'
        Join-Path $releaseRoot 'release-manifest.json'
        Join-Path $releaseRoot 'SHA256SUMS.txt')
    Write-Utf8 $releasePaths[0] "fixture release`n"
    Write-Utf8 $releasePaths[1] "{`"version`":`"1.0.0`"}`n"
    Write-Utf8 $releasePaths[2] "{`"files`":4}`n"
    Write-Utf8 $releasePaths[3] "fixture sums`n"

    $recoveryContractId = 'FSL-CP10-DUAL-AUTHORITY-FLB-TEST'
    $recoveryModel = [pscustomobject][ordered]@{
        schemaVersion = 1
        authorityProfile = 'TestFixture'
        contractId = $recoveryContractId
        checkpoint =
            'CP10-TRACKED-DUAL-AUTHORITY-RECOVERY-BUNDLE-GENERATOR-VALIDATOR'
        runId = '20260729T120000Z-1234abcd'
        rootBinding = [pscustomobject][ordered]@{
            fixtureId = $fixtureId
            sourceLeafName = $sourceLeaf
        }
    }
    $recoveryModule = Import-Module $recoveryModulePath -Force -PassThru
    $null = & $recoveryModule {
        param($RecoveryModel)
        New-FslStage4RecoveryAuthorityBundle -Model $RecoveryModel
    } $recoveryModel
    $wrapperPath = Join-Path $sourceRoot 'elevated-reconcile.ps1'
    $recoveryPath = Join-Path $sourceRoot 'recovery-contract.json'
    $recovery = [IO.File]::ReadAllText(
        $recoveryPath,
        [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
    $gates = @($recovery.contractStageGates)
    $powerShell = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::System)) (
        'WindowsPowerShell\v1.0\powershell.exe')
    $model = New-PublicModel `
        $fixtureId `
        $sourceLeaf `
        $bundleLeaf `
        $recoveryContractId `
        (Get-Sha $recoveryPath)

    # Pure injectable token proof matrix: no account creation, UAC, or token
    # mutation is performed by these cases.
    $tokenProof = [pscustomobject][ordered]@{
        machineName = 'FSL-STAGE4-VM'
        elevationType = 3
        currentAccountSid = $userSid
        linkedAccountSid = $userSid
        currentSidType = 1
        linkedSidType = 1
        currentAdministratorsDenyOnly = $true
        currentAdministratorsEnabled = $false
        linkedAdministratorsDenyOnly = $false
        linkedAdministratorsEnabled = $true
        currentAccountDomain = 'FSL-STAGE4-VM'
        linkedAccountDomain = 'FSL-STAGE4-VM'
    }
    $script:Cases++
    Assert-True (& $module {
        param($Proof)
        Test-FslFlbFormalTokenProofDto $Proof
    } $tokenProof) 'The canonical pure formal-token DTO failed.'
    $nativeTokenProof = & $module {
        $native =
            [FolderSessionLock.Stage4.FormalLauncherNative]::
                ReadFormalTokenProof()
        return [pscustomobject][ordered]@{
            machineName = [string]$native.MachineName
            elevationType = [int]$native.ElevationType
            currentAccountSid = [string]$native.CurrentAccountSid
            linkedAccountSid = [string]$native.LinkedAccountSid
            currentSidType = [int]$native.CurrentSidType
            linkedSidType = [int]$native.LinkedSidType
            currentAdministratorsDenyOnly =
                [bool]$native.CurrentAdministratorsDenyOnly
            currentAdministratorsEnabled =
                [bool]$native.CurrentAdministratorsEnabled
            linkedAdministratorsDenyOnly =
                [bool]$native.LinkedAdministratorsDenyOnly
            linkedAdministratorsEnabled =
                [bool]$native.LinkedAdministratorsEnabled
            currentAccountDomain = [string]$native.CurrentAccountDomain
            linkedAccountDomain = [string]$native.LinkedAccountDomain
        }
    }
    $script:Cases++
    Assert-True (& $module {
        param($Proof)
        Test-FslFlbFormalTokenProofDto $Proof
    } $nativeTokenProof) (
        'The generator native read-only TokenGroups proof failed.')

    # The Windows encoder is checked through CommandLineToArgvW with a dummy
    # argv[0]; apostrophes are ordinary characters, while quotes, whitespace,
    # empty arguments, and trailing backslashes follow Windows argv rules.
    $argvCases = @(
        [string[]]@('plain'),
        [string[]]@('with space', "apostrophe's"),
        [string[]]@('', 'after-empty'),
        [string[]]@('embedded"quote', 'tail\'),
        [string[]]@('multiple\\slashes', "tab`tvalue"),
        [string[]]@(
            '-NoLogo',
            '-NoProfile',
            '-NonInteractive',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            $wrapperPath))
    foreach ($argvCase in $argvCases) {
        $argumentLine = & $module {
            param([string[]]$Arguments)
            Join-FslFlbWindowsArgumentLine $Arguments
        } $argvCase
        $parsed = @(
            [FolderSessionLock.Stage4.FormalLauncherNative]::
                ParseWindowsCommandLine('"dummy.exe" ' + $argumentLine))
        $script:Cases++
        Assert-Equal ($parsed -join [char]0x1F) (
            (@('dummy.exe') + @($argvCase)) -join [char]0x1F) (
            'The Windows argv encoder did not round-trip exactly.')
    }
    $tokenMutations = @(
        { param($p) $p.machineName = 'OTHER-VM' },
        { param($p) $p.elevationType = 2 },
        { param($p) $p.currentAccountSid = 'S-1-5-18' },
        { param($p) $p.linkedAccountSid = 'S-1-5-18' },
        { param($p) $p.currentSidType = 2 },
        { param($p) $p.linkedSidType = 2 },
        { param($p) $p.currentAdministratorsDenyOnly = $false },
        { param($p) $p.currentAdministratorsEnabled = $true },
        { param($p) $p.linkedAdministratorsDenyOnly = $true },
        { param($p) $p.linkedAdministratorsEnabled = $false },
        { param($p) $p.currentAccountDomain = 'OTHER-DOMAIN' },
        { param($p) $p.linkedAccountDomain = 'OTHER-DOMAIN' },
        { param($p) $p.elevationType = '3' },
        {
            param($p)
            $p | Add-Member `
                -NotePropertyName extra `
                -NotePropertyValue 'forbidden'
        })
    foreach ($mutation in $tokenMutations) {
        $caseProof = Copy-Object $tokenProof
        & $mutation $caseProof
        $script:Cases++
        Assert-True (-not (& $module {
            param($Proof)
            Test-FslFlbFormalTokenProofDto $Proof
        } $caseProof)) 'A formal-token DTO mutation was accepted.'
    }

    # Pure terminal mapping/record-3 matrix.
    $terminalCases = @(
        @('Exited', 42, 0, $null),
        @('Exited', 42, 84, [string]$gates[0].gateId),
        @('Exited', 42, -1, $null),
        @('UacCancelled', 42, 84, $null),
        @('LaunchFailed', 42, 84, $null))
    foreach ($terminalCase in $terminalCases) {
        $terminal = & $module {
            param($Outcome, $TargetPid, $ExitCode, $Gates)
            New-FslFlbTerminalDto $Outcome $TargetPid $ExitCode $Gates
        } `
            $terminalCase[0] `
            $terminalCase[1] `
            $terminalCase[2] `
            $gates
        $script:Cases++
        Assert-Equal $terminal.gateId $terminalCase[3] (
            "Terminal gate mapping drifted for $($terminalCase[0])/" +
            "$($terminalCase[2]).")
        if ($terminalCase[0] -cne 'Exited') {
            Assert-True (
                $null -eq $terminal.targetPid -and
                $null -eq $terminal.exitCode -and
                $null -eq $terminal.gateId) (
                'A UAC/launch failure retained target, exit, or gate data.')
        }
    }
    $duplicateExitGates = Copy-Object $gates
    $duplicateExitGates[1].exitCode = $duplicateExitGates[0].exitCode
    $script:Cases++
    Assert-True (-not (& $module {
        param($Gates)
        Test-FslFlbGateMapDto $Gates
    } $duplicateExitGates)) (
        'A duplicate recovery exit-code mapping was accepted pre-latch.')
    Assert-ThrowsCode {
        & $module {
            param($Gates)
            Resolve-FslFlbGateId 84 $Gates
        } $duplicateExitGates
    } 'FSL-FLB-V010-SOURCE-RECOVERY' (
        'An ambiguous terminal gate mapping did not fail closed.')

    # Pure latch validation uses the exact same source rendered into the
    # observer. Prefixes 1/2 and terminal 3 are canonical UTF-8 JSONL, and
    # structural, type, identity, hash, PID, temporal, and terminal drifts
    # fail before any append is trusted.
    $latchRecord1 = [pscustomobject][ordered]@{
        schemaVersion = 1
        recordOrdinal = 1
        attemptId = 'attempt-1'
        runId = '20260729T120000Z-1234abcd'
        checkpoint = 'CP10-INSTALL-WAL-ROLLBACK-OBSERVER-EXECUTION'
        wrapperSha256 = 'A' * 64
        recoveryContractSha256 = 'B' * 64
        phase = 'LaunchCommitted'
        status = 'Pending'
        outcome = $null
        observerPid = 4242
        targetPid = $null
        exitCode = $null
        gateId = $null
        timestampUtc = '2026-07-29T12:00:00.0000000Z'
    }
    $latchRecord2 = Copy-Object $latchRecord1
    $latchRecord2.recordOrdinal = 2
    $latchRecord2.phase = 'RunAsInvoking'
    $latchRecord2.timestampUtc = '2026-07-29T12:00:01.0000000Z'
    $latchRecord3 = Copy-Object $latchRecord1
    $latchRecord3.recordOrdinal = 3
    $latchRecord3.phase = 'LaunchResult'
    $latchRecord3.status = 'Completed'
    $latchRecord3.outcome = 'Exited'
    $latchRecord3.targetPid = 4343
    $latchRecord3.exitCode = 84
    $latchRecord3.gateId = [string]$gates[0].gateId
    $latchRecord3.timestampUtc = '2026-07-29T12:00:02.0000000Z'
    $getLatchBytes = {
        param([object[]]$Records)
        $lines = @($Records | ForEach-Object {
            $_ | ConvertTo-Json -Compress -Depth 4
        })
        return ,([Text.UTF8Encoding]::new($false, $true).GetBytes(
            ($lines -join "`n") + "`n"))
    }
    foreach ($validLatchRecords in @(
        @($latchRecord1),
        @($latchRecord1, $latchRecord2),
        @($latchRecord1, $latchRecord2, $latchRecord3))) {
        $validLatchBytes = & $getLatchBytes $validLatchRecords
        $script:Cases++
        Assert-True (& $module {
            param([byte[]]$Bytes, [object[]]$Expected)
            Test-FslFlbLatchBytes $Bytes $Expected
        } $validLatchBytes $validLatchRecords) (
            'A valid canonical latch prefix/result was rejected.')
    }
    $invalidLatchSets = [Collections.Generic.List[object[]]]::new()
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2),
        (Copy-Object $latchRecord3))
    $caseRecords[1].timestampUtc = '2026-07-29T11:59:59.0000000Z'
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2))
    $caseRecords[1].attemptId = 'other-attempt'
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2))
    $caseRecords[1].wrapperSha256 = 'C' * 64
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2))
    $caseRecords[1].observerPid = 4243
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2))
    $caseRecords[1].recordOrdinal = 3
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2))
    $caseRecords[1].status = 'Completed'
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2),
        (Copy-Object $latchRecord3))
    $caseRecords[2].outcome = 'UacCancelled'
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2),
        (Copy-Object $latchRecord3))
    $caseRecords[2].exitCode = 0
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2),
        (Copy-Object $latchRecord3))
    $caseRecords[2].gateId = $null
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2),
        (Copy-Object $latchRecord3))
    $caseRecords[2].gateId = 'FSL-RAB-CG-002-WRONG-MAPPING'
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @(
        (Copy-Object $latchRecord1),
        (Copy-Object $latchRecord2),
        (Copy-Object $latchRecord3))
    $caseRecords[2].exitCode = -1
    $caseRecords[2].gateId = 'FSL-RAB-CG-001-FORBIDDEN'
    $invalidLatchSets.Add($caseRecords)
    $caseRecords = @((Copy-Object $latchRecord1))
    $caseRecords[0].observerPid = '4242'
    $invalidLatchSets.Add($caseRecords)
    foreach ($invalidLatchRecords in $invalidLatchSets) {
        $invalidLatchBytes = & $getLatchBytes $invalidLatchRecords
        $script:Cases++
        Assert-True (-not (& $module {
            param([byte[]]$Bytes, [object[]]$Expected)
            Test-FslFlbLatchBytes $Bytes $Expected
        } $invalidLatchBytes $invalidLatchRecords)) (
            'A semantic/type/cross-record latch mutation was accepted.')
    }
    $goodLatchRecords = @(
        $latchRecord1,
        $latchRecord2,
        $latchRecord3)
    $goodLatchBytes = & $getLatchBytes $goodLatchRecords
    $latchByteDrifts = @(
        (Join-Bytes @([byte[]]@(0xEF, 0xBB, 0xBF), $goodLatchBytes)),
        [Text.UTF8Encoding]::new($false, $true).GetBytes(
            ([Text.UTF8Encoding]::new($false, $true).
                GetString($goodLatchBytes)).Replace("`n", "`r`n")),
        [byte[]]$goodLatchBytes[0..($goodLatchBytes.Length - 2)])
    foreach ($latchByteDrift in $latchByteDrifts) {
        $script:Cases++
        Assert-True (-not (& $module {
            param([byte[]]$Bytes, [object[]]$Expected)
            Test-FslFlbLatchBytes $Bytes $Expected
        } $latchByteDrift $goodLatchRecords)) (
            'A BOM/CRLF/no-final-LF latch byte drift was accepted.')
    }
    $reorderedRecord = [pscustomobject][ordered]@{
        recordOrdinal = 1
        schemaVersion = 1
        attemptId = $latchRecord1.attemptId
        runId = $latchRecord1.runId
        checkpoint = $latchRecord1.checkpoint
        wrapperSha256 = $latchRecord1.wrapperSha256
        recoveryContractSha256 = $latchRecord1.recoveryContractSha256
        phase = $latchRecord1.phase
        status = $latchRecord1.status
        outcome = $null
        observerPid = 4242
        targetPid = $null
        exitCode = $null
        gateId = $null
        timestampUtc = $latchRecord1.timestampUtc
    }
    $script:Cases++
    Assert-True (-not (& $module {
        param([byte[]]$Bytes, [object[]]$Expected)
        Test-FslFlbLatchBytes $Bytes $Expected
    } (& $getLatchBytes @($reorderedRecord)) @($reorderedRecord))) (
        'A reordered canonical latch record was accepted.')

    # Exact public surface and exact slim capability model.
    $exports = @($module.ExportedFunctions.Keys)
    Assert-Equal (
        $exports -join '|') (
        'New-FslStage4FormalLauncherBundle|' +
        'Test-FslStage4FormalLauncherBundle') (
        'The module exported a capability beyond the exact two commands.')
    foreach ($propertyName in @(
        'filePath',
        'executable',
        'applicationName',
        'commandLine',
        'workingDirectory',
        'sourceRoot',
        'bundleRoot',
        'repository',
        'aclSddl')) {
        $case = Copy-Object $model
        $case | Add-Member -NotePropertyName $propertyName -NotePropertyValue 'x'
        Assert-CodeSet (
            Test-FslStage4FormalLauncherBundle -Model $case) (
            @('FSL-FLB-V001-MODEL')) (
            "Caller-supplied capability $propertyName was accepted.")
    }
    foreach ($invalidLeaf in @(
        'bad"name',
        'bad:name',
        'bad/name',
        'bad\name',
        '..',
        'NUL',
        'trailing.',
        'trailing ')) {
        $case = Copy-Object $model
        $case.rootBinding.bundleLeafName = $invalidLeaf
        Assert-CodeSet (
            Test-FslStage4FormalLauncherBundle -Model $case) (
            @('FSL-FLB-V001-MODEL')) (
            "Invalid or escaping leaf <$invalidLeaf> was accepted.")
    }
    $case = Copy-Object $model
    $case.rootBinding.PSObject.Properties.Remove('bundleLeafName')
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $case) (
        @('FSL-FLB-V001-MODEL')) (
        'A missing nested property was accepted.')
    $case = [pscustomobject][ordered]@{
        authorityProfile = $model.authorityProfile
        schemaVersion = $model.schemaVersion
        contractId = $model.contractId
        checkpoint = $model.checkpoint
        attemptId = $model.attemptId
        runId = $model.runId
        rootBinding = $model.rootBinding
        recoveryAuthority = $model.recoveryAuthority
    }
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $case) (
        @('FSL-FLB-V001-MODEL')) (
        'A reordered top-level model was accepted.')

    # Positive TestFixture generation: single quotes and spaces are legal but
    # the generated artifacts are statically ineligible for formal execution.
    $generated = New-FslStage4FormalLauncherBundle -Model $model
    Assert-Equal $generated.bundleRoot $bundleRoot (
        'The internal bundle root drifted.')
    Assert-Equal $generated.observedFiles.Count 3 (
        'The generator did not report exact-three observed files.')
    $positive = Test-FslStage4FormalLauncherBundle -Model $model
    $script:Cases++
    Assert-True $positive.isValid (
        'The positive generated TestFixture bundle did not validate: ' +
        ((@($positive.errors | ForEach-Object {
            $_.code + ':' + $_.target + ':' + $_.detail
        })) -join ', '))
    $outerPath = Join-Path $bundleRoot 'outer-launcher.ps1'
    $observerPath = Join-Path $bundleRoot 'launch-observer.ps1'
    $contractPath = Join-Path $bundleRoot 'launch-observer-contract.json'
    $outerBytes = [IO.File]::ReadAllBytes($outerPath)
    $observerBytes = [IO.File]::ReadAllBytes($observerPath)
    $contractBytes = [IO.File]::ReadAllBytes($contractPath)
    $contractRaw = [Text.UTF8Encoding]::new($false, $true).GetString(
        $contractBytes)
    $contract = $contractRaw | ConvertFrom-Json
    [uint32]$expectedCreateBreakawayFromJob = 0x01000000
    [uint32]$expectedCreateNoWindow = 0x08000000
    [uint32]$expectedCreationFlags =
        $expectedCreateBreakawayFromJob -bor $expectedCreateNoWindow
    Assert-True (
        $expectedCreationFlags -eq [uint32]0x09000000 -and
        ($expectedCreationFlags -band [uint32]0x00000200) -eq 0 -and
        @($contract.policy.nativeOuterLaunch.creationFlags).Count -eq 2 -and
        $contract.policy.nativeOuterLaunch.creationFlags[0] -is [string] -and
        [string]$contract.policy.nativeOuterLaunch.creationFlags[0] -ceq
            'CREATE_BREAKAWAY_FROM_JOB' -and
        $contract.policy.nativeOuterLaunch.creationFlags[1] -is [string] -and
        [string]$contract.policy.nativeOuterLaunch.creationFlags[1] -ceq
            'CREATE_NO_WINDOW' -and
        [string]$contract.policy.nativeOuterLaunch.numericCreationFlags -ceq
            '0x09000000' -and
        @($contract.policy.nativeOuterLaunch.creationFlags) -cnotcontains
            'CREATE_NEW_PROCESS_GROUP') (
        'The official native creation flag symbols/order/numeric OR drifted.')
    Assert-True (-not [bool]$contract.formalExecutionEligible) (
        'A TestFixture was marked formal-execution eligible.')
    Assert-Equal (
        @($contract.bindingManifest.PSObject.Properties.Name) -join '|') (
        'schemaVersion|fileOrder|outerLauncher|observer|contractName|' +
        'contractLength|contractCanonicalSha256|hashRule|recoveryWrapper|' +
        'recoveryContract|recoveryGateMapSha256|' +
        'executionStateAuthoritySha256|recoveryToolchainAuthoritySha256|' +
        'toolchainRepositorySha256|' +
        'currentAuthorityCanonicalSha256') (
        'The binding manifest nested shape drifted.')
    Assert-Equal $contract.bindingManifest.outerLauncher.name (
        'outer-launcher.ps1') 'The outer manifest name drifted.'
    Assert-Equal $contract.bindingManifest.observer.length (
        $observerBytes.Length) 'The observer manifest length drifted.'
    Assert-Equal $contract.bindingManifest.recoveryContract.name (
        'recovery-contract.json') 'The recovery manifest name drifted.'
    Assert-Equal $contract.bindingManifest.recoveryContract.length (
        (Get-Item -LiteralPath $recoveryPath).Length) (
        'The recovery manifest length drifted.')
    Assert-Equal $contract.bindingManifest.contractLength (
        $contractBytes.Length) 'The contract byte length drifted.'
    Assert-Equal (
        @($contract.policy.recoveryRunAs.PSObject.Properties.Name) -join '|') (
        'applicationName|argumentLine|verb|passThru|wait') (
        'The fixed recovery RunAs policy shape drifted.')
    $expectedRecoveryArguments = [string[]]@(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $wrapperPath)
    $expectedRecoveryLine = & $module {
        param([string[]]$Arguments)
        Join-FslFlbWindowsArgumentLine $Arguments
    } $expectedRecoveryArguments
    Assert-True (
        [string]$contract.policy.recoveryRunAs.applicationName -ceq
            [string]$recovery.futureInvocation.filePath -and
        [string]$contract.policy.recoveryRunAs.argumentLine -ceq
            $expectedRecoveryLine -and
        [string]$contract.policy.recoveryRunAs.verb -ceq 'RunAs' -and
        $contract.policy.recoveryRunAs.passThru -is [bool] -and
        [bool]$contract.policy.recoveryRunAs.passThru -and
        $contract.policy.recoveryRunAs.wait -is [bool] -and
        [bool]$contract.policy.recoveryRunAs.wait) (
        'The fixed recovery RunAs policy values drifted.')
    $zeroContract = Copy-Object $contract
    $zeroContract.bindingManifest.contractCanonicalSha256 = '0' * 64
    $zeroBytes = & $module {
        param($Value)
        Get-FslFlbBytes (ConvertTo-FslFlbCanonicalJson $Value)
    } $zeroContract
    $zeroHash = & $module {
        param($Bytes)
        Get-FslFlbSha256Bytes $Bytes
    } $zeroBytes
    Assert-Equal $zeroHash (
        $contract.bindingManifest.contractCanonicalSha256) (
        'The non-circular canonical self hash was not independently reproducible.')

    # Deterministic regeneration.
    [IO.Directory]::Delete($bundleRoot, $true)
    [void](New-FslStage4FormalLauncherBundle -Model $model)
    Assert-True (
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$outerBytes,
            [byte[]][IO.File]::ReadAllBytes($outerPath)) -and
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$observerBytes,
            [byte[]][IO.File]::ReadAllBytes($observerPath)) -and
        [Linq.Enumerable]::SequenceEqual(
            [byte[]]$contractBytes,
            [byte[]][IO.File]::ReadAllBytes($contractPath))) (
        'Identical authority did not regenerate byte-identical artifacts.')

    # Canonical UTF-8 and internally rehashed result-schema drift.
    $encodingMutations = [Collections.Generic.List[byte[]]]::new()
    $encodingMutations.Add((
        Join-Bytes @([byte[]]@(0xEF, 0xBB, 0xBF), $contractBytes)))
    $encodingMutations.Add((
        [Text.UTF8Encoding]::new($false, $true).GetBytes(
            $contractRaw.Replace("`n", "`r`n"))))
    $encodingMutations.Add([byte[]]$contractBytes[
        0..($contractBytes.Length - 2)])
    $encodingMutations.Add([byte[]]$contractBytes[
        0..([Math]::Floor($contractBytes.Length / 2))])
    foreach ($bytes in $encodingMutations) {
        [IO.File]::WriteAllBytes($contractPath, [byte[]]$bytes)
        $script:Cases++
        Assert-True (-not (
            Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
            'A BOM/CRLF/no-final-LF/truncated contract was accepted.')
        Restore-Bytes $contractPath $contractBytes
    }
    foreach ($contractMutation in @(
        {
            param($c)
            $c.policy.resultSchema.temporalFormat =
                "yyyy-MM-dd'T'HH:mm:ss.fffffffK"
        },
        {
            param($c)
            $c.policy.resultSchema.temporalRelation =
                'record1.timestampUtc >= record2.timestampUtc >= record3.timestampUtc'
        },
        {
            param($c)
            $c.policy.resultSchema.preAppendReadOnly = $false
        })) {
        $caseContract = Copy-Object $contract
        & $contractMutation $caseContract
        Write-RehashedBundleContract $contractPath $caseContract $module
        $script:Cases++
        Assert-True (-not (
            Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
            'An internally rehashed temporal/pre-append mutation was accepted.')
        Restore-Bytes $contractPath $contractBytes
    }

    # Compile the generated observer native proof/verifier definition without
    # invoking its entry point, latch, wrapper, UAC, or any child process.
    $observerSource = [Text.UTF8Encoding]::new(
        $false,
        $true).GetString($observerBytes)
    $outerSource = [Text.UTF8Encoding]::new(
        $false,
        $true).GetString($outerBytes)
    Assert-True (
        $outerSource -match
            'public const uint CREATE_BREAKAWAY_FROM_JOB=0x01000000;' -and
        $outerSource -match
            'public const uint CREATE_NO_WINDOW=0x08000000;' -and
        $outerSource -match
            'public const uint FSL_CREATION_FLAGS=' -and
        $outerSource -match
            'CREATE_BREAKAWAY_FROM_JOB\|CREATE_NO_WINDOW;' -and
        $outerSource -match
            '\[FslFormalNativeLauncher\]::FSL_CREATION_FLAGS' -and
        $outerSource -notmatch 'CREATE_NEW_PROCESS_GROUP' -and
        $observerSource -match
            "\[string\]\`$creationFlags\[0\]-cne'CREATE_BREAKAWAY_FROM_JOB'" -and
        $observerSource -match
            "\[string\]\`$creationFlags\[1\]-cne'CREATE_NO_WINDOW'" -and
        $observerSource -match "'0x09000000'" -and
        $observerSource -notmatch 'CREATE_NEW_PROCESS_GROUP') (
        'The outer/observer static native creation flag bindings drifted.')
    $nativeMatch = [regex]::Match(
        $observerSource,
        '(?s)Add-Type -TypeDefinition @"\n(?<source>.*?)\n"@ -ReferencedAssemblies')
    Assert-True $nativeMatch.Success (
        'The observer native verifier source was not fixed and extractable.')
    if (-not ('FslFormalObserverIdentity' -as [type])) {
        Add-Type `
            -TypeDefinition $nativeMatch.Groups['source'].Value `
            -ReferencedAssemblies @(
                'System.dll',
                'System.Core.dll',
                'System.Security.dll')
    }
    Assert-True ($null -ne ('FslFormalObserverIdentity' -as [type])) (
        'The generated observer native verifier did not compile.')
    $observerTokenProof =
        [FslFormalObserverIdentity]::ReadTokenProof()
    $script:Cases++
    Assert-True (
        [string]$observerTokenProof.MachineName -ceq 'FSL-STAGE4-VM' -and
        [int]$observerTokenProof.ElevationType -eq 3 -and
        [string]$observerTokenProof.CurrentAccountSid -ceq $userSid -and
        [string]$observerTokenProof.LinkedAccountSid -ceq $userSid -and
        [int]$observerTokenProof.CurrentSidType -eq 1 -and
        [int]$observerTokenProof.LinkedSidType -eq 1 -and
        [bool]$observerTokenProof.CurrentAdministratorsDenyOnly -and
        -not [bool]$observerTokenProof.CurrentAdministratorsEnabled -and
        -not [bool]$observerTokenProof.LinkedAdministratorsDenyOnly -and
        [bool]$observerTokenProof.LinkedAdministratorsEnabled -and
        [string]$observerTokenProof.CurrentAccountDomain -ceq
            'FSL-STAGE4-VM' -and
        [string]$observerTokenProof.LinkedAccountDomain -ceq
            'FSL-STAGE4-VM') (
        'The extracted observer native read-only TokenGroups proof failed.')
    $observerParsedArgv = @(
        [FslFormalObserverIdentity]::ParseWindowsCommandLine(
            '"dummy.exe" ' + $expectedRecoveryLine))
    $script:Cases++
    Assert-Equal ($observerParsedArgv -join [char]0x1F) (
        (@('dummy.exe') + $expectedRecoveryArguments) -join [char]0x1F) (
        'The independent observer argv parser did not round-trip policy argv.')
    foreach ($positiveZlib in @($storedZlib, $fixedZlib, $dynamicZlib)) {
        $script:Cases++
        Assert-True (
            [FslFormalObserverIdentity]::ValidateZlibEnvelope(
                [byte[]]$positiveZlib)) (
            'The independent observer zlib scanner rejected a valid stream.')
    }
    foreach ($badZlib in $zlibMutations) {
        $script:Cases++
        Assert-True (-not (
            [FslFormalObserverIdentity]::ValidateZlibEnvelope(
                [byte[]]$badZlib))) (
            'The independent observer zlib scanner accepted a mutation.')
    }
    $latchHelperTemplate = & $module {
        $script:FlbLatchHelperTemplate.TrimEnd("`r", "`n")
    }
    Assert-True (
        $observerSource.IndexOf(
            $latchHelperTemplate,
            [StringComparison]::Ordinal) -ge 0) (
        'The observer latch helper did not come from the single module template.')
    $moduleSource = [IO.File]::ReadAllText($modulePath)
    Assert-True (
        $moduleSource -notmatch 'CheckTokenMembership' -and
        $moduleSource -notmatch '\b1309\b' -and
        $moduleSource -match 'GetTokenInformation' -and
        $moduleSource -match 'OffsetOf' -and
        $moduleSource -match 'SizeOf' -and
        $moduleSource -match 'EqualSid') (
        'Token proof regressed from strict TOKEN_GROUPS/EqualSid semantics.')

    # Static nonexecution and full pre-latch ordering.
    foreach ($path in @($outerPath, $observerPath)) {
        $scriptText = [IO.File]::ReadAllText($path)
        Assert-True (
            $scriptText.StartsWith(
                "param()`nSet-StrictMode",
                [StringComparison]::Ordinal) -and
            $scriptText -notmatch '\$args\.Count-ne2' -and
            $scriptText -match '\$PSBoundParameters\.Count') (
            "$([IO.Path]::GetFileName($path)) did not freeze bound/unbound input.")
        $tokens = $null
        $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            $path,
            [ref]$tokens,
            [ref]$parseErrors)
        Assert-Equal @($parseErrors).Count 0 (
            "$([IO.Path]::GetFileName($path)) did not parse in Windows PowerShell.")
        if ($path -ceq $observerPath) {
            $runAs = @($ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -ceq 'Start-Process'
            }, $true))
            $gate = @($ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -ceq 'Assert-FormalPreLatch'
            }, $true))
            $tokenProofCall = @($ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -ceq 'Assert-FormalTokenProof'
            }, $true))
            $writes = @($ast.FindAll({
                param($node)
                $node -is
                    [Management.Automation.Language.InvokeMemberExpressionAst] -and
                $node.Member.Value -ceq 'new' -and
                $node.Expression.Extent.Text -match 'FileStream'
            }, $true))
            Assert-True (
                $runAs.Count -eq 1 -and
                $gate.Count -eq 1 -and
                $tokenProofCall.Count -eq 1 -and
                $writes.Count -ge 1 -and
                $tokenProofCall[0].Extent.StartOffset -lt
                    $writes[0].Extent.StartOffset -and
                $tokenProofCall[0].Extent.StartOffset -lt
                    $runAs[0].Extent.StartOffset -and
                $gate[0].Extent.StartOffset -lt $writes[0].Extent.StartOffset -and
                $gate[0].Extent.StartOffset -lt $runAs[0].Extent.StartOffset) (
                'The complete pre-latch gate does not precede writes and RunAs.')
            $observerText = [IO.File]::ReadAllText($observerPath)
            $record1Readback = $observerText.IndexOf(
                'Assert-Latch $contract.policy.latch.path @($record1)',
                [StringComparison]::Ordinal)
            $record2Readback = $observerText.IndexOf(
                'Assert-Latch $contract.policy.latch.path @($record1,$record2)',
                [StringComparison]::Ordinal)
            $runAsOffset = $runAs[0].Extent.StartOffset
            $record2Readbacks = [regex]::Matches(
                $observerText,
                [regex]::Escape(
                    'Assert-Latch $contract.policy.latch.path @($record1,$record2)'))
            $record3Write = $observerText.IndexOf(
                'try{Write-Record $stream $record3}',
                [StringComparison]::Ordinal)
            $record3Readback = $observerText.IndexOf(
                'Assert-Latch $contract.policy.latch.path @($record1,$record2,$record3)',
                [StringComparison]::Ordinal)
            $successExit = $observerText.LastIndexOf(
                '  exit 0',
                [StringComparison]::Ordinal)
            Assert-True (
                $runAs[0].Extent.Text -match
                    '-ArgumentList\s+\$fixedRecoveryArgumentLine' -and
                $runAs[0].Extent.Text -notmatch '-ArgumentList\s+@\(' -and
                $record1Readback -gt 0 -and
                $record1Readback -lt $runAsOffset -and
                $record2Readback -gt $record1Readback -and
                $record2Readback -lt $runAsOffset -and
                $record2Readbacks.Count -eq 2 -and
                $record2Readbacks[1].Index -gt $runAsOffset -and
                $record3Write -gt $record2Readbacks[1].Index -and
                $record3Readback -gt $record3Write -and
                $record3Readback -lt $successExit) (
                'RunAs argv or r1/r2/pre-r3/r3 exact latch readback order drifted.')
            Assert-True (
                $observerText.IndexOf(
                    'Test fixtures are never formal-execution eligible.',
                    [StringComparison]::Ordinal) -lt
                $observerText.IndexOf(
                    "    Initialize-NativeIdentity`n",
                    [StringComparison]::Ordinal)) (
                'TestFixture rejection did not precede native proof activity.')
        }
    }
    $moduleAst = [Management.Automation.Language.Parser]::ParseFile(
        $modulePath,
        [ref]$null,
        [ref]$null)
    foreach ($name in @(
        'New-FslStage4FormalLauncherBundle',
        'Test-FslStage4FormalLauncherBundle')) {
        $function = @($moduleAst.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq $name
        }, $true))[0]
        Assert-True (
            @($function.Body.FindAll({
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -in @(
                    'Start-Process',
                    'Invoke-Expression',
                    'git')
            }, $true)).Count -eq 0) (
            "Public preparation command $name contains a process/dynamic launch.")
    }

    # Exactly 22 single-leaf mutations must map one-to-one to V009-01..22.
    $expectedPredicates = & $module { Get-FslFlbPredicateTexts }
    $mutations = @(
        '[string]$contract.contractId -cne $fixedContractId',
        '[string]$contract.attemptId -cne $fixedAttemptId',
        '[string]$contract.authority.identity.userSid -ceq $fixedAttemptId',
        '[int]$contract.authority.identity.sessionId -ne $fixedSessionId',
        '[string]$contract.bindingManifest.observer.sha256 -ceq (Get-Hash $fixedLauncherPath)',
        '[string]$contract.bindingManifest.outerLauncher.sha256 -ceq (Get-Hash $fixedObserverPath)',
        '[string]$contract.policy.nativeOuterLaunch.primitive -ceq ''CreateProcessA''',
        '[string]$contract.policy.nativeOuterLaunch.applicationName -ceq $fixedWorkingDirectory',
        '[string]$contract.policy.nativeOuterLaunch.commandLine -ceq $fixedPowerShell',
        '[string]$contract.policy.nativeOuterLaunch.workingDirectory -ceq $fixedPowerShell',
        '[string]$contract.policy.nativeOuterLaunch.numericCreationFlags -ceq ''0x08000000''',
        '[bool]$contract.policy.nativeOuterLaunch.inheritHandles',
        '-not [bool]$contract.policy.nativeOuterLaunch.currentUser',
        '-not [bool]$contract.policy.nativeOuterLaunch.requireNonElevated',
        '-not [bool]$contract.policy.nativeOuterLaunch.requireInteractive',
        '[string]$contract.policy.nativeOuterLaunch.requiredUserSid -ceq $fixedAttemptId',
        '[int]$contract.policy.nativeOuterLaunch.requiredSessionId -eq ($fixedSessionId + 1)',
        '[string]$contract.policy.nativeOuterLaunch.windowStyle -ceq ''Normal''',
        '-not [bool]$contract.policy.nativeOuterLaunch.noWindow',
        '[bool]$contract.policy.nativeOuterLaunch.wait',
        '[bool]$contract.policy.nativeOuterLaunch.fallbackAllowed',
        'Test-ExactCreationFlags @(''CREATE_NO_WINDOW'',''CREATE_BREAKAWAY_FROM_JOB'')')
    Assert-Equal $expectedPredicates.Count 22 (
        'The canonical predicate source did not contain exactly 22 leaves.')
    Assert-Equal $mutations.Count 22 (
        'The mutation test matrix did not contain exactly 22 cases.')
    for ($index = 0; $index -lt 22; $index++) {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($outerBytes)
        $needle = '(' + [string]$expectedPredicates[$index] + ')'
        $replacement = '(' + [string]$mutations[$index] + ')'
        Assert-Equal ([regex]::Matches(
            $text,
            [regex]::Escape($needle)).Count) 1 (
            "Predicate $($index + 1) was not an exact single source leaf.")
        Write-Utf8 $outerPath ($text.Replace($needle, $replacement))
        $result = Test-FslStage4FormalLauncherBundle -Model $model
        Assert-CodeSet $result (
            @('FSL-FLB-V009-PREDICATE-{0:D2}' -f ($index + 1))) (
            "Predicate $($index + 1) did not return its exact stable code.")
        Restore-Bytes $outerPath $outerBytes
    }

    # Missing, duplicate, reordered, and extra leaves aggregate to SET.
    $canonicalOuter = [Text.UTF8Encoding]::new(
        $false,
        $true).GetString($outerBytes)
    $line1 = '    (' + $expectedPredicates[0] + ") -and`n"
    $line2 = '    (' + $expectedPredicates[1] + ") -and`n"
    $setMutations = @(
        $canonicalOuter.Replace($line1, ''),
        $canonicalOuter.Replace($line1, $line1 + $line1),
        $canonicalOuter.Replace($line1 + $line2, $line2 + $line1),
        $canonicalOuter.Replace(
            '    (' + $expectedPredicates[21] + ")`n",
            '    (' + $expectedPredicates[21] + ") -and`n    (`$true)`n"))
    foreach ($text in $setMutations) {
        Write-Utf8 $outerPath $text
        Assert-CodeSet (
            Test-FslStage4FormalLauncherBundle -Model $model) (
            @('FSL-FLB-V009-PREDICATE-SET')) (
            'A predicate set mutation did not aggregate to exact V009-SET.')
        Restore-Bytes $outerPath $outerBytes
    }
    $creationFlagLeaf = '(' + [string]$expectedPredicates[21] + ')'
    foreach ($creationFlagMutation in @(
        'Test-ExactCreationFlags @(''CREATE_BREAKAWAY_FROM_JOB'')',
        'Test-ExactCreationFlags @(''CREATE_BREAKAWAY_FROM_JOB'',''CREATE_BREAKAWAY_FROM_JOB'')',
        'Test-ExactCreationFlags @(''CREATE_NO_WINDOW'',''CREATE_BREAKAWAY_FROM_JOB'')',
        'Test-ExactCreationFlags @(''CREATE_BREAKAWAY_FROM_JOB'',''CREATE_NO_WINDOW'',''EXTRA'')')) {
        Write-Utf8 $outerPath (
            $canonicalOuter.Replace(
                $creationFlagLeaf,
                '(' + $creationFlagMutation + ')'))
        Assert-CodeSet (
            Test-FslStage4FormalLauncherBundle -Model $model) (
            @('FSL-FLB-V009-PREDICATE-22')) (
            'A missing/duplicate/reordered/extra creation flag leaf did not ' +
            'map to exact ordinal 22.')
        Restore-Bytes $outerPath $outerBytes
    }

    # Schema-3 recovery authority drift is rejected even when the public hash
    # is recomputed by the caller.
    $recoveryOriginal = [IO.File]::ReadAllBytes($recoveryPath)
    $recoveryOriginalSddl = Get-Sddl $recoveryPath $false
    $schema3Mutations = @(
        { param($r) $r.schemaVersion = 2 },
        { param($r) $r.authorityProfile = 'Other' },
        { param($r) $r.contractId = 'OTHER-CONTRACT' },
        { param($r) $r.checkpoint = 'OTHER-CHECKPOINT' },
        { param($r) $r.runId = '20260729T180001Z-deadbeef' },
        { param($r) $r.executionStateAuthority.gitCommit = 'd' * 40 },
        { param($r) $r.executionStateAuthority.gitTree = 'e' * 40 },
        { param($r) $r.recoveryToolchainAuthority.gitCommit = 'a' * 40 },
        { param($r) $r.recoveryToolchainAuthority.gitTree = 'b' * 40 },
        { param($r) $r.operatorIdentity.userSid = 'S-1-5-18' },
        { param($r) $r.recoverySource.wrapper.sha256 = 'A' * 64 },
        { param($r) $r.recoverySource.contract.schemaVersion = 4 },
        { param($r) $r.transaction.walPrefixRecordCount = '4' },
        {
            param($r)
            $r.transaction.expectedPost.PSObject.Properties.Remove(
                'addedPhases')
        },
        { param($r) $r.canonicalEvidence.files[0].sha256 = 'B' * 64 },
        {
            param($r)
            $r.canonicalEvidence.predecessorFiles[0].sha256 = 'C' * 64
        },
        { param($r) $r.externalAnchors.files[0].sha256 = 'D' * 64 },
        { param($r) $r.release.files[0].sha256 = 'E' * 64 },
        { param($r) $r.systemPrestate.programDataAbsent = $false },
        { param($r) $r.futureInvocation.passThru = 1 },
        {
            param($r)
            $r.contractStageGates = @($r.contractStageGates[0..54])
        },
        {
            param($r)
            $temporary = $r.contractStageGates[0]
            $r.contractStageGates[0] = $r.contractStageGates[1]
            $r.contractStageGates[1] = $temporary
        },
        {
            param($r)
            $r.contractStageGates[1].gateId =
                $r.contractStageGates[0].gateId
        },
        { param($r) $r.contractStageGates[0].exitCode = '84' })
    foreach ($schema3Mutation in $schema3Mutations) {
        $caseRecovery = Copy-Object $recovery
        & $schema3Mutation $caseRecovery
        Set-RecoveryContract $recoveryPath $caseRecovery $userSid $model
        Assert-CodeSet (
            Test-FslStage4FormalLauncherBundle -Model $model) (
            @('FSL-FLB-V010-SOURCE-RECOVERY')) (
            'A schema-3 recovery authority mutation was accepted.')
        Restore-Bytes $recoveryPath $recoveryOriginal
        & $module {
            param($Path, $Sddl)
            Set-FslFlbSddl $Path $Sddl $false
        } $recoveryPath $recoveryOriginalSddl
        $model.recoveryAuthority.contractSha256 = Get-Sha $recoveryPath
    }

    # Bundle bytes, exact set, ACL, junction, hard-link, and latch mutations.
    Write-Utf8 $observerPath (
        [Text.UTF8Encoding]::new($false, $true).GetString($observerBytes) +
        "# drift`n")
    Assert-True (-not (
        Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
        'Observer byte drift was accepted.')
    Restore-Bytes $observerPath $observerBytes
    $extraPath = Join-Path $bundleRoot 'extra.ps1'
    Write-Utf8 $extraPath "extra`n"
    Assert-True (-not (
        Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
        'An extra bundle file was accepted.')
    [IO.File]::Delete($extraPath)
    $latchPath = Join-Path $bundleRoot 'launch-attempt.jsonl'
    Write-Utf8 $latchPath "{}`n"
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V003-FILESET', 'FSL-FLB-V012-LATCH')) (
        'An existing latch did not fail closed with exact errors.')
    [IO.File]::Delete($latchPath)
    $originalAcl = Get-Sddl $observerPath $false
    [IO.File]::Delete($observerPath)
    $script:Cases++
    Assert-True (-not (
        Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
        'A missing required bundle file was accepted.')
    Restore-Bytes $observerPath $observerBytes
    & $module {
        param($Path, $Sddl)
        Set-FslFlbSddl $Path $Sddl $false
    } $observerPath $originalAcl
    $caseIntermediate = Join-Path $bundleRoot 'observer-case-intermediate.tmp'
    $caseAlias = Join-Path $bundleRoot 'LAUNCH-OBSERVER.ps1'
    [IO.File]::Move($observerPath, $caseIntermediate)
    [IO.File]::Move($caseIntermediate, $caseAlias)
    $script:Cases++
    Assert-True (-not (
        Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
        'A case-aliased required bundle file was accepted.')
    [IO.File]::Move($caseAlias, $caseIntermediate)
    [IO.File]::Move($caseIntermediate, $observerPath)

    [IO.File]::Delete($observerPath)
    New-Item `
        -ItemType HardLink `
        -Path $observerPath `
        -Target $outerPath | Out-Null
    Assert-True (-not (
        Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
        'A bundle hard-link substitution was accepted.')
    [IO.File]::Delete($observerPath)
    Restore-Bytes $observerPath $observerBytes
    & $module {
        param($Path, $Sddl)
        Set-FslFlbSddl $Path $Sddl $false
    } $observerPath $originalAcl

    $bundleBacking = Join-Path $fixtureRoot 'bundle-junction-backing'
    [IO.Directory]::Move($bundleRoot, $bundleBacking)
    New-Item `
        -ItemType Junction `
        -Path $bundleRoot `
        -Target $bundleBacking | Out-Null
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V002-ROOT')) (
        'A bundle-root junction substitution was accepted.')
    [IO.Directory]::Delete($bundleRoot, $false)
    [IO.Directory]::Move($bundleBacking, $bundleRoot)

    $security = [IO.File]::GetAccessControl($observerPath)
    $security.SetAccessRuleProtection($false, $true)
    [IO.File]::SetAccessControl($observerPath, $security)
    Assert-True (-not (
        Test-FslStage4FormalLauncherBundle -Model $model).isValid) (
        'A bundle-file ACL mutation was accepted.')
    & $module {
        param($Path, $Sddl)
        Set-FslFlbSddl $Path $Sddl $false
    } $observerPath $originalAcl

    $sourceFileSddl = Get-Sddl $wrapperPath $false
    $sourceFileSecurity = [IO.File]::GetAccessControl($wrapperPath)
    $sourceFileSecurity.SetAccessRuleProtection($false, $true)
    [IO.File]::SetAccessControl($wrapperPath, $sourceFileSecurity)
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V013-ACL')) (
        'A source-file ACL mutation was accepted.')
    & $module {
        param($Path, $Sddl)
        Set-FslFlbSddl $Path $Sddl $false
    } $wrapperPath $sourceFileSddl
    $sourceRootSddl = Get-Sddl $sourceRoot $true
    $sourceRootSecurity = [IO.Directory]::GetAccessControl($sourceRoot)
    $sourceRootSecurity.SetAccessRuleProtection($false, $true)
    [IO.Directory]::SetAccessControl($sourceRoot, $sourceRootSecurity)
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V013-ACL')) (
        'A source-root ACL mutation was accepted.')
    & $module {
        param($Path, $Sddl)
        Set-FslFlbSddl $Path $Sddl $true
    } $sourceRoot $sourceRootSddl
    $sourceHardLink = Join-Path $fixtureRoot 'source-wrapper-hardlink.ps1'
    New-Item -ItemType HardLink -Path $sourceHardLink -Target $wrapperPath |
        Out-Null
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V013-ACL')) (
        'A hard-linked source authority file was accepted.')
    [IO.File]::Delete($sourceHardLink)
    $sourceBacking = Join-Path $fixtureRoot 'source-junction-backing'
    [IO.Directory]::Move($sourceRoot, $sourceBacking)
    New-Item -ItemType Junction -Path $sourceRoot -Target $sourceBacking |
        Out-Null
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V002-ROOT')) (
        'A source-root junction/reparse substitution was accepted.')
    [IO.Directory]::Delete($sourceRoot, $false)
    [IO.Directory]::Move($sourceBacking, $sourceRoot)

    # Canonical evidence and release drift are re-read from current objects.
    $evidenceOriginal = [IO.File]::ReadAllBytes($evidencePaths[0])
    $hardLinkTarget = Join-Path $fixtureRoot 'evidence-hardlink-target.txt'
    New-Item `
        -ItemType HardLink `
        -Path $hardLinkTarget `
        -Target $evidencePaths[0] | Out-Null
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V010-SOURCE-RECOVERY')) (
        'A canonical evidence hard link was accepted.')
    [IO.File]::Delete($hardLinkTarget)
    Write-Utf8 $evidencePaths[0] "drift`n"
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V010-SOURCE-RECOVERY')) (
        'Canonical evidence drift was accepted.')
    Restore-Bytes $evidencePaths[0] $evidenceOriginal
    $releaseOriginal = [IO.File]::ReadAllBytes($releasePaths[0])
    Write-Utf8 $releasePaths[0] "drift`n"
    Assert-CodeSet (
        Test-FslStage4FormalLauncherBundle -Model $model) (
        @('FSL-FLB-V010-SOURCE-RECOVERY')) (
        'Frozen release drift was accepted.')
    Restore-Bytes $releasePaths[0] $releaseOriginal

    # The schema-v3 dual-authority seam is intentionally exercised here as
    # static preparation behavior. The dedicated recovery-authority suite
    # owns its full schema, packed-object, mutation, and ACL matrices.
    $moduleText = [IO.File]::ReadAllText(
        $modulePath,
        [Text.UTF8Encoding]::new($false, $true))
    $observerText = [IO.File]::ReadAllText(
        $observerPath,
        [Text.UTF8Encoding]::new($false, $true))
    $helper = @($moduleAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq 'Test-FslFlbRecoveryAuthorityV3'
    }, $true))
    $helperCommands = @($helper[0].Body.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true))
    $seamChecks = @(
        { $helper.Count -eq 1 },
        { @($helperCommands | Where-Object {
                    $_.GetCommandName() -ceq
                        'Test-FslStage4RecoveryAuthorityBundle'
                }).Count -eq 1 },
        { @($helperCommands | Where-Object {
                    $_.GetCommandName() -ceq
                        'New-FslStage4RecoveryAuthorityBundle'
                }).Count -eq 0 },
        { $moduleText -match
            'FolderSessionLock\.Stage4\.RecoveryAuthorityBundle\.psm1' },
        { $moduleText -match
            'CP10-TRACKED-DUAL-AUTHORITY-RECOVERY-BUNDLE-GENERATOR-VALIDATOR' },
        { $moduleText -match
            'executionStateAuthoritySha256' },
        { $moduleText -match
            'recoveryToolchainAuthoritySha256' },
        { $moduleText -match 'toolchainRepositorySha256' },
        { $moduleText -match 'recoveryGateMapSha256' },
        { $moduleText -match '\$Gates\.Count -ne 56' },
        { $moduleText -match "'FSL-RAB-CG-'" },
        { $observerText -match
            '\$fixedRecoveryValidatorPath\s*=' },
        { $observerText -match
            'Test-FslStage4RecoveryAuthorityBundle -Model \$recoveryModel' },
        { $observerText -match
            '\$gates=@\(\$opaque\.gates\);\$gatePrefix=''FSL-RAB-CG-'';\$gateCount=56' },
        { $observerText -match
            'Opaque recovery bindings drifted\.' },
        { $observerText -match
            'Dual recovery authority drifted\.' },
        { $observerText -match
            'executionStateAuthoritySha256' },
        { $observerText -match
            'recoveryToolchainAuthoritySha256' },
        { $observerText -match
            'toolchainRepositorySha256' },
        { $observerText -match
            'recoveryGateMapSha256' },
        { $contract.bindingManifest.PSObject.Properties.Name -contains
            'executionStateAuthoritySha256' },
        { $contract.bindingManifest.PSObject.Properties.Name -contains
            'recoveryToolchainAuthoritySha256' },
        { $contract.bindingManifest.PSObject.Properties.Name -contains
            'toolchainRepositorySha256' },
        { @($module.ExportedFunctions.Keys | Sort-Object) -join '|' -ceq
            'New-FslStage4FormalLauncherBundle|Test-FslStage4FormalLauncherBundle' })
    $additionalSeamChecks = @(
        { $moduleAst.EndBlock.Extent.Text.Length -gt 0 },
        { @($module.ExportedFunctions.Keys).Count -eq 2 },
        { Test-Path -LiteralPath (
                Join-Path $projectRoot (
                    'eng\stage4\FolderSessionLock.Stage4.RecoveryAuthorityBundle.psm1')) },
        { @($helperCommands | Where-Object {
                    $_.GetCommandName() -ceq 'Import-Module'
                }).Count -eq 1 },
        { $observerText -notmatch
            'New-FslStage4RecoveryAuthorityBundle' },
        { $observerText -match
            'schemaVersion=1;authorityProfile=\$fixedRecoveryAuthorityProfile' },
        { $observerText -match
            'sourceLeafName=\$fixedRecoverySourceLeaf' },
        { $observerText -match
            '\[string\]\$opaque\.executionGitCommit-ceq\s+\[string\]\$opaque\.recoveryGitCommit' })
    for ($index = 0; $index -lt 24; $index++) {
        $script:Cases++
        Assert-True ([bool](& $seamChecks[$index])) (
            "Schema-v3 Formal seam case $index failed.")
        if ($index -lt 8) {
            Assert-True ([bool](& $additionalSeamChecks[$index])) (
                "Schema-v3 Formal seam additional assertion $index failed.")
        }
    }

    $final = Test-FslStage4FormalLauncherBundle -Model $model
    $script:Cases++
    Assert-True $final.isValid (
        'The fixture did not return to a valid final state: ' +
        (@($final.errors | ForEach-Object {
            $_.code + ':' + $_.target + ':' + $_.detail
        }) -join '|'))
    Assert-True (-not (Test-Path -LiteralPath $latchPath)) (
        'Preparation or validation created the one-shot latch.')

    Write-Output (
        "STAGE4_FORMAL_LAUNCHER_BUNDLE_PASS Cases=$script:Cases " +
        "Assertions=$script:Assertions")
}
finally {
    if ($null -ne $fixtureRoot -and
        (Test-Path -LiteralPath $fixtureRoot -PathType Container)) {
        Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force -File |
            ForEach-Object { $_.IsReadOnly = $false }
        [IO.Directory]::Delete($fixtureRoot, $true)
    }
    Remove-Module $module -Force -ErrorAction SilentlyContinue
}
