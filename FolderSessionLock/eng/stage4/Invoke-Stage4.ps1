[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
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

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [string]$PublisherThumbprint,
    [string]$SigningCertificateThumbprint,
    [string]$ReleaseRoot,
    [string]$ScenarioId,
    [string]$TestTarget,
    [string]$ReviewerVerdictPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'FolderSessionLock.Stage4.psm1') -Force

$arguments = @{
    Command = $Command
    RunId = $RunId
}

foreach ($name in @(
    'PublisherThumbprint',
    'SigningCertificateThumbprint',
    'ReleaseRoot',
    'ScenarioId',
    'TestTarget',
    'ReviewerVerdictPath')) {
    $value = Get-Variable -Name $name -ValueOnly
    if (-not [string]::IsNullOrWhiteSpace($value)) {
        $arguments[$name] = $value
    }
}

exit (Invoke-FslStage4Command @arguments)
