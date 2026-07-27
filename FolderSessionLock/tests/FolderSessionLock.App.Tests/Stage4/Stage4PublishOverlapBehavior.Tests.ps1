$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-Publish {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [string[]]$AdditionalArguments = @()
    )

    $arguments = @(
        'publish',
        $Project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'false',
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false') +
        $AdditionalArguments +
        @('-o', $OutputDirectory)
    $output = & dotnet.exe $arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for ${Project}:`r`n$output"
    }
}

$repository = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\..'))
$testParent = Join-Path $env:TEMP 'FolderSessionLock.Tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('D'))
$appOutput = Join-Path $testRoot 'app'
$brokerOutput = Join-Path $testRoot 'broker'

try {
    [System.IO.Directory]::CreateDirectory($appOutput) | Out-Null
    [System.IO.Directory]::CreateDirectory($brokerOutput) | Out-Null

    Invoke-Publish `
        -Project (
            Join-Path $repository `
                'src\FolderSessionLock.App\FolderSessionLock.App.csproj') `
        -OutputDirectory $appOutput `
        -AdditionalArguments @('-p:BrokerPublisherThumbprint=')
    Invoke-Publish `
        -Project (
            Join-Path $repository `
                'src\FolderSessionLock.Broker\FolderSessionLock.Broker.csproj') `
        -OutputDirectory $brokerOutput

    $appFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $appOutput -File) {
        Assert-True (-not $appFiles.ContainsKey($file.Name)) (
            "The App publish produced a duplicate filename: $($file.Name)")
        $appFiles.Add($file.Name, $file.FullName)
    }
    $brokerFiles = @{}
    foreach ($file in Get-ChildItem -LiteralPath $brokerOutput -File) {
        Assert-True (-not $brokerFiles.ContainsKey($file.Name)) (
            "The Broker publish produced a duplicate filename: $($file.Name)")
        $brokerFiles.Add($file.Name, $file.FullName)
    }

    $overlaps = @(
        $appFiles.Keys |
            Where-Object { $brokerFiles.ContainsKey($_) } |
            Sort-Object)
    Assert-True ($overlaps.Count -gt 0) (
        'The App and Broker publishes did not contain overlapping filenames.')

    $overlapHashes = @{}
    foreach ($name in $overlaps) {
        $appHash = (
            Get-FileHash -LiteralPath $appFiles[$name] -Algorithm SHA256).Hash
        $brokerHash = (
            Get-FileHash -LiteralPath $brokerFiles[$name] -Algorithm SHA256).Hash
        Assert-True ($appHash -ceq $brokerHash) (
            "Publish collision has different content: $name")
        $overlapHashes.Add($name, $appHash)
    }

    foreach ($requiredOverlap in @(
            'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
            'Microsoft.Extensions.Logging.Abstractions.dll')) {
        Assert-True ($overlapHashes.ContainsKey($requiredOverlap)) (
            "Required publish overlap was absent: $requiredOverlap")
        Assert-True (
            (Get-FileHash `
                -LiteralPath $appFiles[$requiredOverlap] `
                -Algorithm SHA256).Hash -ceq
            (Get-FileHash `
                -LiteralPath $brokerFiles[$requiredOverlap] `
                -Algorithm SHA256).Hash) (
            "Required publish overlap did not match: $requiredOverlap")
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        [System.IO.Directory]::Delete($testRoot, $true)
    }
}

Write-Output 'STAGE4_PUBLISH_OVERLAP_BEHAVIOR_PASS'
