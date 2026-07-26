$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Rejected {
    param([scriptblock]$Action, [string]$Message)
    $rejected = $false
    try {
        & $Action
    }
    catch {
        $rejected = $true
    }
    Assert-True $rejected $Message
}

$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$module = Import-Module (
    Join-Path $repository 'eng\stage4\FolderSessionLock.Stage4.psm1') `
    -Force `
    -PassThru
$outerRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'FolderSessionLock.Stage4.RepositoryIntegrity.' +
    [Guid]::NewGuid().ToString('D'))
[System.IO.Directory]::CreateDirectory($outerRoot) | Out-Null

try {
    & $module {
        param($repository, $outerRoot)

        function New-TestRelease {
            param([string]$Root)
            [System.IO.Directory]::CreateDirectory($Root) | Out-Null
            Write-FslUtf8NoBom (Join-Path $Root 'alpha.bin') 'alpha'
            Write-FslUtf8NoBom (Join-Path $Root 'Beta.dll') 'beta'
            $payload = @()
            foreach ($name in @('alpha.bin', 'Beta.dll')) {
                $path = Join-Path $Root $name
                $payload += [pscustomobject]@{
                    relativePath = $name
                    length = (Get-Item -LiteralPath $path).Length
                    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                }
            }
            $manifestPath = Join-Path $Root 'release-manifest.json'
            Write-FslUtf8NoBom $manifestPath (
                ([ordered]@{
                    schemaVersion = 1
                    files = $payload
                } | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
            $sumLines = @()
            foreach ($file in $payload) {
                $sumLines += "$($file.sha256)  $($file.relativePath)"
            }
            $manifestHash = (
                Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
            $sumLines += "$manifestHash  release-manifest.json"
            $sumsPath = Join-Path $Root 'SHA256SUMS.txt'
            Write-FslUtf8NoBom $sumsPath (
                ($sumLines -join [Environment]::NewLine) +
                [Environment]::NewLine)
            $descriptor = [ordered]@{
                schemaVersion = 1
                gitCommit = '0' * 40
                manifestSha256 = $manifestHash
                sumsSha256 = (
                    Get-FileHash -LiteralPath $sumsPath -Algorithm SHA256).Hash
                exactReleaseFiles = @(
                    'Beta.dll',
                    'SHA256SUMS.txt',
                    'alpha.bin',
                    'release-descriptor.json',
                    'release-manifest.json')
            }
            $descriptorPath = Join-Path $Root 'release-descriptor.json'
            Write-FslUtf8NoBom $descriptorPath (
                ($descriptor | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
            return (Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash
        }

        function New-ReleaseCase {
            param([string]$Name)
            $root = Join-Path $outerRoot $Name
            $hash = New-TestRelease $root
            return [pscustomobject]@{ Root = $root; Hash = $hash }
        }

        $release = New-ReleaseCase 'release-pass'
        [void](Read-FslFrozenReleaseDescriptor $release.Root $release.Hash)

        foreach ($missing in @('release-manifest.json', 'SHA256SUMS.txt')) {
            $case = New-ReleaseCase ('missing-' + $missing.Replace('.', '-'))
            [System.IO.File]::Delete((Join-Path $case.Root $missing))
            Assert-Rejected {
                [void](Read-FslFrozenReleaseDescriptor $case.Root $case.Hash)
            } "A missing $missing was accepted."
        }

        $case = New-ReleaseCase 'extra'
        Write-FslUtf8NoBom (Join-Path $case.Root 'extra.bin') 'extra'
        Assert-Rejected {
            [void](Read-FslFrozenReleaseDescriptor $case.Root $case.Hash)
        } 'An extra release file was accepted.'

        foreach ($variant in @('duplicate', 'case-alias', 'hash-mismatch')) {
            $case = New-ReleaseCase ('manifest-' + $variant)
            $manifestPath = Join-Path $case.Root 'release-manifest.json'
            $manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
            if ($variant -ceq 'duplicate') {
                $manifest.files = @($manifest.files) + @($manifest.files[0])
            }
            elseif ($variant -ceq 'case-alias') {
                $alias = $manifest.files[0] | Select-Object *
                $alias.relativePath = $alias.relativePath.ToUpperInvariant()
                $manifest.files = @($manifest.files) + $alias
            }
            else {
                $manifest.files[0].sha256 = 'F' * 64
            }
            Write-FslUtf8NoBom $manifestPath (
                ($manifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine)
            Assert-Rejected {
                [void](Read-FslFrozenReleaseDescriptor $case.Root $case.Hash)
            } "A manifest $variant was accepted."
        }

        $case = New-ReleaseCase 'sums-duplicate'
        $sumsPath = Join-Path $case.Root 'SHA256SUMS.txt'
        [System.IO.File]::AppendAllText(
            $sumsPath,
            [System.IO.File]::ReadAllLines($sumsPath)[0] +
            [Environment]::NewLine)
        Assert-Rejected {
            [void](Read-FslFrozenReleaseDescriptor $case.Root $case.Hash)
        } 'A duplicate SHA256SUMS entry was accepted.'

        $case = New-ReleaseCase 'frozen-change'
        $descriptorBefore = [System.IO.File]::ReadAllBytes(
            (Join-Path $case.Root 'release-descriptor.json'))
        Write-FslUtf8NoBom (Join-Path $case.Root 'alpha.bin') 'changed'
        Assert-Rejected {
            [void](Read-FslFrozenReleaseDescriptor $case.Root $case.Hash)
        } 'A frozen payload change was accepted.'
        $descriptorAfter = [System.IO.File]::ReadAllBytes(
            (Join-Path $case.Root 'release-descriptor.json'))
        Assert-True (
            (Get-FslSha256 $descriptorBefore) -ceq
            (Get-FslSha256 $descriptorAfter)) (
            'Validation rewrote and re-recognized a frozen descriptor.')

        $case = New-ReleaseCase 'copy-pass'
        $destination = Join-Path $outerRoot 'copy-pass-destination'
        [System.IO.Directory]::CreateDirectory($destination) | Out-Null
        [void](Copy-FslFrozenRelease $case.Root $destination $case.Hash)

        $case = New-ReleaseCase 'copy-before-tamper'
        $destination = Join-Path $outerRoot 'copy-before-tamper-destination'
        [System.IO.Directory]::CreateDirectory($destination) | Out-Null
        $changed = $false
        Assert-Rejected {
            [void](Copy-FslFrozenRelease `
                $case.Root `
                $destination `
                $case.Hash `
                $null `
                {
                    param($name)
                    if (-not $changed) {
                        $script:changed = $true
                        Write-FslUtf8NoBom (Join-Path $case.Root $name) 'tampered'
                    }
                })
        } 'A source tamper immediately before copy was accepted.'
        Assert-True (
            @(Get-ChildItem -LiteralPath $destination -File).Count -eq 0) (
            'A failed source-tamper copy left destination files.')

        $case = New-ReleaseCase 'copy-interrupt'
        $destination = Join-Path $outerRoot 'copy-interrupt-destination'
        [System.IO.Directory]::CreateDirectory($destination) | Out-Null
        Assert-Rejected {
            [void](Copy-FslFrozenRelease `
                $case.Root `
                $destination `
                $case.Hash `
                $null `
                { throw 'injected before-copy interruption' })
        } 'A before-copy interruption was swallowed.'
        Assert-True (
            @(Get-ChildItem -LiteralPath $destination -File).Count -eq 0) (
            'A before-copy interruption left destination files.')

        $case = New-ReleaseCase 'copy-destination-tamper'
        $destination = Join-Path $outerRoot 'copy-destination-tamper-destination'
        [System.IO.Directory]::CreateDirectory($destination) | Out-Null
        $destinationChanged = $false
        Assert-Rejected {
            [void](Copy-FslFrozenRelease `
                $case.Root `
                $destination `
                $case.Hash `
                $null `
                $null `
                {
                    param($name)
                    if (-not $destinationChanged) {
                        $script:destinationChanged = $true
                        Write-FslUtf8NoBom (Join-Path $destination $name) 'tampered'
                    }
                })
        } 'A destination tamper immediately after copy was accepted.'

        function New-StateContext {
            param([string]$Name)
            $root = Join-Path $outerRoot $Name
            [System.IO.Directory]::CreateDirectory($root) | Out-Null
            $context = [pscustomobject]@{
                RunId = '20260725T120000Z-0123abcd'
                RepositoryRoot = $repository
                EvidenceRoot = $root
                PrestatePath = (Join-Path $root 'prestate.json')
                StatePath = (Join-Path $root 'stage4-state.json')
                JournalPath = (Join-Path $root 'stage4-journal.jsonl')
                AnchorPath = (Join-Path $root 'stage4-anchor.json')
                InstallWalPath = (Join-Path $root 'install-wal.jsonl')
                ExternalAnchorRoot = (Join-Path $root 'external-anchor')
                ExternalAnchorKeyPath = (
                    Join-Path $root 'external-anchor\key.dpapi')
                ExternalAnchorSlot0Path = (
                    Join-Path $root 'external-anchor\anchor-0.json')
                ExternalAnchorSlot1Path = (
                    Join-Path $root 'external-anchor\anchor-1.json')
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
            return $context
        }

        $context = New-StateContext 'state-cache-missing'
        [System.IO.File]::Delete($context.StatePath)
        Assert-True (
            (Read-FslState $context).transition -ceq 'PreflightCaptured') (
            'A missing state cache was not rebuilt from the anchored journal.')
        $context = New-StateContext 'state-cache-torn'
        Write-FslUtf8NoBom $context.StatePath '{'
        Assert-True (
            (Read-FslState $context).transition -ceq 'PreflightCaptured') (
            'A torn state cache was not rebuilt from the anchored journal.')

        $context = New-StateContext 'journal-truncated'
        $bytes = [System.IO.File]::ReadAllBytes($context.JournalPath)
        [System.IO.File]::WriteAllBytes(
            $context.JournalPath,
            $bytes[0..($bytes.Length - 2)])
        Assert-Rejected {
            [void](Read-FslState $context)
        } 'An anchored journal truncation was accepted.'

        $context = New-StateContext 'anchor-mismatch'
        $anchor = [System.IO.File]::ReadAllText($context.AnchorPath) | ConvertFrom-Json
        $anchor.entrySha256 = 'F' * 64
        Write-FslUtf8NoBom $context.AnchorPath (
            ($anchor | ConvertTo-Json -Compress) + [Environment]::NewLine)
        Assert-Rejected {
            [void](Read-FslState $context)
        } 'An anchor mismatch was accepted.'

        $context = New-StateContext 'complete-unanchored-tail'
        [System.IO.File]::AppendAllText(
            $context.JournalPath,
            '{}' + [Environment]::NewLine)
        Assert-Rejected {
            [void](Read-FslState $context)
        } 'A complete unanchored journal tail was accepted.'

        $context = New-StateContext 'torn-unanchored-tail'
        $anchoredLength = (Get-Item -LiteralPath $context.JournalPath).Length
        [System.IO.File]::AppendAllText($context.JournalPath, '{"torn":')
        [void](Read-FslState $context)
        Assert-True (
            (Get-Item -LiteralPath $context.JournalPath).Length -eq
            $anchoredLength) 'An incomplete torn tail was not recovered.'

        $context = New-StateContext 'consistent-state-journal-tamper'
        $entry = [System.IO.File]::ReadAllText($context.JournalPath).Trim() |
            ConvertFrom-Json
        $entry.state.transition = 'PublishCompleted'
        $entry.transition = 'PublishCompleted'
        $core = ConvertTo-FslJournalCore $entry
        $entry.entrySha256 = Get-FslSha256 (
            [System.Text.UTF8Encoding]::new($false).GetBytes(
                ($core | ConvertTo-Json -Compress -Depth 20)))
        Write-FslUtf8NoBom $context.JournalPath (
            ($entry | ConvertTo-Json -Compress -Depth 20) +
            [Environment]::NewLine)
        Write-FslUtf8NoBom $context.StatePath (
            ($entry.state | ConvertTo-Json -Depth 20) +
            [Environment]::NewLine)
        Assert-Rejected {
            [void](Read-FslState $context)
        } 'Consistent state+journal tampering bypassed the independent anchor.'

        function New-WalSemanticCase {
            param([string]$Name, [int]$OperationCount = 2)
            $context = New-StateContext "wal-$Name"
            $operations = @()
            foreach ($index in 0..($OperationCount - 1)) {
                $operations += [pscustomobject]@{
                    operationId = "directory-$index"
                    kind = 'DirectoryCreate'
                    target = Join-Path $outerRoot "$Name-directory-$index"
                    desired = [pscustomobject]@{ mustNotExist = $true }
                }
            }
            $plan = @(New-FslDurablePlan $operations)
            $transaction = Start-FslDurableTransaction `
                $context "Semantic-$Name" 'Rollback' 'WalTest' $plan
            $begin = @(Read-FslInstallWal $context | Where-Object {
                $_.phase -ceq 'Begin'
            })[0]
            return [pscustomobject]@{
                Context = $context
                Plan = $transaction.Plan
                Begin = $begin
            }
        }

        function Add-SemanticRecord {
            param(
                [psobject]$Case,
                [int]$Ordinal,
                [string]$Phase,
                [string]$Kind
            )
            $operation = $Case.Plan[$Ordinal]
            Add-FslInstallWalRecord $Case.Context ([pscustomobject]@{
                transactionId = [string]$Case.Begin.transactionId
                planHash = [string]$Case.Begin.planHash
                ordinal = $Ordinal
                operationId = [string]$operation.operationId
                kind = if ([string]::IsNullOrWhiteSpace($Kind)) {
                    [string]$operation.kind
                }
                else {
                    $Kind
                }
                target = [string]$operation.target
                phase = $Phase
                desired = $operation.desired
                proof = if ($Phase -ceq 'Applied') {
                    [pscustomobject]@{ exact = $true }
                }
                else {
                    $null
                }
            })
        }

        $case = New-WalSemanticCase 'valid'
        foreach ($ordinal in 0..1) {
            Add-SemanticRecord $case $ordinal 'Intent' ''
            Add-SemanticRecord $case $ordinal 'Applied' ''
        }
        Complete-FslDurableTransaction `
            $case.Context $case.Begin.transactionId
        Assert-True (
            @(Read-FslInstallWal $case.Context |
                Where-Object { $_.phase -ceq 'Committed' }).Count -eq 1) (
            'A valid schema-3 WAL plan did not commit.')

        $case = New-WalSemanticCase 'duplicate'
        Add-SemanticRecord $case 0 'Intent' ''
        Add-SemanticRecord $case 0 'Intent' ''
        Assert-Rejected {
            [void](Read-FslInstallWal $case.Context)
        } 'A duplicate WAL phase was accepted.'

        $case = New-WalSemanticCase 'order'
        Add-SemanticRecord $case 1 'Intent' ''
        Assert-Rejected {
            [void](Read-FslInstallWal $case.Context)
        } 'An out-of-order WAL ordinal was accepted.'

        $case = New-WalSemanticCase 'kind'
        Add-SemanticRecord $case 0 'Intent' 'DirectorySetAcl'
        Assert-Rejected {
            [void](Read-FslInstallWal $case.Context)
        } 'A WAL kind that differed from the frozen plan was accepted.'

        $case = New-WalSemanticCase 'terminal'
        Assert-Rejected {
            Complete-FslDurableTransaction `
                $case.Context $case.Begin.transactionId
        } 'A Commit before complete Applied proofs was accepted.'

        $case = New-WalSemanticCase 'corruption'
        $text = [System.IO.File]::ReadAllText(
            $case.Context.InstallWalPath).Replace(
                '"workflow":"WalTest"',
                '"workflow":"Install"')
        Write-FslUtf8NoBom $case.Context.InstallWalPath $text
        Write-FslExternalAnchor $case.Context
        Assert-Rejected {
            [void](Read-FslInstallWal $case.Context)
        } 'Anchored WAL record corruption was accepted.'

        $case = New-WalSemanticCase 'truncation'
        $bytes = [System.IO.File]::ReadAllBytes($case.Context.InstallWalPath)
        [System.IO.File]::WriteAllBytes(
            $case.Context.InstallWalPath,
            $bytes[0..($bytes.Length - 2)])
        Assert-Rejected {
            [void](Read-FslInstallWal $case.Context)
        } 'Anchored WAL truncation was accepted.'

        $case = New-WalSemanticCase 'torn'
        $anchoredLength = (
            Get-Item -LiteralPath $case.Context.InstallWalPath).Length
        [System.IO.File]::AppendAllText(
            $case.Context.InstallWalPath,
            '{"torn":')
        [void](Read-FslInstallWal $case.Context)
        Assert-True (
            (Get-Item -LiteralPath $case.Context.InstallWalPath).Length -eq
            $anchoredLength) (
            'An unanchored torn WAL tail was not removed.')

        $case = New-WalSemanticCase 'schema2'
        Write-FslUtf8NoBom $case.Context.InstallWalPath (
            '{"schemaVersion":2}' + [Environment]::NewLine)
        Write-FslExternalAnchor $case.Context
        Assert-Rejected {
            [void](Read-FslInstallWal $case.Context)
        } 'An incomplete schema-2 WAL was accepted.'

        $serviceSnapshot = [pscustomobject]@{
            serviceName = 'FolderSessionLockRecovery'
            displayName = 'Folder Session Lock Recovery Service'
            description = $script:ServiceDescription
            startName = 'LocalSystem'
            startMode = 'Auto'
            state = 'Stopped'
            processId = 0
            imagePath = '"C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe" --mode recovery-service'
            start = 2
            delayedAutoStart = 0
            serviceSidType = 1
        }
        $deleteCounter = [pscustomobject]@{ Count = 0 }
        Invoke-FslVerifiedServiceDelete `
            $serviceSnapshot `
            'C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe' `
            { $deleteCounter.Count++ }
        Assert-True ($deleteCounter.Count -eq 1) (
            'An exact stopped service did not invoke delete exactly once.')
        foreach ($field in @(
            'serviceName',
            'displayName',
            'description',
            'startName',
            'startMode',
            'state',
            'imagePath',
            'start',
            'delayedAutoStart',
            'serviceSidType')) {
            $bad = $serviceSnapshot | Select-Object *
            if ($field -in @('start', 'delayedAutoStart', 'serviceSidType')) {
                $bad.$field = 99
            }
            else {
                $bad.$field = 'mismatch'
            }
            $deleteCounter.Count = 0
            Assert-Rejected {
                Invoke-FslVerifiedServiceDelete `
                    $bad `
                    'C:\Program Files\FolderSessionLock\FolderSessionLock.Broker.exe' `
                    { $deleteCounter.Count++ }
            } "SCM mismatch $field was accepted."
            Assert-True ($deleteCounter.Count -eq 0) (
                "SCM mismatch $field invoked delete.")
        }

        $toolPath = 'C:\Windows Kits\10\bin\1.0.0.0\x64\signtool.exe'
        function New-FakeTrustedTool {
            $nodes = @()
            $path = $toolPath
            while ($null -ne $path) {
                $nodes += [pscustomobject]@{
                    requestedPath = $path.TrimEnd('\')
                    finalPath = $path.TrimEnd('\')
                    identity = 'A' * 24
                    isReparse = $false
                    ownerSid = 'S-1-5-18'
                    untrustedWritableSids = @()
                    aclSddl = 'O:SYG:SYD:P(A;;FA;;;SY)'
                }
                $root = [System.IO.Path]::GetPathRoot($path).TrimEnd('\')
                $path = if ($path.TrimEnd('\') -ceq $root) {
                    $null
                }
                else {
                    $parent = [System.IO.Directory]::GetParent($path)
                    $parent.FullName.TrimEnd('\')
                }
            }
            return [pscustomobject]@{
                file = $nodes[0]
                pathChain = $nodes
                sha256 = 'B' * 64
                signerThumbprint = 'C' * 40
                signerSpkiSha256 =
                    $script:ApprovedMicrosoftSignToolSpkiSha256[0]
            }
        }
        $trustedTool = New-FakeTrustedTool
        Assert-FslTrustedToolDescriptor $trustedTool $toolPath
        foreach ($mutation in @(
            'finalPath',
            'isReparse',
            'ownerSid',
            'untrustedWritableSids',
            'signerSpkiSha256',
            'signerThumbprint',
            'identity',
            'missingAncestor')) {
            $bad = New-FakeTrustedTool
            switch ($mutation) {
                'finalPath' {
                    $bad.pathChain[0].finalPath = 'C:\other\signtool.exe'
                }
                'isReparse' { $bad.pathChain[1].isReparse = $true }
                'ownerSid' {
                    $bad.pathChain[1].ownerSid = 'S-1-5-32-545'
                }
                'untrustedWritableSids' {
                    $bad.pathChain[1].untrustedWritableSids =
                        @('S-1-5-32-545')
                }
                'signerSpkiSha256' {
                    $bad.signerSpkiSha256 = 'D' * 64
                }
                'signerThumbprint' { $bad.signerThumbprint = 'bad' }
                'identity' { $bad.pathChain[1].identity = 'bad' }
                'missingAncestor' {
                    $bad.pathChain = @($bad.pathChain |
                        Select-Object -First ($bad.pathChain.Count - 1))
                }
            }
            Assert-Rejected {
                Assert-FslTrustedToolDescriptor $bad $toolPath
            } "A fake SignTool $mutation failure was accepted."
        }
        $realSignTool = Get-FslSignTool
        Assert-True (
            (Test-Path -LiteralPath $realSignTool -PathType Leaf)) (
            'The real Windows Kits SignTool did not pass read-only validation.')

        $verdictRoot = Join-Path $outerRoot 'verdict'
        [System.IO.Directory]::CreateDirectory($verdictRoot) | Out-Null
        foreach ($accepted in @('PASS', "PASS`r`n", 'FAIL', "FAIL`n")) {
            $path = Join-Path $verdictRoot (
                'accepted-' + [Guid]::NewGuid().ToString('N') + '.txt')
            Write-FslUtf8NoBom $path $accepted
            $actual = Read-FslReviewerVerdict $path
            Assert-True ($actual -cin @('PASS', 'FAIL')) (
                'A single reviewer verdict token was not parsed.')
        }
        foreach ($rejected in @(
            'pass',
            'Pass',
            "PASS`nFAIL",
            "PASS`nPASS",
            'PASS approved',
            "review`nPASS",
            " PASS ",
            ('P' * 20))) {
            $path = Join-Path $verdictRoot (
                'rejected-' + [Guid]::NewGuid().ToString('N') + '.txt')
            Write-FslUtf8NoBom $path $rejected
            Assert-Rejected {
                [void](Read-FslReviewerVerdict $path)
            } "Conflicting or non-exact reviewer verdict was accepted: $rejected"
        }

        $gitRoot = Join-Path $outerRoot 'git-repository'
        [System.IO.Directory]::CreateDirectory($gitRoot) | Out-Null
        & git.exe -C $gitRoot init -b cp10-vm-transfer | Out-Null
        & git.exe -C $gitRoot config user.email stage4@example.invalid
        & git.exe -C $gitRoot config user.name Stage4
        Write-FslUtf8NoBom (Join-Path $gitRoot 'tracked.txt') 'baseline'
        & git.exe -C $gitRoot add tracked.txt
        & git.exe -C $gitRoot commit -m baseline | Out-Null
        $runId = '20260725T120000Z-0123abcd'
        $allowed = Join-Path $gitRoot "docs\evidence\stage-4\$runId"
        [System.IO.Directory]::CreateDirectory($allowed) | Out-Null
        Write-FslUtf8NoBom (Join-Path $allowed 'evidence.txt') 'allowed'
        $repoContext = [pscustomobject]@{
            RepositoryRoot = $gitRoot
            RunId = $runId
        }
        Assert-FslRepositoryMutationGate $repoContext
        Write-FslUtf8NoBom (Join-Path $gitRoot 'tracked.txt') 'changed'
        Assert-Rejected {
            Assert-FslRepositoryMutationGate $repoContext
        } 'A tracked mutation outside current-Run evidence was accepted.'
        Write-FslUtf8NoBom (Join-Path $gitRoot 'tracked.txt') 'baseline'
        Write-FslUtf8NoBom (Join-Path $gitRoot 'untracked.txt') 'unknown'
        Assert-Rejected {
            Assert-FslRepositoryMutationGate $repoContext
        } 'An untracked mutation outside current-Run evidence was accepted.'
        [System.IO.File]::Delete((Join-Path $gitRoot 'untracked.txt'))
        $other = Join-Path $gitRoot 'docs\evidence\stage-4\other-run'
        [System.IO.Directory]::CreateDirectory($other) | Out-Null
        Write-FslUtf8NoBom (Join-Path $other 'evidence.txt') 'other'
        Assert-Rejected {
            Assert-FslRepositoryMutationGate $repoContext
        } 'Other-Run evidence mutation was accepted.'
    } $repository $outerRoot
}
finally {
    if (Test-Path -LiteralPath $outerRoot) {
        Get-ChildItem -LiteralPath $outerRoot -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { -not $_.PSIsContainer } |
            ForEach-Object { $_.IsReadOnly = $false }
        [System.IO.Directory]::Delete($outerRoot, $true)
    }
}

Write-Output 'STAGE4_REPOSITORY_INTEGRITY_BEHAVIOR_PASS'
