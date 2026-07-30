Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RabModelNames = @(
    'schemaVersion',
    'authorityProfile',
    'contractId',
    'checkpoint',
    'runId',
    'rootBinding')
$script:RabRootBindingNames = @('fixtureId', 'sourceLeafName')
$script:RabContractNames = @(
    'schemaVersion',
    'authorityProfile',
    'contractId',
    'checkpoint',
    'runId',
    'executionStateAuthority',
    'recoveryToolchainAuthority',
    'operatorIdentity',
    'recoverySource',
    'transaction',
    'canonicalEvidence',
    'externalAnchors',
    'release',
    'systemPrestate',
    'contractStageGates',
    'futureInvocation',
    'allowedWrites',
    'forbiddenActions',
    'bindingManifest')
$script:RabProfiles = @('Formal', 'TestFixture')
$script:RabSourceNames = @(
    'elevated-reconcile.ps1',
    'recovery-contract.json')
$script:RabExecutionEvidenceNames = @(
    'stage4-state.json',
    'stage4-journal.jsonl',
    'install-wal.jsonl')
$script:RabCanonicalEvidenceNames = @(
    'build-results.txt',
    'commands.txt',
    'prestate.json',
    'signature-verification.txt',
    'stage4-anchor.json')
$script:RabEvidenceNames =
    @($script:RabExecutionEvidenceNames) +
    @($script:RabCanonicalEvidenceNames)
$script:RabPredecessorNames = @(
    'elevated-reconcile.ps1',
    'recovery-contract.json')
$script:RabFixedToolchainFiles = @(
    'eng/stage4/FolderSessionLock.Stage4.psm1',
    'eng/stage4/FolderSessionLock.Stage4.Native.cs',
    'eng/stage4/Invoke-Stage4.ps1',
    'eng/stage4/FolderSessionLock.Stage4.FormalLauncherBundle.psm1',
    'tests/FolderSessionLock.App.Tests/Stage4/Stage4FormalLauncherBundle.Tests.ps1')
$script:RabRunIdPattern = '^\d{8}T\d{6}Z-[0-9a-f]{8}$'
$script:RabGitPattern = '^[0-9a-f]{40}$'
$script:RabShaPattern = '^[0-9A-F]{64}$'
$script:RabZeros = '0' * 64
$script:RabSelfHashRule =
    'SHA256(canonical UTF-8 bytes after replacing only bindingManifest.contractCanonicalSha256 with 64 ASCII zeroes)'
$script:RabAllowedWrites = @(
    'Append exactly WAL records 5 through 7 using the existing Stage 4 reconciler.',
    'Rotate only the two external anchor slots to generations 14 and 13.',
    'Delete only the already-bound empty FolderSessionLock installation directory.')
$script:RabForbiddenActions = @(
    'No second Install, retry, fallback, relaunch, or alternate recovery path.',
    'No caller-supplied path, executable, argument, command, ACL, Git identity, evidence, anchor, or release binding.',
    'No manual WAL, state, journal, evidence, anchor, release, service, registry, certificate, account, ACL, VMware, restart, or logout mutation.',
    'No copied reconciler, controller invocation, product executable invocation, dynamic command, Invoke-Expression, or shell fallback.')

function Stop-FslRab {
    param(
        [string]$Code,
        [string]$Message,
        [AllowNull()][Exception]$Inner)
    $exception = if ($null -eq $Inner) {
        [InvalidOperationException]::new($Message)
    }
    else {
        [InvalidOperationException]::new($Message, $Inner)
    }
    $exception.Data['FslRecoveryAuthorityBundleCode'] = $Code
    throw $exception
}

function New-FslRabError {
    param([string]$Code, [string]$Target, [string]$Detail)
    return [pscustomobject][ordered]@{
        code = $Code
        target = $Target
        detail = $Detail
    }
}

function Get-FslRabNames {
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return @() }
    return @($Value.PSObject.Properties | ForEach-Object Name)
}

function Test-FslRabNames {
    param([AllowNull()]$Value, [string[]]$Expected)
    if ($null -eq $Value) { return $false }
    return ((Get-FslRabNames $Value) -join [char]0) -ceq
        ($Expected -join [char]0)
}

