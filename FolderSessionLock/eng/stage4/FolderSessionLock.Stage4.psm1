Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$nativeSource = Join-Path $PSScriptRoot 'FolderSessionLock.Stage4.Native.cs'
if (-not ('FolderSessionLock.Stage4.Native' -as [type])) {
    Add-Type -Path $nativeSource -ReferencedAssemblies @(
        'System.dll',
        'System.Core.dll',
        'System.Security.dll')
}
if (-not ('FolderSessionLock.Stage4.WalFileInformation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderSessionLock.Stage4
{
    public static class WalFileInformation
    {
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        public static uint GetLinkCount(string path)
        {
            using (SafeFileHandle handle = CreateFile(
                path,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                ByHandleFileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return information.NumberOfLinks;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);
    }
}
'@
}

$script:ExpectedMachine = 'FSL-STAGE4-VM'
$script:ExpectedBranch = 'cp10-vm-transfer'
$script:ApprovedCommit = '2c463c6f6c707079bf2f79f6175bb1dbca63012c'
$script:ServiceName = 'FolderSessionLockRecovery'
$script:ServiceDisplayName = 'Folder Session Lock Recovery Service'
$script:ServiceDescription =
    'Removes verified Folder Session Lock ACL entries left by previous Windows logon sessions.'
$script:TestCertificatePrefix = 'FolderSessionLock Stage4 VM Test Signing'
$script:RunIdPattern = '^\d{8}T\d{6}Z-[0-9a-f]{8}$'
$script:ThumbprintPattern = '^[0-9A-Fa-f]{40}$'
$script:ApprovedMicrosoftSignToolSpkiSha256 = @(
    'AE002463D94D86D83D468162495B8AA28D178CD831161A2DDF0C252566511146')
$script:FirstPartyPortableExecutables = @(
    'FolderSessionLock.App.exe',
    'FolderSessionLock.App.dll',
    'FolderSessionLock.Broker.exe',
    'FolderSessionLock.Broker.dll',
    'FolderSessionLock.Core.dll',
    'FolderSessionLock.Windows.dll')
$script:StateSchemaVersion = 1
$script:WalSchemaVersion = 3
$script:WalPrimitiveKinds = @(
    'DirectoryCreate',
    'DirectorySetAcl',
    'FileCopyAtomic',
    'ServiceCreate',
    'ServiceDescription',
    'ServiceSid',
    'ServiceDelayed',
    'ServiceStop',
    'ServiceDelete',
    'DeleteFile',
    'DeleteDirectory',
    'CertificateDelete')
$script:KnownTransitions = @(
    'PreflightCaptured',
    'CertificateCreating',
    'CertificateReady',
    'CertificateRolledBack',
    'PublishCompleted',
    'SignatureVerified',
    'InstallStarted',
    'PlatformReadinessVerified',
    'ServiceCreated',
    'Installed',
    'Verified',
    'LogoutPrepared',
    'RestartPrepared',
    'Resumed',
    'Uninstalled',
    'CleanupCompleted',
    'EvidenceFinalized')
$script:ExitCodes = @{
    Success = 0
    InvalidArguments = 2
    EnvironmentGate = 3
    PreExistingConflict = 4
    Signing = 5
    InstallAcl = 6
    Service = 7
    ValidationEvidence = 8
    Cleanup = 9
}

function Write-FslUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Stop-FslStage4 {
    param(
        [Parameter(Mandatory = $true)][int]$ExitCode,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $exception = [System.InvalidOperationException]::new($Message)
    $exception.Data['FslStage4ExitCode'] = $ExitCode
    throw $exception
}

function Get-FslRepositoryRoot {
    $root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    if (-not (Test-Path -LiteralPath (Join-Path $root 'FolderSessionLock.sln') -PathType Leaf)) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate 'The Stage 4 tool is not inside the authoritative repository.'
    }

    return $root
}

function Test-FslFullyQualifiedPath {
    param([AllowNull()][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not [System.IO.Path]::IsPathRooted($Path)) {
        return $false
    }

    try {
        $root = [System.IO.Path]::GetPathRoot($Path)
        $full = [System.IO.Path]::GetFullPath($Path)
        return -not [string]::IsNullOrWhiteSpace($root) -and
            $full.Length -gt $root.Length
    }
    catch {
        return $false
    }
}

function Get-FslKnownFolder {
    param([Parameter(Mandatory = $true)][Environment+SpecialFolder]$Folder)

    $path = [Environment]::GetFolderPath($Folder)
    if (-not (Test-FslFullyQualifiedPath $path)) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate "The required Windows known folder is unavailable: $Folder."
    }

    return [System.IO.Path]::GetFullPath($path)
}

function Get-FslContext {
    param(
        [Parameter(Mandatory = $true)][string]$RunId,
        [string]$ReleaseRoot
    )

    if ($RunId -cnotmatch $script:RunIdPattern) {
        Stop-FslStage4 $script:ExitCodes.InvalidArguments 'RunId must match yyyyMMddTHHmmssZ-<8 lowercase hex>.'
    }

    $repository = Get-FslRepositoryRoot
    $evidence = Join-Path $repository (Join-Path 'docs\evidence\stage-4' $RunId)
    $programFiles = Get-FslKnownFolder ([Environment+SpecialFolder]::ProgramFiles)
    $programData = Get-FslKnownFolder ([Environment+SpecialFolder]::CommonApplicationData)
    $localAppData = Get-FslKnownFolder ([Environment+SpecialFolder]::LocalApplicationData)
    $install = Join-Path $programFiles 'FolderSessionLock'
    $data = Join-Path $programData 'FolderSessionLock'
    $version = '1.0.0'
    [xml]$project = Get-Content -LiteralPath (
        Join-Path $repository 'src\FolderSessionLock.App\FolderSessionLock.App.csproj') -Raw
    $declaredVersion = @($project.SelectNodes(
            '/Project/PropertyGroup/Version') |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Select-Object -First 1)
    if ($declaredVersion.Count -eq 1) {
        $version = [string]$declaredVersion[0]
    }
    $commit = (& git.exe -C $repository rev-parse HEAD 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate 'The release commit could not be determined.'
    }
    $expectedRelease = Join-Path (Join-Path 'C:\FSL-Release' $version) $commit
    $release = $expectedRelease
    if (-not [string]::IsNullOrWhiteSpace($ReleaseRoot) -and
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath($ReleaseRoot),
            [System.IO.Path]::GetFullPath($expectedRelease),
            [StringComparison]::OrdinalIgnoreCase)) {
        Stop-FslStage4 $script:ExitCodes.InvalidArguments (
            'ReleaseRoot must be exactly C:\FSL-Release\<version>\<commit>.')
    }

    $release = [System.IO.Path]::GetFullPath($release)
    if (-not $release.StartsWith('C:\FSL-Release\', [StringComparison]::OrdinalIgnoreCase)) {
        Stop-FslStage4 $script:ExitCodes.InvalidArguments 'ReleaseRoot must be below C:\FSL-Release\.'
    }

    $externalAnchorRoot = Join-Path $localAppData (
        Join-Path 'FolderSessionLock\Stage4\Anchors' $RunId)
    return [pscustomobject]@{
        RunId = $RunId
        RepositoryRoot = $repository
        EvidenceRoot = $evidence
        ReleaseRoot = $release
        InstallDirectory = $install
        ProgramDataRoot = $data
        BrokerPath = (Join-Path $install 'FolderSessionLock.Broker.exe')
        PrestatePath = (Join-Path $evidence 'prestate.json')
        StatePath = (Join-Path $evidence 'stage4-state.json')
        JournalPath = (Join-Path $evidence 'stage4-journal.jsonl')
        AnchorPath = (Join-Path $evidence 'stage4-anchor.json')
        InstallWalPath = (Join-Path $evidence 'install-wal.jsonl')
        CommandsPath = (Join-Path $evidence 'commands.txt')
        ExternalAnchorRoot = $externalAnchorRoot
        ExternalAnchorKeyPath = (Join-Path $externalAnchorRoot 'key.dpapi')
        ExternalAnchorSlot0Path = (Join-Path $externalAnchorRoot 'anchor-0.json')
        ExternalAnchorSlot1Path = (Join-Path $externalAnchorRoot 'anchor-1.json')
    }
}

function Assert-FslMachineGate {
    $names = @(
        $env:COMPUTERNAME,
        [Environment]::MachineName,
        (& hostname.exe),
        (Get-CimInstance Win32_ComputerSystem).Name)
    foreach ($name in $names) {
        if (-not [string]::Equals(
            ([string]$name).Trim(),
            $script:ExpectedMachine,
            [StringComparison]::OrdinalIgnoreCase)) {
            Stop-FslStage4 $script:ExitCodes.EnvironmentGate "Stage 4 system operations require $($script:ExpectedMachine)."
        }
    }

    if (-not [Environment]::Is64BitOperatingSystem) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate 'Stage 4 requires a 64-bit operating system.'
    }

    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    if ($operatingSystem.Caption -notmatch 'Windows 11 (Pro|Enterprise)') {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate 'Stage 4 requires Windows 11 Pro or Enterprise.'
    }

    $pending = @(
        (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'),
        (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'),
        ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue) -ne $null)
    )
    if ($pending -contains $true) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate 'A pending Windows restart blocks Stage 4.'
    }
}

function Get-FslSecureBootRegistryEvidence {
    $path = 'SYSTEM\CurrentControlSet\Control\SecureBoot\State'
    $key = $null
    try {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::LocalMachine,
            [Microsoft.Win32.RegistryView]::Registry64)
        try {
            $key = $base.OpenSubKey($path, $false)
            if ($null -eq $key) {
                Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
                    'The fixed Secure Boot registry key is missing.')
            }
            $kind = $key.GetValueKind('UEFISecureBootEnabled')
            $value = $key.GetValue(
                'UEFISecureBootEnabled',
                $null,
                [Microsoft.Win32.RegistryValueOptions]::
                    DoNotExpandEnvironmentNames)
        }
        finally {
            if ($null -ne $key) {
                $key.Dispose()
            }
            $base.Dispose()
        }
    }
    catch {
        if ($_.Exception.Data['FslStage4ExitCode']) {
            throw
        }
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'The fixed Secure Boot registry value could not be read.')
    }
    return [pscustomobject][ordered]@{
        path = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State'
        name = 'UEFISecureBootEnabled'
        kind = [string]$kind
        valueType = if ($null -eq $value) {
            $null
        }
        else {
            $value.GetType().FullName
        }
        rawValue = $value
    }
}

function Get-FslNativeTpmDeviceInfo {
    $raw = [FolderSessionLock.Stage4.Native]::GetTpmDeviceInfo()
    return [pscustomobject][ordered]@{
        result = [uint32]$raw.Result
        structVersion = [uint32]$raw.StructVersion
        tpmVersion = [uint32]$raw.TpmVersion
        tpmInterfaceType = [uint32]$raw.TpmInterfaceType
        tpmImpRevision = [uint32]$raw.TpmImpRevision
    }
}

function Test-FslCurrentTokenAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-FslPreflightPlatformEvidence {
    param(
        [AllowNull()][psobject]$SecureBoot,
        [AllowNull()][psobject]$Tpm,
        [AllowNull()]$IsElevated
    )

    if ($null -eq $SecureBoot -or
        $SecureBoot.path -cne
            'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -or
        $SecureBoot.name -cne 'UEFISecureBootEnabled' -or
        $SecureBoot.kind -cne 'DWord' -or
        $SecureBoot.valueType -cne 'System.Int32' -or
        $SecureBoot.rawValue -isnot [int] -or
        [int]$SecureBoot.rawValue -ne 1) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'Secure Boot registry attestation failed.')
    }
    if ($null -eq $Tpm -or
        $Tpm.result -notin @([int]0, [uint32]0) -or
        [uint32]$Tpm.structVersion -ne 1 -or
        [uint32]$Tpm.tpmVersion -ne 2 -or
        [uint32]$Tpm.tpmInterfaceType -notin @(1, 2)) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'Native TPM device attestation failed.')
    }
    if ($IsElevated -isnot [bool] -or [bool]$IsElevated) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'Preflight requires a non-elevated current token.')
    }
}

function Assert-FslAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate 'This command requires an elevated administrator PowerShell.'
    }
}

function Get-FslGitValue {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & git.exe -C $Context.RepositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate "git failed: $($output -join ' ')"
    }

    return (($output | Out-String).Trim())
}

function Assert-FslRepositoryGate {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    $branch = Get-FslGitValue $Context @('branch', '--show-current')
    if ($branch -cne $script:ExpectedBranch) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate "Expected branch $($script:ExpectedBranch)."
    }

    $head = Get-FslGitValue $Context @('rev-parse', 'HEAD')
    $approvedAncestor = & git.exe -C $Context.RepositoryRoot merge-base --is-ancestor $script:ApprovedCommit $head
    if ($LASTEXITCODE -ne 0) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate 'HEAD is not descended from the approved CP9 commit.'
    }

    $operationMarkers = @(
        'MERGE_HEAD',
        'REBASE_HEAD',
        'CHERRY_PICK_HEAD',
        'REVERT_HEAD',
        'BISECT_LOG')
    $gitDirectory = Get-FslGitValue $Context @('rev-parse', '--git-dir')
    if (-not (Test-FslFullyQualifiedPath $gitDirectory)) {
        $gitDirectory = Join-Path $Context.RepositoryRoot $gitDirectory
    }

    foreach ($marker in $operationMarkers) {
        if (Test-Path -LiteralPath (Join-Path $gitDirectory $marker)) {
            Stop-FslStage4 $script:ExitCodes.EnvironmentGate "Git operation in progress: $marker."
        }
    }
}

function Assert-FslRepositoryMutationGate {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    $raw = & git.exe -C $Context.RepositoryRoot status `
        --porcelain=v1 -z --untracked-files=all 2>$null
    if ($LASTEXITCODE -ne 0) {
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'The repository mutation set could not be determined.')
    }
    $allowedPrefix = (
        'docs/evidence/stage-4/' + $Context.RunId + '/')
    foreach ($record in @($raw -split "`0" | Where-Object { $_.Length -gt 0 })) {
        if ($record.Length -lt 4) {
            Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
                'The repository mutation record is invalid.')
        }
        $path = $record.Substring(3).Replace('\', '/')
        if (-not $path.StartsWith($allowedPrefix, [StringComparison]::Ordinal)) {
            Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
                "Only current-Run evidence may mutate after preflight: $path.")
        }
    }
}

function ConvertTo-FslThumbprint {
    param(
        [Parameter(Mandatory = $true)][string]$Thumbprint,
        [int]$FailureCode = 2
    )

    if ($Thumbprint -cnotmatch $script:ThumbprintPattern) {
        Stop-FslStage4 $FailureCode 'A publisher or signing thumbprint must be exactly 40 hexadecimal characters.'
    }

    return $Thumbprint.ToUpperInvariant()
}

function Get-FslPathProof {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-FslStage4 $script:ExitCodes.Cleanup "A protected path is a reparse point: $Path."
    }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    $expected = [System.IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals(
        $resolved.TrimEnd('\'),
        $expected.TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase)) {
        Stop-FslStage4 $script:ExitCodes.Cleanup "A protected final path changed: $Path."
    }
    $identityOutput = & fsutil.exe file queryFileID $expected 2>&1
    if ($LASTEXITCODE -ne 0) {
        Stop-FslStage4 $script:ExitCodes.Cleanup "A protected file identity is unavailable: $Path."
    }
    $identityText = ($identityOutput | Out-String)
    $match = [regex]::Match($identityText, '0x(?<id>[0-9a-fA-F]{32})')
    if (-not $match.Success) {
        Stop-FslStage4 $script:ExitCodes.Cleanup "A protected file identity is invalid: $Path."
    }
    return [pscustomobject]@{
        finalPath = $expected.TrimEnd('\')
        fileId = $match.Groups['id'].Value.ToUpperInvariant()
        aclSddl = (Get-Acl -LiteralPath $expected).Sddl
    }
}

function Assert-FslPathProof {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][psobject]$Expected
    )
    $actual = Get-FslPathProof $Path
    if ($actual.finalPath -cne $Expected.finalPath -or
        $actual.fileId -cne $Expected.fileId -or
        $actual.aclSddl -cne $Expected.aclSddl) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            "Protected path identity, final path, or ACL changed: $Path.")
    }
}

function Test-FslExactSystemAdminServiceAcl {
    param([Parameter(Mandatory = $true)][string]$Path)

    $acl = Get-Acl -LiteralPath $Path
    $owner = ([Security.Principal.NTAccount]$acl.Owner).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    if ($owner -cne 'S-1-5-18' -or -not $acl.AreAccessRulesProtected) {
        return $false
    }
    $serviceSid = ([Security.Principal.NTAccount]"NT SERVICE\$($script:ServiceName)").Translate(
        [Security.Principal.SecurityIdentifier]).Value
    $expected = @('S-1-5-18', 'S-1-5-32-544', $serviceSid)
    $rules = @($acl.GetAccessRules(
        $true,
        $false,
        [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 3) {
        return $false
    }
    for ($index = 0; $index -lt 3; $index++) {
        $rule = $rules[$index]
        if ($rule.IdentityReference.Value -cne $expected[$index] -or
            $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            $rule.FileSystemRights -ne [Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.InheritanceFlags -ne [Security.AccessControl.InheritanceFlags]::None -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            return $false
        }
    }
    return $true
}

function Add-FslCommandEvidence {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$CommandLine
    )

    $line = '{0:o} {1}' -f [DateTime]::UtcNow, $CommandLine
    $existing = ''
    if (Test-Path -LiteralPath $Context.CommandsPath) {
        $existing = [System.IO.File]::ReadAllText($Context.CommandsPath)
    }

    Write-FslUtf8NoBom $Context.CommandsPath ($existing + $line + [Environment]::NewLine)
}

function Get-FslSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash($Bytes)).Replace('-', '')
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-FslExternalAnchorEntropy {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    return [System.Text.UTF8Encoding]::new($false).GetBytes(
        "FolderSessionLock.Stage4.Anchor.v1`n$($Context.RunId)`n" +
        [Environment]::MachineName)
}

function Set-FslExternalAnchorAcl {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    [System.IO.Directory]::CreateDirectory($Context.ExternalAnchorRoot) | Out-Null
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($identity)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($identity, $system, $administrators)) {
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    [System.IO.Directory]::SetAccessControl($Context.ExternalAnchorRoot, $security)
}

function Initialize-FslExternalAnchor {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    if (Test-Path -LiteralPath $Context.ExternalAnchorRoot) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict (
            'The protected external anchor already exists.')
    }
    Set-FslExternalAnchorAcl $Context
    $key = [FolderSessionLock.Stage4.Native]::RandomBytes(32)
    $protected = [FolderSessionLock.Stage4.Native]::ProtectCurrentUser(
        $key,
        (Get-FslExternalAnchorEntropy $Context))
    [FolderSessionLock.Stage4.Native]::AtomicWrite(
        $Context.ExternalAnchorKeyPath,
        $protected)
}

function Get-FslExternalAnchorKey {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    if (-not (Test-Path -LiteralPath $Context.ExternalAnchorKeyPath -PathType Leaf)) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The protected external anchor key is missing.')
    }
    try {
        return [FolderSessionLock.Stage4.Native]::UnprotectCurrentUser(
            [System.IO.File]::ReadAllBytes($Context.ExternalAnchorKeyPath),
            (Get-FslExternalAnchorEntropy $Context))
    }
    catch {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The protected external anchor key is invalid.')
    }
}