function Assert-FslRabModel {
    param([psobject]$Model)
    if (-not (Test-FslRabNames $Model $script:RabModelNames) -or
        $Model.schemaVersion -isnot [int] -or
        [int]$Model.schemaVersion -ne 1 -or
        $Model.authorityProfile -isnot [string] -or
        [string]$Model.authorityProfile -cnotin $script:RabProfiles -or
        $Model.contractId -isnot [string] -or
        [string]::IsNullOrWhiteSpace([string]$Model.contractId) -or
        $Model.checkpoint -isnot [string] -or
        [string]$Model.checkpoint -cne
            'CP10-TRACKED-DUAL-AUTHORITY-RECOVERY-BUNDLE-GENERATOR-VALIDATOR' -or
        $Model.runId -isnot [string] -or
        [string]$Model.runId -cnotmatch $script:RabRunIdPattern -or
        -not (Test-FslRabNames $Model.rootBinding $script:RabRootBindingNames) -or
        $Model.rootBinding.sourceLeafName -isnot [string]) {
        Stop-FslRab 'FSL-RAB-V001-MODEL' (
            'The public model shape, order, case, type, or fixed values are invalid.') $null
    }
    $leaf = [string]$Model.rootBinding.sourceLeafName
    if ([string]::IsNullOrWhiteSpace($leaf) -or
        $leaf -in @('.', '..') -or
        $leaf.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $leaf.Contains('\') -or
        $leaf.Contains('/')) {
        Stop-FslRab 'FSL-RAB-V001-MODEL' (
            'The source leaf name is not one literal Windows leaf.') $null
    }
    if ([string]$Model.authorityProfile -ceq 'Formal') {
        if ($null -ne $Model.rootBinding.fixtureId) {
            Stop-FslRab 'FSL-RAB-V001-MODEL' (
                'Formal rootBinding.fixtureId must be null.') $null
        }
    }
    else {
        if ($Model.rootBinding.fixtureId -isnot [string]) {
            Stop-FslRab 'FSL-RAB-V001-MODEL' (
                'TestFixture rootBinding.fixtureId must be a canonical Guid D string.') $null
        }
        $parsed = [Guid]::Empty
        $fixture = [string]$Model.rootBinding.fixtureId
        if (-not [Guid]::TryParseExact($fixture, 'D', [ref]$parsed) -or
            $parsed.ToString('D') -cne $fixture) {
            Stop-FslRab 'FSL-RAB-V001-MODEL' (
                'TestFixture rootBinding.fixtureId must be lowercase canonical Guid D.') $null
        }
    }
}

function Get-FslRabRoots {
    param([psobject]$Model)
    if ([string]$Model.authorityProfile -ceq 'Formal') {
        $base = Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData)) (
            'FolderSessionLock\Stage4\Recovery\' + [string]$Model.runId)
        $project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
        $evidence = Join-Path $project (
            'docs\evidence\stage-4\' + [string]$Model.runId)
        $authority = $evidence
        $repository = $project
        $predecessor = Join-Path $base 'install-wal-rollback-1'
        $anchors = Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::LocalApplicationData)) (
            'FolderSessionLock\Stage4\Anchors\' + [string]$Model.runId)
        $release = $null
        $install = Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::ProgramFiles)) 'FolderSessionLock'
        $programData = Join-Path (
            [Environment]::GetFolderPath(
                [Environment+SpecialFolder]::CommonApplicationData)) 'FolderSessionLock'
    }
    else {
        $base = Join-Path ([IO.Path]::GetTempPath()) (
            'FolderSessionLock.Tests\' + [string]$Model.rootBinding.fixtureId)
        $authority = Join-Path $base 'recovery-authority-fixture'
        $repository = Join-Path $authority 'repository'
        $evidence = Join-Path $authority 'execution-state'
        $predecessor = Join-Path $authority 'install-wal-rollback-1'
        $anchors = Join-Path $authority 'external-anchors'
        $release = Join-Path $authority 'frozen-release'
        $install = Join-Path $authority 'install-prestate'
        $programData = Join-Path $authority 'program-data-absent'
    }
    $base = [IO.Path]::GetFullPath($base).TrimEnd('\')
    $source = [IO.Path]::GetFullPath((
        Join-Path $base ([string]$Model.rootBinding.sourceLeafName))).TrimEnd('\')
    if (-not $source.StartsWith(
            $base + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
        Stop-FslRab 'FSL-RAB-V002-ROOT' (
            'The resolved source root escaped its fixed authority base.') $null
    }
    return [pscustomobject][ordered]@{
        baseRoot = $base
        sourceRoot = $source
        authorityRoot = [IO.Path]::GetFullPath($authority).TrimEnd('\')
        repositoryRoot = [IO.Path]::GetFullPath($repository).TrimEnd('\')
        evidenceRoot = [IO.Path]::GetFullPath($evidence).TrimEnd('\')
        predecessorRoot = [IO.Path]::GetFullPath($predecessor).TrimEnd('\')
        anchorRoot = [IO.Path]::GetFullPath($anchors).TrimEnd('\')
        releaseRoot = if ($null -eq $release) {
            $null
        }
        else { [IO.Path]::GetFullPath($release).TrimEnd('\') }
        installDirectory = [IO.Path]::GetFullPath($install).TrimEnd('\')
        programDataRoot = [IO.Path]::GetFullPath($programData).TrimEnd('\')
    }
}

function Get-FslRabBytes {
    param([string]$Text)
    return [Text.UTF8Encoding]::new($false, $true).GetBytes($Text)
}

function Get-FslRabSha256Bytes {
    param([byte[]]$Bytes)
    $hash = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $hash.ComputeHash($Bytes)).Replace('-', '')
    }
    finally { $hash.Dispose() }
}

function Get-FslRabSha256 {
    param([string]$Path)
    return Get-FslRabSha256Bytes ([IO.File]::ReadAllBytes($Path))
}

function ConvertTo-FslRabCanonicalJson {
    param($Value)
    $text = $Value | ConvertTo-Json -Depth 64
    return $text.Replace("`r`n", "`n").TrimEnd("`r", "`n") + "`n"
}

function Get-FslRabObjectHash {
    param($Value)
    return Get-FslRabSha256Bytes (
        Get-FslRabBytes (ConvertTo-FslRabCanonicalJson $Value))
}

function Get-FslRabFileRecord {
    param([string]$Path)
    return [pscustomobject][ordered]@{
        path = [IO.Path]::GetFullPath($Path)
        length = [int64](Get-Item -LiteralPath $Path).Length
        sha256 = Get-FslRabSha256 $Path
    }
}

function Get-FslRabExactFileRecords {
    param([string]$Root, [int]$Count, [string]$Code)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        Stop-FslRab $Code 'A required exact-set root is absent.' $null
    }
    $items = @(Get-ChildItem -LiteralPath $Root -Force)
    if ($items.Count -ne $Count -or @($items | Where-Object PSIsContainer).Count -ne 0) {
        Stop-FslRab $Code (
            "A required root is not an exact-$Count ordinary-file set.") $null
    }
    return @($items | Sort-Object Name | ForEach-Object {
        Get-FslRabFileRecord $_.FullName
    })
}

function Get-FslRabExactNamedFileRecords {
    param(
        [string]$Root,
        [string[]]$Names,
        [string]$Code)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        Stop-FslRab $Code 'A required exact-set root is absent.' $null
    }
    $items = @(Get-ChildItem -LiteralPath $Root -Force)
    $expected = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($name in $Names) { [void]$expected.Add($name) }
    if ($items.Count -ne $Names.Count -or
        @($items | Where-Object {
            $_.PSIsContainer -or
            ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not $expected.Contains($_.Name)
        }).Count -ne 0) {
        Stop-FslRab $Code (
            'A required root is not its exact named ordinary-file set.') $null
    }
    return @($Names | ForEach-Object {
        Get-FslRabFileRecord (Join-Path $Root $_)
    })
}

function Read-FslRabUInt32Be {
    param([byte[]]$Bytes, [int]$Offset)
    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git integer escaped its object bounds.') $null
    }
    return [uint32](
        ([uint32]$Bytes[$Offset] -shl 24) -bor
        ([uint32]$Bytes[$Offset + 1] -shl 16) -bor
        ([uint32]$Bytes[$Offset + 2] -shl 8) -bor
        [uint32]$Bytes[$Offset + 3])
}

function Read-FslRabUInt64Be {
    param([byte[]]$Bytes, [int]$Offset)
    [uint64]$high = Read-FslRabUInt32Be $Bytes $Offset
    [uint64]$low = Read-FslRabUInt32Be $Bytes ($Offset + 4)
    return [uint64](($high -shl 32) -bor $low)
}

function Get-FslRabSha1Bytes {
    param([byte[]]$Bytes)
    $hash = [Security.Cryptography.SHA1]::Create()
    try { return ,$hash.ComputeHash($Bytes) }
    finally { $hash.Dispose() }
}

function Get-FslRabSha1Hex {
    param([byte[]]$Bytes)
    return [BitConverter]::ToString(
        (Get-FslRabSha1Bytes $Bytes)).Replace('-', '').ToLowerInvariant()
}

function Get-FslRabGitObjectId {
    param([string]$Type, [byte[]]$Data)
    $header = [Text.Encoding]::ASCII.GetBytes(
        $Type + ' ' + $Data.Length + [char]0)
    $all = [byte[]]::new($header.Length + $Data.Length)
    [Array]::Copy($header, 0, $all, 0, $header.Length)
    [Array]::Copy($Data, 0, $all, $header.Length, $Data.Length)
    return Get-FslRabSha1Hex $all
}

function Expand-FslRabLooseZlib {
    param([byte[]]$Bytes)
    if ($Bytes.Length -lt 7) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A loose Git object is truncated.') $null
    }
    $cmf = [int]$Bytes[0]
    $flg = [int]$Bytes[1]
    if (($cmf -band 15) -ne 8 -or
        ($cmf -shr 4) -gt 7 -or
        ((($cmf -shl 8) -bor $flg) % 31) -ne 0 -or
        ($flg -band 0x20) -ne 0) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A loose Git zlib envelope is invalid.') $null
    }
    $input = [IO.MemoryStream]::new(
        $Bytes, 2, $Bytes.Length - 6, $false)
    $deflate = [IO.Compression.DeflateStream]::new(
        $input, [IO.Compression.CompressionMode]::Decompress)
    $output = [IO.MemoryStream]::new()
    try {
        $deflate.CopyTo($output)
        $data = $output.ToArray()
    }
    catch {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A loose Git deflate stream is invalid.') $_.Exception
    }
    finally {
        $output.Dispose()
        $deflate.Dispose()
        $input.Dispose()
    }
    [uint32]$a = 1
    [uint32]$b = 0
    foreach ($value in $data) {
        $a = [uint32](($a + $value) % 65521)
        $b = [uint32](($b + $a) % 65521)
    }
    $stored = Read-FslRabUInt32Be $Bytes ($Bytes.Length - 4)
    if ([uint32](($b -shl 16) -bor $a) -ne $stored) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A loose Git Adler-32 checksum drifted.') $null
    }
    return ,$data
}

function Expand-FslRabPackDeflate {
    param([byte[]]$Pack, [int]$Offset, [int64]$ExpectedLength)
    if ($Offset -lt 12 -or
        $Offset -ge $Pack.Length - 20 -or
        $ExpectedLength -lt 0 -or
        $ExpectedLength -gt 268435456) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A pack deflate boundary is invalid.') $null
    }
    $cmf = [int]$Pack[$Offset]
    $flg = [int]$Pack[$Offset + 1]
    if (($cmf -band 15) -ne 8 -or
        ($cmf -shr 4) -gt 7 -or
        ((($cmf -shl 8) -bor $flg) % 31) -ne 0 -or
        ($flg -band 0x20) -ne 0) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A packed Git zlib envelope is invalid.') $null
    }
    $input = [IO.MemoryStream]::new(
        $Pack, $Offset + 2, $Pack.Length - 22 - $Offset, $false)
    $deflate = [IO.Compression.DeflateStream]::new(
        $input, [IO.Compression.CompressionMode]::Decompress)
    $output = [IO.MemoryStream]::new()
    try {
        $deflate.CopyTo($output)
        $data = $output.ToArray()
    }
    catch {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A packed Git deflate stream is invalid.') $_.Exception
    }
    finally {
        $output.Dispose()
        $deflate.Dispose()
        $input.Dispose()
    }
    if ($data.LongLength -ne $ExpectedLength) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A packed Git object length drifted.') $null
    }
    return ,$data
}

function Read-FslRabDeltaVarInt {
    param([byte[]]$Bytes, [ref]$Offset)
    [uint64]$value = 0
    $shift = 0
    do {
        if ($Offset.Value -ge $Bytes.Length -or $shift -gt 56) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git delta varint is invalid.') $null
        }
        $current = [int]$Bytes[$Offset.Value]
        $Offset.Value++
        $value = $value -bor (
            [uint64]($current -band 0x7F) -shl $shift)
        $shift += 7
    } while (($current -band 0x80) -ne 0)
    return $value
}

function Expand-FslRabDelta {
    param([byte[]]$Base, [byte[]]$Delta)
    $offset = 0
    [uint64]$baseLength = Read-FslRabDeltaVarInt $Delta ([ref]$offset)
    [uint64]$resultLength = Read-FslRabDeltaVarInt $Delta ([ref]$offset)
    if ($baseLength -ne $Base.LongLength -or
        $resultLength -gt 268435456) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git delta base/result length is invalid.') $null
    }
    $output = [IO.MemoryStream]::new()
    try {
        while ($offset -lt $Delta.Length) {
            $opcode = [int]$Delta[$offset]
            $offset++
            if (($opcode -band 0x80) -ne 0) {
                [uint64]$copyOffset = 0
                [uint64]$copyLength = 0
                for ($byteIndex = 0; $byteIndex -lt 4; $byteIndex++) {
                    if (($opcode -band (1 -shl $byteIndex)) -ne 0) {
                        if ($offset -ge $Delta.Length) {
                            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                                'A Git delta copy offset is truncated.') $null
                        }
                        $copyOffset = $copyOffset -bor (
                            [uint64]$Delta[$offset] -shl (8 * $byteIndex))
                        $offset++
                    }
                }
                for ($byteIndex = 0; $byteIndex -lt 3; $byteIndex++) {
                    if (($opcode -band (0x10 -shl $byteIndex)) -ne 0) {
                        if ($offset -ge $Delta.Length) {
                            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                                'A Git delta copy length is truncated.') $null
                        }
                        $copyLength = $copyLength -bor (
                            [uint64]$Delta[$offset] -shl (8 * $byteIndex))
                        $offset++
                    }
                }
                if ($copyLength -eq 0) { $copyLength = 0x10000 }
                if ($copyOffset + $copyLength -gt $Base.LongLength) {
                    Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                        'A Git delta copy escaped its base object.') $null
                }
                $output.Write(
                    $Base, [int]$copyOffset, [int]$copyLength)
            }
            elseif ($opcode -gt 0) {
                if ($offset + $opcode -gt $Delta.Length) {
                    Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                        'A Git delta literal is truncated.') $null
                }
                $output.Write($Delta, $offset, $opcode)
                $offset += $opcode
            }
            else {
                Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                    'A Git delta opcode is invalid.') $null
            }
        }
        $result = $output.ToArray()
    }
    finally { $output.Dispose() }
    if ($result.LongLength -ne $resultLength) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git delta result length drifted.') $null
    }
    return ,$result
}

function Get-FslRabPackIndexEntry {
    param([string]$GitDirectory, [string]$ObjectId)
    $needle = [byte[]]::new(20)
    for ($index = 0; $index -lt 20; $index++) {
        $needle[$index] = [Convert]::ToByte(
            $ObjectId.Substring($index * 2, 2), 16)
    }
    $packRoot = Join-Path $GitDirectory 'objects\pack'
    if (-not (Test-Path -LiteralPath $packRoot -PathType Container)) {
        return $null
    }
    foreach ($indexPath in @(
        Get-ChildItem -LiteralPath $packRoot -Filter '*.idx' -File |
            Sort-Object Name | ForEach-Object FullName)) {
        $bytes = [IO.File]::ReadAllBytes($indexPath)
        if ($bytes.Length -lt 8 + 1024 + 40 -or
            $bytes[0] -ne 0xFF -or $bytes[1] -ne 0x74 -or
            $bytes[2] -ne 0x4F -or $bytes[3] -ne 0x63 -or
            (Read-FslRabUInt32Be $bytes 4) -ne 2) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack index header/version is invalid.') $null
        }
        $count = [int](Read-FslRabUInt32Be $bytes (8 + 255 * 4))
        $namesOffset = 8 + 1024
        $crcOffset = $namesOffset + $count * 20
        $offsetsOffset = $crcOffset + $count * 4
        $largeCount = 0
        for ($entry = 0; $entry -lt $count; $entry++) {
            $value = Read-FslRabUInt32Be $bytes ($offsetsOffset + $entry * 4)
            if (($value -band 0x80000000) -ne 0) { $largeCount++ }
        }
        $expectedLength =
            $offsetsOffset + $count * 4 + $largeCount * 8 + 40
        if ($bytes.Length -ne $expectedLength) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack index table length is invalid.') $null
        }
        $body = [byte[]]::new($bytes.Length - 20)
        [Array]::Copy($bytes, 0, $body, 0, $body.Length)
        $storedIndexHash = [byte[]]::new(20)
        [Array]::Copy($bytes, $body.Length, $storedIndexHash, 0, 20)
        if (([BitConverter]::ToString((Get-FslRabSha1Bytes $body))) -cne
            ([BitConverter]::ToString($storedIndexHash))) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack index checksum drifted.') $null
        }
        $low = 0
        $high = $count - 1
        $found = -1
        while ($low -le $high) {
            $middle = [int](($low + $high) / 2)
            $comparison = 0
            for ($byteIndex = 0; $byteIndex -lt 20; $byteIndex++) {
                $actual = [int]$bytes[
                    $namesOffset + $middle * 20 + $byteIndex]
                if ($actual -ne [int]$needle[$byteIndex]) {
                    $comparison = if ($actual -lt $needle[$byteIndex]) {
                        -1
                    }
                    else { 1 }
                    break
                }
            }
            if ($comparison -eq 0) {
                $found = $middle
                break
            }
            if ($comparison -lt 0) { $low = $middle + 1 }
            else { $high = $middle - 1 }
        }
        if ($found -lt 0) { continue }
        [uint64]$packOffset = Read-FslRabUInt32Be (
            $bytes) ($offsetsOffset + $found * 4)
        if (($packOffset -band 0x80000000) -ne 0) {
            $largeIndex = [int]($packOffset -band 0x7FFFFFFF)
            if ($largeIndex -ge $largeCount) {
                Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                    'A Git large pack offset is invalid.') $null
            }
            $packOffset = Read-FslRabUInt64Be $bytes (
                $offsetsOffset + $count * 4 + $largeIndex * 8)
        }
        $packPath = [IO.Path]::ChangeExtension($indexPath, '.pack')
        if (-not (Test-Path -LiteralPath $packPath -PathType Leaf)) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack paired with an index is absent.') $null
        }
        $pack = [IO.File]::ReadAllBytes($packPath)
        if ($pack.Length -lt 32 -or
            [Text.Encoding]::ASCII.GetString($pack, 0, 4) -cne 'PACK' -or
            (Read-FslRabUInt32Be $pack 4) -notin @([uint32]2, [uint32]3) -or
            (Read-FslRabUInt32Be $pack 8) -ne [uint32]$count) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack header/version/count is invalid.') $null
        }
        $packBody = [byte[]]::new($pack.Length - 20)
        [Array]::Copy($pack, 0, $packBody, 0, $packBody.Length)
        $packHash = Get-FslRabSha1Bytes $packBody
        $trailer = [byte[]]::new(20)
        [Array]::Copy($pack, $packBody.Length, $trailer, 0, 20)
        $indexPackHash = [byte[]]::new(20)
        [Array]::Copy($bytes, $bytes.Length - 40, $indexPackHash, 0, 20)
        if (([BitConverter]::ToString($packHash)) -cne
                ([BitConverter]::ToString($trailer)) -or
            ([BitConverter]::ToString($packHash)) -cne
                ([BitConverter]::ToString($indexPackHash))) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack/index checksum binding drifted.') $null
        }
        if ($packOffset -lt 12 -or $packOffset -ge $pack.Length - 20) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack object offset is invalid.') $null
        }
        return [pscustomobject]@{
            pack = $pack
            offset = [int64]$packOffset
            gitDirectory = $GitDirectory
        }
    }
    return $null
}