function Get-FslBindingFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            path = [System.IO.Path]::GetFileName($Path)
            exists = $false
            length = 0
            sha256 = $null
        }
    }
    return [pscustomobject]@{
        path = [System.IO.Path]::GetFileName($Path)
        exists = $true
        length = (Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Get-FslExternalAnchorBinding {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    return [ordered]@{
        runId = $Context.RunId
        machineName = [Environment]::MachineName
        repositoryRoot = [System.IO.Path]::GetFullPath($Context.RepositoryRoot)
        branch = (Get-FslGitValue $Context @('branch', '--show-current'))
        gitCommit = (Get-FslGitValue $Context @('rev-parse', 'HEAD'))
        prestate = Get-FslBindingFile $Context.PrestatePath
        journal = Get-FslBindingFile $Context.JournalPath
        wal = Get-FslBindingFile $Context.InstallWalPath
        state = Get-FslBindingFile $Context.StatePath
        stateAnchor = Get-FslBindingFile $Context.AnchorPath
    }
}

function Get-FslValidExternalAnchorSlots {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    $key = Get-FslExternalAnchorKey $Context
    $valid = @()
    foreach ($path in @(
        $Context.ExternalAnchorSlot0Path,
        $Context.ExternalAnchorSlot1Path)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        try {
            $slot = [System.IO.File]::ReadAllText($path) | ConvertFrom-Json
            $payloadBytes = [Convert]::FromBase64String([string]$slot.payload)
            $calculated = [FolderSessionLock.Stage4.Native]::HmacSha256(
                $key,
                $payloadBytes)
            if (-not [FolderSessionLock.Stage4.Native]::FixedTimeEqualsHex(
                $calculated,
                [string]$slot.hmacSha256)) {
                continue
            }
            $payload = [System.Text.UTF8Encoding]::new($false, $true).GetString(
                $payloadBytes) | ConvertFrom-Json
            if ($payload.schemaVersion -ne 1 -or
                $payload.runId -cne $Context.RunId -or
                $payload.machineName -cne [Environment]::MachineName -or
                [int64]$payload.generation -lt 1) {
                continue
            }
            $valid += [pscustomobject]@{
                Path = $path
                Slot = $slot
                Payload = $payload
            }
        }
        catch {
        }
    }
    return @($valid | Sort-Object { [int64]$_.Payload.generation } -Descending)
}

function Write-FslExternalAnchor {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    Set-FslExternalAnchorAcl $Context
    $key = Get-FslExternalAnchorKey $Context
    $valid = @(Get-FslValidExternalAnchorSlots $Context)
    $generation = if ($valid.Count -eq 0) {
        1L
    }
    else {
        [int64]$valid[0].Payload.generation + 1L
    }
    $payload = [ordered]@{
        schemaVersion = 1
        runId = $Context.RunId
        machineName = [Environment]::MachineName
        generation = $generation
        recordedUtc = [DateTime]::UtcNow.ToString('o')
        binding = Get-FslExternalAnchorBinding $Context
    }
    $payloadBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        ($payload | ConvertTo-Json -Compress -Depth 20))
    $slot = [ordered]@{
        payload = [Convert]::ToBase64String($payloadBytes)
        hmacSha256 = [FolderSessionLock.Stage4.Native]::HmacSha256(
            $key,
            $payloadBytes)
    }
    $target = if (($generation % 2) -eq 0) {
        $Context.ExternalAnchorSlot0Path
    }
    else {
        $Context.ExternalAnchorSlot1Path
    }
    Write-FslAtomicUtf8NoBom $target (
        ($slot | ConvertTo-Json -Compress) + [Environment]::NewLine)
    if ($valid.Count -eq 0) {
        Write-FslExternalAnchor $Context
    }
}

function Assert-FslExternalAnchor {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    $valid = @(Get-FslValidExternalAnchorSlots $Context)
    if ($valid.Count -eq 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'No valid protected external anchor slot exists.')
    }
    $anchoredWal = $valid[0].Payload.binding.wal
    $currentWalExists =
        Test-Path -LiteralPath $Context.InstallWalPath -PathType Leaf
    if (-not $anchoredWal.exists -and $currentWalExists) {
        [FolderSessionLock.Stage4.Native]::Truncate(
            $Context.InstallWalPath,
            0L)
        [System.IO.File]::Delete($Context.InstallWalPath)
    }
    elseif ($anchoredWal.exists -and -not $currentWalExists) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The installation WAL is missing before its protected anchor.')
    }
    elseif ($anchoredWal.exists -and $currentWalExists) {
        $currentWalLength = (Get-Item -LiteralPath $Context.InstallWalPath).Length
        if ($currentWalLength -lt [int64]$anchoredWal.length) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'The installation WAL was truncated before its protected anchor.')
        }
        if ($currentWalLength -gt [int64]$anchoredWal.length) {
            [FolderSessionLock.Stage4.Native]::Truncate(
                $Context.InstallWalPath,
                [int64]$anchoredWal.length)
        }
    }
    $expected = $valid[0].Payload.binding | ConvertTo-Json -Compress -Depth 20
    $actual = (Get-FslExternalAnchorBinding $Context) |
        ConvertTo-Json -Compress -Depth 20
    if ($expected -cne $actual) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Repository, prestate, journal, WAL, state, or anchor binding changed.')
    }
    return $valid[0]
}

function Remove-FslExternalAnchor {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    if (-not (Test-Path -LiteralPath $Context.ExternalAnchorRoot)) {
        return
    }
    foreach ($path in @(
        $Context.ExternalAnchorSlot0Path,
        $Context.ExternalAnchorSlot1Path,
        $Context.ExternalAnchorKeyPath)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [System.IO.File]::Delete($path)
        }
    }
    if (@(Get-ChildItem -LiteralPath $Context.ExternalAnchorRoot -Force).Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'Protected external anchor retirement found unknown content.')
    }
    [System.IO.Directory]::Delete($Context.ExternalAnchorRoot, $false)
}

function Write-FslAtomicUtf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content
    )

    [FolderSessionLock.Stage4.Native]::AtomicWrite(
        $Path,
        [System.Text.UTF8Encoding]::new($false).GetBytes($Content))
}

function Add-FslWriteThroughLine {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Line
    )

    if ($Line.Contains("`r") -or $Line.Contains("`n")) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'A journal record must be one physical line.')
    }
    $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        $Line)
    $before = 0L
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $before = (Get-Item -LiteralPath $Path).Length
    }
    $after = [FolderSessionLock.Stage4.Native]::AppendLine($Path, $bytes)
    return ($after - $before)
}

function ConvertTo-FslJournalCore {
    param([Parameter(Mandatory = $true)][psobject]$Entry)

    return [ordered]@{
        schemaVersion = [int]$Entry.schemaVersion
        runId = [string]$Entry.runId
        machineName = [string]$Entry.machineName
        branch = [string]$Entry.branch
        gitCommit = [string]$Entry.gitCommit
        sequence = [int]$Entry.sequence
        transition = [string]$Entry.transition
        recordedUtc = [string]$Entry.recordedUtc
        previousEntrySha256 = [string]$Entry.previousEntrySha256
        state = $Entry.state
    }
}

function Read-FslAnchoredJournal {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    if (-not (Test-Path -LiteralPath $Context.JournalPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $Context.AnchorPath -PathType Leaf)) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The Stage 4 journal or independent anchor is missing.')
    }
    try {
        $anchor = [System.IO.File]::ReadAllText($Context.AnchorPath) | ConvertFrom-Json
    }
    catch {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The Stage 4 journal anchor is torn or invalid.')
    }
    $journalBytes = [System.IO.File]::ReadAllBytes($Context.JournalPath)
    $anchoredLength = [long]$anchor.journalLength
    if ($anchoredLength -lt 1 -or $journalBytes.LongLength -lt $anchoredLength) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The Stage 4 journal was truncated before its independent anchor.')
    }
    if ($journalBytes.LongLength -gt $anchoredLength) {
        $tailLength = [int]($journalBytes.LongLength - $anchoredLength)
        $tail = [System.Text.Encoding]::UTF8.GetString(
            $journalBytes,
            [int]$anchoredLength,
            $tailLength)
        if ($tail.EndsWith("`n", [StringComparison]::Ordinal)) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'A complete unanchored Stage 4 journal record was found.')
        }
        [FolderSessionLock.Stage4.Native]::Truncate(
            $Context.JournalPath,
            $anchoredLength)
        $journalBytes = $journalBytes[0..([int]$anchoredLength - 1)]
    }

    $text = [System.Text.Encoding]::UTF8.GetString($journalBytes)
    if (-not $text.EndsWith("`n", [StringComparison]::Ordinal)) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The anchored Stage 4 journal does not end on a record boundary.')
    }
    $lines = @($text -split "`n" |
        ForEach-Object { $_.TrimEnd("`r") } |
        Where-Object { $_.Length -gt 0 })
    if ($lines.Count -eq 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'The Stage 4 journal is empty.'
    }
    $previous = '0' * 64
    $entries = @()
    for ($index = 0; $index -lt $lines.Count; $index++) {
        try {
            $entry = $lines[$index] | ConvertFrom-Json
        }
        catch {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'An anchored Stage 4 journal record is invalid.')
        }
        $core = ConvertTo-FslJournalCore $entry
        $coreBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
            ($core | ConvertTo-Json -Compress -Depth 20))
        $calculated = Get-FslSha256 $coreBytes
        if ($entry.entrySha256 -cne $calculated -or
            $entry.previousEntrySha256 -cne $previous -or
            [int]$entry.sequence -ne ($index + 1) -or
            $entry.runId -cne $Context.RunId -or
            $entry.machineName -cne [Environment]::MachineName -or
            $entry.transition -cnotin $script:KnownTransitions) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'The Stage 4 journal hash chain or identity is invalid.')
        }
        $previous = $calculated
        $entries += $entry
    }
    $last = $entries[-1]
    if ($anchor.schemaVersion -ne $script:StateSchemaVersion -or
        $anchor.runId -cne $Context.RunId -or
        $anchor.machineName -cne [Environment]::MachineName -or
        [int]$anchor.sequence -ne [int]$last.sequence -or
        $anchor.entrySha256 -cne $last.entrySha256 -or
        [long]$anchor.journalLength -ne $journalBytes.LongLength) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The Stage 4 journal does not match its independent anchor.')
    }
    return [pscustomobject]@{
        Anchor = $anchor
        Entries = $entries
        Last = $last
    }
}

function Resolve-FslPlatformReadinessState {
    param([Parameter(Mandatory = $true)][psobject]$State)

    $properties = @(
        'PlatformReadinessStatus',
        'SecureBootVerified',
        'TpmNativeVerified',
        'TpmCmdletVerified',
        'PlatformReadinessVerifiedUtc')
    $present = @($properties | Where-Object {
        $State.PSObject.Properties.Name -ccontains $_
    })
    if ($present.Count -eq 0) {
        foreach ($property in ([ordered]@{
            PlatformReadinessStatus = 'DeferredUntilElevated'
            SecureBootVerified = $false
            TpmNativeVerified = $false
            TpmCmdletVerified = $false
            PlatformReadinessVerifiedUtc = $null
        }).GetEnumerator()) {
            Add-Member -InputObject $State `
                -NotePropertyName $property.Key `
                -NotePropertyValue $property.Value
        }
        return $State
    }
    if ($present.Count -ne $properties.Count) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Platform readiness state is partial.')
    }
    if ($State.SecureBootVerified -isnot [bool] -or
        $State.TpmNativeVerified -isnot [bool] -or
        $State.TpmCmdletVerified -isnot [bool]) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Platform readiness verification flags are invalid.')
    }
    if ($State.PlatformReadinessStatus -ceq 'DeferredUntilElevated') {
        if ($State.SecureBootVerified -or
            $State.TpmNativeVerified -or
            $State.TpmCmdletVerified -or
            $null -ne $State.PlatformReadinessVerifiedUtc) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'Deferred platform readiness state is invalid.')
        }
        return $State
    }
    if ($State.PlatformReadinessStatus -ceq 'Verified') {
        $verifiedUtc = [DateTimeOffset]::MinValue
        if (-not $State.SecureBootVerified -or
            -not $State.TpmNativeVerified -or
            -not $State.TpmCmdletVerified -or
            $State.PlatformReadinessVerifiedUtc -isnot [string] -or
            -not [DateTimeOffset]::TryParseExact(
                [string]$State.PlatformReadinessVerifiedUtc,
                'o',
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::RoundtripKind,
                [ref]$verifiedUtc)) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'Verified platform readiness state is invalid.')
        }
        return $State
    }
    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
        'Platform readiness status is invalid.')
}

function Read-FslState {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    if (-not (Test-Path -LiteralPath $Context.PrestatePath -PathType Leaf)) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'Preflight evidence is missing.'
    }
    $chain = Read-FslAnchoredJournal $Context
    $state = $chain.Last.state
    $stateContent = ($state | ConvertTo-Json -Depth 20) + [Environment]::NewLine
    $cacheValid = $false
    if (Test-Path -LiteralPath $Context.StatePath -PathType Leaf) {
        try {
            $cachedContent = [System.IO.File]::ReadAllText($Context.StatePath)
            $cached = $cachedContent | ConvertFrom-Json
            $cacheValid = (Get-FslSha256 (
                [System.Text.UTF8Encoding]::new($false).GetBytes($cachedContent))) -ceq
                $chain.Anchor.stateSha256
            if ($cacheValid -and
                (($cached | ConvertTo-Json -Compress -Depth 20) -cne
                    ($state | ConvertTo-Json -Compress -Depth 20))) {
                $cacheValid = $false
            }
        }
        catch {
            $cacheValid = $false
        }
    }
    if (-not $cacheValid) {
        Write-FslAtomicUtf8NoBom $Context.StatePath $stateContent
    }
    $prestate = [System.IO.File]::ReadAllText($Context.PrestatePath) | ConvertFrom-Json
    $currentBranch = Get-FslGitValue $Context @('branch', '--show-current')
    $currentCommit = Get-FslGitValue $Context @('rev-parse', 'HEAD')
    if ($state.schemaVersion -ne $script:StateSchemaVersion -or
        $state.runId -cne $Context.RunId -or
        $state.machineName -cne [Environment]::MachineName -or
        $state.branch -cne $prestate.branch -or
        $state.gitCommit -cne $prestate.gitCommit -or
        $state.branch -cne $currentBranch -or
        $state.gitCommit -cne $currentCommit -or
        $state.transition -cnotin $script:KnownTransitions -or
        [int]$state.sequence -ne [int]$chain.Last.sequence -or
        $state.transition -cne $chain.Last.transition) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Stage 4 state identity, transition, or journal validation failed.')
    }
    [void](Assert-FslExternalAnchor $Context)
    return Resolve-FslPlatformReadinessState $state
}

function Write-FslState {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$State,
        [Parameter(Mandatory = $true)][string]$Transition
    )

    if ($Transition -cnotin $script:KnownTransitions) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'Unknown Stage 4 transition.'
    }
    $State.transition = $Transition
    $State.sequence = [int]$State.sequence + 1
    $content = ($State | ConvertTo-Json -Depth 20) + [Environment]::NewLine
    $stateBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($content)
    $stateHash = Get-FslSha256 $stateBytes
    $previous = '0' * 64
    $journalLength = 0L
    if ((Test-Path -LiteralPath $Context.JournalPath -PathType Leaf) -or
        (Test-Path -LiteralPath $Context.AnchorPath -PathType Leaf)) {
        $chain = Read-FslAnchoredJournal $Context
        $previous = [string]$chain.Last.entrySha256
        $journalLength = [long]$chain.Anchor.journalLength
    }
    $core = [ordered]@{
        schemaVersion = $script:StateSchemaVersion
        runId = $State.runId
        machineName = $State.machineName
        branch = $State.branch
        gitCommit = $State.gitCommit
        sequence = $State.sequence
        transition = $Transition
        recordedUtc = [DateTime]::UtcNow.ToString('o')
        previousEntrySha256 = $previous
        state = $State
    }
    $coreJson = $core | ConvertTo-Json -Compress -Depth 20
    $entryHash = Get-FslSha256 (
        [System.Text.UTF8Encoding]::new($false).GetBytes($coreJson))
    $entry = [ordered]@{}
    foreach ($property in $core.Keys) {
        $entry[$property] = $core[$property]
    }
    $entry['entrySha256'] = $entryHash
    $written = Add-FslWriteThroughLine $Context.JournalPath (
        $entry | ConvertTo-Json -Compress -Depth 20)
    $journalLength += $written
    $anchor = [ordered]@{
        schemaVersion = $script:StateSchemaVersion
        runId = $State.runId
        machineName = $State.machineName
        sequence = $State.sequence
        entrySha256 = $entryHash
        stateSha256 = $stateHash
        journalLength = $journalLength
    }
    Write-FslAtomicUtf8NoBom $Context.AnchorPath (
        ($anchor | ConvertTo-Json -Compress) + [Environment]::NewLine)
    Write-FslAtomicUtf8NoBom $Context.StatePath $content
    Write-FslExternalAnchor $Context
}

function Assert-FslTransition {
    param(
        [Parameter(Mandatory = $true)][psobject]$State,
        [Parameter(Mandatory = $true)][string[]]$Allowed
    )
    if ($State.transition -cnotin $Allowed) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            "Command is not allowed from transition $($State.transition).")
    }
}

function Test-FslJournalTransition {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$Transition
    )
    $chain = Read-FslAnchoredJournal $Context
    return @($chain.Entries |
        Where-Object { $_.transition -ceq $Transition }).Count -gt 0
}

function Test-FslServiceExists {
    return (Get-Service -Name $script:ServiceName -ErrorAction SilentlyContinue) -ne $null
}

function Get-FslServiceSnapshot {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    $snapshot = Get-FslRawServiceSnapshot
    Assert-FslServiceSnapshotExact $snapshot $Context.BrokerPath $false
    return $snapshot
}

function Get-FslRawServiceSnapshot {
    $service = Get-CimInstance Win32_Service -Filter (
        "Name='$($script:ServiceName)'")
    if ($null -eq $service) {
        Stop-FslStage4 $script:ExitCodes.Service 'The fixed recovery service is missing.'
    }
    $registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$($script:ServiceName)"
    $registry = Get-ItemProperty -LiteralPath $registryPath
    return [pscustomobject]@{
        schemaVersion = 1
        serviceName = [string]$service.Name
        displayName = [string]$service.DisplayName
        description = [string]$service.Description
        startName = [string]$service.StartName
        startMode = [string]$service.StartMode
        state = [string]$service.State
        processId = [uint32]$service.ProcessId
        imagePath = [string]$registry.ImagePath
        start = [int]$registry.Start
        delayedAutoStart = [int]$registry.DelayedAutoStart
        serviceSidType = [int]$registry.ServiceSidType
    }
}