function Read-FslRabPackedObjectAt {
    param(
        [string]$GitDirectory,
        [byte[]]$Pack,
        [int64]$ObjectOffset,
        [string]$ExpectedObjectId,
        [int]$Depth)
    if ($Depth -gt 64 -or
        $ObjectOffset -lt 12 -or
        $ObjectOffset -ge $Pack.Length - 20) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git pack delta chain is invalid.') $null
    }
    $cursor = [int]$ObjectOffset
    $first = [int]$Pack[$cursor]
    $cursor++
    $typeCode = ($first -shr 4) -band 7
    [int64]$size = $first -band 0x0F
    $shift = 4
    while (($first -band 0x80) -ne 0) {
        if ($cursor -ge $Pack.Length - 20 -or $shift -gt 60) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git pack object header is invalid.') $null
        }
        $first = [int]$Pack[$cursor]
        $cursor++
        $size = $size -bor ([int64]($first -band 0x7F) -shl $shift)
        $shift += 7
    }
    $base = $null
    if ($typeCode -eq 6) {
        if ($cursor -ge $Pack.Length - 20) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'An OFS_DELTA base is truncated.') $null
        }
        $value = [int64]($Pack[$cursor] -band 0x7F)
        $byte = [int]$Pack[$cursor]
        $cursor++
        while (($byte -band 0x80) -ne 0) {
            if ($cursor -ge $Pack.Length - 20) {
                Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                    'An OFS_DELTA offset is truncated.') $null
            }
            $byte = [int]$Pack[$cursor]
            $cursor++
            $value = (($value + 1) -shl 7) -bor ($byte -band 0x7F)
        }
        $baseOffset = $ObjectOffset - $value
        $base = Read-FslRabPackedObjectAt `
            $GitDirectory $Pack $baseOffset $null ($Depth + 1)
    }
    elseif ($typeCode -eq 7) {
        if ($cursor + 20 -gt $Pack.Length - 20) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A REF_DELTA base is truncated.') $null
        }
        $baseId = [BitConverter]::ToString(
            $Pack, $cursor, 20).Replace('-', '').ToLowerInvariant()
        $cursor += 20
        $base = Read-FslRabGitObject $GitDirectory $baseId ($Depth + 1)
    }
    $data = Expand-FslRabPackDeflate $Pack $cursor $size
    if ($typeCode -in @(1, 2, 3, 4)) {
        $type = @('', 'commit', 'tree', 'blob', 'tag')[$typeCode]
        $result = [pscustomobject]@{ type = $type; data = $data }
    }
    elseif ($typeCode -in @(6, 7)) {
        $result = [pscustomobject]@{
            type = [string]$base.type
            data = Expand-FslRabDelta ([byte[]]$base.data) $data
        }
    }
    else {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git pack object type is unsupported.') $null
    }
    $actualId = Get-FslRabGitObjectId $result.type ([byte[]]$result.data)
    if (-not [string]::IsNullOrEmpty($ExpectedObjectId) -and
        $actualId -cne $ExpectedObjectId) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A packed Git object ID drifted.') $null
    }
    return $result
}

function Read-FslRabGitObject {
    param([string]$GitDirectory, [string]$ObjectId, [int]$Depth = 0)
    if ($ObjectId -cnotmatch $script:RabGitPattern -or $Depth -gt 64) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git object identity/depth is invalid.') $null
    }
    $loosePath = Join-Path $GitDirectory (
        'objects\' + $ObjectId.Substring(0, 2) + '\' +
        $ObjectId.Substring(2))
    if (Test-Path -LiteralPath $loosePath -PathType Leaf) {
        $expanded = Expand-FslRabLooseZlib (
            [IO.File]::ReadAllBytes($loosePath))
        $nul = [Array]::IndexOf($expanded, [byte]0)
        if ($nul -le 0) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A loose Git object header is invalid.') $null
        }
        $header = [Text.Encoding]::ASCII.GetString($expanded, 0, $nul)
        $match = [regex]::Match(
            $header, '^(?<type>commit|tree|blob|tag) (?<length>\d+)$')
        if (-not $match.Success) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A loose Git object type/length is invalid.') $null
        }
        $data = [byte[]]::new($expanded.Length - $nul - 1)
        [Array]::Copy($expanded, $nul + 1, $data, 0, $data.Length)
        if ([int64]$match.Groups['length'].Value -ne $data.LongLength -or
            (Get-FslRabGitObjectId $match.Groups['type'].Value $data) -cne
                $ObjectId) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A loose Git object checksum/length drifted.') $null
        }
        return [pscustomobject]@{
            type = $match.Groups['type'].Value
            data = $data
        }
    }
    $entry = Get-FslRabPackIndexEntry $GitDirectory $ObjectId
    if ($null -eq $entry) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A required loose or packed Git object is absent.') $null
    }
    return Read-FslRabPackedObjectAt `
        $GitDirectory `
        ([byte[]]$entry.pack) `
        ([int64]$entry.offset) `
        $ObjectId `
        $Depth
}

function Get-FslRabCommit {
    param([string]$GitDirectory, [string]$ObjectId)
    $object = Read-FslRabGitObject $GitDirectory $ObjectId
    if ($object.type -cne 'commit') {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git commit reference did not resolve to a commit.') $null
    }
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString(
            [byte[]]$object.data)
    }
    catch {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git commit is not strict UTF-8.') $_.Exception
    }
    $treeMatch = [regex]::Match($text, '^tree (?<id>[0-9a-f]{40})\n')
    if (-not $treeMatch.Success) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'A Git commit tree is absent.') $null
    }
    $parents = @([regex]::Matches(
        $text, '(?m)^parent (?<id>[0-9a-f]{40})$') |
        ForEach-Object { $_.Groups['id'].Value })
    return [pscustomobject]@{
        tree = $treeMatch.Groups['id'].Value
        parents = $parents
    }
}

function Get-FslRabTreeEntry {
    param([string]$GitDirectory, [string]$TreeId, [string]$Path)
    $segments = @($Path.Split('/'))
    $currentTree = $TreeId
    for ($segmentIndex = 0;
        $segmentIndex -lt $segments.Count;
        $segmentIndex++) {
        $object = Read-FslRabGitObject $GitDirectory $currentTree
        if ($object.type -cne 'tree') {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git tree path traversed a non-tree.') $null
        }
        $bytes = [byte[]]$object.data
        $offset = 0
        $match = $null
        while ($offset -lt $bytes.Length) {
            $space = [Array]::IndexOf($bytes, [byte]0x20, $offset)
            $nul = if ($space -ge 0) {
                [Array]::IndexOf($bytes, [byte]0, $space + 1)
            }
            else { -1 }
            if ($space -le $offset -or
                $nul -le $space + 1 -or
                $nul + 21 -gt $bytes.Length) {
                Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                    'A Git tree entry is malformed.') $null
            }
            $mode = [Text.Encoding]::ASCII.GetString(
                $bytes, $offset, $space - $offset)
            $name = [Text.UTF8Encoding]::new($false, $true).GetString(
                $bytes, $space + 1, $nul - $space - 1)
            $oid = [BitConverter]::ToString(
                $bytes, $nul + 1, 20).Replace('-', '').ToLowerInvariant()
            if ($name -ceq $segments[$segmentIndex]) {
                $match = [pscustomobject]@{
                    mode = $mode
                    objectId = $oid
                }
                break
            }
            $offset = $nul + 21
        }
        if ($null -eq $match) { return $null }
        if ($segmentIndex -lt $segments.Count - 1) {
            if ($match.mode -cne '40000') { return $null }
            $currentTree = $match.objectId
        }
    }
    return $match
}

function Get-FslRabIndexEntries {
    param([string]$GitRoot, [string]$GitDirectory)
    $path = Join-Path $GitDirectory 'index'
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 32 -or
        [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -cne 'DIRC' -or
        (Read-FslRabUInt32Be $bytes 4) -ne 2) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'The Git index header/version is invalid.') $null
    }
    $body = [byte[]]::new($bytes.Length - 20)
    [Array]::Copy($bytes, 0, $body, 0, $body.Length)
    $stored = [byte[]]::new(20)
    [Array]::Copy($bytes, $body.Length, $stored, 0, 20)
    if (([BitConverter]::ToString((Get-FslRabSha1Bytes $body))) -cne
        ([BitConverter]::ToString($stored))) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'The Git index checksum drifted.') $null
    }
    $count = [int](Read-FslRabUInt32Be $bytes 8)
    $offset = 12
    $entries = @()
    $previous = $null
    for ($entryIndex = 0; $entryIndex -lt $count; $entryIndex++) {
        $start = $offset
        if ($offset + 63 -gt $body.Length) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git index entry is truncated.') $null
        }
        $mode = Read-FslRabUInt32Be $bytes ($offset + 24)
        $oid = [BitConverter]::ToString(
            $bytes, $offset + 40, 20).Replace('-', '').ToLowerInvariant()
        $flags = [uint16](
            ([uint16]$bytes[$offset + 60] -shl 8) -bor
            [uint16]$bytes[$offset + 61])
        if (($flags -band 0xF000) -ne 0 -or
            $mode -notin @([uint32]0x000081A4, [uint32]0x000081ED)) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git index stage/flag/mode is unsupported.') $null
        }
        $nul = [Array]::IndexOf($bytes, [byte]0, $offset + 62)
        if ($nul -lt 0 -or $nul -ge $body.Length) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git index path is unterminated.') $null
        }
        $relative = [Text.UTF8Encoding]::new($false, $true).GetString(
            $bytes, $offset + 62, $nul - $offset - 62)
        if ([string]::IsNullOrEmpty($relative) -or
            $relative.Contains('\') -or
            $relative.StartsWith('/') -or
            @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).
                Count -ne 0 -or
            ($null -ne $previous -and
                [string]::CompareOrdinal($previous, $relative) -ge 0)) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git index path/order is invalid.') $null
        }
        $previous = $relative
        $entryLength = $nul - $start + 1
        $offset = $start + (($entryLength + 7) -band (-bnot 7))
        if ($offset -gt $body.Length) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git index entry padding escaped the index.') $null
        }
        for ($padding = $nul + 1; $padding -lt $offset; $padding++) {
            if ($bytes[$padding] -ne 0) {
                Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                    'A Git index padding byte is nonzero.') $null
            }
        }
        $entries += [pscustomobject]@{
            path = $relative
            mode = $mode
            objectId = $oid
        }
    }
    while ($offset -lt $body.Length) {
        if ($offset + 8 -gt $body.Length) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git index extension is truncated.') $null
        }
        $signature = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
        $length = [int](Read-FslRabUInt32Be $bytes ($offset + 4))
        if ($signature -cnotmatch '^[A-Z][A-Z0-9]{3}$' -or
            $length -lt 0 -or $offset + 8 + $length -gt $body.Length) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A Git index extension is invalid.') $null
        }
        $offset += 8 + $length
    }
    return $entries
}