function Assert-FslServiceSnapshotExact {
    param(
        [Parameter(Mandatory = $true)][psobject]$Snapshot,
        [Parameter(Mandatory = $true)][string]$BrokerPath,
        [Parameter(Mandatory = $true)][bool]$RequireStopped
    )

    $expectedImagePath = "`"$BrokerPath`" --mode recovery-service"
    if ($Snapshot.serviceName -cne $script:ServiceName -or
        $Snapshot.displayName -cne $script:ServiceDisplayName -or
        $Snapshot.description -cne $script:ServiceDescription -or
        $Snapshot.startName -cne 'LocalSystem' -or
        $Snapshot.startMode -cne 'Auto' -or
        $Snapshot.imagePath -cne $expectedImagePath -or
        $Snapshot.start -ne 2 -or
        $Snapshot.delayedAutoStart -ne 0 -or
        $Snapshot.serviceSidType -ne 1 -or
        ($RequireStopped -and $Snapshot.state -cne 'Stopped')) {
        Stop-FslStage4 $script:ExitCodes.Service (
            'The recovery service configuration does not match the fixed contract.')
    }
}

function Invoke-FslVerifiedServiceDelete {
    param(
        [Parameter(Mandatory = $true)][psobject]$Snapshot,
        [Parameter(Mandatory = $true)][string]$BrokerPath,
        [Parameter(Mandatory = $true)][scriptblock]$DeleteAction
    )

    Assert-FslServiceSnapshotExact $Snapshot $BrokerPath $true
    & $DeleteAction
}

function Invoke-FslPreflight {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    Assert-FslMachineGate
    Assert-FslRepositoryGate $Context
    try {
        $secureBootEvidence = Get-FslSecureBootRegistryEvidence
        $tpmEvidence = Get-FslNativeTpmDeviceInfo
        $isElevated = Test-FslCurrentTokenAdministrator
    }
    catch {
        if ($_.Exception.Data['FslStage4ExitCode']) {
            throw
        }
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'Non-elevated platform evidence could not be captured.')
    }
    Assert-FslPreflightPlatformEvidence `
        $secureBootEvidence $tpmEvidence $isElevated

    $initialGitStatus = Get-FslGitValue $Context @(
        'status', '--porcelain=v1', '--untracked-files=all')
    if (-not [string]::IsNullOrWhiteSpace($initialGitStatus)) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict (
            'The Stage 4 preflight requires a clean Git working tree.')
    }

    if (Test-Path -LiteralPath $Context.EvidenceRoot) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict 'The RunId evidence directory already exists.'
    }
    if (Test-Path -LiteralPath $Context.ExternalAnchorRoot) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict (
            'The RunId protected external anchor already exists.')
    }

    $conflicts = @()
    if (Test-FslServiceExists) {
        $conflicts += 'service'
    }
    if (Test-Path -LiteralPath $Context.InstallDirectory) {
        $conflicts += 'ProgramFiles'
    }
    if (Test-Path -LiteralPath $Context.ProgramDataRoot) {
        $conflicts += 'ProgramData'
    }
    if (Test-Path -LiteralPath $Context.ReleaseRoot) {
        $conflicts += 'release'
    }
    $processes = @(Get-Process | Where-Object {
        $_.ProcessName -like 'FolderSessionLock*' -or
        $_.ProcessName -in @('testhost', 'vstest.console')
    })
    if ($processes.Count -gt 0) {
        $conflicts += 'processes'
    }
    $certificates = @(
        Get-ChildItem Cert:\LocalMachine\My,Cert:\LocalMachine\TrustedPeople `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -like "CN=$($script:TestCertificatePrefix)*" })
    if ($certificates.Count -gt 0) {
        $conflicts += 'certificates'
    }
    if ($conflicts.Count -gt 0) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict (
            'Pre-existing Stage 4 conflicts: ' + ($conflicts -join ', '))
    }

    [System.IO.Directory]::CreateDirectory($Context.EvidenceRoot) | Out-Null
    $os = Get-CimInstance Win32_OperatingSystem
    $prestate = [ordered]@{
        runId = $Context.RunId
        capturedUtc = [DateTime]::UtcNow.ToString('o')
        machineName = [Environment]::MachineName
        computerName = $env:COMPUTERNAME
        cimName = (Get-CimInstance Win32_ComputerSystem).Name
        osCaption = $os.Caption
        osVersion = $os.Version
        osBuildNumber = $os.BuildNumber
        secureBootRegistry = $secureBootEvidence
        tbsDeviceInfo = $tpmEvidence
        isElevated = $isElevated
        repositoryRoot = $Context.RepositoryRoot
        branch = (Get-FslGitValue $Context @('branch', '--show-current'))
        gitCommit = (Get-FslGitValue $Context @('rev-parse', 'HEAD'))
        gitStatus = $initialGitStatus
        serviceExisted = $false
        installDirectoryExisted = $false
        programDataRootExisted = $false
        releaseRootExisted = $false
        matchingTestCertificateThumbprints = @()
        relevantProcesses = @()
    }
    Write-FslUtf8NoBom $Context.PrestatePath (($prestate | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
    Initialize-FslExternalAnchor $Context
    Write-FslState $Context ([pscustomobject]@{
        schemaVersion = $script:StateSchemaVersion
        runId = $Context.RunId
        machineName = [Environment]::MachineName
        branch = $prestate.branch
        gitCommit = $prestate.gitCommit
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
    }) 'PreflightCaptured'
    Add-FslCommandEvidence $Context "Preflight -RunId $($Context.RunId)"
}

function Invoke-FslCreateTestCertificate {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    try {
        Assert-FslMachineGate
        Assert-FslAdministrator
    }
    catch {
        if ($_.Exception.Data['FslStage4ExitCode']) {
            throw
        }
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'Elevated base or administrator verification failed.')
    }
    try {
        $secureBootConfirmed = Confirm-SecureBootUEFI
        if ($secureBootConfirmed -isnot [bool] -or
            -not $secureBootConfirmed) {
            Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
                'Elevated Secure Boot confirmation failed.')
        }
        $tpm = Get-Tpm
        if ($null -eq $tpm -or
            $tpm.PSObject.Properties.Name -cnotcontains 'TpmPresent' -or
            $tpm.PSObject.Properties.Name -cnotcontains 'TpmReady' -or
            $tpm.TpmPresent -isnot [bool] -or
            $tpm.TpmReady -isnot [bool] -or
            -not $tpm.TpmPresent -or
            -not $tpm.TpmReady) {
            Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
                'Elevated TPM cmdlet confirmation failed.')
        }
    }
    catch {
        if ($_.Exception.Data['FslStage4ExitCode']) {
            throw
        }
        Stop-FslStage4 $script:ExitCodes.EnvironmentGate (
            'Elevated platform readiness could not be confirmed.')
    }
    $state = Read-FslState $Context
    Invoke-FslReconcileInstallWal $Context $state
    Assert-FslTransition $state @('PreflightCaptured', 'CertificateRolledBack')
    if (-not [string]::IsNullOrWhiteSpace([string]$state.CreatedCertificateThumbprint)) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict 'This run already created a test certificate.'
    }
    $prestate =
        [System.IO.File]::ReadAllText($Context.PrestatePath) |
        ConvertFrom-Json
    Assert-FslPreflightPlatformEvidence `
        $prestate.secureBootRegistry `
        $prestate.tbsDeviceInfo `
        $prestate.isElevated
    $state.PlatformReadinessStatus = 'Verified'
    $state.SecureBootVerified = $true
    $state.TpmNativeVerified = $true
    $state.TpmCmdletVerified = $true
    $state.PlatformReadinessVerifiedUtc =
        [DateTime]::UtcNow.ToString('o')
    Write-FslState $Context $state 'PlatformReadinessVerified'

    $subject = "CN=$($script:TestCertificatePrefix) [$($Context.RunId)]"
    Write-FslState $Context $state 'CertificateCreating'
    $thumbprint = $null
    try {
        $certificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $subject `
            -FriendlyName "$($script:TestCertificatePrefix) [$($Context.RunId)]" `
            -CertStoreLocation 'Cert:\LocalMachine\My' `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -KeyExportPolicy NonExportable `
            -NotAfter ([DateTime]::UtcNow.AddDays(7))
        if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
            Stop-FslStage4 $script:ExitCodes.Signing 'The VM test signing certificate could not be created.'
        }
        $thumbprint = $certificate.Thumbprint.ToUpperInvariant()
        $state.CreatedCertificateThumbprint = $thumbprint
        Write-FslState $Context $state 'CertificateCreating'
        $publicPath = Join-Path $Context.EvidenceRoot 'stage4-test-signing-public.cer'
        Export-Certificate -Cert $certificate -FilePath $publicPath -Force | Out-Null
        Import-Certificate -FilePath $publicPath `
            -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
        $trusted = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople |
            Where-Object { $_.Thumbprint -ceq $thumbprint -and $_.Subject -ceq $subject })
        if ($trusted.Count -ne 1) {
            Stop-FslStage4 $script:ExitCodes.Signing 'The VM test certificate trust transaction failed.'
        }
        $state.TrustedCertificateThumbprint = $thumbprint
        Write-FslState $Context $state 'CertificateReady'
    }
    catch {
        $originalMessage = $_.Exception.Message
        foreach ($store in @('Cert:\LocalMachine\TrustedPeople', 'Cert:\LocalMachine\My')) {
            @(Get-ChildItem $store -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Subject -ceq $subject -and
                    ($null -eq $thumbprint -or $_.Thumbprint -ceq $thumbprint)
                }) | ForEach-Object { Remove-Item -LiteralPath $_.PSPath -Force }
        }
        $residual = @(
            Get-ChildItem Cert:\LocalMachine\My,Cert:\LocalMachine\TrustedPeople `
                -ErrorAction SilentlyContinue |
                Where-Object { $_.Subject -ceq $subject })
        if ($residual.Count -ne 0) {
            Stop-FslStage4 $script:ExitCodes.Cleanup 'Certificate transaction rollback left a residual certificate.'
        }
        $state.CreatedCertificateThumbprint = $null
        $state.TrustedCertificateThumbprint = $null
        Write-FslState $Context $state 'CertificateRolledBack'
        Stop-FslStage4 $script:ExitCodes.Signing (
            "Certificate transaction failed and was rolled back: $originalMessage")
    }
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'signature-verification.txt') (
        "TEST CERTIFICATE CREATED`r`nRunId=$($Context.RunId)`r`n" +
        "Thumbprint=$($certificate.Thumbprint.ToUpperInvariant())`r`n" +
        "PrivateKeyExported=NO`r`nProductionCertificate=NO`r`n")
    Add-FslCommandEvidence $Context "CreateTestCertificate -RunId $($Context.RunId)"
}

function Assert-FslTrustedToolDescriptor {
    param(
        [Parameter(Mandatory = $true)][psobject]$Descriptor,
        [Parameter(Mandatory = $true)][string]$ExpectedPath
    )

    $trustedOwners = @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $expected = [System.IO.Path]::GetFullPath($ExpectedPath).TrimEnd('\')
    if ($Descriptor.file.finalPath -cne $expected -or
        $Descriptor.signerSpkiSha256 -cnotin
            $script:ApprovedMicrosoftSignToolSpkiSha256 -or
        [string]$Descriptor.signerThumbprint -cnotmatch '^[0-9A-F]{40}$' -or
        @($Descriptor.pathChain).Count -lt 2) {
        Stop-FslStage4 $script:ExitCodes.Signing (
            'SignTool path or Microsoft SPKI allowlist validation failed.')
    }
    $expectedNode = $expected
    foreach ($node in @($Descriptor.pathChain)) {
        if ($node.requestedPath -cne $expectedNode -or
            $node.finalPath -cne $expectedNode -or
            $node.isReparse -or
            $node.ownerSid -cnotin $trustedOwners -or
            @($node.untrustedWritableSids).Count -ne 0 -or
            [string]$node.identity -cnotmatch '^[0-9A-F]{24}$' -or
            [string]::IsNullOrWhiteSpace([string]$node.aclSddl)) {
            Stop-FslStage4 $script:ExitCodes.Signing (
                'SignTool or an ancestor failed handle identity or ACL trust.')
        }
        $root = [System.IO.Path]::GetPathRoot($expectedNode).TrimEnd('\')
        if ($expectedNode -ceq $root) {
            $expectedNode = $null
        }
        else {
            $parent = [System.IO.Directory]::GetParent($expectedNode)
            $expectedNode = $parent.FullName.TrimEnd('\')
        }
    }
    if ($null -ne $expectedNode) {
        Stop-FslStage4 $script:ExitCodes.Signing (
            'SignTool descriptor omitted an ancestor.')
    }
}

function Get-FslTrustedToolDescriptor {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $trustedWriteSids = @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $chain = @()
    $currentPath = $full
    $isFile = $true
    while ($null -ne $currentPath) {
        $identity = [FolderSessionLock.Stage4.Native]::DescribeFile(
            $currentPath,
            -not $isFile)
        $acl = Get-Acl -LiteralPath $currentPath
        $ownerSid = ([Security.Principal.NTAccount]$acl.Owner).Translate(
            [Security.Principal.SecurityIdentifier]).Value
        $mutationMask = if ($isFile) {
            [Security.AccessControl.FileSystemRights]0x000D0116
        }
        else {
            [Security.AccessControl.FileSystemRights]0x000D0150
        }
        $untrustedWritable = @($acl.GetAccessRules(
            $true,
            $true,
            [Security.Principal.SecurityIdentifier]) |
            Where-Object {
                $_.AccessControlType -eq
                    [Security.AccessControl.AccessControlType]::Allow -and
                ($_.PropagationFlags -band
                    [Security.AccessControl.PropagationFlags]::InheritOnly) -eq
                    0 -and
                (($_.FileSystemRights -band $mutationMask) -ne 0) -and
                $_.IdentityReference.Value -cnotin $trustedWriteSids
            } |
            ForEach-Object { $_.IdentityReference.Value })
        $chain += [pscustomobject]@{
            requestedPath = $identity.RequestedPath
            finalPath = $identity.FinalPath
            identity = $identity.Identity
            isReparse = $identity.IsReparse
            ownerSid = $ownerSid
            untrustedWritableSids = $untrustedWritable
            aclSddl = $acl.Sddl
        }
        $root = [System.IO.Path]::GetPathRoot($currentPath).TrimEnd('\')
        if ($currentPath.TrimEnd('\') -ceq $root) {
            $currentPath = $null
        }
        else {
            $parent = [System.IO.Directory]::GetParent($currentPath)
            $currentPath = $parent.FullName
            $isFile = $false
        }
    }
    $signature = [FolderSessionLock.Stage4.Native]::VerifyAuthenticode($full)
    return [pscustomobject]@{
        file = $chain[0]
        pathChain = $chain
        sha256 = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        signerThumbprint = $signature.Thumbprint.ToUpperInvariant()
        signerSpkiSha256 = $signature.SpkiSha256.ToUpperInvariant()
    }
}

function Get-FslSignTool {
    $programFilesX86 = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFilesX86)
    $kits = [System.IO.Path]::GetFullPath(
        (Join-Path $programFilesX86 'Windows Kits\10\bin'))
    $candidate = Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [Version]$_.Name } -Descending |
        ForEach-Object {
            Get-Item -LiteralPath (Join-Path $_.FullName 'x64\signtool.exe') `
                -ErrorAction SilentlyContinue
        } |
        Select-Object -First 1
    if ($null -eq $candidate) {
        Stop-FslStage4 $script:ExitCodes.Signing 'SignTool was not found.'
    }

    $resolved = [System.IO.Path]::GetFullPath($candidate.FullName)
    if (-not $resolved.StartsWith(
        $kits + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
        $resolved -notmatch '\\x64\\signtool\.exe$') {
        Stop-FslStage4 $script:ExitCodes.Signing 'SignTool was not in a trusted Windows Kits x64 path.'
    }

    $descriptor = Get-FslTrustedToolDescriptor $resolved
    Assert-FslTrustedToolDescriptor $descriptor $resolved
    return $resolved
}

function Invoke-FslTrustedSignTool {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][int]$FailureCode,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][psobject]$Context
    )

    $before = Get-FslTrustedToolDescriptor $Path
    Assert-FslTrustedToolDescriptor $before $Path
    $result = Invoke-FslCheckedProcess `
        $Path $ArgumentList $FailureCode $Description $Context
    $after = Get-FslTrustedToolDescriptor $Path
    Assert-FslTrustedToolDescriptor $after $Path
    if (($before | ConvertTo-Json -Compress -Depth 20) -cne
        ($after | ConvertTo-Json -Compress -Depth 20)) {
        Stop-FslStage4 $script:ExitCodes.Signing (
            'SignTool identity changed while it was being used.')
    }
    return $result
}

function Get-FslFirstPartyPePaths {
    param([Parameter(Mandatory = $true)][string]$Root)

    $actualNames = @(Get-ChildItem -LiteralPath $Root -File |
        Where-Object {
            $_.Name -like 'FolderSessionLock.*.exe' -or
            $_.Name -like 'FolderSessionLock.*.dll'
        } |
        Select-Object -ExpandProperty Name |
        Sort-Object)
    $expectedNames = @($script:FirstPartyPortableExecutables | Sort-Object)
    if (($actualNames -join "`n") -cne ($expectedNames -join "`n")) {
        Stop-FslStage4 $script:ExitCodes.Signing (
            'The first-party PE set does not match the fixed release contract.')
    }
    $paths = @()
    foreach ($name in $script:FirstPartyPortableExecutables) {
        $path = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Stop-FslStage4 $script:ExitCodes.Signing "Required first-party PE is missing: $name."
        }
        $paths += $path
    }
    return $paths
}

function Invoke-FslCheckedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][int]$FailureCode,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][psobject]$Context,
        [string]$EvidenceFile
    )

    Add-FslCommandEvidence $Context (
        $Description + ' ' + (($ArgumentList | ForEach-Object {
            if ($_ -match '\s') { '"' + $_ + '"' } else { $_ }
        }) -join ' '))
    $output = & $FilePath @ArgumentList 2>&1
    $exitCode = $LASTEXITCODE
    $text = (($output | Out-String).TrimEnd() + [Environment]::NewLine +
        "ExitCode=$exitCode" + [Environment]::NewLine)
    if (-not [string]::IsNullOrWhiteSpace($EvidenceFile)) {
        Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot $EvidenceFile) $text
    }
    if ($exitCode -ne 0) {
        Stop-FslStage4 $FailureCode "$Description failed with exit code $exitCode."
    }

    return $text
}

function Invoke-FslPublish {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$PublisherThumbprint,
        [string]$SigningCertificateThumbprint
    )

    Assert-FslMachineGate
    Assert-FslRepositoryGate $Context
    $pin = ConvertTo-FslThumbprint $PublisherThumbprint $script:ExitCodes.Signing
    $state = Read-FslState $Context
    Assert-FslTransition $state @('PreflightCaptured', 'CertificateReady', 'CertificateRolledBack')
    if (Test-Path -LiteralPath $Context.ReleaseRoot) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict 'The release root already exists.'
    }

    $staging = Join-Path 'C:\FSL-Release' (".stage4-$($Context.RunId)")
    if (Test-Path -LiteralPath $staging) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict 'The release staging directory already exists.'
    }

    try {
        [System.IO.Directory]::CreateDirectory($staging) | Out-Null
        $appStage = Join-Path $staging 'app'
        $brokerStage = Join-Path $staging 'broker'
        $common = @(
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained', 'false',
            '-p:PublishSingleFile=false',
            '-p:DebugType=None',
            '-p:DebugSymbols=false')
        $appProject = Join-Path $Context.RepositoryRoot 'src\FolderSessionLock.App\FolderSessionLock.App.csproj'
        $brokerProject = Join-Path $Context.RepositoryRoot 'src\FolderSessionLock.Broker\FolderSessionLock.Broker.csproj'
        $appArguments = @('publish', $appProject) + $common +
            @("-p:BrokerPublisherThumbprint=$pin", '-o', $appStage)
        $brokerArguments = @('publish', $brokerProject) + $common + @('-o', $brokerStage)
        $buildLog = Invoke-FslCheckedProcess dotnet.exe $appArguments `
            $script:ExitCodes.ValidationEvidence 'dotnet publish App' $Context
        $buildLog += Invoke-FslCheckedProcess dotnet.exe $brokerArguments `
            $script:ExitCodes.ValidationEvidence 'dotnet publish Broker' $Context

        [System.IO.Directory]::CreateDirectory($Context.ReleaseRoot) | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $appStage -File) {
            Copy-Item -LiteralPath $file.FullName -Destination (
                Join-Path $Context.ReleaseRoot $file.Name)
        }
        foreach ($file in Get-ChildItem -LiteralPath $brokerStage -File) {
            $destination = Join-Path $Context.ReleaseRoot $file.Name
            if (Test-Path -LiteralPath $destination) {
                $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
                if ($sourceHash -cne $destinationHash) {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        "Publish collision has different content: $($file.Name)")
                }
            }
            else {
                Copy-Item -LiteralPath $file.FullName -Destination $destination
            }
        }

        $signingPin = $SigningCertificateThumbprint
        if ([string]::IsNullOrWhiteSpace($signingPin)) {
            $signingPin = [string]$state.CreatedCertificateThumbprint
        }
        if (-not [string]::IsNullOrWhiteSpace($signingPin)) {
            $signingPin = ConvertTo-FslThumbprint $signingPin $script:ExitCodes.Signing
            if ($signingPin -cne $pin) {
                Stop-FslStage4 $script:ExitCodes.Signing 'The signing certificate and App publisher pin must match.'
            }
            $signTool = Get-FslSignTool
            foreach ($portableExecutable in Get-FslFirstPartyPePaths $Context.ReleaseRoot) {
                Invoke-FslTrustedSignTool $signTool @(
                    'sign', '/sm', '/sha1', $signingPin, '/fd', 'SHA256', $portableExecutable
                ) $script:ExitCodes.Signing 'signtool sign' $Context | Out-Null
            }
        }

        $state.ReleaseDescriptorSha256 = New-FslReleaseMetadata $Context
        Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'build-results.txt') $buildLog
        $state.ReleaseRoot = $Context.ReleaseRoot
        Write-FslState $Context $state 'PublishCompleted'
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}