function Get-FslRabHead {
    param([string]$GitDirectory)
    $headText = [IO.File]::ReadAllText(
        (Join-Path $GitDirectory 'HEAD'),
        [Text.UTF8Encoding]::new($false, $true)).Trim()
    if (-not $headText.StartsWith(
            'ref: refs/heads/', [StringComparison]::Ordinal)) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'Detached or invalid Git HEAD is forbidden.') $null
    }
    $reference = $headText.Substring(5)
    $branch = $reference.Substring('refs/heads/'.Length)
    $referencePath = Join-Path $GitDirectory $reference.Replace('/', '\')
    if (Test-Path -LiteralPath $referencePath -PathType Leaf) {
        $commit = [IO.File]::ReadAllText($referencePath).Trim()
    }
    else {
        $commit = $null
        $packedPath = Join-Path $GitDirectory 'packed-refs'
        if (Test-Path -LiteralPath $packedPath -PathType Leaf) {
            foreach ($line in [IO.File]::ReadAllLines($packedPath)) {
                if ($line -match '^(?<id>[0-9a-f]{40}) (?<ref>refs/[^\s]+)$' -and
                    $Matches.ref -ceq $reference) {
                    $commit = $Matches.id
                    break
                }
            }
        }
    }
    if ($commit -cnotmatch $script:RabGitPattern) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'The current Git branch ref is unavailable.') $null
    }
    return [pscustomobject]@{ branch = $branch; commit = $commit }
}

function Test-FslRabAncestor {
    param([string]$GitDirectory, [string]$Ancestor, [string]$Descendant)
    if ($Ancestor -cnotmatch $script:RabGitPattern -or
        $Descendant -cnotmatch $script:RabGitPattern -or
        $Ancestor -ceq $Descendant) {
        return $false
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $pending.Push($Descendant)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        if (-not $seen.Add($current)) { continue }
        if ($seen.Count -gt 100000) {
            Stop-FslRab 'FSL-RAB-V008-ANCESTRY' (
                'The Git ancestry graph exceeded its fixed bound.') $null
        }
        $commit = Get-FslRabCommit $GitDirectory $current
        foreach ($parent in @($commit.parents)) {
            if ($parent -ceq $Ancestor) { return $true }
            $pending.Push($parent)
        }
    }
    return $false
}

function Get-FslRabRepository {
    param([string]$Root, [bool]$RequireCompletelyClean)
    $projectRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $candidate = $projectRoot
    $gitRoot = $null
    while (-not [string]::IsNullOrEmpty($candidate)) {
        if (Test-Path -LiteralPath (
                Join-Path $candidate '.git') -PathType Container) {
            $gitRoot = $candidate
            break
        }
        $parent = Split-Path -Parent $candidate
        if ($parent -ceq $candidate) { break }
        $candidate = $parent
    }
    if ($null -eq $gitRoot) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'The fixed toolchain Git directory is unavailable.') $null
    }
    $gitDirectory = Join-Path $gitRoot '.git'
    $head = Get-FslRabHead $gitDirectory
    $commit = Get-FslRabCommit $gitDirectory $head.commit
    $entries = @(Get-FslRabIndexEntries $gitRoot $gitDirectory)
    $byPath = @{}
    $trackedClean = $true
    foreach ($entry in $entries) {
        $byPath[[string]$entry.path] = $entry
        $treeEntry = Get-FslRabTreeEntry `
            $gitDirectory $commit.tree ([string]$entry.path)
        $worktreePath = Join-Path $gitRoot (
            ([string]$entry.path).Replace('/', '\'))
        if ($null -eq $treeEntry -or
            [string]$treeEntry.objectId -cne [string]$entry.objectId -or
            -not (Test-Path -LiteralPath $worktreePath -PathType Leaf) -or
            (Get-FslRabGitObjectId 'blob' (
                [IO.File]::ReadAllBytes($worktreePath))) -cne
                [string]$entry.objectId) {
            $trackedClean = $false
            break
        }
    }
    $prefix = if ($projectRoot -ceq $gitRoot) {
        ''
    }
    else {
        $projectRoot.Substring($gitRoot.Length + 1).Replace('\', '/') + '/'
    }
    if ($RequireCompletelyClean) {
        $known = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
        foreach ($entry in $entries) { [void]$known.Add([string]$entry.path) }
        foreach ($file in @(
            Get-ChildItem -LiteralPath $gitRoot -Recurse -File -Force |
                Where-Object {
                    $_.FullName -notmatch '\\\.git\\' -and
                    $_.FullName -notmatch '\\(bin|obj|TestResults)\\'
                })) {
            $relative = $file.FullName.Substring(
                $gitRoot.Length + 1).Replace('\', '/')
            if (-not $known.Contains($relative)) {
                $trackedClean = $false
                break
            }
        }
    }
    if (-not $trackedClean) {
        Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
            'The recovery toolchain index/worktree/tree binding is not clean.') $null
    }
    $sourceFiles = @()
    foreach ($relative in $script:RabFixedToolchainFiles) {
        $indexPath = $prefix + $relative
        $path = Join-Path $projectRoot $relative.Replace('/', '\')
        $entry = $byPath[$indexPath]
        $treeEntry = Get-FslRabTreeEntry $gitDirectory $commit.tree $indexPath
        if ($null -eq $entry -or $null -eq $treeEntry -or
            [string]$entry.objectId -cne [string]$treeEntry.objectId -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Stop-FslRab 'FSL-RAB-V007-TOOLCHAIN-AUTHORITY' (
                'A fixed recovery toolchain source binding drifted.') $null
        }
        $sourceFiles += [pscustomobject][ordered]@{
            relativePath = $relative
            blobObjectId = [string]$entry.objectId
            length = [int64](Get-Item -LiteralPath $path).Length
            sha256 = Get-FslRabSha256 $path
        }
    }
    return [pscustomobject][ordered]@{
        repository = $projectRoot
        gitRoot = $gitRoot
        gitDirectory = $gitDirectory
        branch = [string]$head.branch
        gitCommit = [string]$head.commit
        gitTree = [string]$commit.tree
        trackedClean = $true
        sourceFiles = $sourceFiles
        repositorySha256 = Get-FslRabObjectHash $sourceFiles
    }
}

function Get-FslRabGateMap {
    $suffixes = @(
        'ARGUMENTS','MACHINE','TOKEN','IDENTITY','SOURCE-ROOT','SOURCE-FILESET',
        'CONTRACT-BYTES','CONTRACT-SCHEMA','CONTRACT-SELF-HASH','EXECUTION-COMMIT',
        'EXECUTION-TREE','TOOLCHAIN-COMMIT','TOOLCHAIN-TREE','COMMIT-ANCESTRY',
        'REPOSITORY-CLEAN','TOOLCHAIN-FILE-01','TOOLCHAIN-FILE-02',
        'TOOLCHAIN-FILE-03','TOOLCHAIN-FILE-04','TOOLCHAIN-FILE-05','STATE',
        'JOURNAL','WAL-PREFIX','EVIDENCE-EXACT-SET','PREDECESSOR-EXACT-SET',
        'ANCHOR-EXACT-SET','ANCHOR-LATEST','ANCHOR-PREVIOUS','ANCHOR-KEY',
        'RELEASE-EXACT-SET','RELEASE-DESCRIPTOR','RELEASE-MANIFEST',
        'RELEASE-SUMS','INSTALL-DIRECTORY','INSTALL-EMPTY','PROGRAMDATA-ABSENT',
        'SERVICE-ABSENT','SERVICE-REGISTRY-ABSENT','PRODUCT-PROCESS-ABSENT',
        'ENABLE-LUA','APPINFO','TRANSACTION','PLAN','EXPECTED-WAL',
        'EXPECTED-ANCHORS','EXPECTED-STATE','EXPECTED-DIRECTORY',
        'FUTURE-INVOCATION','ALLOWED-WRITES','FORBIDDEN-ACTIONS','WRAPPER-AST',
        'WRAPPER-IMPORT','WRAPPER-STATE','WRAPPER-CONTEXT',
        'WRAPPER-RECONCILE-ONCE','NONEXECUTION')
    $gates = @()
    for ($index = 0; $index -lt $suffixes.Count; $index++) {
        $gates += [pscustomobject][ordered]@{
            gateId = 'FSL-RAB-CG-{0:D3}-{1}' -f ($index + 1), $suffixes[$index]
            exitCode = 84 + $index
        }
    }
    return $gates
}

function Test-FslRabGateMap {
    param([object[]]$Gates)
    $expected = @(Get-FslRabGateMap)
    if ($Gates.Count -ne 56) { return $false }
    for ($index = 0; $index -lt 56; $index++) {
        if (-not (Test-FslRabNames $Gates[$index] @('gateId', 'exitCode')) -or
            $Gates[$index].gateId -isnot [string] -or
            $Gates[$index].exitCode -isnot [int] -or
            [string]$Gates[$index].gateId -cne [string]$expected[$index].gateId -or
            [int]$Gates[$index].exitCode -ne [int]$expected[$index].exitCode) {
            return $false
        }
    }
    return $true
}

function Get-FslRabExecutionAuthority {
    param([psobject]$Model, [psobject]$Roots, [psobject]$Repository)
    $statePath = Join-Path $Roots.evidenceRoot 'stage4-state.json'
    $journalPath = Join-Path $Roots.evidenceRoot 'stage4-journal.jsonl'
    $walPath = Join-Path $Roots.evidenceRoot 'install-wal.jsonl'
    foreach ($path in @($statePath, $journalPath, $walPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Stop-FslRab 'FSL-RAB-V006-EXECUTION-AUTHORITY' (
                'A fixed execution-state authority file is absent.') $null
        }
    }
    try {
        $state = [IO.File]::ReadAllText(
            $statePath,
            [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
    }
    catch {
        Stop-FslRab 'FSL-RAB-V006-EXECUTION-AUTHORITY' (
            'The fixed Stage 4 state is invalid.') $_.Exception
    }
    if ($state.gitCommit -isnot [string] -or
        [string]$state.gitCommit -cnotmatch $script:RabGitPattern -or
        $state.sequence -isnot [int] -or
        [int]$state.sequence -ne 6 -or
        [string]$state.transition -cne 'InstallStarted') {
        Stop-FslRab 'FSL-RAB-V006-EXECUTION-AUTHORITY' (
            'The execution-state commit/sequence/transition drifted.') $null
    }
    if (-not (Test-FslRabAncestor `
            $Repository.gitDirectory `
            ([string]$state.gitCommit) `
            ([string]$Repository.gitCommit))) {
        Stop-FslRab 'FSL-RAB-V008-ANCESTRY' (
            'The execution-state commit is not a strict ancestor of the recovery toolchain commit.') $null
    }
    $oldTree = [string](Get-FslRabCommit `
        $Repository.gitDirectory `
        ([string]$state.gitCommit)).tree
    $journalLines = @([IO.File]::ReadAllLines(
        $journalPath,
        [Text.UTF8Encoding]::new($false, $true)))
    $walLines = @([IO.File]::ReadAllLines(
        $walPath,
        [Text.UTF8Encoding]::new($false, $true)))
    if ($journalLines.Count -lt 1 -or $walLines.Count -ne 4) {
        Stop-FslRab 'FSL-RAB-V010-STATE-WAL' (
            'The journal or four-record WAL prefix drifted.') $null
    }
    $records = [pscustomobject][ordered]@{
        state = Get-FslRabFileRecord $statePath
        journal = Get-FslRabFileRecord $journalPath
        installWal = Get-FslRabFileRecord $walPath
    }
    return [pscustomobject][ordered]@{
        machineName = [Environment]::MachineName
        runId = [string]$Model.runId
        branch = [string]$Repository.branch
        gitCommit = [string]$state.gitCommit
        gitTree = $oldTree
        stateSequence = [int]$state.sequence
        stateTransition = [string]$state.transition
        files = $records
        authoritySha256 = Get-FslRabObjectHash $records
    }
}