function New-FslReleaseMetadata {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    $runGuide = Join-Path $Context.ReleaseRoot 'README-RUN.txt'
    Write-FslUtf8NoBom $runGuide (
        "FolderSessionLock Stage 4 Release`r`n" +
        "RID: win-x64`r`nDeployment: framework-dependent, multi-file`r`n" +
        "Dependency: Microsoft Windows Desktop Runtime 8 (x64)`r`n" +
        "Primary UI: FolderSessionLock.App.exe`r`n" +
        "Elevated broker and recovery service: FolderSessionLock.Broker.exe`r`n" +
        "Install directory: %ProgramFiles%\FolderSessionLock`r`n" +
        "The recovery service must be installed by the Stage 4 controller.`r`n")
    $files = @()
    $sums = [System.Text.StringBuilder]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Context.ReleaseRoot -File |
        Sort-Object Name) {
        if ($file.Name -in @('release-manifest.json', 'SHA256SUMS.txt')) {
            continue
        }
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        $signer = $null
        $timestampStatus = 'NotPresent'
        if ($null -ne $signature.SignerCertificate) {
            $signer = [pscustomobject]@{
                subject = $signature.SignerCertificate.Subject
                thumbprint = $signature.SignerCertificate.Thumbprint.ToUpperInvariant()
            }
        }
        if ($null -ne $signature.TimeStamperCertificate) {
            $timestampStatus = 'Present'
        }
        $files += [pscustomobject]@{
            relativePath = $file.Name
            length = $file.Length
            sha256 = $hash
            productVersion = $version.ProductVersion
            fileVersion = $version.FileVersion
            authenticodeStatus = [string]$signature.Status
            signer = $signer
            timestampStatus = $timestampStatus
        }
        [void]$sums.AppendLine("$hash  $($file.Name)")
    }
    $manifest = [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        gitCommit = (Get-FslGitValue $Context @('rev-parse', 'HEAD'))
        rid = 'win-x64'
        deploymentMode = 'framework-dependent-multi-file'
        firstPartyPortableExecutables = $script:FirstPartyPortableExecutables
        files = $files
    }
    Write-FslUtf8NoBom (Join-Path $Context.ReleaseRoot 'release-manifest.json') (
        ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
    $manifestHash = (Get-FileHash -LiteralPath (
        Join-Path $Context.ReleaseRoot 'release-manifest.json') -Algorithm SHA256).Hash
    [void]$sums.AppendLine("$manifestHash  release-manifest.json")
    Write-FslUtf8NoBom (Join-Path $Context.ReleaseRoot 'SHA256SUMS.txt') $sums.ToString()
    $sumsHash = (Get-FileHash -LiteralPath (
        Join-Path $Context.ReleaseRoot 'SHA256SUMS.txt') -Algorithm SHA256).Hash
    $descriptor = [ordered]@{
        schemaVersion = 1
        gitCommit = (Get-FslGitValue $Context @('rev-parse', 'HEAD'))
        manifestSha256 = $manifestHash
        sumsSha256 = $sumsHash
        exactReleaseFiles = @(
            @($files.relativePath) +
            @('release-manifest.json', 'SHA256SUMS.txt', 'release-descriptor.json') |
                Sort-Object)
    }
    $descriptorPath = Join-Path $Context.ReleaseRoot 'release-descriptor.json'
    Write-FslUtf8NoBom $descriptorPath (
        ($descriptor | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
    return (Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash
}

function Read-FslFrozenReleaseDescriptor {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ExpectedDescriptorSha256
    )

    $descriptorPath = Join-Path $Root 'release-descriptor.json'
    $manifestPath = Join-Path $Root 'release-manifest.json'
    $sumsPath = Join-Path $Root 'SHA256SUMS.txt'
    foreach ($required in @($descriptorPath, $manifestPath, $sumsPath)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'A frozen release metadata file is missing.')
        }
    }
    if ((Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash -cne
        $ExpectedDescriptorSha256) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The frozen release descriptor hash changed.')
    }
    try {
        $descriptor = [System.IO.File]::ReadAllText($descriptorPath) | ConvertFrom-Json
        $manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    }
    catch {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Frozen release JSON is invalid.')
    }
    if ($descriptor.schemaVersion -ne 1 -or $manifest.schemaVersion -ne 1 -or
        (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne
            $descriptor.manifestSha256 -or
        (Get-FileHash -LiteralPath $sumsPath -Algorithm SHA256).Hash -cne
            $descriptor.sumsSha256) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Frozen release metadata hashes do not agree.')
    }
    if (@(Get-ChildItem -LiteralPath $Root -Directory).Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The frozen release contains an unexpected directory.')
    }
    $actualNames = @(Get-ChildItem -LiteralPath $Root -File |
        Select-Object -ExpandProperty Name |
        Sort-Object)
    $expectedNames = @($descriptor.exactReleaseFiles | ForEach-Object { [string]$_ } |
        Sort-Object)
    if ($actualNames.Count -ne $expectedNames.Count -or
        ($actualNames -join "`n") -cne ($expectedNames -join "`n") -or
        @($expectedNames | Group-Object { $_.ToUpperInvariant() } |
            Where-Object { $_.Count -ne 1 }).Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The frozen release exact file set changed.')
    }
    $payload = @($manifest.files)
    $payloadNames = @($payload | ForEach-Object { [string]$_.relativePath })
    if ($payloadNames.Count -eq 0 -or
        @($payloadNames | Group-Object { $_.ToUpperInvariant() } |
            Where-Object { $_.Count -ne 1 }).Count -ne 0 -or
        @($payloadNames | Where-Object {
            [System.IO.Path]::GetFileName($_) -cne $_
        }).Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The frozen release manifest contains duplicate or invalid names.')
    }
    $expectedPayloadNames = @($expectedNames |
        Where-Object {
            $_ -cnotin @(
                'release-manifest.json',
                'SHA256SUMS.txt',
                'release-descriptor.json')
        } |
        Sort-Object)
    if ((@($payloadNames | Sort-Object) -join "`n") -cne
        ($expectedPayloadNames -join "`n")) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The frozen release manifest does not describe the exact payload.')
    }
    foreach ($file in $payload) {
        $path = Join-Path $Root ([string]$file.relativePath)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne [long]$file.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne
                [string]$file.sha256) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                "Frozen release payload mismatch: $($file.relativePath).")
        }
    }
    $sumLines = @([System.IO.File]::ReadAllLines($sumsPath) |
        Where-Object { $_.Length -gt 0 })
    $sumMap = @{}
    foreach ($line in $sumLines) {
        if ($line -cnotmatch '^(?<hash>[0-9A-F]{64})  (?<name>[^\\/:]+)$') {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'The frozen SHA256SUMS format is invalid.')
        }
        $key = $Matches.name.ToUpperInvariant()
        if ($sumMap.ContainsKey($key)) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'The frozen SHA256SUMS contains a duplicate or case alias.')
        }
        $sumMap[$key] = [pscustomobject]@{
            Name = $Matches.name
            Hash = $Matches.hash
        }
    }
    $sumExpected = @($expectedPayloadNames + 'release-manifest.json' | Sort-Object)
    if ($sumMap.Count -ne $sumExpected.Count) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The frozen SHA256SUMS exact set changed.')
    }
    foreach ($name in $sumExpected) {
        $entry = $sumMap[$name.ToUpperInvariant()]
        $path = Join-Path $Root $name
        if ($null -eq $entry -or $entry.Name -cne $name -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $entry.Hash) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                "Frozen SHA256SUMS mismatch: $name.")
        }
    }
    return $descriptor
}

function Copy-FslFrozenRelease {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][string]$DescriptorSha256,
        [psobject]$WalContext,
        [scriptblock]$BeforeCopy,
        [scriptblock]$AfterCopy
    )

    $descriptor = Read-FslFrozenReleaseDescriptor $SourceRoot $DescriptorSha256
    $copied = [System.Collections.ArrayList]::new()
    try {
        foreach ($name in @($descriptor.exactReleaseFiles | Sort-Object)) {
            if ($null -ne $WalContext) {
                Add-FslInstallWalRecord $WalContext ([ordered]@{
                    schemaVersion = 1
                    operationId = "Copy:$name"
                    kind = 'CopyFile'
                    target = $name
                    phase = 'Intent'
                })
            }
            if ($null -ne $BeforeCopy) {
                & $BeforeCopy $name
            }
            [System.IO.File]::Copy(
                (Join-Path $SourceRoot $name),
                (Join-Path $DestinationRoot $name),
                $false)
            $source = Join-Path $SourceRoot $name
            $destination = Join-Path $DestinationRoot $name
            $proof = [pscustomobject]@{
                Name = $name
                Length = (Get-Item -LiteralPath $destination).Length
                Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            }
            [void]$copied.Add($proof)
            if ($null -ne $WalContext) {
                Add-FslInstallWalRecord $WalContext ([ordered]@{
                    schemaVersion = 1
                    operationId = "Copy:$name"
                    kind = 'CopyFile'
                    target = $name
                    phase = 'Applied'
                    proof = $proof
                })
            }
            if ($null -ne $AfterCopy) {
                & $AfterCopy $name
            }
            [void](Read-FslFrozenReleaseDescriptor $SourceRoot $DescriptorSha256)
            if ((Get-Item -LiteralPath $source).Length -ne $proof.Length -or
                (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne
                    $proof.Sha256 -or
                (Get-Item -LiteralPath $destination).Length -ne $proof.Length -or
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne
                    $proof.Sha256) {
                Stop-FslStage4 $script:ExitCodes.InstallAcl (
                    "A release file changed during copy: $name.")
            }
        }
    }
    catch {
        $failure = $_
        $rollback = @($copied)
        [Array]::Reverse($rollback)
        foreach ($proof in $rollback) {
            $destination = Join-Path $DestinationRoot $proof.Name
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or
                (Get-Item -LiteralPath $destination).Length -ne $proof.Length -or
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -cne
                    $proof.Sha256) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    "Copied release rollback refused a replacement: $($proof.Name).")
            }
            [System.IO.File]::Delete($destination)
            if ($null -ne $WalContext) {
                Add-FslInstallWalRecord $WalContext ([ordered]@{
                    schemaVersion = 1
                    operationId = "Copy:$($proof.Name)"
                    kind = 'CopyFile'
                    target = $proof.Name
                    phase = 'RolledBack'
                })
            }
        }
        throw $failure
    }
    $destinationDescriptor = Read-FslFrozenReleaseDescriptor (
        $DestinationRoot) $DescriptorSha256
    return $destinationDescriptor
}

function Invoke-FslVerifySignature {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$PublisherThumbprint
    )

    Assert-FslMachineGate
    $pin = ConvertTo-FslThumbprint $PublisherThumbprint $script:ExitCodes.Signing
    $state = Read-FslState $Context
    Assert-FslTransition $state @('PublishCompleted', 'SignatureVerified', 'Installed', 'Verified')
    if ([string]::IsNullOrWhiteSpace([string]$state.ReleaseRoot) -or
        -not (Test-Path -LiteralPath $state.ReleaseRoot -PathType Container)) {
        Stop-FslStage4 $script:ExitCodes.Signing 'The published release is unavailable.'
    }
    [void](Read-FslFrozenReleaseDescriptor `
        $state.ReleaseRoot `
        ([string]$state.ReleaseDescriptorSha256))

    $signTool = Get-FslSignTool
    $evidence = [System.Text.StringBuilder]::new()
    foreach ($executable in Get-FslFirstPartyPePaths $state.ReleaseRoot) {
        $verification = Invoke-FslTrustedSignTool $signTool @(
            'verify', '/pa', '/all', '/v', $executable
        ) $script:ExitCodes.Signing 'signtool verify' $Context
        $signature = Get-AuthenticodeSignature -LiteralPath $executable
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne $pin) {
            Stop-FslStage4 $script:ExitCodes.Signing (
                "Authenticode publisher verification failed for $([System.IO.Path]::GetFileName($executable)).")
        }
        [void]$evidence.AppendLine("File=$([System.IO.Path]::GetFileName($executable))")
        [void]$evidence.AppendLine("Status=$($signature.Status)")
        [void]$evidence.AppendLine(
            "SignerThumbprint=$($signature.SignerCertificate.Thumbprint.ToUpperInvariant())")
        [void]$evidence.AppendLine("SHA256=$((Get-FileHash $executable -Algorithm SHA256).Hash)")
        [void]$evidence.AppendLine($verification.TrimEnd())
    }
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'signature-verification.txt') $evidence.ToString()
    Write-FslState $Context $state 'SignatureVerified'
}

function Get-FslDirectorySecurityForGrants {
    param([Parameter(Mandatory = $true)][string[]]$Grants)

    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner(
        [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))
    $security.SetAccessRuleProtection($true, $false)
    foreach ($grant in $Grants) {
        if ($grant -cnotmatch '^(?<account>.+):(?<flags>\(OI\)\(CI\))?\((?<rights>F|RX)\)$') {
            Stop-FslStage4 $script:ExitCodes.InstallAcl (
                "Invalid frozen directory grant: $grant.")
        }
        $identity = if ($Matches.account.StartsWith(
            '*',
            [StringComparison]::Ordinal)) {
            [Security.Principal.SecurityIdentifier]::new(
                $Matches.account.Substring(1))
        }
        else {
            ([Security.Principal.NTAccount]$Matches.account).Translate(
                [Security.Principal.SecurityIdentifier])
        }
        $rights = if ($Matches.rights -ceq 'F') {
            [Security.AccessControl.FileSystemRights]::FullControl
        }
        else {
            [Security.AccessControl.FileSystemRights]::ReadAndExecute
        }
        $inheritance = if ($Matches.flags -ceq '(OI)(CI)') {
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
        }
        else {
            [Security.AccessControl.InheritanceFlags]::None
        }
        $security.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $identity,
                $rights,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }
    return $security
}

function Invoke-FslDirectorySetAclPrimitive {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Grants
    )

    $security = Get-FslDirectorySecurityForGrants $Grants
    $bytes = [byte[]]::new($security.BinaryLength)
    $security.GetSecurityDescriptorBinaryForm($bytes, 0)
    $identity = [FolderSessionLock.Stage4.Native]::SetDirectorySecurity(
        $Path,
        $bytes)
    $actual = Get-Acl -LiteralPath $Path
    if ($actual.Sddl -cne $security.Sddl -or
        $identity.RequestedPath -cne
            [System.IO.Path]::GetFullPath($Path).TrimEnd('\') -or
        $identity.FinalPath -cne $identity.RequestedPath -or
        $identity.IsReparse) {
        Stop-FslStage4 $script:ExitCodes.InstallAcl (
            'Handle-bound directory ACL proof did not match the frozen grant set.')
    }
    $proof = Get-FslPathProof $Path
    return [pscustomobject]@{
        finalPath = $proof.finalPath
        fileId = $proof.fileId
        aclSddl = $proof.aclSddl
        handleIdentity = $identity.Identity
    }
}

function Publish-FslWalBoundary {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$OperationId,
        [Parameter(Mandatory = $true)][string]$Boundary
    )

    if (-not ($Context.PSObject.Properties.Name -contains
        'WalBoundaryDirectory') -or
        [string]::IsNullOrWhiteSpace(
            [string]$Context.WalBoundaryDirectory)) {
        return
    }
    [System.IO.Directory]::CreateDirectory(
        [string]$Context.WalBoundaryDirectory) | Out-Null
    $safeOperation = $OperationId -replace '[^A-Za-z0-9_.-]', '_'
    $path = Join-Path $Context.WalBoundaryDirectory (
        "$safeOperation.$Boundary.marker")
    [FolderSessionLock.Stage4.Native]::AtomicWrite(
        $path,
        [System.Text.UTF8Encoding]::new($false).GetBytes(
            "$OperationId`n$Boundary`n"))
    if (($Context.PSObject.Properties.Name -contains 'WalPauseBoundary') -and
        [string]$Context.WalPauseBoundary -ceq
            "$OperationId/$Boundary") {
        while ($true) {
            Start-Sleep -Milliseconds 100
        }
    }
}

function Get-FslDeterministicTemporaryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$TransactionId
    )

    $fullTarget = [System.IO.Path]::GetFullPath($Target)
    $parent = [System.IO.Path]::GetDirectoryName($fullTarget)
    $leaf = [System.IO.Path]::GetFileName($fullTarget)
    return Join-Path $parent ".$leaf.$TransactionId.tmp"
}

function Assert-FslWalOrdinarySingleLink {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = [System.IO.Path]::GetFullPath($Path)
    $identity = [FolderSessionLock.Stage4.Native]::DescribeFile($full, $false)
    $links = [FolderSessionLock.Stage4.WalFileInformation]::GetLinkCount($full)
    if ($identity.IsReparse -or
        $identity.FinalPath -cne $full.TrimEnd('\') -or
        $links -ne 1) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'A WAL file is not an ordinary, non-reparse, single-link file.')
    }
}

function Assert-FslWalTemporarySecurity {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ParentPath
    )

    $parentAcl = Get-Acl -LiteralPath $ParentPath
    $fileAcl = Get-Acl -LiteralPath $Path
    $parentOwner = ([Security.Principal.NTAccount]$parentAcl.Owner).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    $fileOwner = ([Security.Principal.NTAccount]$fileAcl.Owner).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($fileOwner -cnotin @(
        $parentOwner,
        $currentSid,
        'S-1-5-18',
        'S-1-5-32-544')) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'A WAL temporary file has an unsafe owner.')
    }

    $directoryMutationMask =
        [Security.AccessControl.FileSystemRights]0x000D0150
    $fileMutationMask =
        [Security.AccessControl.FileSystemRights]0x000D0116
    $parentWriters = @($parentAcl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]) |
        Where-Object {
            $_.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            ($_.PropagationFlags -band
                [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and
            (($_.FileSystemRights -band $directoryMutationMask) -ne 0)
        } |
        ForEach-Object { $_.IdentityReference.Value } |
        Sort-Object -Unique)
    $unexpectedWriters = @($fileAcl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]) |
        Where-Object {
            $_.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow -and
            ($_.PropagationFlags -band
                [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and
            (($_.FileSystemRights -band $fileMutationMask) -ne 0) -and
            $_.IdentityReference.Value -cnotin $parentWriters
        })
    if ($unexpectedWriters.Count -ne 0 -or
        [string]::IsNullOrWhiteSpace([string]$fileAcl.Sddl)) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'A WAL temporary file has an unsafe DACL.')
    }
}

function Test-FslWalFilePrefix {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][long]$ExpectedLength
    )

    $candidateLength = (Get-Item -LiteralPath $Candidate -Force).Length
    if ($candidateLength -lt 0 -or $candidateLength -gt $ExpectedLength) {
        return $false
    }
    $sourceStream = [System.IO.FileStream]::new(
        $Source,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $candidateStream = [System.IO.FileStream]::new(
            $Candidate,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        try {
            $sourceBuffer = [byte[]]::new(65536)
            $candidateBuffer = [byte[]]::new(65536)
            $remaining = $candidateLength
            while ($remaining -gt 0) {
                $requested = [int][Math]::Min(
                    [long]$sourceBuffer.Length,
                    $remaining)
                $sourceRead = $sourceStream.Read(
                    $sourceBuffer,
                    0,
                    $requested)
                $candidateRead = $candidateStream.Read(
                    $candidateBuffer,
                    0,
                    $requested)
                if ($sourceRead -ne $requested -or
                    $candidateRead -ne $requested) {
                    return $false
                }
                for ($index = 0; $index -lt $requested; $index++) {
                    if ($sourceBuffer[$index] -ne $candidateBuffer[$index]) {
                        return $false
                    }
                }
                $remaining -= $requested
            }
            return $candidateStream.ReadByte() -eq -1
        }
        finally {
            $candidateStream.Dispose()
        }
    }
    finally {
        $sourceStream.Dispose()
    }
}

function Assert-FslFileCopyPlanPreconditions {
    param(
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][object[]]$Plan
    )

    foreach ($operation in @($Plan | Where-Object {
        $_.kind -ceq 'FileCopyAtomic'
    })) {
        $target = [System.IO.Path]::GetFullPath([string]$operation.target)
        $parent = [System.IO.Path]::GetDirectoryName($target)
        $temporary =
            [System.IO.Path]::GetFullPath(
                [string]$operation.desired.temporaryPath)
        $expectedTemporary =
            Get-FslDeterministicTemporaryPath $target $TransactionId
        if ($temporary -cne $expectedTemporary -or
            [string]$operation.desired.targetParent -cne $parent -or
            (Test-Path -LiteralPath $target) -or
            (Test-Path -LiteralPath $temporary) -or
            -not (Test-FslWalFileMatches `
                ([string]$operation.desired.source) $operation.desired)) {
            Stop-FslStage4 $script:ExitCodes.InstallAcl (
                'FileCopyAtomic Begin preconditions or deterministic name failed.')
        }
        Assert-FslWalOrdinarySingleLink (
            [string]$operation.desired.source)
        if (Test-Path -LiteralPath $parent) {
            $parentIdentity =
                [FolderSessionLock.Stage4.Native]::DescribeFile($parent, $true)
            if ($parentIdentity.IsReparse -or
                $parentIdentity.FinalPath -cne $parent.TrimEnd('\')) {
                Stop-FslStage4 $script:ExitCodes.InstallAcl (
                    'FileCopyAtomic Begin found an unsafe target parent.')
            }
        }
    }
}

function Get-FslFileCopyIntentProof {
    param(
        [Parameter(Mandatory = $true)][psobject]$Operation,
        [Parameter(Mandatory = $true)][string]$TransactionId
    )

    $target = [System.IO.Path]::GetFullPath([string]$Operation.target)
    $parent = [System.IO.Path]::GetDirectoryName($target)
    $temporary =
        [System.IO.Path]::GetFullPath(
            [string]$Operation.desired.temporaryPath)
    if ($temporary -cne
            (Get-FslDeterministicTemporaryPath $target $TransactionId) -or
        [string]$Operation.desired.targetParent -cne $parent -or
        -not (Test-Path -LiteralPath $parent -PathType Container) -or
        (Test-Path -LiteralPath $target) -or
        (Test-Path -LiteralPath $temporary) -or
        -not (Test-FslWalFileMatches `
            ([string]$Operation.desired.source) $Operation.desired)) {
        Stop-FslStage4 $script:ExitCodes.InstallAcl (
            'FileCopyAtomic Intent preconditions failed.')
    }
    Assert-FslWalOrdinarySingleLink ([string]$Operation.desired.source)
    $sourceProof = Get-FslWalFileProof ([string]$Operation.desired.source)
    $parentProof = Get-FslPathProof $parent
    return [pscustomobject][ordered]@{
        targetParent = $parent
        parentProof = $parentProof
        sourceProof = $sourceProof
        targetAbsent = $true
        temporaryAbsent = $true
    }
}

function Assert-FslFileCopyIntentBinding {
    param(
        [Parameter(Mandatory = $true)][psobject]$Operation,
        [Parameter(Mandatory = $true)][psobject]$Intent
    )

    if ($null -eq $Intent.proof -or
        $null -eq $Intent.proof.parentProof -or
        $null -eq $Intent.proof.sourceProof) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'FileCopyAtomic Intent has no parent/source binding.')
    }
    $targetParent =
        [System.IO.Path]::GetDirectoryName(
            [System.IO.Path]::GetFullPath([string]$Operation.target))
    if ([string]$Intent.proof.targetParent -cne $targetParent) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'FileCopyAtomic Intent parent path changed.')
    }
    Assert-FslPathProof $targetParent $Intent.proof.parentProof
    if (-not (Test-FslWalFileMatches `
        ([string]$Operation.desired.source) $Operation.desired)) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'FileCopyAtomic frozen source content changed.')
    }
    Assert-FslPathProof `
        ([string]$Operation.desired.source) $Intent.proof.sourceProof
    Assert-FslWalOrdinarySingleLink ([string]$Operation.desired.source)
}

function Invoke-FslFileCopyAtomicPrimitive {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$Operation,
        [Parameter(Mandatory = $true)][psobject]$Intent
    )

    $source = [string]$Operation.desired.source
    $target = [string]$Operation.target
    $temporary = [string]$Operation.desired.temporaryPath
    $targetParent = [System.IO.Path]::GetDirectoryName($target)
    Assert-FslFileCopyIntentBinding $Operation $Intent
    if ([System.IO.Path]::GetDirectoryName($temporary) -cne $targetParent -or
        $temporary -cne
            (Get-FslDeterministicTemporaryPath `
                $target ([string]$Intent.transactionId)) -or
        (Test-Path -LiteralPath $target) -or
        (Test-Path -LiteralPath $temporary) -or
        -not (Test-FslWalFileMatches $source $Operation.desired)) {
        Stop-FslStage4 $script:ExitCodes.InstallAcl (
            'Atomic copy precondition or frozen source proof failed.')
    }
    $sourceStream = [System.IO.FileStream]::new(
        $source,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $targetStream = [System.IO.FileStream]::new(
            $temporary,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None,
            1048576,
            [System.IO.FileOptions]::WriteThrough)
        try {
            Publish-FslWalBoundary `
                $Context $Operation.operationId 'AfterTempCreate'
            $buffer = [byte[]]::new(1048576)
            $written = [long]0
            $duringPublished = $false
            while (($read = $sourceStream.Read(
                $buffer,
                0,
                $buffer.Length)) -gt 0) {
                $targetStream.Write($buffer, 0, $read)
                $written += $read
                if (-not $duringPublished -and
                    $written -gt 0 -and
                    $written -lt [long]$Operation.desired.length) {
                    $targetStream.Flush($true)
                    Publish-FslWalBoundary `
                        $Context $Operation.operationId 'DuringTempWrite'
                    $duringPublished = $true
                }
            }
            if ($written -ne [long]$Operation.desired.length) {
                Stop-FslStage4 $script:ExitCodes.InstallAcl (
                    'Atomic copy source length changed during the copy.')
            }
            $targetStream.Flush($true)
        }
        finally {
            $targetStream.Dispose()
        }
    }
    finally {
        $sourceStream.Dispose()
    }
    if (-not (Test-FslWalFileMatches $temporary $Operation.desired)) {
        Stop-FslStage4 $script:ExitCodes.InstallAcl (
            'Atomic copy temporary proof failed.')
    }
    Assert-FslWalOrdinarySingleLink $temporary
    Assert-FslWalTemporarySecurity $temporary $targetParent
    $temporaryProof = Get-FslWalFileProof $temporary
    Publish-FslWalBoundary `
        $Context $Operation.operationId 'AfterTempFlush'
    [FolderSessionLock.Stage4.Native]::RenameNoReplace($temporary, $target)
    Publish-FslWalBoundary `
        $Context $Operation.operationId 'AfterRename'
    Assert-FslFileCopyIntentBinding $Operation $Intent
    if (-not (Test-FslWalFileMatches $target $Operation.desired) -or
        (Test-Path -LiteralPath $temporary)) {
        Stop-FslStage4 $script:ExitCodes.InstallAcl (
            'Atomic copy final proof failed.')
    }
    Assert-FslWalOrdinarySingleLink $target
    Assert-FslWalTemporarySecurity $target $targetParent
    return [pscustomobject]@{
        temporaryPath = $temporary
        temporaryProof = $temporaryProof
        finalProof = Get-FslWalFileProof $target
    }
}

function Add-FslInstallWalRecord {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$Record
    )

    if (-not ($Context.PSObject.Properties.Name -contains 'InstallWalPath')) {
        Stop-FslStage4 $script:ExitCodes.InstallAcl 'Install WAL path is missing.'
    }
    [void](Assert-FslExternalAnchor $Context)
    $records = @(Read-FslInstallWal -Context $Context -SkipAnchorValidation)
    $sequence = $records.Count + 1
    $previous = if ($records.Count -eq 0) {
        '0' * 64
    }
    else {
        [string]$records[-1].recordSha256
    }
    $core = [ordered]@{
        schemaVersion = $script:WalSchemaVersion
        runId = [string]$Context.RunId
        machineName = [Environment]::MachineName
        sequence = $sequence
        transactionId = [string]$Record.transactionId
        planHash = [string]$Record.planHash
        ordinal = [int]$Record.ordinal
        operationId = [string]$Record.operationId
        kind = [string]$Record.kind
        target = [string]$Record.target
        phase = [string]$Record.phase
        desired = $Record.desired
        proof = $Record.proof
        previousRecordSha256 = $previous
        recordedUtc = [DateTime]::UtcNow.ToString('o')
    }
    $coreJson = $core | ConvertTo-Json -Compress -Depth 30
    $entry = [ordered]@{}
    foreach ($key in $core.Keys) {
        $entry[$key] = $core[$key]
    }
    $entry.recordSha256 = Get-FslSha256 (
        [System.Text.UTF8Encoding]::new($false).GetBytes($coreJson))
    [void](Add-FslWriteThroughLine $Context.InstallWalPath (
        $entry | ConvertTo-Json -Compress -Depth 30))
    Write-FslExternalAnchor $Context
}

function Read-FslInstallWal {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [switch]$SkipAnchorValidation
    )

    if (-not $SkipAnchorValidation) {
        [void](Assert-FslExternalAnchor $Context)
    }
    if (-not (Test-Path -LiteralPath $Context.InstallWalPath -PathType Leaf)) {
        return @()
    }
    $lines = @([System.IO.File]::ReadAllLines($Context.InstallWalPath) |
        Where-Object { $_.Length -gt 0 })
    $records = @()
    $previous = '0' * 64
    for ($index = 0; $index -lt $lines.Count; $index++) {
        try {
            $record = $lines[$index] | ConvertFrom-Json
        }
        catch {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'The protected installation WAL contains invalid JSON.')
        }
        $core = [ordered]@{
            schemaVersion = [int]$record.schemaVersion
            runId = [string]$record.runId
            machineName = [string]$record.machineName
            sequence = [int]$record.sequence
            transactionId = [string]$record.transactionId
            planHash = [string]$record.planHash
            ordinal = [int]$record.ordinal
            operationId = [string]$record.operationId
            kind = [string]$record.kind
            target = [string]$record.target
            phase = [string]$record.phase
            desired = $record.desired
            proof = $record.proof
            previousRecordSha256 = [string]$record.previousRecordSha256
            recordedUtc = [string]$record.recordedUtc
        }
        $calculated = Get-FslSha256 (
            [System.Text.UTF8Encoding]::new($false).GetBytes(
                ($core | ConvertTo-Json -Compress -Depth 30)))
        if ($record.schemaVersion -ne $script:WalSchemaVersion -or
            $record.runId -cne $Context.RunId -or
            $record.machineName -cne [Environment]::MachineName -or
            [int]$record.sequence -ne ($index + 1) -or
            $record.previousRecordSha256 -cne $previous -or
            $record.recordSha256 -cne $calculated -or
            $record.phase -cnotin @(
                'Begin', 'Intent', 'Applied', 'RolledBack',
                'Committed', 'Aborted') -or
            [string]::IsNullOrWhiteSpace([string]$record.transactionId) -or
            [string]$record.planHash -cnotmatch '^[0-9A-F]{64}$') {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'The protected installation WAL chain or identity is invalid.')
        }
        $previous = [string]$record.recordSha256
        $records += $record
    }
    Assert-FslInstallWalSemantics $records
    return @($records)
}

function ConvertTo-FslWalPlanCore {
    param([Parameter(Mandatory = $true)][object[]]$Plan)

    $core = @()
    for ($index = 0; $index -lt $Plan.Count; $index++) {
        $operation = $Plan[$index]
        $core += [pscustomobject][ordered]@{
            ordinal = [int]$operation.ordinal
            operationId = [string]$operation.operationId
            kind = [string]$operation.kind
            target = [string]$operation.target
            desired = $operation.desired
        }
    }
    return @($core)
}

function Get-FslWalPlanHash {
    param([Parameter(Mandatory = $true)][object[]]$Plan)

    $json = ConvertTo-FslWalPlanCore $Plan |
        ConvertTo-Json -Compress -Depth 30
    return Get-FslSha256 (
        [System.Text.UTF8Encoding]::new($false).GetBytes($json))
}

function New-FslDurablePlan {
    param([Parameter(Mandatory = $true)][object[]]$Operations)

    if ($Operations.Count -eq 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'A durable transaction plan cannot be empty.')
    }
    $plan = @()
    for ($index = 0; $index -lt $Operations.Count; $index++) {
        $operation = $Operations[$index]
        if ([string]$operation.kind -cnotin $script:WalPrimitiveKinds -or
            [string]::IsNullOrWhiteSpace([string]$operation.operationId) -or
            [string]::IsNullOrWhiteSpace([string]$operation.target) -or
            $null -eq $operation.desired) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'A durable plan contains an invalid primitive.')
        }
        $target = if ([string]$operation.kind -cin @(
            'DirectoryCreate',
            'DirectorySetAcl',
            'FileCopyAtomic',
            'DeleteFile',
            'DeleteDirectory')) {
            [System.IO.Path]::GetFullPath([string]$operation.target)
        }
        else {
            [string]$operation.target
        }
        $plan += [pscustomobject][ordered]@{
            ordinal = $index
            operationId = [string]$operation.operationId
            kind = [string]$operation.kind
            target = $target
            desired = $operation.desired
        }
    }
    if (@($plan | Group-Object operationId |
        Where-Object { $_.Count -ne 1 }).Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'A durable plan contains a duplicate operation identifier.')
    }
    return @($plan)
}

function Assert-FslInstallWalSemantics {
    param([Parameter(Mandatory = $true)][object[]]$Records)

    $seenTransactions = @{}
    $activeTransaction = $null
    foreach ($record in $Records) {
        if ($null -eq $activeTransaction -or
            $record.transactionId -cne $activeTransaction) {
            if ($seenTransactions.ContainsKey(
                [string]$record.transactionId)) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'WAL transactions are interleaved or reopened.')
            }
            $activeTransaction = [string]$record.transactionId
            $seenTransactions[$activeTransaction] = $true
        }
    }
    foreach ($transaction in @($Records | Group-Object transactionId)) {
        $group = @($transaction.Group | Sort-Object sequence)
        $begin = @($group | Where-Object { $_.phase -ceq 'Begin' })
        if ($begin.Count -ne 1 -or $group[0].phase -cne 'Begin' -or
            $begin[0].kind -cne 'Transaction' -or
            $begin[0].ordinal -ne -1 -or
            $null -eq $begin[0].desired.plan -or
            $begin[0].desired.recoveryMode -cnotin @('Rollback', 'Forward') -or
            $begin[0].desired.workflow -cnotin @(
                'Install', 'Uninstall', 'Cleanup', 'WalTest')) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'A WAL transaction Begin record or frozen plan is invalid.')
        }
        $plan = @(ConvertTo-FslWalPlanCore @($begin[0].desired.plan))
        if ($plan.Count -eq 0 -or
            (Get-FslWalPlanHash $plan) -cne $begin[0].planHash -or
            [string]$begin[0].desired.planHash -cne $begin[0].planHash) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'A WAL frozen plan hash is invalid.')
        }
        for ($index = 0; $index -lt $plan.Count; $index++) {
            if ($plan[$index].ordinal -ne $index -or
                $plan[$index].kind -cnotin $script:WalPrimitiveKinds -or
                [string]::IsNullOrWhiteSpace($plan[$index].operationId) -or
                [string]::IsNullOrWhiteSpace($plan[$index].target) -or
                $null -eq $plan[$index].desired) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'A WAL frozen plan primitive is invalid or out of order.')
            }
        }
        if (@($plan | Group-Object operationId |
            Where-Object { $_.Count -ne 1 }).Count -ne 0) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'A WAL frozen plan contains a duplicate operation.')
        }

        $terminal = @($group | Where-Object {
            $_.phase -in @('Committed', 'Aborted')
        })
        if ($terminal.Count -gt 1 -or
            ($terminal.Count -eq 1 -and
                $group[-1].sequence -ne $terminal[0].sequence)) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                'A WAL transaction has duplicate or non-final terminal state.')
        }
        $nextIntent = 0
        $operationPhases = @{}
        foreach ($record in @($group | Select-Object -Skip 1)) {
            if ($record.planHash -cne $begin[0].planHash) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'A WAL record changed its frozen plan hash.')
            }
            if ($record.phase -in @('Committed', 'Aborted')) {
                if ($record.ordinal -ne -1 -or
                    $record.operationId -cne 'transaction' -or
                    $record.kind -cne 'Transaction') {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        'A WAL terminal record is malformed.')
                }
                continue
            }
            if ($record.ordinal -lt 0 -or $record.ordinal -ge $plan.Count) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'A WAL record ordinal is outside its frozen plan.')
            }
            $expected = $plan[$record.ordinal]
            if ($record.operationId -cne $expected.operationId -or
                $record.kind -cne $expected.kind -or
                $record.target -cne $expected.target -or
                (($record.desired | ConvertTo-Json -Compress -Depth 30) -cne
                    ($expected.desired |
                        ConvertTo-Json -Compress -Depth 30))) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'A WAL record does not match its frozen primitive.')
            }
            $key = [string]$record.ordinal
            if (-not $operationPhases.ContainsKey($key)) {
                $operationPhases[$key] = @()
            }
            if ($record.phase -cin @($operationPhases[$key])) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'A WAL primitive contains a duplicate phase.')
            }
            if ($record.phase -ceq 'Intent') {
                if ($record.ordinal -ne $nextIntent -or
                    @($operationPhases[$key]).Count -ne 0) {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        'A WAL Intent is duplicate or out of order.')
                }
                $nextIntent++
            }
            elseif ($record.phase -ceq 'Applied') {
                if (@($operationPhases[$key]) -cnotcontains 'Intent' -or
                    @($operationPhases[$key]) -contains 'RolledBack' -or
                    $null -eq $record.proof) {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        'A WAL Applied record has no unique Intent.')
                }
            }
            elseif ($record.phase -ceq 'RolledBack') {
                if ($begin[0].desired.recoveryMode -cne 'Rollback' -or
                    @($operationPhases[$key]) -cnotcontains 'Intent') {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        'A WAL RolledBack record is not legal.')
                }
                $rolledOrdinals = @($group |
                    Where-Object {
                        $_.phase -ceq 'RolledBack' -and
                        $_.sequence -lt $record.sequence
                    } |
                    ForEach-Object { [int]$_.ordinal })
                if ($rolledOrdinals.Count -gt 0 -and
                    $record.ordinal -ge $rolledOrdinals[-1]) {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        'WAL rollback order is not strict reverse order.')
                }
            }
            else {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'A WAL primitive phase is unknown.')
            }
            $operationPhases[$key] += [string]$record.phase
        }
        if ($terminal.Count -eq 1 -and $terminal[0].phase -ceq 'Committed') {
            foreach ($index in 0..($plan.Count - 1)) {
                if (@($operationPhases[[string]$index]) -cnotcontains 'Applied') {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        'A WAL Commit preceded a complete Applied plan.')
                }
            }
        }
        if ($terminal.Count -eq 1 -and $terminal[0].phase -ceq 'Aborted') {
            if ($begin[0].desired.recoveryMode -cne 'Rollback') {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'A forward transaction can never become Aborted.')
            }
            foreach ($key in $operationPhases.Keys) {
                if (@($operationPhases[$key]) -contains 'Intent' -and
                    @($operationPhases[$key]) -cnotcontains 'RolledBack') {
                    Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                        'A WAL Abort preceded complete rollback records.')
                }
            }
        }
    }
}

function Start-FslDurableTransaction {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Rollback', 'Forward')][string]$RecoveryMode,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Install', 'Uninstall', 'Cleanup', 'WalTest')]
        [string]$Workflow,
        [Parameter(Mandatory = $true)][object[]]$Plan
    )

    $records = @(Read-FslInstallWal $Context)
    $open = @($records |
        Group-Object transactionId |
        Where-Object {
            @($_.Group.phase | Where-Object {
                $_ -in @('Committed', 'Aborted')
            }).Count -eq 0
        })
    if ($open.Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'An incomplete durable transaction must be reconciled first.')
    }
    $frozenPlan = @(New-FslDurablePlan $Plan)
    Assert-FslFileCopyPlanPreconditions $TransactionId $frozenPlan
    $planHash = Get-FslWalPlanHash $frozenPlan
    Add-FslInstallWalRecord $Context ([pscustomobject]@{
        transactionId = $TransactionId
        planHash = $planHash
        ordinal = -1
        operationId = 'transaction'
        kind = 'Transaction'
        target = ''
        phase = 'Begin'
        desired = [pscustomobject][ordered]@{
            recoveryMode = $RecoveryMode
            workflow = $Workflow
            planHash = $planHash
            plan = $frozenPlan
        }
        proof = $null
    })
    [void](Read-FslInstallWal $Context)
    return [pscustomobject]@{
        TransactionId = $TransactionId
        PlanHash = $planHash
        Plan = $frozenPlan
        RecoveryMode = $RecoveryMode
        Workflow = $Workflow
    }
}

function Complete-FslDurableTransaction {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$TransactionId
    )

    $begin = @(Read-FslInstallWal $Context | Where-Object {
        $_.transactionId -ceq $TransactionId -and
        $_.phase -ceq 'Begin'
    })
    if ($begin.Count -ne 1) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'A durable Commit has no unique frozen plan.')
    }
    Add-FslInstallWalRecord $Context ([pscustomobject]@{
        transactionId = $TransactionId
        planHash = [string]$begin[0].planHash
        ordinal = -1
        operationId = 'transaction'
        kind = 'Transaction'
        target = ''
        phase = 'Committed'
        desired = $null
        proof = [pscustomobject]@{
            committedUtc = [DateTime]::UtcNow.ToString('o')
        }
    })
    [void](Read-FslInstallWal $Context)
}

function Add-FslPlannedWalRecord {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$Begin,
        [Parameter(Mandatory = $true)][psobject]$Operation,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Intent', 'Applied', 'RolledBack')][string]$Phase,
        [AllowNull()][psobject]$Proof
    )

    Add-FslInstallWalRecord $Context ([pscustomobject]@{
        transactionId = [string]$Begin.transactionId
        planHash = [string]$Begin.planHash
        ordinal = [int]$Operation.ordinal
        operationId = [string]$Operation.operationId
        kind = [string]$Operation.kind
        target = [string]$Operation.target
        phase = $Phase
        desired = $Operation.desired
        proof = $Proof
    })
}

function Invoke-FslPrimitive {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$Operation,
        [AllowNull()][psobject]$Intent
    )

    switch ([string]$Operation.kind) {
        'DirectoryCreate' {
            if (Test-Path -LiteralPath $Operation.target) {
                Stop-FslStage4 $script:ExitCodes.InstallAcl (
                    "DirectoryCreate target already exists: $($Operation.target).")
            }
            [System.IO.Directory]::CreateDirectory(
                [string]$Operation.target) | Out-Null
            return Get-FslPathProof ([string]$Operation.target)
        }
        'DirectorySetAcl' {
            return Invoke-FslDirectorySetAclPrimitive `
                ([string]$Operation.target) @($Operation.desired.grants)
        }
        'FileCopyAtomic' {
            if ($null -eq $Intent) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'FileCopyAtomic requires its durable Intent binding.')
            }
            return Invoke-FslFileCopyAtomicPrimitive `
                $Context $Operation $Intent
        }
        'ServiceCreate' {
            $binPath = [string]$Operation.desired.imagePath
            Invoke-FslCheckedProcess sc.exe @(
                'create', $script:ServiceName,
                "binPath= $binPath",
                'start= auto',
                'obj= LocalSystem',
                "DisplayName= $($script:ServiceDisplayName)") `
                $script:ExitCodes.Service 'sc create' $Context | Out-Null
            return Get-FslRawServiceSnapshot
        }
        'ServiceDescription' {
            Invoke-FslCheckedProcess sc.exe @(
                'description',
                $script:ServiceName,
                [string]$Operation.desired.value) `
                $script:ExitCodes.Service 'sc description' $Context | Out-Null
            return Get-FslRawServiceSnapshot
        }
        'ServiceSid' {
            Invoke-FslCheckedProcess sc.exe @(
                'sidtype', $script:ServiceName, 'unrestricted') `
                $script:ExitCodes.Service 'sc sidtype' $Context | Out-Null
            return Get-FslRawServiceSnapshot
        }
        'ServiceDelayed' {
            Set-ItemProperty `
                -LiteralPath (
                    "HKLM:\SYSTEM\CurrentControlSet\Services\" +
                    $script:ServiceName) `
                -Name DelayedAutoStart `
                -Type DWord `
                -Value ([int]$Operation.desired.value)
            return Get-FslRawServiceSnapshot
        }
        { $_ -in @(
            'ServiceStop',
            'ServiceDelete',
            'DeleteFile',
            'DeleteDirectory',
            'CertificateDelete') } {
            $intent = [pscustomobject]@{
                kind = [string]$Operation.kind
                target = [string]$Operation.target
                desired = $Operation.desired
            }
            return Complete-FslForwardDurableOperation $Context $intent
        }
        default {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                "Unknown planned primitive: $($Operation.kind).")
        }
    }
}