function Get-FslRabRelease {
    param([psobject]$Roots)
    if ($null -eq $Roots.releaseRoot) {
        $state = [IO.File]::ReadAllText(
            (Join-Path $Roots.evidenceRoot 'stage4-state.json'),
            [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
        if ($state.releaseRoot -isnot [string]) {
            Stop-FslRab 'FSL-RAB-V013-RELEASE' (
                'The formal frozen release root is unavailable.') $null
        }
        $root = [IO.Path]::GetFullPath([string]$state.releaseRoot).TrimEnd('\')
    }
    else { $root = [string]$Roots.releaseRoot }
    $files = @(Get-ChildItem -LiteralPath $root -File -Force | Sort-Object Name |
        ForEach-Object { Get-FslRabFileRecord $_.FullName })
    foreach ($name in @(
        'release-descriptor.json',
        'release-manifest.json',
        'SHA256SUMS.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) {
            Stop-FslRab 'FSL-RAB-V013-RELEASE' (
                'A fixed release authority file is absent.') $null
        }
    }
    return [pscustomobject][ordered]@{
        root = $root
        fileCount = $files.Count
        files = $files
        fingerprintSha256 = Get-FslRabObjectHash $files
        descriptorSha256 = Get-FslRabSha256 (
            Join-Path $root 'release-descriptor.json')
        manifestSha256 = Get-FslRabSha256 (
            Join-Path $root 'release-manifest.json')
        sumsSha256 = Get-FslRabSha256 (
            Join-Path $root 'SHA256SUMS.txt')
    }
}

function Get-FslRabTransaction {
    param([psobject]$Roots)
    if (-not (Test-Path -LiteralPath $Roots.installDirectory -PathType Container) -or
        @(Get-ChildItem -LiteralPath $Roots.installDirectory -Force).Count -ne 0) {
        Stop-FslRab 'FSL-RAB-V014-SYSTEM' (
            'The bound installation directory is absent or not empty.') $null
    }
    return [pscustomobject][ordered]@{
        transactionId = 'Install-WAL-rollback'
        workflow = 'Install'
        recoveryMode = 'Rollback'
        walPrefixRecordCount = 4
        expectedPost = [pscustomobject][ordered]@{
            walRecordCount = 7
            latestGeneration = 14
            latestSlot = 'anchor-0.json'
            previousGeneration = 13
            previousSlot = 'anchor-1.json'
            stateSequence = 6
            stateTransition = 'InstallStarted'
            directoryAbsent = $true
            addedPhases = @(
                '1|InstallDirectorySetAcl|RolledBack',
                '0|InstallDirectoryCreate|RolledBack',
                '-1|transaction|Aborted')
        }
    }
}

function ConvertTo-FslRabLiteral {
    param([string]$Value)
    if ($Value.Contains("`r") -or $Value.Contains("`n")) {
        Stop-FslRab 'FSL-RAB-V016-WRAPPER' (
            'A fixed wrapper literal contains a newline.') $null
    }
    return "'" + $Value.Replace("'", "''") + "'"
}

function Render-FslRabWrapper {
    param(
        [psobject]$Model,
        [psobject]$Roots,
        [psobject]$Repository,
        [psobject]$Execution,
        [psobject]$Release)
    $modulePath = Join-Path $Repository.repository (
        'eng\stage4\FolderSessionLock.Stage4.psm1')
    $statePath = [string]$Execution.files.state.path
    $template = @'
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixedRunId = @@RUN_ID@@
$fixedReleaseRoot = @@RELEASE_ROOT@@
$fixedModulePath = @@MODULE_PATH@@
$fixedStatePath = @@STATE_PATH@@
if ($PSBoundParameters.Count -ne 0 -or $args.Count -ne 0) { exit 84 }
$module = Import-Module $fixedModulePath -Force -PassThru
$state = [IO.File]::ReadAllText(
    $fixedStatePath,
    [Text.UTF8Encoding]::new($false, $true)) | ConvertFrom-Json
$context = & $module {
    param($runId, $releaseRoot)
    Get-FslContext $runId $releaseRoot
} $fixedRunId $fixedReleaseRoot
& $module {
    param($context, $state)
    Invoke-FslReconcileInstallWal $context $state
} $context $state
exit 0
'@
    return $template.
        Replace('@@RUN_ID@@', (ConvertTo-FslRabLiteral ([string]$Model.runId))).
        Replace('@@RELEASE_ROOT@@', (ConvertTo-FslRabLiteral ([string]$Release.root))).
        Replace('@@MODULE_PATH@@', (ConvertTo-FslRabLiteral $modulePath)).
        Replace('@@STATE_PATH@@', (ConvertTo-FslRabLiteral $statePath)).
        Replace("`r`n", "`n")
}

function Test-FslRabWrapperAst {
    param([string]$Text)
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput(
        $Text,
        [ref]$tokens,
        [ref]$errors)
    if (@($errors).Count -ne 0) { return $false }
    $commands = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true))
    $reconcile = @($commands | Where-Object {
        $_.GetCommandName() -ceq 'Invoke-FslReconcileInstallWal'
    })
    $forbidden = @($commands | Where-Object {
        $_.GetCommandName() -cin @(
            'Start-Process',
            'Invoke-Expression',
            'Invoke-FslStage4',
            'Invoke-FslInstall')
    })
    return $reconcile.Count -eq 1 -and $forbidden.Count -eq 0
}

function Get-FslRabAuthority {
    param([psobject]$Model)
    $roots = Get-FslRabRoots $Model
    foreach ($root in @(
        $roots.baseRoot,
        $roots.authorityRoot,
        $roots.repositoryRoot,
        $roots.evidenceRoot,
        $roots.predecessorRoot,
        $roots.anchorRoot,
        $roots.installDirectory)) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            Stop-FslRab 'FSL-RAB-V002-ROOT' (
                'A fixed private authority root is absent.') $null
        }
    }
    $repository = Get-FslRabRepository `
        $roots.repositoryRoot `
        ([string]$Model.authorityProfile -ceq 'Formal')
    $execution = Get-FslRabExecutionAuthority $Model $roots $repository
    $allEvidence = Get-FslRabExactNamedFileRecords `
        $roots.evidenceRoot `
        $script:RabEvidenceNames `
        'FSL-RAB-V011-EVIDENCE'
    $evidence = @($script:RabCanonicalEvidenceNames | ForEach-Object {
        $name = $_
        $allEvidence | Where-Object {
            [IO.Path]::GetFileName([string]$_.path) -ceq $name
        } | Select-Object -First 1
    })
    $predecessor = Get-FslRabExactNamedFileRecords `
        $roots.predecessorRoot `
        $script:RabPredecessorNames `
        'FSL-RAB-V011-EVIDENCE'
    $anchors = Get-FslRabExactFileRecords $roots.anchorRoot 3 (
        'FSL-RAB-V012-ANCHORS')
    $anchorNames = @($anchors | ForEach-Object {
        [IO.Path]::GetFileName([string]$_.path)
    })
    if (($anchorNames -join '|') -cne
        'anchor-0.json|anchor-1.json|key.dpapi') {
        Stop-FslRab 'FSL-RAB-V012-ANCHORS' (
            'The external anchor exact-three names drifted.') $null
    }
    $release = Get-FslRabRelease $roots
    $transaction = Get-FslRabTransaction $roots
    if (Test-Path -LiteralPath $roots.programDataRoot) {
        Stop-FslRab 'FSL-RAB-V014-SYSTEM' (
            'The fixed ProgramData prestate must be absent.') $null
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $operator = [pscustomobject][ordered]@{
        machineName = [Environment]::MachineName
        userSid = $identity.User.Value
        sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        isInteractive = [Environment]::UserInteractive
    }
    $toolchain = [pscustomobject][ordered]@{
        repository = $repository.repository
        branch = $repository.branch
        gitCommit = $repository.gitCommit
        gitTree = $repository.gitTree
        trackedClean = $repository.trackedClean
        sourceFiles = $repository.sourceFiles
        authoritySha256 = Get-FslRabObjectHash $repository.sourceFiles
    }
    return [pscustomobject][ordered]@{
        roots = $roots
        repository = $repository
        execution = $execution
        toolchain = $toolchain
        operator = $operator
        evidence = $evidence
        predecessor = $predecessor
        anchors = $anchors
        release = $release
        transaction = $transaction
    }
}