function Invoke-FslPlannedOperation {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$TransactionId,
        [Parameter(Mandatory = $true)][int]$Ordinal
    )

    $records = @(Read-FslInstallWal $Context)
    $begin = @($records | Where-Object {
        $_.transactionId -ceq $TransactionId -and
        $_.phase -ceq 'Begin'
    })
    if ($begin.Count -ne 1) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The planned executor has no unique Begin record.')
    }
    $plan = @($begin[0].desired.plan)
    if ($Ordinal -lt 0 -or $Ordinal -ge $plan.Count) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The planned executor ordinal is invalid.')
    }
    $operation = $plan[$Ordinal]
    $phases = @($records | Where-Object {
        $_.transactionId -ceq $TransactionId -and
        $_.ordinal -eq $Ordinal
    })
    if (@($phases | Where-Object {
        $_.phase -ceq 'Applied'
    }).Count -eq 1) {
        return @($phases | Where-Object {
            $_.phase -ceq 'Applied'
        })[0].proof
    }
    if ($phases.Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'An interrupted primitive must be reconciled before execution.')
    }
    $intentProof = if ($operation.kind -ceq 'FileCopyAtomic') {
        Get-FslFileCopyIntentProof $operation $TransactionId
    }
    else {
        $null
    }
    Add-FslPlannedWalRecord `
        $Context $begin[0] $operation 'Intent' $intentProof
    $intent = @(Read-FslInstallWal $Context | Where-Object {
        $_.transactionId -ceq $TransactionId -and
        $_.ordinal -eq $Ordinal -and
        $_.phase -ceq 'Intent'
    })
    if ($intent.Count -ne 1) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The planned executor did not persist a unique Intent.')
    }
    Publish-FslWalBoundary `
        $Context $operation.operationId 'AfterIntent'
    $proof = Invoke-FslPrimitive $Context $operation $intent[0]
    if ($null -eq $proof) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'A planned primitive returned no proof.')
    }
    Add-FslPlannedWalRecord `
        $Context $begin[0] $operation 'Applied' $proof
    Publish-FslWalBoundary `
        $Context $operation.operationId 'AfterApplied'
    return $proof
}

function Invoke-FslExecuteDurablePlan {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$TransactionId
    )

    $begin = @(Read-FslInstallWal $Context | Where-Object {
        $_.transactionId -ceq $TransactionId -and
        $_.phase -ceq 'Begin'
    })
    if ($begin.Count -ne 1) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The durable plan has no unique Begin record.')
    }
    foreach ($operation in @($begin[0].desired.plan)) {
        [void](Invoke-FslPlannedOperation `
            $Context $TransactionId ([int]$operation.ordinal))
    }
}

function Test-FslWalFileMatches {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][psobject]$Expected
    )

    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-Item -LiteralPath $Path).Length -eq [long]$Expected.length -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ceq
            [string]$Expected.sha256
}

function Get-FslWalFileProof {
    param([Parameter(Mandatory = $true)][string]$Path)

    $pathProof = Get-FslPathProof $Path
    return [pscustomobject]@{
        length = (Get-Item -LiteralPath $Path).Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        finalPath = $pathProof.finalPath
        fileId = $pathProof.fileId
        aclSddl = $pathProof.aclSddl
    }
}

function Test-FslDirectoryAclMatchesGrants {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$Grants
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }
    $acl = Get-Acl -LiteralPath $Path
    $owner = ([Security.Principal.NTAccount]$acl.Owner).Translate(
        [Security.Principal.SecurityIdentifier]).Value
    if ($owner -cne 'S-1-5-18' -or -not $acl.AreAccessRulesProtected) {
        return $false
    }
    $rules = @($acl.GetAccessRules(
        $true,
        $false,
        [Security.Principal.SecurityIdentifier]) |
        Where-Object {
            $_.AccessControlType -eq
                [Security.AccessControl.AccessControlType]::Allow
        })
    if ($rules.Count -ne $Grants.Count) {
        return $false
    }
    foreach ($grant in $Grants) {
        if ($grant -cnotmatch '^(?<account>.+):(?<flags>\(OI\)\(CI\))?\((?<rights>F|RX)\)$') {
            return $false
        }
        $account = $Matches.account
        if ($account.StartsWith('*', [StringComparison]::Ordinal)) {
            $sid = $account.Substring(1)
        }
        else {
            try {
                $sid = ([Security.Principal.NTAccount]$account).Translate(
                    [Security.Principal.SecurityIdentifier]).Value
            }
            catch {
                return $false
            }
        }
        $expectedRights = if ($Matches.rights -ceq 'F') {
            [Security.AccessControl.FileSystemRights]::FullControl
        }
        else {
            [Security.AccessControl.FileSystemRights]::ReadAndExecute
        }
        $expectedInheritance = if ($Matches.flags -ceq '(OI)(CI)') {
            [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
        }
        else {
            [Security.AccessControl.InheritanceFlags]::None
        }
        $match = @($rules | Where-Object {
            $_.IdentityReference.Value -ceq $sid -and
            ($_.FileSystemRights -band $expectedRights) -eq $expectedRights -and
            $_.InheritanceFlags -eq $expectedInheritance -and
            $_.PropagationFlags -eq
                [Security.AccessControl.PropagationFlags]::None
        })
        if ($match.Count -ne 1) {
            return $false
        }
    }
    return $true
}

function Assert-FslServiceOwnedForRollback {
    param(
        [Parameter(Mandatory = $true)][psobject]$Snapshot,
        [Parameter(Mandatory = $true)][string]$BrokerPath
    )

    $expectedImagePath = "`"$BrokerPath`" --mode recovery-service"
    if ($Snapshot.serviceName -cne $script:ServiceName -or
        $Snapshot.displayName -cne $script:ServiceDisplayName -or
        $Snapshot.startName -cne 'LocalSystem' -or
        $Snapshot.startMode -cne 'Auto' -or
        $Snapshot.imagePath -cne $expectedImagePath -or
        $Snapshot.start -ne 2 -or
        $Snapshot.state -cne 'Stopped' -or
        $Snapshot.processId -ne 0) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'Durable service rollback refused an unknown or running service.')
    }
}

function Assert-FslServiceOwnedBase {
    param(
        [Parameter(Mandatory = $true)][psobject]$Snapshot,
        [Parameter(Mandatory = $true)][string]$BrokerPath
    )

    $expectedImagePath = "`"$BrokerPath`" --mode recovery-service"
    if ($Snapshot.serviceName -cne $script:ServiceName -or
        $Snapshot.displayName -cne $script:ServiceDisplayName -or
        $Snapshot.startName -cne 'LocalSystem' -or
        $Snapshot.startMode -cne 'Auto' -or
        $Snapshot.imagePath -cne $expectedImagePath -or
        $Snapshot.start -ne 2) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'Durable service operation refused an unknown service.')
    }
}

function Undo-FslDurableOperation {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$Intent,
        [AllowNull()][psobject]$Applied
    )

    switch ([string]$Intent.kind) {
        'FileCopyAtomic' {
            $temporary = [string]$Intent.desired.temporaryPath
            $target = [string]$Intent.target
            $targetParent = [System.IO.Path]::GetDirectoryName(
                [System.IO.Path]::GetFullPath($target))
            if ($temporary -cne
                    (Get-FslDeterministicTemporaryPath `
                        $target ([string]$Intent.transactionId)) -or
                [string]$Intent.desired.targetParent -cne $targetParent) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Atomic-copy recovery refused a non-deterministic temp path.')
            }
            Assert-FslFileCopyIntentBinding $Intent $Intent
            $temporaryExists = Test-Path -LiteralPath $temporary
            $finalExists = Test-Path -LiteralPath $target
            if ($temporaryExists -and $finalExists) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Atomic-copy recovery found both temporary and final files.')
            }
            if ($temporaryExists) {
                if (-not (Test-Path -LiteralPath $temporary -PathType Leaf)) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        'Atomic-copy recovery refused a non-file temp object.')
                }
                Assert-FslWalOrdinarySingleLink $temporary
                Assert-FslWalTemporarySecurity $temporary $targetParent
                if (-not (Test-FslWalFilePrefix `
                    ([string]$Intent.desired.source) `
                    $temporary ([long]$Intent.desired.length))) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        'Atomic-copy recovery refused a non-source-prefix temp.')
                }
                [System.IO.File]::Delete($temporary)
            }
            if ($finalExists) {
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        'Atomic-copy recovery refused a non-file final object.')
                }
                if (-not (Test-FslWalFileMatches `
                    $target $Intent.desired)) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        'Atomic-copy recovery refused an unknown final file.')
                }
                Assert-FslWalOrdinarySingleLink $target
                Assert-FslWalTemporarySecurity $target $targetParent
                if ($null -ne $Applied -and
                    $null -ne $Applied.proof.finalProof) {
                    Assert-FslPathProof `
                        $target $Applied.proof.finalProof
                }
                [System.IO.File]::Delete($target)
            }
            if ((Test-Path -LiteralPath $temporary) -or
                (Test-Path -LiteralPath $target)) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Atomic-copy rollback left a file side effect.')
            }
            return
        }
        'DirectorySetAcl' {
            return
        }
        'DirectoryCreate' {
            if (-not (Test-Path -LiteralPath $Intent.target)) {
                return
            }
            if (@(Get-ChildItem -LiteralPath $Intent.target -Force).Count -ne 0) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    "Durable rollback refused a changed directory: $($Intent.target).")
            }
            if ($null -ne $Applied) {
                Assert-FslPathProof `
                    ([string]$Intent.target) $Applied.proof
            }
            else {
                $identity = [FolderSessionLock.Stage4.Native]::DescribeFile(
                    [string]$Intent.target,
                    $true)
                if ($identity.IsReparse -or
                    $identity.FinalPath -cne
                        [System.IO.Path]::GetFullPath(
                            [string]$Intent.target).TrimEnd('\')) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        'DirectoryCreate recovery refused an unknown directory.')
                }
            }
            [System.IO.Directory]::Delete([string]$Intent.target, $false)
            return
        }
        { $_ -in @(
            'ServiceDescription',
            'ServiceSid',
            'ServiceDelayed') } {
            if (-not (Test-FslServiceExists)) {
                return
            }
            $snapshot = Get-FslRawServiceSnapshot
            Assert-FslServiceOwnedForRollback `
                $snapshot ([string]$Intent.desired.brokerPath)
            switch ([string]$Intent.kind) {
                'ServiceDescription' {
                    if ($snapshot.description -cnotin @(
                        '', [string]$Intent.desired.value)) {
                        Stop-FslStage4 $script:ExitCodes.Cleanup (
                            'The service description was replaced during rollback.')
                    }
                }
                'ServiceSid' {
                    if ($snapshot.serviceSidType -notin @(
                        0, [int]$Intent.desired.value)) {
                        Stop-FslStage4 $script:ExitCodes.Cleanup (
                            'The service SID type was replaced during rollback.')
                    }
                }
                'ServiceDelayed' {
                    if ($snapshot.delayedAutoStart -notin @(
                        0, [int]$Intent.desired.value)) {
                        Stop-FslStage4 $script:ExitCodes.Cleanup (
                            'The delayed-start value was replaced during rollback.')
                    }
                }
            }
            return
        }
        'ServiceCreate' {
            if (-not (Test-FslServiceExists)) {
                return
            }
            $snapshot = Get-FslRawServiceSnapshot
            Assert-FslServiceOwnedForRollback `
                $snapshot ([string]$Intent.desired.brokerPath)
            Invoke-FslCheckedProcess sc.exe @(
                'delete', $script:ServiceName) `
                $script:ExitCodes.Cleanup 'durable sc delete' $Context | Out-Null
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while ((Test-FslServiceExists) -and
                [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 250
            }
            if (Test-FslServiceExists) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Durable service rollback did not complete.')
            }
            return
        }
        default {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                "Unknown durable operation kind cannot be reconciled: $($Intent.kind).")
        }
    }
}

function Assert-FslDurableOperationApplied {
    param(
        [Parameter(Mandatory = $true)][psobject]$Intent,
        [Parameter(Mandatory = $true)][psobject]$Applied
    )

    switch ([string]$Intent.kind) {
        'FileCopyAtomic' {
            if (-not (Test-FslWalFileMatches `
                $Intent.target $Applied.proof.finalProof) -or
                (Test-Path -LiteralPath $Intent.desired.temporaryPath)) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    "A committed durable file proof changed: $($Intent.target).")
            }
            Assert-FslPathProof `
                ([string]$Intent.target) $Applied.proof.finalProof
            return
        }
        'DirectorySetAcl' {
            if (-not (Test-FslDirectoryAclMatchesGrants `
                ([string]$Intent.target) @($Intent.desired.grants))) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    "A committed directory ACL changed: $($Intent.target).")
            }
            Assert-FslPathProof ([string]$Intent.target) $Applied.proof
            return
        }
        'DirectoryCreate' {
            Assert-FslPathProof ([string]$Intent.target) $Applied.proof
            return
        }
        'ServiceCreate' {
            $snapshot = Get-FslRawServiceSnapshot
            Assert-FslServiceOwnedForRollback `
                $snapshot ([string]$Intent.desired.brokerPath)
            return
        }
        'ServiceDescription' {
            if ((Get-FslRawServiceSnapshot).description -cne
                [string]$Intent.desired.value) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'The committed service description changed.')
            }
            return
        }
        'ServiceSid' {
            if ((Get-FslRawServiceSnapshot).serviceSidType -ne
                [int]$Intent.desired.value) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'The committed service SID type changed.')
            }
            return
        }
        'ServiceDelayed' {
            if ((Get-FslRawServiceSnapshot).delayedAutoStart -ne
                [int]$Intent.desired.value) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'The committed service delayed-start value changed.')
            }
            return
        }
        default {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                "Unknown applied durable operation kind: $($Intent.kind).")
        }
    }
}

function Complete-FslForwardDurableOperation {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$Intent
    )

    switch ([string]$Intent.kind) {
        'ServiceStop' {
            if (-not (Test-FslServiceExists)) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Forward service-stop recovery found the service missing.')
            }
            $snapshot = Get-FslRawServiceSnapshot
            Assert-FslServiceOwnedBase `
                $snapshot ([string]$Intent.desired.brokerPath)
            if ($snapshot.state -cne 'Stopped') {
                Invoke-FslCheckedProcess sc.exe @(
                    'stop', $script:ServiceName) `
                    $script:ExitCodes.Cleanup 'forward sc stop' $Context |
                    Out-Null
                (Get-Service -Name $script:ServiceName).WaitForStatus(
                    'Stopped',
                    [TimeSpan]::FromSeconds(30))
            }
        }
        'DeleteFile' {
            if (Test-Path -LiteralPath $Intent.target) {
                if (-not (Test-FslWalFileMatches `
                    ([string]$Intent.target) $Intent.desired.expected)) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        "Forward recovery refused a replaced file: $($Intent.target).")
                }
                Assert-FslPathProof `
                    ([string]$Intent.target) $Intent.desired.expected
                [System.IO.File]::Delete([string]$Intent.target)
            }
        }
        'DeleteDirectory' {
            if (Test-Path -LiteralPath $Intent.target) {
                Assert-FslPathProof `
                    ([string]$Intent.target) $Intent.desired.expected
                if (@(Get-ChildItem -LiteralPath $Intent.target -Force).Count -ne 0) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        "Forward recovery refused a non-empty directory: $($Intent.target).")
                }
                [System.IO.Directory]::Delete([string]$Intent.target, $false)
            }
        }
        'ServiceDelete' {
            if (Test-FslServiceExists) {
                $snapshot = Get-FslRawServiceSnapshot
                if (($snapshot | ConvertTo-Json -Compress -Depth 10) -cne
                    ($Intent.desired.expected |
                        ConvertTo-Json -Compress -Depth 10)) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        'Forward recovery refused a replaced service.')
                }
                Assert-FslServiceSnapshotExact `
                    $snapshot ([string]$Intent.desired.brokerPath) $true
                Invoke-FslCheckedProcess sc.exe @(
                    'delete', $script:ServiceName) `
                    $script:ExitCodes.Cleanup 'forward sc delete' $Context |
                    Out-Null
                $deadline = [DateTime]::UtcNow.AddSeconds(30)
                while ((Test-FslServiceExists) -and
                    [DateTime]::UtcNow -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                }
                if (Test-FslServiceExists) {
                    Stop-FslStage4 $script:ExitCodes.Cleanup (
                        'Forward service deletion did not complete.')
                }
            }
        }
        'CertificateDelete' {
            $store = [string]$Intent.desired.store
            $matches = @(Get-ChildItem $store -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Thumbprint -ceq [string]$Intent.desired.thumbprint -and
                    $_.Subject -ceq [string]$Intent.desired.subject
                })
            if ($matches.Count -gt 1) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Forward certificate recovery found duplicate identities.')
            }
            if ($matches.Count -eq 1) {
                Remove-Item -LiteralPath $matches[0].PSPath -Force
            }
        }
        default {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                "Unknown forward durable operation kind: $($Intent.kind).")
        }
    }
    return [pscustomobject]@{
        absent = $true
        verifiedUtc = [DateTime]::UtcNow.ToString('o')
    }
}

function Invoke-FslReconcileInstallWal {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [AllowNull()][psobject]$State
    )

    $records = @(Read-FslInstallWal $Context)
    if ($records.Count -eq 0) {
        return
    }
    foreach ($transaction in @($records | Group-Object transactionId)) {
        $group = @($transaction.Group | Sort-Object sequence)
        $begin = @($group | Where-Object { $_.phase -ceq 'Begin' })[0]
        $terminal = @($group | Where-Object {
            $_.phase -in @('Committed', 'Aborted')
        })
        if ($terminal.Count -eq 1) {
            continue
        }
        $plan = @($begin.desired.plan)
        if ($begin.desired.recoveryMode -ceq 'Forward') {
            foreach ($operation in $plan) {
                $operationRecords = @($group | Where-Object {
                    $_.ordinal -eq [int]$operation.ordinal
                })
                $applied = @($operationRecords | Where-Object {
                    $_.phase -ceq 'Applied'
                })
                if ($applied.Count -eq 1) {
                    if ($operation.kind -cin @(
                        'DeleteFile', 'DeleteDirectory') -and
                        (Test-Path -LiteralPath $operation.target)) {
                        Stop-FslStage4 $script:ExitCodes.Cleanup (
                            "A forward-deleted target reappeared: " +
                            $operation.target)
                    }
                    continue
                }
                $intent = @($operationRecords | Where-Object {
                    $_.phase -ceq 'Intent'
                })
                if ($intent.Count -eq 0) {
                    [void](Invoke-FslPlannedOperation `
                        $Context $transaction.Name ([int]$operation.ordinal))
                    $group = @(Read-FslInstallWal $Context | Where-Object {
                        $_.transactionId -ceq $transaction.Name
                    })
                    continue
                }
                $proof = Invoke-FslPrimitive `
                    $Context $operation $intent[0]
                Add-FslPlannedWalRecord `
                    $Context $begin $operation 'Applied' $proof
                Publish-FslWalBoundary `
                    $Context $operation.operationId 'AfterApplied'
                $group = @(Read-FslInstallWal $Context | Where-Object {
                    $_.transactionId -ceq $transaction.Name
                })
            }
            Complete-FslDurableTransaction $Context $transaction.Name
            Publish-FslWalBoundary `
                $Context 'transaction' 'AfterCommit'
            continue
        }

        $intents = @($group | Where-Object {
            $_.phase -ceq 'Intent'
        } | Sort-Object ordinal -Descending)
        foreach ($intent in $intents) {
            if (@($group | Where-Object {
                $_.ordinal -eq $intent.ordinal -and
                $_.phase -ceq 'RolledBack'
            }).Count -eq 1) {
                continue
            }
            $applied = @($group | Where-Object {
                $_.ordinal -eq $intent.ordinal -and
                $_.phase -ceq 'Applied'
            })
            $appliedRecord = if ($applied.Count -eq 1) {
                $applied[0]
            }
            else {
                $null
            }
            Undo-FslDurableOperation $Context $intent $appliedRecord
            Add-FslPlannedWalRecord `
                $Context $begin $intent 'RolledBack' $null
            $group = @(Read-FslInstallWal $Context | Where-Object {
                $_.transactionId -ceq $transaction.Name
            })
        }
        foreach ($operation in $plan) {
            if ($operation.kind -ceq 'FileCopyAtomic' -and
                ((Test-Path -LiteralPath $operation.target) -or
                    (Test-Path -LiteralPath (
                        [string]$operation.desired.temporaryPath)))) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Install rollback left a final or temporary file.')
            }
        }
        Add-FslInstallWalRecord $Context ([pscustomobject]@{
            transactionId = [string]$transaction.Name
            planHash = [string]$begin.planHash
            ordinal = -1
            operationId = 'transaction'
            kind = 'Transaction'
            target = ''
            phase = 'Aborted'
            desired = $null
            proof = [pscustomobject]@{
                reconciledUtc = [DateTime]::UtcNow.ToString('o')
            }
        })
        Publish-FslWalBoundary $Context 'transaction' 'AfterAbort'
    }
}

function Get-FslProgramDataContracts {
    $serviceInherit = "NT SERVICE\$($script:ServiceName):(OI)(CI)(F)"
    $serviceLeaf = "NT SERVICE\$($script:ServiceName):(F)"
    $contracts = @()
    foreach ($relative in @(
        '', 'Recovery', 'Recovery\Records', 'Replay', 'Replay\v1', 'Logs')) {
        $contracts += [pscustomobject]@{
            RelativePath = $relative
            Grants = @(
                '*S-1-5-18:(OI)(CI)(F)',
                '*S-1-5-32-544:(OI)(CI)(F)',
                $serviceInherit)
        }
    }
    $contracts += [pscustomobject]@{
        RelativePath = 'Logs\v1'
        Grants = @(
            '*S-1-5-18:(F)',
            '*S-1-5-32-544:(F)',
            $serviceLeaf)
    }
    foreach ($mode in @('consent-broker', 'recovery-service', 'recovery-once')) {
        $contracts += [pscustomobject]@{
            RelativePath = "Logs\v1\$mode"
            Grants = @(
                '*S-1-5-18:(F)',
                '*S-1-5-32-544:(F)',
                $serviceLeaf)
        }
    }
    $contracts += [pscustomobject]@{
        RelativePath = 'Readiness'
        Grants = @(
            '*S-1-5-18:(F)',
            '*S-1-5-32-544:(F)',
            $serviceLeaf,
            '*S-1-5-32-545:(RX)')
    }
    return @($contracts)
}

function New-FslInstallPlan {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$State,
        [Parameter(Mandatory = $true)][string]$TransactionId
    )

    $operations = @(
        [pscustomobject]@{
            operationId = 'InstallDirectoryCreate'
            kind = 'DirectoryCreate'
            target = $Context.InstallDirectory
            desired = [pscustomobject]@{ mustNotExist = $true }
        },
        [pscustomobject]@{
            operationId = 'InstallDirectorySetAcl'
            kind = 'DirectorySetAcl'
            target = $Context.InstallDirectory
            desired = [pscustomobject]@{
                grants = @(
                    '*S-1-5-18:(OI)(CI)(F)',
                    '*S-1-5-32-544:(OI)(CI)(F)',
                    '*S-1-5-32-545:(OI)(CI)(RX)')
            }
        })
    $descriptor = Read-FslFrozenReleaseDescriptor `
        $State.ReleaseRoot ([string]$State.ReleaseDescriptorSha256)
    foreach ($name in @($descriptor.exactReleaseFiles | Sort-Object)) {
        $source = Join-Path $State.ReleaseRoot $name
        $target = Join-Path $Context.InstallDirectory $name
        $operations += [pscustomobject]@{
            operationId = "Copy:$name"
            kind = 'FileCopyAtomic'
            target = $target
            desired = [pscustomobject][ordered]@{
                source = $source
                targetParent = $Context.InstallDirectory
                temporaryPath = Join-Path $Context.InstallDirectory (
                    ".$name.$TransactionId.tmp")
                length = (Get-Item -LiteralPath $source).Length
                sha256 = (
                    Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            }
        }
    }
    $binPath = "`"$($Context.BrokerPath)`" --mode recovery-service"
    $operations += @(
        [pscustomobject]@{
            operationId = 'ServiceCreate'
            kind = 'ServiceCreate'
            target = $script:ServiceName
            desired = [pscustomobject]@{
                brokerPath = $Context.BrokerPath
                imagePath = $binPath
            }
        },
        [pscustomobject]@{
            operationId = 'ServiceDescription'
            kind = 'ServiceDescription'
            target = $script:ServiceName
            desired = [pscustomobject]@{
                brokerPath = $Context.BrokerPath
                value = $script:ServiceDescription
            }
        },
        [pscustomobject]@{
            operationId = 'ServiceSid'
            kind = 'ServiceSid'
            target = $script:ServiceName
            desired = [pscustomobject]@{
                brokerPath = $Context.BrokerPath
                value = 1
            }
        },
        [pscustomobject]@{
            operationId = 'ServiceDelayed'
            kind = 'ServiceDelayed'
            target = $script:ServiceName
            desired = [pscustomobject]@{
                brokerPath = $Context.BrokerPath
                value = 0
            }
        })
    foreach ($contract in Get-FslProgramDataContracts) {
        $path = if ([string]::IsNullOrEmpty($contract.RelativePath)) {
            $Context.ProgramDataRoot
        }
        else {
            Join-Path $Context.ProgramDataRoot $contract.RelativePath
        }
        $key = if ([string]::IsNullOrEmpty($contract.RelativePath)) {
            'Root'
        }
        else {
            $contract.RelativePath.Replace('\', '-')
        }
        $operations += [pscustomobject]@{
            operationId = "ProgramDataCreate:$key"
            kind = 'DirectoryCreate'
            target = $path
            desired = [pscustomobject]@{ mustNotExist = $true }
        }
        $operations += [pscustomobject]@{
            operationId = "ProgramDataSetAcl:$key"
            kind = 'DirectorySetAcl'
            target = $path
            desired = [pscustomobject]@{ grants = @($contract.Grants) }
        }
    }
    return @(New-FslDurablePlan $operations)
}

function Get-FslInstallProof {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$State
    )

    return [pscustomobject]@{
        releaseManifestSha256 = (
            Get-FileHash -LiteralPath (
                Join-Path $State.ReleaseRoot 'release-manifest.json') `
                -Algorithm SHA256).Hash
        installDirectory = Get-FslPathProof $Context.InstallDirectory
        programDataRoot = Get-FslPathProof $Context.ProgramDataRoot
        programDataDirectories = @(
            Get-ChildItem -LiteralPath $Context.ProgramDataRoot -Directory -Recurse |
                Sort-Object FullName |
                ForEach-Object {
                    [pscustomobject]@{
                        relativePath = $_.FullName.Substring(
                            $Context.ProgramDataRoot.Length).TrimStart('\')
                        proof = Get-FslPathProof $_.FullName
                    }
                })
        installedFiles = @(
            Get-ChildItem -LiteralPath $Context.InstallDirectory -File |
                Sort-Object Name |
                ForEach-Object {
                    $proof = Get-FslWalFileProof $_.FullName
                    [pscustomobject]@{
                        relativePath = $_.Name
                        length = $proof.length
                        sha256 = $proof.sha256
                        finalPath = $proof.finalPath
                        fileId = $proof.fileId
                        aclSddl = $proof.aclSddl
                    }
                })
    }
}

function Invoke-FslInstall {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$PublisherThumbprint
    )

    Assert-FslMachineGate
    Assert-FslAdministrator
    $state = Read-FslState $Context
    Invoke-FslReconcileInstallWal $Context $state
    if ($state.transition -ceq 'InstallStarted') {
        $wal = @(Read-FslInstallWal $Context)
        $committedInstall = @($wal |
            Group-Object transactionId |
            Where-Object {
                @($_.Group | Where-Object {
                    $_.phase -ceq 'Begin' -and
                    $_.desired.workflow -ceq 'Install'
                }).Count -eq 1 -and
                @($_.Group | Where-Object {
                    $_.phase -ceq 'Committed'
                }).Count -eq 1
            } |
            Select-Object -Last 1)
        if ($committedInstall.Count -eq 1) {
            $begin = @($committedInstall[0].Group | Where-Object {
                $_.phase -ceq 'Begin'
            })[0]
            foreach ($operation in @($begin.desired.plan)) {
                $applied = @($committedInstall[0].Group | Where-Object {
                    $_.ordinal -eq $operation.ordinal -and
                    $_.phase -ceq 'Applied'
                })
                Assert-FslDurableOperationApplied $operation $applied[0]
            }
            $state.Installed = $true
            $state.ServiceCreated = $true
            $state.InstallProof = Get-FslInstallProof $Context $state
            Write-FslState $Context $state 'Installed'
        }
    }
    if ($state.transition -ceq 'Installed') {
        Add-FslCommandEvidence $Context (
            "Install -RunId $($Context.RunId) -Idempotent")
        return
    }
    if ($state.transition -cne 'InstallStarted') {
        Invoke-FslVerifySignature $Context $PublisherThumbprint
        $state = Read-FslState $Context
    }
    Assert-FslTransition $state @('SignatureVerified', 'InstallStarted')
    if ((Test-FslServiceExists) -or
        (Test-Path -LiteralPath $Context.InstallDirectory) -or
        (Test-Path -LiteralPath $Context.ProgramDataRoot)) {
        Stop-FslStage4 $script:ExitCodes.PreExistingConflict (
            'Install targets are not clean.')
    }
    $state.InstallStarted = $true
    Write-FslState $Context $state 'InstallStarted'
    $transactionId = 'Install-' + $Context.RunId + '-' +
        [Guid]::NewGuid().ToString('N')
    $plan = @(New-FslInstallPlan $Context $state $transactionId)
    [void](Start-FslDurableTransaction `
        $Context $transactionId 'Rollback' 'Install' $plan)
    Publish-FslWalBoundary $Context 'transaction' 'AfterBegin'
    Invoke-FslExecuteDurablePlan $Context $transactionId

    [void](Read-FslFrozenReleaseDescriptor `
        $Context.InstallDirectory ([string]$state.ReleaseDescriptorSha256))
    $expectedPublisher = ConvertTo-FslThumbprint `
        $PublisherThumbprint $script:ExitCodes.Signing
    foreach ($installedPe in Get-FslFirstPartyPePaths $Context.InstallDirectory) {
        $signature = Get-AuthenticodeSignature -LiteralPath $installedPe
        if ($signature.Status -ne
                [System.Management.Automation.SignatureStatus]::Valid -or
            $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne
                $expectedPublisher) {
            Stop-FslStage4 $script:ExitCodes.Signing (
                "Installed first-party signature is invalid: " +
                [System.IO.Path]::GetFileName($installedPe))
        }
    }
    $proof = Get-FslInstallProof $Context $state
    Complete-FslDurableTransaction $Context $transactionId
    Publish-FslWalBoundary $Context 'transaction' 'AfterCommit'
    $state.Installed = $true
    $state.ServiceCreated = $true
    $state.InstallProof = $proof
    Write-FslState $Context $state 'Installed'
    Add-FslCommandEvidence $Context "Install -RunId $($Context.RunId)"
}

function Invoke-FslVerify {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$PublisherThumbprint
    )

    Assert-FslMachineGate
    Assert-FslAdministrator
    Invoke-FslVerifySignature $Context $PublisherThumbprint
    $state = Read-FslState $Context
    Assert-FslTransition $state @('SignatureVerified')
    if (-not (Test-FslServiceExists)) {
        Stop-FslStage4 $script:ExitCodes.Service 'The fixed recovery service is missing.'
    }

    $before = Get-FslServiceSnapshot $Context
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'service-config.txt') (
        (($before | ConvertTo-Json -Depth 4) + [Environment]::NewLine))
    $statusBefore = $before
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'service-status-before.txt') (
        (($statusBefore | ConvertTo-Json -Depth 4) + [Environment]::NewLine))
    Invoke-FslCheckedProcess sc.exe @('start', $script:ServiceName) `
        $script:ExitCodes.Service 'sc start' $Context | Out-Null
    $service = Get-Service -Name $script:ServiceName
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    $statusAfter = Get-FslServiceSnapshot $Context
    if ($statusAfter.state -cne 'Running' -or $statusAfter.processId -eq 0) {
        Stop-FslStage4 $script:ExitCodes.Service (
            'SCM did not report the recovery service running as LocalSystem.')
    }
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'service-status-after.txt') (
        (($statusAfter | ConvertTo-Json -Depth 4) + [Environment]::NewLine))

    $environment = @{
        FSL_STAGE4_BROKER_PATH = $Context.BrokerPath
        FSL_STAGE4_INSTALL_DIRECTORY = $Context.InstallDirectory
        FSL_STAGE4_PROGRAMDATA_ROOT = $Context.ProgramDataRoot
        FSL_STAGE4_PUBLISHER_THUMBPRINT = (ConvertTo-FslThumbprint $PublisherThumbprint 5)
    }
    foreach ($entry in $environment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    $buildLog = Invoke-FslCheckedProcess dotnet.exe @(
        'build',
        (Join-Path $Context.RepositoryRoot 'FolderSessionLock.sln'),
        '-c', 'Release',
        '--no-restore',
        "-p:BrokerPublisherThumbprint=$PublisherThumbprint") `
        $script:ExitCodes.ValidationEvidence 'dotnet build Stage4 verification' $Context
    $buildEvidencePath = Join-Path $Context.EvidenceRoot 'build-results.txt'
    $existingBuildLog = [System.IO.File]::ReadAllText($buildEvidencePath)
    Write-FslUtf8NoBom $buildEvidencePath ($existingBuildLog + $buildLog)
    $canonicalTrx = Join-Path $Context.EvidenceRoot 'test-results.trx'
    $coreTests = Join-Path $Context.RepositoryRoot (
        'tests\FolderSessionLock.Core.Tests\bin\Release\net8.0\FolderSessionLock.Core.Tests.dll')
    $appTests = Join-Path $Context.RepositoryRoot (
        'tests\FolderSessionLock.App.Tests\bin\Release\net8.0-windows\FolderSessionLock.App.Tests.dll')
    $windowsTests = Join-Path $Context.RepositoryRoot (
        'tests\FolderSessionLock.Windows.Tests\bin\Release\net8.0-windows\FolderSessionLock.Windows.Tests.dll')
    $testLog = Invoke-FslCheckedProcess dotnet.exe @(
        'vstest', $coreTests, $appTests, $windowsTests,
        '--Logger:trx;LogFileName=test-results.trx',
        "--ResultsDirectory:$($Context.EvidenceRoot)") `
        $script:ExitCodes.ValidationEvidence 'dotnet vstest canonical Stage4' $Context
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'stage4vm-test-results.txt') $testLog
    Assert-FslCanonicalTrx $canonicalTrx | Out-Null
    Write-FslState $Context $state 'Verified'
}

function Assert-FslCanonicalTrx {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'The canonical TRX is missing.'
    }
    [xml]$trx = Get-Content -LiteralPath $Path -Raw
    if ($trx.DocumentElement.LocalName -cne 'TestRun') {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'The canonical TRX root is invalid.'
    }
    $counters = @($trx.SelectNodes('//*[local-name()="Counters"]'))
    $results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    if ($counters.Count -ne 1) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'The canonical TRX must have one counter set.'
    }
    $counter = $counters[0]
    $total = [int]$counter.total
    if ($total -le 0 -or
        [int]$counter.executed -ne $total -or
        [int]$counter.passed -ne $total -or
        [int]$counter.failed -ne 0 -or
        [int]$counter.notExecuted -ne 0 -or
        [int]$counter.error -ne 0 -or
        [int]$counter.timeout -ne 0 -or
        [int]$counter.aborted -ne 0 -or
        $results.Count -ne $total -or
        @($results | Where-Object { $_.outcome -cne 'Passed' }).Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'The canonical TRX does not prove all tests executed and passed.')
    }
    return [pscustomobject]@{
        Total = $total
        Passed = [int]$counter.passed
        Failed = [int]$counter.failed
        Skipped = [int]$counter.notExecuted
    }
}

function Assert-FslContinuationTarget {
    param([Parameter(Mandatory = $true)][string]$TestTarget)

    $full = [System.IO.Path]::GetFullPath($TestTarget)
    $allowedRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'FolderSessionLock.Tests'
    $allowedRoot = [System.IO.Path]::GetFullPath($allowedRoot)
    if (-not $full.StartsWith(
        $allowedRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        Stop-FslStage4 $script:ExitCodes.InvalidArguments 'Restart/logout targets must be below the test TEMP root.'
    }
    $leaf = Split-Path -Leaf $full
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParseExact($leaf, 'D', [ref]$parsed)) {
        Stop-FslStage4 $script:ExitCodes.InvalidArguments 'Restart/logout test targets require a Guid leaf.'
    }
    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'The restart/logout test target is missing.'
    }
    return $full
}

function Invoke-FslPrepareContinuation {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][ValidateSet('Logout', 'Restart')][string]$Kind,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][string]$TestTarget
    )

    Assert-FslMachineGate
    $state = Read-FslState $Context
    Assert-FslTransition $state @('Verified', 'Resumed')
    $target = Assert-FslContinuationTarget $TestTarget
    if ([string]::IsNullOrWhiteSpace($ScenarioId) -or $ScenarioId.Length -gt 64) {
        Stop-FslStage4 $script:ExitCodes.InvalidArguments 'A bounded scenario identifier is required.'
    }
    $state.Continuation = [pscustomobject]@{
        kind = $Kind
        scenarioId = $ScenarioId
        testTarget = $target
        preparedUtc = [DateTime]::UtcNow.ToString('o')
        machineName = [Environment]::MachineName
        gitCommit = (Get-FslGitValue $Context @('rev-parse', 'HEAD'))
        resumed = $false
    }
    Write-FslState $Context $state ($Kind + 'Prepared')
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot "continuation-$ScenarioId.json") (
        ($state.Continuation | ConvertTo-Json -Depth 5) + [Environment]::NewLine)
    Add-FslCommandEvidence $Context "Prepare$Kind -RunId $($Context.RunId) -ScenarioId $ScenarioId"
}

function Invoke-FslResume {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    Assert-FslMachineGate
    Assert-FslRepositoryGate $Context
    $state = Read-FslState $Context
    Assert-FslTransition $state @('LogoutPrepared', 'RestartPrepared')
    if ($null -eq $state.Continuation -or $state.Continuation.resumed) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'No pending continuation exists.'
    }
    if ($state.Continuation.machineName -cne [Environment]::MachineName -or
        $state.Continuation.gitCommit -cne (Get-FslGitValue $Context @('rev-parse', 'HEAD'))) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'Continuation identity changed.'
    }
    [void](Assert-FslContinuationTarget $state.Continuation.testTarget)
    $state.Continuation.resumed = $true
    $state.Continuation.resumedUtc = [DateTime]::UtcNow.ToString('o')
    Write-FslState $Context $state 'Resumed'
    Add-FslCommandEvidence $Context "Resume -RunId $($Context.RunId)"
}