function New-FslRabContractBase {
    param(
        [psobject]$Model,
        [psobject]$Authority,
        [byte[]]$WrapperBytes)
    $wrapperPath = Join-Path $Authority.roots.sourceRoot (
        'elevated-reconcile.ps1')
    $contractPath = Join-Path $Authority.roots.sourceRoot (
        'recovery-contract.json')
    $gates = @(Get-FslRabGateMap)
    $executionHash = Get-FslRabObjectHash $Authority.execution
    $toolchainHash = Get-FslRabObjectHash $Authority.toolchain
    $gateHash = Get-FslRabObjectHash $gates
    $wrapperHash = Get-FslRabSha256Bytes $WrapperBytes
    return [pscustomobject][ordered]@{
        schemaVersion = 3
        authorityProfile = [string]$Model.authorityProfile
        contractId = [string]$Model.contractId
        checkpoint = [string]$Model.checkpoint
        runId = [string]$Model.runId
        executionStateAuthority = $Authority.execution
        recoveryToolchainAuthority = $Authority.toolchain
        operatorIdentity = $Authority.operator
        recoverySource = [pscustomobject][ordered]@{
            root = $Authority.roots.sourceRoot
            exactChildren = $script:RabSourceNames
            wrapper = [pscustomobject][ordered]@{
                path = $wrapperPath
                length = [int64]$WrapperBytes.Length
                sha256 = $wrapperHash
            }
            contract = [pscustomobject][ordered]@{
                path = $contractPath
                schemaVersion = 3
                selfHashRule = $script:RabSelfHashRule
            }
        }
        transaction = $Authority.transaction
        canonicalEvidence = [pscustomobject][ordered]@{
            root = $Authority.roots.evidenceRoot
            files = $Authority.evidence
            predecessorRoot = $Authority.roots.predecessorRoot
            predecessorFiles = $Authority.predecessor
        }
        externalAnchors = [pscustomobject][ordered]@{
            root = $Authority.roots.anchorRoot
            files = $Authority.anchors
        }
        release = $Authority.release
        systemPrestate = [pscustomobject][ordered]@{
            installDirectory = $Authority.roots.installDirectory
            installDirectoryEmpty = $true
            programDataRoot = $Authority.roots.programDataRoot
            programDataAbsent = $true
            serviceName = 'FolderSessionLockRecovery'
            serviceAbsent = $true
            serviceRegistryPath =
                'HKLM:\SYSTEM\CurrentControlSet\Services\FolderSessionLockRecovery'
            serviceRegistryAbsent = $true
            productProcessCount = 0
        }
        contractStageGates = $gates
        futureInvocation = [pscustomobject][ordered]@{
            filePath = Join-Path (
                [Environment]::GetFolderPath(
                    [Environment+SpecialFolder]::System)) (
                'WindowsPowerShell\v1.0\powershell.exe')
            arguments = @(
                '-NoLogo',
                '-NoProfile',
                '-NonInteractive',
                '-ExecutionPolicy',
                'Bypass',
                '-File',
                $wrapperPath)
            verb = 'RunAs'
            passThru = $true
            wait = $true
            redirectStandardOutput = $false
            redirectStandardError = $false
        }
        allowedWrites = $script:RabAllowedWrites
        forbiddenActions = $script:RabForbiddenActions
        bindingManifest = [pscustomobject][ordered]@{
            contractCanonicalSha256 = $script:RabZeros
            contractLength = 0
            wrapperSha256 = $wrapperHash
            executionStateAuthoritySha256 = $executionHash
            recoveryToolchainAuthoritySha256 = $toolchainHash
            toolchainRepositorySha256 =
                [string]$Authority.repository.repositorySha256
            recoveryGateMapSha256 = $gateHash
            hashRule = $script:RabSelfHashRule
        }
    }
}

function Complete-FslRabContract {
    param([psobject]$Contract)
    for ($iteration = 0; $iteration -lt 8; $iteration++) {
        $Contract.bindingManifest.contractCanonicalSha256 = $script:RabZeros
        $bytes = Get-FslRabBytes (ConvertTo-FslRabCanonicalJson $Contract)
        if ([int]$Contract.bindingManifest.contractLength -eq $bytes.Length) {
            break
        }
        $Contract.bindingManifest.contractLength = $bytes.Length
    }
    $Contract.bindingManifest.contractCanonicalSha256 = $script:RabZeros
    $zeroBytes = Get-FslRabBytes (ConvertTo-FslRabCanonicalJson $Contract)
    $Contract.bindingManifest.contractCanonicalSha256 =
        Get-FslRabSha256Bytes $zeroBytes
    return Get-FslRabBytes (ConvertTo-FslRabCanonicalJson $Contract)
}

function Set-FslRabSddl {
    param([string]$Path, [string]$Sddl, [bool]$Directory)
    $security = if ($Directory) {
        [Security.AccessControl.DirectorySecurity]::new()
    }
    else { [Security.AccessControl.FileSecurity]::new() }
    $sections =
        [Security.AccessControl.AccessControlSections]::Owner -bor
        [Security.AccessControl.AccessControlSections]::Group -bor
        [Security.AccessControl.AccessControlSections]::Access
    $security.SetSecurityDescriptorSddlForm($Sddl, $sections)
    if ($Directory) { [IO.Directory]::SetAccessControl($Path, $security) }
    else { [IO.File]::SetAccessControl($Path, $security) }
}

function Get-FslRabSddl {
    param([string]$UserSid, [bool]$Directory)
    $ace = if ($Directory) {
        "(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;$UserSid)"
    }
    else { "(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;$UserSid)" }
    return "O:$UserSid" + "G:S-1-5-21-0-0-0-513D:P$ace"
}

function Test-FslRabProtectedAcl {
    param([string]$Path, [bool]$Directory, [string]$UserSid)
    try {
        $security = if ($Directory) {
            [IO.Directory]::GetAccessControl($Path)
        }
        else { [IO.File]::GetAccessControl($Path) }
        $owner = ([Security.Principal.NTAccount]$security.Owner).Translate(
            [Security.Principal.SecurityIdentifier]).Value
        $rules = @($security.GetAccessRules(
            $true,
            $false,
            [Security.Principal.SecurityIdentifier]))
        if ($owner -cne $UserSid -or
            -not $security.AreAccessRulesProtected -or
            $rules.Count -ne 3) {
            return $false
        }
        $expected = @('S-1-5-18', 'S-1-5-32-544', $UserSid)
        for ($index = 0; $index -lt 3; $index++) {
            if ($rules[$index].IdentityReference.Value -cne $expected[$index] -or
                $rules[$index].AccessControlType -ne
                    [Security.AccessControl.AccessControlType]::Allow -or
                $rules[$index].FileSystemRights -ne
                    [Security.AccessControl.FileSystemRights]::FullControl -or
                $rules[$index].IsInherited) {
                return $false
            }
        }
        return $true
    }
    catch { return $false }
}

function Write-FslRabNew {
    param([string]$Path, [byte[]]$Bytes)
    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::Read,
        4096,
        [IO.FileOptions]::WriteThrough)
    try {
        $stream.Write($Bytes, 0, $Bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
}

function Remove-FslRabPartial {
    param([string]$Root, [hashtable]$Hashes)
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    $children = @(Get-ChildItem -LiteralPath $Root -Force)
    foreach ($child in $children) {
        if ($child.PSIsContainer -or
            -not $Hashes.ContainsKey($child.Name) -or
            (Get-FslRabSha256 $child.FullName) -cne $Hashes[$child.Name]) {
            return
        }
    }
    foreach ($child in $children) { [IO.File]::Delete($child.FullName) }
    if (@(Get-ChildItem -LiteralPath $Root -Force).Count -eq 0) {
        [IO.Directory]::Delete($Root, $false)
    }
}

function Get-FslRabObservedFiles {
    param([string]$Root)
    return @($script:RabSourceNames | ForEach-Object {
        $path = Join-Path $Root $_
        [pscustomobject][ordered]@{
            name = $_
            path = $path
            length = if (Test-Path -LiteralPath $path -PathType Leaf) {
                [int64](Get-Item -LiteralPath $path).Length
            }
            else { $null }
            sha256 = if (Test-Path -LiteralPath $path -PathType Leaf) {
                Get-FslRabSha256 $path
            }
            else { $null }
        }
    })
}

function Sort-FslRabErrors {
    param([Collections.IList]$Errors)
    return @($Errors | Sort-Object code, target, detail)
}

function New-FslStage4RecoveryAuthorityBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNull()]
        [psobject]$Model)
    Assert-FslRabModel $Model
    $authority = Get-FslRabAuthority $Model
    $root = $authority.roots.sourceRoot
    if (Test-Path -LiteralPath $root) {
        Stop-FslRab 'FSL-RAB-V002-ROOT' (
            'The internal source root must not exist.') $null
    }
    $wrapperText = Render-FslRabWrapper `
        $Model `
        $authority.roots `
        $authority.repository `
        $authority.execution `
        $authority.release
    if (-not (Test-FslRabWrapperAst $wrapperText)) {
        Stop-FslRab 'FSL-RAB-V016-WRAPPER' (
            'The generated wrapper AST is not the exact non-duplicated reconciler form.') $null
    }
    $wrapperBytes = Get-FslRabBytes $wrapperText
    $contract = New-FslRabContractBase $Model $authority $wrapperBytes
    $contractBytes = Complete-FslRabContract $contract
    $hashes = @{
        'elevated-reconcile.ps1' = Get-FslRabSha256Bytes $wrapperBytes
        'recovery-contract.json' = Get-FslRabSha256Bytes $contractBytes
    }
    $sid = $authority.operator.userSid
    try {
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Set-FslRabSddl $root (Get-FslRabSddl $sid $true) $true
        foreach ($item in @(
            @('elevated-reconcile.ps1', $wrapperBytes),
            @('recovery-contract.json', $contractBytes))) {
            $path = Join-Path $root $item[0]
            Write-FslRabNew $path ([byte[]]$item[1])
            Set-FslRabSddl $path (Get-FslRabSddl $sid $false) $false
        }
    }
    catch {
        Remove-FslRabPartial $root $hashes
        if ($_.Exception.Data.Contains('FslRecoveryAuthorityBundleCode')) {
            throw
        }
        Stop-FslRab 'FSL-RAB-V002-ROOT' (
            'Recovery authority bundle generation failed.') $_.Exception
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        bundleRoot = $root
        contractCanonicalSha256 =
            [string]$contract.bindingManifest.contractCanonicalSha256
        observedFiles = @(Get-FslRabObservedFiles $root)
    }
}

function Test-FslStage4RecoveryAuthorityBundle {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNull()]
        [psobject]$Model)
    $errors = [Collections.Generic.List[object]]::new()
    $root = $null
    $observed = @()
    $opaque = $null
    try {
        Assert-FslRabModel $Model
        $authority = Get-FslRabAuthority $Model
        $root = $authority.roots.sourceRoot
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            Stop-FslRab 'FSL-RAB-V002-ROOT' (
                'The recovery authority source root is absent.') $null
        }
        $children = @(Get-ChildItem -LiteralPath $root -Force)
        if ($children.Count -ne 2 -or
            (@($children | ForEach-Object Name) -join '|') -cne
                'elevated-reconcile.ps1|recovery-contract.json') {
            Stop-FslRab 'FSL-RAB-V003-FILESET' (
                'The recovery authority root is not exact-two with exact case.') $null
        }
        $wrapperText = Render-FslRabWrapper `
            $Model `
            $authority.roots `
            $authority.repository `
            $authority.execution `
            $authority.release
        $wrapperBytes = Get-FslRabBytes $wrapperText
        $expected = New-FslRabContractBase $Model $authority $wrapperBytes
        $contractBytes = Complete-FslRabContract $expected
        $wrapperPath = Join-Path $root 'elevated-reconcile.ps1'
        $contractPath = Join-Path $root 'recovery-contract.json'
        foreach ($item in @(
            @($wrapperPath, $wrapperBytes),
            @($contractPath, $contractBytes))) {
            if (-not (Test-Path -LiteralPath $item[0] -PathType Leaf) -or
                (Get-FslRabSha256 $item[0]) -cne
                    (Get-FslRabSha256Bytes ([byte[]]$item[1]))) {
                Stop-FslRab 'FSL-RAB-V004-FILE-BYTES' (
                    'A recovery authority file is not its canonical byte sequence.') $null
            }
        }
        $raw = [IO.File]::ReadAllText(
            $contractPath,
            [Text.UTF8Encoding]::new($false, $true))
        $parsed = $raw | ConvertFrom-Json
        if (-not (Test-FslRabNames $parsed $script:RabContractNames) -or
            $parsed.schemaVersion -isnot [int] -or
            [int]$parsed.schemaVersion -ne 3 -or
            -not (Test-FslRabGateMap @($parsed.contractStageGates)) -or
            [int]$parsed.bindingManifest.contractLength -ne
                [Text.UTF8Encoding]::new($false, $true).GetByteCount($raw) -or
            [string]$parsed.executionStateAuthority.gitCommit -ceq
                [string]$parsed.recoveryToolchainAuthority.gitCommit) {
            Stop-FslRab 'FSL-RAB-V005-CONTRACT-CANONICAL' (
                'The schema-v3 canonical dual-authority contract drifted.') $null
        }
        if (-not (Test-FslRabWrapperAst $wrapperText)) {
            Stop-FslRab 'FSL-RAB-V016-WRAPPER' (
                'The recovery wrapper AST drifted.') $null
        }
        $sid = $authority.operator.userSid
        if (-not (Test-FslRabProtectedAcl $root $true $sid) -or
            -not (Test-FslRabProtectedAcl $wrapperPath $false $sid) -or
            -not (Test-FslRabProtectedAcl $contractPath $false $sid)) {
            Stop-FslRab 'FSL-RAB-V017-ACL' (
                'The recovery authority exact-three-principal ACL drifted.') $null
        }
        $observed = @(Get-FslRabObservedFiles $root)
        $opaque = [pscustomobject][ordered]@{
            contractId = [string]$parsed.contractId
            contractSha256 = Get-FslRabSha256 $contractPath
            wrapperSha256 = [string]$parsed.bindingManifest.wrapperSha256
            executionStateAuthoritySha256 =
                [string]$parsed.bindingManifest.executionStateAuthoritySha256
            recoveryToolchainAuthoritySha256 =
                [string]$parsed.bindingManifest.recoveryToolchainAuthoritySha256
            toolchainRepositorySha256 =
                [string]$parsed.bindingManifest.toolchainRepositorySha256
            recoveryGateMapSha256 =
                [string]$parsed.bindingManifest.recoveryGateMapSha256
            executionGitCommit =
                [string]$parsed.executionStateAuthority.gitCommit
            recoveryGitCommit =
                [string]$parsed.recoveryToolchainAuthority.gitCommit
            recoveryGitTree =
                [string]$parsed.recoveryToolchainAuthority.gitTree
            recoveryRepository =
                [string]$parsed.recoveryToolchainAuthority.repository
            recoveryGitRoot = [string]$authority.repository.gitRoot
            recoveryGitDirectory =
                [string]$authority.repository.gitDirectory
            recoveryBranch =
                [string]$parsed.recoveryToolchainAuthority.branch
            recoveryTrackedClean =
                [bool]$parsed.recoveryToolchainAuthority.trackedClean
            gates = @($parsed.contractStageGates)
            futureInvocation = $parsed.futureInvocation
            executionEvidence = $parsed.executionStateAuthority.files
            canonicalEvidence = $parsed.canonicalEvidence
            externalAnchors = $parsed.externalAnchors
            release = $parsed.release
            transaction = $parsed.transaction
            systemPrestate = $parsed.systemPrestate
            sourceRoot = [string]$parsed.recoverySource.root
        }
    }
    catch {
        $code = [string]$_.Exception.Data[
            'FslRecoveryAuthorityBundleCode']
        if ([string]::IsNullOrEmpty($code)) {
            $code = 'FSL-RAB-V001-MODEL'
        }
        [void]$errors.Add((New-FslRabError `
            $code `
            'authority' `
            $_.Exception.Message))
    }
    $sorted = @(Sort-FslRabErrors $errors)
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        isValid = $sorted.Count -eq 0
        bundleRoot = $root
        errors = $sorted
        observedFiles = $observed
        opaqueAuthority = $opaque
    }
}

Export-ModuleMember -Function @(
    'New-FslStage4RecoveryAuthorityBundle',
    'Test-FslStage4RecoveryAuthorityBundle')