function New-FslUninstallPlan {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][psobject]$State
    )

    $operations = @()
    if (Test-FslServiceExists) {
        if (-not [bool]$State.ServiceCreated) {
            Stop-FslStage4 $script:ExitCodes.Cleanup (
                'The current run did not record creating the service.')
        }
        $snapshot = Get-FslRawServiceSnapshot
        $stopped = $snapshot | Select-Object *
        $stopped.state = 'Stopped'
        $stopped.processId = 0
        $operations += [pscustomobject]@{
            operationId = 'StopService'
            kind = 'ServiceStop'
            target = $script:ServiceName
            desired = [pscustomobject]@{
                brokerPath = $Context.BrokerPath
                expected = $snapshot
            }
        }
        $operations += [pscustomobject]@{
            operationId = 'DeleteService'
            kind = 'ServiceDelete'
            target = $script:ServiceName
            desired = [pscustomobject]@{
                brokerPath = $Context.BrokerPath
                expected = $stopped
            }
        }
    }
    $proof = $State.InstallProof
    if (Test-Path -LiteralPath $Context.InstallDirectory) {
        Assert-FslPathProof `
            $Context.InstallDirectory $proof.installDirectory
        $actual = @(Get-ChildItem -LiteralPath $Context.InstallDirectory -File |
            Sort-Object Name)
        $expected = @($proof.installedFiles | Sort-Object relativePath)
        if ($actual.Count -ne $expected.Count -or
            @(Get-ChildItem -LiteralPath $Context.InstallDirectory -Directory).
                Count -ne 0) {
            Stop-FslStage4 $script:ExitCodes.Cleanup (
                'Install delete plan found an unknown leaf set.')
        }
        for ($index = 0; $index -lt $expected.Count; $index++) {
            if ($actual[$index].Name -cne $expected[$index].relativePath) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Install delete plan found a case or name mismatch.')
            }
            Assert-FslPathProof $actual[$index].FullName $expected[$index]
            if (-not (Test-FslWalFileMatches `
                $actual[$index].FullName $expected[$index])) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'Install delete plan found a replaced file.')
            }
            $operations += [pscustomobject]@{
                operationId = "DeleteInstallFile:$($actual[$index].Name)"
                kind = 'DeleteFile'
                target = $actual[$index].FullName
                desired = [pscustomobject]@{
                    expected = $expected[$index]
                }
            }
        }
        $operations += [pscustomobject]@{
            operationId = 'DeleteInstallDirectory'
            kind = 'DeleteDirectory'
            target = $Context.InstallDirectory
            desired = [pscustomobject]@{
                expected = $proof.installDirectory
            }
        }
    }
    if (Test-Path -LiteralPath $Context.ProgramDataRoot) {
        Assert-FslPathProof $Context.ProgramDataRoot $proof.programDataRoot
        $expectedDirectories = @($proof.programDataDirectories)
        $actualDirectories = @(
            Get-ChildItem -LiteralPath $Context.ProgramDataRoot -Directory -Recurse)
        if ($actualDirectories.Count -ne $expectedDirectories.Count) {
            Stop-FslStage4 $script:ExitCodes.Cleanup (
                'ProgramData delete plan found an unknown directory.')
        }
        $logPattern =
            '^[0-9]{8}T[0-9]{13}Z-(0|[1-9][0-9]{0,9})-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}-[0-9]{4}\.jsonl$'
        foreach ($file in Get-ChildItem `
            -LiteralPath $Context.ProgramDataRoot -File -Recurse) {
            $relative = $file.FullName.Substring(
                $Context.ProgramDataRoot.Length).TrimStart('\')
            if ($file.Name -cnotmatch $logPattern -or
                -not (Test-FslExactSystemAdminServiceAcl $file.FullName)) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    "ProgramData delete plan refused an unknown leaf: $relative.")
            }
            $operations += [pscustomobject]@{
                operationId = "DeleteProgramDataFile:$relative"
                kind = 'DeleteFile'
                target = $file.FullName
                desired = [pscustomobject]@{
                    expected = Get-FslWalFileProof $file.FullName
                }
            }
        }
        foreach ($directory in $actualDirectories |
            Sort-Object { $_.FullName.Length } -Descending) {
            $relative = $directory.FullName.Substring(
                $Context.ProgramDataRoot.Length).TrimStart('\')
            $expected = @($expectedDirectories | Where-Object {
                $_.relativePath -ceq $relative
            })
            if ($expected.Count -ne 1) {
                Stop-FslStage4 $script:ExitCodes.Cleanup (
                    'ProgramData delete plan has no exact directory proof.')
            }
            Assert-FslPathProof $directory.FullName $expected[0].proof
            $operations += [pscustomobject]@{
                operationId = "DeleteProgramDataDirectory:$relative"
                kind = 'DeleteDirectory'
                target = $directory.FullName
                desired = [pscustomobject]@{
                    expected = $expected[0].proof
                }
            }
        }
        $operations += [pscustomobject]@{
            operationId = 'DeleteProgramDataRoot'
            kind = 'DeleteDirectory'
            target = $Context.ProgramDataRoot
            desired = [pscustomobject]@{
                expected = $proof.programDataRoot
            }
        }
    }
    return @(New-FslDurablePlan $operations)
}

function Invoke-FslUninstall {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    Assert-FslMachineGate
    Assert-FslAdministrator
    $state = Read-FslState $Context
    Invoke-FslReconcileInstallWal $Context $state
    $committed = @(Read-FslInstallWal $Context |
        Group-Object transactionId |
        Where-Object {
            @($_.Group | Where-Object {
                $_.phase -ceq 'Begin' -and
                $_.desired.workflow -ceq 'Uninstall'
            }).Count -eq 1 -and
            @($_.Group | Where-Object {
                $_.phase -ceq 'Committed'
            }).Count -eq 1
        } |
        Select-Object -Last 1)
    if ($committed.Count -eq 1 -and
        $state.transition -cne 'Uninstalled' -and
        $state.transition -cne 'CleanupCompleted') {
        if ((Test-FslServiceExists) -or
            (Test-Path -LiteralPath $Context.InstallDirectory) -or
            (Test-Path -LiteralPath $Context.ProgramDataRoot)) {
            Stop-FslStage4 $script:ExitCodes.Cleanup (
                'Committed uninstall has residual product state.')
        }
        $state.Installed = $false
        $state.InstallStarted = $false
        $state.ServiceCreated = $false
        Write-FslState $Context $state 'Uninstalled'
    }
    Assert-FslTransition $state @(
        'PreflightCaptured', 'CertificateCreating', 'CertificateReady',
        'CertificateRolledBack', 'PublishCompleted', 'SignatureVerified',
        'InstallStarted', 'ServiceCreated', 'Installed', 'Verified',
        'LogoutPrepared', 'RestartPrepared', 'Resumed', 'Uninstalled',
        'CleanupCompleted')
    if ($state.transition -cin @('Uninstalled', 'CleanupCompleted')) {
        if ((Test-FslServiceExists) -or
            (Test-Path -LiteralPath $Context.InstallDirectory) -or
            (Test-Path -LiteralPath $Context.ProgramDataRoot)) {
            Stop-FslStage4 $script:ExitCodes.Cleanup (
                'An idempotent uninstall found residual product state.')
        }
        return
    }
    $prestate = [System.IO.File]::ReadAllText(
        $Context.PrestatePath) | ConvertFrom-Json
    if ($prestate.serviceExisted -or
        $prestate.installDirectoryExisted -or
        $prestate.programDataRootExisted) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'Pre-existing product state makes automatic uninstall unsafe.')
    }
    $transactionId = 'Uninstall-' + $Context.RunId + '-' +
        [Guid]::NewGuid().ToString('N')
    $plan = @(New-FslUninstallPlan $Context $state)
    [void](Start-FslDurableTransaction `
        $Context $transactionId 'Forward' 'Uninstall' $plan)
    Publish-FslWalBoundary $Context 'transaction' 'AfterBegin'
    Invoke-FslExecuteDurablePlan $Context $transactionId
    Complete-FslDurableTransaction $Context $transactionId
    Publish-FslWalBoundary $Context 'transaction' 'AfterCommit'
    $state.Installed = $false
    $state.InstallStarted = $false
    $state.ServiceCreated = $false
    Write-FslState $Context $state 'Uninstalled'
    Add-FslCommandEvidence $Context "Uninstall -RunId $($Context.RunId)"
}

function Invoke-FslCleanup {
    param([Parameter(Mandatory = $true)][psobject]$Context)

    Assert-FslMachineGate
    Assert-FslAdministrator
    $existingState = Read-FslState $Context
    Invoke-FslReconcileInstallWal $Context $existingState
    if ($existingState.transition -ceq 'CleanupCompleted') {
        if ((Test-FslServiceExists) -or
            (Test-Path -LiteralPath $Context.InstallDirectory) -or
            (Test-Path -LiteralPath $Context.ProgramDataRoot)) {
            Stop-FslStage4 $script:ExitCodes.Cleanup (
                'Repeated cleanup found replacement or residual product state.')
        }
        return
    }
    Invoke-FslUninstall $Context
    $state = Read-FslState $Context
    Assert-FslTransition $state @('Uninstalled')
    $subject = "CN=$($script:TestCertificatePrefix) [$($Context.RunId)]"
    if (-not (Test-FslJournalTransition $Context 'CertificateCreating') -and
        -not [string]::IsNullOrWhiteSpace([string]$state.CreatedCertificateThumbprint)) {
        Stop-FslStage4 $script:ExitCodes.Cleanup 'Certificate state has no creation journal.'
    }
    $operations = @()
    foreach ($store in @(
        'Cert:\LocalMachine\TrustedPeople',
        'Cert:\LocalMachine\My')) {
        $certificates = @(Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -ceq $subject })
        foreach ($item in $certificates) {
            if (-not (Test-FslJournalTransition $Context 'CertificateCreating')) {
                Stop-FslStage4 $script:ExitCodes.Cleanup 'Certificate identity is not owned by this run.'
            }
            $operations += [pscustomobject]@{
                operationId =
                    "DeleteCertificate:${store}:$($item.Thumbprint)"
                kind = 'CertificateDelete'
                target = "$store\$($item.Thumbprint)"
                desired = [pscustomobject]@{
                    store = $store
                    thumbprint = $item.Thumbprint
                    subject = $subject
                }
            }
        }
    }
    $transactionId = $null
    if ($operations.Count -gt 0) {
        $transactionId = 'Cleanup-' + $Context.RunId + '-' +
            [Guid]::NewGuid().ToString('N')
        $plan = @(New-FslDurablePlan $operations)
        [void](Start-FslDurableTransaction `
            $Context $transactionId 'Forward' 'Cleanup' $plan)
        Publish-FslWalBoundary $Context 'transaction' 'AfterBegin'
        Invoke-FslExecuteDurablePlan $Context $transactionId
        Complete-FslDurableTransaction $Context $transactionId
        Publish-FslWalBoundary $Context 'transaction' 'AfterCommit'
    }
    $certificateResidual = @(
        Get-ChildItem Cert:\LocalMachine\My,Cert:\LocalMachine\TrustedPeople `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -ceq $subject })
    if ($certificateResidual.Count -ne 0) {
        Stop-FslStage4 $script:ExitCodes.Cleanup 'Certificate cleanup is incomplete.'
    }
    $leftovers = @()
    if (Test-FslServiceExists) { $leftovers += 'service' }
    if (Test-Path -LiteralPath $Context.InstallDirectory) { $leftovers += 'install' }
    if (Test-Path -LiteralPath $Context.ProgramDataRoot) { $leftovers += 'programData' }
    if (Get-Process | Where-Object { $_.ProcessName -like 'FolderSessionLock*' }) {
        $leftovers += 'process'
    }
    if ($leftovers.Count -gt 0) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'Cleanup is incomplete: ' + ($leftovers -join ', '))
    }
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'cleanup-results.txt') (
        "RunId=$($Context.RunId)`r`n" +
        "ServiceRemaining=0`r`nInstallDirectoryRemaining=0`r`n" +
        "ProgramDataRootRemaining=0`r`nProductProcessesRemaining=0`r`n")
    Write-FslState $Context $state 'CleanupCompleted'
    Add-FslCommandEvidence $Context "Cleanup -RunId $($Context.RunId)"
}

function Read-FslReviewerVerdict {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Reviewer verdict evidence is missing.')
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 4 -or $bytes.Length -gt 16) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Reviewer verdict must contain exactly one bounded verdict token.')
    }
    try {
        $encoding = [System.Text.UTF8Encoding]::new($false, $true)
        $text = $encoding.GetString($bytes)
    }
    catch {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Reviewer verdict is not strict UTF-8.')
    }
    $normalized = $text.Replace("`r`n", "`n")
    if ($normalized.EndsWith("`n", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 1)
    }
    if ($normalized -cnotin @('PASS', 'FAIL')) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Reviewer verdict must be exactly one uppercase PASS or FAIL token.')
    }
    return $normalized
}

function Invoke-FslFinalizeEvidence {
    param(
        [Parameter(Mandatory = $true)][psobject]$Context,
        [Parameter(Mandatory = $true)][string]$ReviewerVerdictPath
    )

    $state = Read-FslState $Context
    Assert-FslTransition $state @('CleanupCompleted')
    $required = @(
        'commands.txt',
        'build-results.txt',
        'test-results.trx',
        'service-config.txt',
        'service-status-before.txt',
        'service-status-after.txt',
        'signature-verification.txt',
        'acl-before.txt',
        'acl-locked.txt',
        'acl-after-recovery.txt',
        'recovery-record-transitions.txt',
        'access-probe-results.json',
        'application-events.txt',
        'cleanup-results.txt',
        'scenario-results.json')
    foreach ($name in $required) {
        $path = Join-Path $Context.EvidenceRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -eq 0) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence "Required evidence is missing or empty: $name."
        }
    }
    $verdict = [System.IO.Path]::GetFullPath($ReviewerVerdictPath)
    $reviewerVerdict = Read-FslReviewerVerdict $verdict
    Copy-Item -LiteralPath $verdict -Destination (
        Join-Path $Context.EvidenceRoot 'reviewer-verdict.md') -Force

    $testResult = Assert-FslCanonicalTrx (
        Join-Path $Context.EvidenceRoot 'test-results.trx')
    $prestate = [System.IO.File]::ReadAllText($Context.PrestatePath) | ConvertFrom-Json
    $scenarioResults = [System.IO.File]::ReadAllText(
        (Join-Path $Context.EvidenceRoot 'scenario-results.json')) | ConvertFrom-Json
    if ($scenarioResults.schemaVersion -ne 1 -or
        $scenarioResults.runId -cne $Context.RunId -or
        $null -eq $scenarioResults.scenarios -or
        @($scenarioResults.scenarios).Count -eq 0) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'Scenario evidence schema or RunId is invalid.'
    }
    foreach ($scenario in @($scenarioResults.scenarios)) {
        if ($scenario.result -notin @('PASS', 'FAIL', 'BLOCKED') -or
            [string]::IsNullOrWhiteSpace([string]$scenario.scenarioId) -or
            [string]::IsNullOrWhiteSpace([string]$scenario.description) -or
            [string]::IsNullOrWhiteSpace([string]$scenario.expectedResult) -or
            [string]::IsNullOrWhiteSpace([string]$scenario.actualResult) -or
            @($scenario.evidenceFiles).Count -eq 0) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'A scenario result is incomplete.'
        }
        foreach ($evidenceFile in @($scenario.evidenceFiles)) {
            $candidate = [System.IO.Path]::GetFullPath(
                (Join-Path $Context.EvidenceRoot ([string]$evidenceFile)))
            if (-not $candidate.StartsWith(
                $Context.EvidenceRoot + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    "Scenario evidence file is unavailable: $evidenceFile.")
            }
        }
    }
    $buildText = [System.IO.File]::ReadAllText(
        (Join-Path $Context.EvidenceRoot 'build-results.txt'))
    $signatureText = [System.IO.File]::ReadAllText(
        (Join-Path $Context.EvidenceRoot 'signature-verification.txt'))
    $cleanupText = [System.IO.File]::ReadAllText(
        (Join-Path $Context.EvidenceRoot 'cleanup-results.txt'))
    $buildPassed = ([regex]::Matches($buildText, '(?m)^ExitCode=0\r?$').Count -ge 2)
    $signaturePassed = (
        $signatureText -match '(?m)^Status=Valid\r?$' -and
        $signatureText -notmatch '(?m)^Status=(?!Valid\r?$)')
    $cleanupPassed = @(
        'ServiceRemaining=0',
        'InstallDirectoryRemaining=0',
        'ProgramDataRootRemaining=0',
        'ProductProcessesRemaining=0') |
        ForEach-Object { $cleanupText.IndexOf($_, [StringComparison]::Ordinal) -ge 0 }
    if (-not $buildPassed -or -not $signaturePassed -or
        ($cleanupPassed -contains $false) -or
        (@($scenarioResults.scenarios).result -contains 'FAIL') -or
        (@($scenarioResults.scenarios).result -contains 'BLOCKED')) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'Evidence does not support a completed Stage 4 run.'
    }
    $booleanFields = @(
        'crossAccountElevationRejected',
        'preLoginRecoveryPassed',
        'aclRestored',
        'temporaryDirectoriesRemoved',
        'recoveryRecordsRemoved')
    foreach ($field in $booleanFields) {
        if ($scenarioResults.$field -isnot [bool]) {
            Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                "Scenario evidence field must be boolean: $field.")
        }
    }
    if ($null -eq $scenarioResults.remainingRisks) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
            'Scenario evidence must explicitly provide remainingRisks.')
    }
    $manifest = [ordered]@{
        evidenceSchemaVersion = 1
        runId = $Context.RunId
        stage = 4
        gitCommit = $prestate.gitCommit
        machineName = $script:ExpectedMachine
        osVersion = "$($prestate.osVersion) build $($prestate.osBuildNumber)"
        startedUtc = $prestate.capturedUtc
        completedUtc = [DateTime]::UtcNow.ToString('o')
        executor = 'human-and-codex'
        serviceName = $script:ServiceName
        scenarios = @($scenarioResults.scenarios)
        buildPassed = $buildPassed
        testsPassed = ($testResult.Total -gt 0 -and $testResult.Passed -eq $testResult.Total)
        signaturePassed = $signaturePassed
        crossAccountElevationRejected = [bool]$scenarioResults.crossAccountElevationRejected
        preLoginRecoveryPassed = [bool]$scenarioResults.preLoginRecoveryPassed
        aclRestored = [bool]$scenarioResults.aclRestored
        temporaryDirectoriesRemoved = [bool]$scenarioResults.temporaryDirectoriesRemoved
        recoveryRecordsRemoved = [bool]$scenarioResults.recoveryRecordsRemoved
        reviewerVerdict = $reviewerVerdict
        remainingRisks = @($scenarioResults.remainingRisks)
    }
    if ($reviewerVerdict -cne 'PASS' -or
        -not $manifest.crossAccountElevationRejected -or
        -not $manifest.preLoginRecoveryPassed -or
        -not $manifest.aclRestored -or
        -not $manifest.temporaryDirectoriesRemoved -or
        -not $manifest.recoveryRecordsRemoved) {
        Stop-FslStage4 $script:ExitCodes.ValidationEvidence 'Reviewer or scenario evidence is not PASS.'
    }
    Write-FslUtf8NoBom (Join-Path $Context.EvidenceRoot 'manifest.json') (
        ($manifest | ConvertTo-Json -Depth 10) + [Environment]::NewLine)
    Write-FslState $Context $state 'EvidenceFinalized'
    Add-FslCommandEvidence $Context "FinalizeEvidence -RunId $($Context.RunId)"
    Remove-FslExternalAnchor $Context
    if (Test-Path -LiteralPath $Context.ExternalAnchorRoot) {
        Stop-FslStage4 $script:ExitCodes.Cleanup (
            'Protected external anchor retirement is incomplete.')
    }
}

function Invoke-FslStage4Command {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet(
            'Preflight',
            'CreateTestCertificate',
            'Publish',
            'VerifySignature',
            'Install',
            'Verify',
            'PrepareLogout',
            'PrepareRestart',
            'Resume',
            'Uninstall',
            'Cleanup',
            'FinalizeEvidence')]
        [string]$Command,
        [Parameter(Mandatory = $true)][string]$RunId,
        [string]$PublisherThumbprint,
        [string]$SigningCertificateThumbprint,
        [string]$ReleaseRoot,
        [string]$ScenarioId,
        [string]$TestTarget,
        [string]$ReviewerVerdictPath
    )

    try {
        $context = Get-FslContext $RunId $ReleaseRoot
        if ($Command -cne 'Preflight') {
            Assert-FslRepositoryGate $context
            Assert-FslRepositoryMutationGate $context
        }
        if ($Command -cnotin @('Preflight', 'CreateTestCertificate')) {
            $readinessState = Read-FslState $context
            if ($readinessState.PlatformReadinessStatus -cne 'Verified' -or
                -not $readinessState.SecureBootVerified -or
                -not $readinessState.TpmNativeVerified -or
                -not $readinessState.TpmCmdletVerified -or
                $null -eq $readinessState.PlatformReadinessVerifiedUtc) {
                Stop-FslStage4 $script:ExitCodes.ValidationEvidence (
                    'Platform readiness is deferred until elevation.')
            }
        }
        switch ($Command) {
            'Preflight' { Invoke-FslPreflight $context }
            'CreateTestCertificate' { Invoke-FslCreateTestCertificate $context }
            'Publish' {
                if ([string]::IsNullOrWhiteSpace($PublisherThumbprint)) {
                    Stop-FslStage4 2 'Publish requires PublisherThumbprint.'
                }
                Invoke-FslPublish $context $PublisherThumbprint $SigningCertificateThumbprint
            }
            'VerifySignature' {
                if ([string]::IsNullOrWhiteSpace($PublisherThumbprint)) {
                    Stop-FslStage4 2 'VerifySignature requires PublisherThumbprint.'
                }
                Invoke-FslVerifySignature $context $PublisherThumbprint
            }
            'Install' {
                if ([string]::IsNullOrWhiteSpace($PublisherThumbprint)) {
                    Stop-FslStage4 2 'Install requires PublisherThumbprint.'
                }
                Invoke-FslInstall $context $PublisherThumbprint
            }
            'Verify' {
                if ([string]::IsNullOrWhiteSpace($PublisherThumbprint)) {
                    Stop-FslStage4 2 'Verify requires PublisherThumbprint.'
                }
                Invoke-FslVerify $context $PublisherThumbprint
            }
            'PrepareLogout' {
                if ([string]::IsNullOrWhiteSpace($ScenarioId) -or
                    [string]::IsNullOrWhiteSpace($TestTarget)) {
                    Stop-FslStage4 2 'PrepareLogout requires ScenarioId and TestTarget.'
                }
                Invoke-FslPrepareContinuation $context 'Logout' $ScenarioId $TestTarget
            }
            'PrepareRestart' {
                if ([string]::IsNullOrWhiteSpace($ScenarioId) -or
                    [string]::IsNullOrWhiteSpace($TestTarget)) {
                    Stop-FslStage4 2 'PrepareRestart requires ScenarioId and TestTarget.'
                }
                Invoke-FslPrepareContinuation $context 'Restart' $ScenarioId $TestTarget
            }
            'Resume' { Invoke-FslResume $context }
            'Uninstall' { Invoke-FslUninstall $context }
            'Cleanup' { Invoke-FslCleanup $context }
            'FinalizeEvidence' {
                if ([string]::IsNullOrWhiteSpace($ReviewerVerdictPath)) {
                    Stop-FslStage4 2 'FinalizeEvidence requires ReviewerVerdictPath.'
                }
                Invoke-FslFinalizeEvidence $context $ReviewerVerdictPath
            }
        }
        return $script:ExitCodes.Success
    }
    catch {
        $exitCode = $_.Exception.Data['FslStage4ExitCode']
        if ($null -eq $exitCode) {
            $exitCode = $script:ExitCodes.ValidationEvidence
        }
        [Console]::Error.WriteLine($_.Exception.Message)
        return [int]$exitCode
    }
}

Export-ModuleMember -Function Invoke-FslStage4Command
