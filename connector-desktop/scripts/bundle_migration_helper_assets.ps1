[CmdletBinding()]
param(
    [string]$StructuraMsiPath,
    [string]$PlatformMsiPath,
    [string]$NetBirdMsiPath,
    [string]$TargetBundleDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:MigrationHelperScriptsRoot = $PSScriptRoot
$script:MigrationHelperRepositoryRoot = Split-Path -Parent $PSScriptRoot
# The legacy staging script has a param block with these same names. Dot-sourcing
# it at script scope binds its defaults over this CLI's already-bound arguments.
$migrationHelperCliInputs = @{
    StructuraMsiPath = $StructuraMsiPath
    PlatformMsiPath = $PlatformMsiPath
    NetBirdMsiPath = $NetBirdMsiPath
    TargetBundleDirectory = $TargetBundleDirectory
}
. (Join-Path $PSScriptRoot 'stage_legacy_rollback_assets.ps1')
$StructuraMsiPath = $migrationHelperCliInputs.StructuraMsiPath
$PlatformMsiPath = $migrationHelperCliInputs.PlatformMsiPath
$NetBirdMsiPath = $migrationHelperCliInputs.NetBirdMsiPath
$TargetBundleDirectory = $migrationHelperCliInputs.TargetBundleDirectory
Remove-Variable migrationHelperCliInputs

$script:ExpectedNetBirdLockSha256 = 'EE48F195FAE2A08B83C0A44F238C237C79F05F27BCA8DBC995542B1FCD23FA96'
$script:ExpectedNetBirdVerifierSha256 = 'DFFFA2EC3BBDFB57583F66AB651F12259AFFFBCF39AA014B11411F9C152EA834'
$script:ExpectedNetBirdInstallerSha256 = '50F822C0F5F6E54E7618096CDD36B63BF4B169E125C9DA06052FD4FD96115210'

function Resolve-HelperBundleInput([string]$Path, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Name is required." }
    $full = [IO.Path]::GetFullPath($Path)
    Assert-LegacyRollbackNoReparseAncestors $full
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($item.PSIsContainer -or (Test-LegacyRollbackReparsePoint $item)) {
        throw "$Name must be a regular non-reparse file."
    }
    return $item.FullName
}

function Copy-HelperBundleFileCreateNew([string]$SourcePath, [string]$DestinationPath) {
    $input = [IO.File]::Open($SourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $output = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output, 131072); $output.Flush($true) }
        finally { $output.Dispose() }
    } finally { $input.Dispose() }
}

function Get-HelperBundleNetBirdPin([string]$LockPath, [bool]$FixtureMode) {
    $lockItem = Get-Item -LiteralPath ([IO.Path]::GetFullPath($LockPath)) -Force -ErrorAction Stop
    Assert-LegacyRollbackNoReparseAncestors $lockItem.FullName
    if ($lockItem.PSIsContainer -or (Test-LegacyRollbackReparsePoint $lockItem)) {
        throw 'The official NetBird lock must be a regular non-reparse file.'
    }
    $lockHash = ((Get-FileHash -LiteralPath $lockItem.FullName -Algorithm SHA256).Hash).ToUpperInvariant()
    if (!$FixtureMode -and $lockHash -cne $script:ExpectedNetBirdLockSha256) {
        throw 'The official NetBird lock did not match the pinned repository lock hash.'
    }
    $lock = Get-Content -LiteralPath $lockItem.FullName -Raw | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$lock.installerName) -or
        [string]$lock.installerName -cne [IO.Path]::GetFileName([string]$lock.installerName) -or
        [int64]$lock.installerBytes -le 0 -or
        [string]$lock.installerSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$lock.version -cnotmatch '^\d+\.\d+\.\d+$') {
        throw 'The official NetBird lock has an unsupported or incomplete installer pin.'
    }
    if (!$FixtureMode -and ([string]$lock.installerSha256).ToUpperInvariant() -cne $script:ExpectedNetBirdInstallerSha256) {
        throw 'The official NetBird installer hash did not match the pinned release asset.'
    }
    return [pscustomobject]@{
        Name = [string]$lock.installerName
        Version = [string]$lock.version
        Bytes = [int64]$lock.installerBytes
        Sha256 = ([string]$lock.installerSha256).ToUpperInvariant()
        LockPath = $lockItem.FullName
    }
}

function Invoke-HelperBundleNetBirdVerification(
    [string]$MsiPath, $Pin, [string]$VerifierPath, [bool]$FixtureMode, [scriptblock]$Verifier) {
    if (!$FixtureMode) {
        $resolvedVerifier = Resolve-HelperBundleInput $VerifierPath 'NetBird verifier'
        $verifierHash = ((Get-FileHash -LiteralPath $resolvedVerifier -Algorithm SHA256).Hash).ToUpperInvariant()
        if ($verifierHash -cne $script:ExpectedNetBirdVerifierSha256) {
            throw 'The official NetBird verifier did not match its pinned repository hash.'
        }
    } else {
        $resolvedVerifier = $VerifierPath
    }
    if ($null -ne $Verifier) {
        if (!$FixtureMode) { throw 'A custom NetBird verifier is allowed only in fixture mode.' }
        $output = @(& $Verifier $MsiPath)
    } else {
        $output = @(& $resolvedVerifier -MsiPath $MsiPath)
    }
    $results = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -contains 'verified' })
    $unexpected = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -notcontains 'verified' })
    if ($results.Count -ne 1 -or $unexpected.Count -ne 0) { throw 'NetBird verifier returned an ambiguous result.' }
    $result = $results[0]
    Assert-LegacyRollbackNoReparseAncestors $MsiPath
    $source = Get-Item -LiteralPath $MsiPath -Force -ErrorAction Stop
    $currentHash = ((Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash).ToUpperInvariant()
    $expectedBytes = if ($FixtureMode) { [int64]$source.Length } else { $Pin.Bytes }
    $expectedHash = if ($FixtureMode) { $currentHash } else { $Pin.Sha256 }
    $reportedHash = [string]$result.sha256
    if ($reportedHash -notmatch '^[0-9a-fA-F]{64}$') { $reportedHash = '' }
    else { $reportedHash = $reportedHash.ToUpperInvariant() }
    if ((Test-LegacyRollbackReparsePoint $source) -or $source.PSIsContainer -or
        !$result.verified -or [string]$result.version -cne $Pin.Version -or
        [int64]$result.bytes -ne $expectedBytes -or $reportedHash -cne $expectedHash -or
        $source.Name -cne $Pin.Name -or $source.Length -ne $expectedBytes -or $currentHash -cne $expectedHash -or
        (!$FixtureMode -and ([string]$result.authenticodeStatus -cne 'Valid' -or
            [string]$result.signerThumbprint -cne '7B41FCCAFCB794720FE07D381F9CBDF18AB5900F')) ) {
        throw 'NetBird MSI did not match the official version, size, hash, or signature pin.'
    }
    $effectivePin = if ($FixtureMode) {
        [pscustomobject]@{ Name = $Pin.Name; Version = $Pin.Version; Bytes = $expectedBytes; Sha256 = $expectedHash }
    } else { $Pin }
    return [pscustomobject]@{ SourcePath = $MsiPath; Hash = $currentHash; Pin = $effectivePin }
}

function Remove-HelperBundleOwnedTemporary(
    [string]$Path, [string]$ExpectedParent, [string[]]$KnownNames) {
    if (!(Test-Path -LiteralPath $Path)) { return }
    $full = [IO.Path]::GetFullPath($Path)
    if ((Split-Path -Leaf $full) -notmatch '^\.migration-helper-assets-[0-9a-f]{32}$' -or
        ![string]::Equals([IO.Path]::GetDirectoryName($full), $ExpectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the exact helper bundle temporary directory.'
    }
    Assert-LegacyRollbackNoReparseAncestors $full
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if (!$item.PSIsContainer -or (Test-LegacyRollbackReparsePoint $item)) {
        throw 'Refusing cleanup of a replaced helper bundle temporary directory.'
    }
    foreach ($name in $KnownNames) {
        $pathToDelete = Join-Path $full $name
        if (!(Test-Path -LiteralPath $pathToDelete)) { continue }
        Assert-LegacyRollbackNoReparseAncestors $pathToDelete
        $entry = Get-Item -LiteralPath $pathToDelete -Force -ErrorAction Stop
        if ($entry.PSIsContainer -or (Test-LegacyRollbackReparsePoint $entry)) {
            throw 'Refusing cleanup of an unexpected or reparse helper bundle entry.'
        }
        Remove-Item -LiteralPath $pathToDelete -Force -ErrorAction Stop
    }
    if (@(Get-ChildItem -LiteralPath $full -Force).Count -ne 0) {
        throw 'The helper bundle temporary directory contains an unknown entry; it was retained.'
    }
    [IO.Directory]::Delete($full, $false)
}

function Invoke-MigrationHelperAssetBundle {
    param(
        [Parameter(Mandatory = $true)][string]$StructuraMsiPath,
        [Parameter(Mandatory = $true)][string]$PlatformMsiPath,
        [Parameter(Mandatory = $true)][string]$NetBirdMsiPath,
        [Parameter(Mandatory = $true)][string]$TargetBundleDirectory,
        [string]$LegacyLockPath = (Join-Path $script:MigrationHelperRepositoryRoot 'infra/migration/legacy-msi.lock.json'),
        [string]$NetBirdLockPath = (Join-Path $script:MigrationHelperRepositoryRoot 'infra/netbird/v0.79.0-windows-x64.lock.json'),
        [string]$NetBirdVerifierPath = (Join-Path $script:MigrationHelperScriptsRoot 'verify_netbird_installer.ps1'),
        [bool]$FixtureMode = $false,
        [scriptblock]$MsiPropertyReader,
        [scriptblock]$CopyOperation,
        [scriptblock]$NetBirdVerifier
    )

    $structura = Resolve-HelperBundleInput $StructuraMsiPath 'Structura MSI'
    $platform = Resolve-HelperBundleInput $PlatformMsiPath 'Platform MSI'
    $netbird = Resolve-HelperBundleInput $NetBirdMsiPath 'NetBird MSI'
    if ([string]::Equals($structura, $platform, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($structura, $netbird, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($platform, $netbird, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The three trusted MSI inputs must be distinct files.'
    }
    $target = Test-LegacyRollbackStagingDirectory $TargetBundleDirectory $FixtureMode
    $parent = [IO.Path]::GetDirectoryName($target)
    if ([string]::IsNullOrWhiteSpace($parent)) { throw 'The target bundle directory must have a parent.' }

    $pin = Get-HelperBundleNetBirdPin $NetBirdLockPath $FixtureMode
    if ($pin.Name -in @('Connector.Desktop.Setup.msi', 'Platform.Connector.Desktop.Setup.msi', 'legacy-msi.lock.json')) {
        throw 'The NetBird fixed basename collides with a legacy helper asset.'
    }
    $verifiedNetBird = Invoke-HelperBundleNetBirdVerification $netbird $pin $NetBirdVerifierPath $FixtureMode $NetBirdVerifier
    $legacyLockItem = Get-Item -LiteralPath ([IO.Path]::GetFullPath($LegacyLockPath)) -Force -ErrorAction Stop
    Assert-LegacyRollbackNoReparseAncestors $legacyLockItem.FullName
    if ($legacyLockItem.PSIsContainer -or (Test-LegacyRollbackReparsePoint $legacyLockItem)) {
        throw 'The legacy MSI lock must be a regular non-reparse file.'
    }

    $temporary = Join-Path $parent ('.migration-helper-assets-' + [guid]::NewGuid().ToString('N'))
    $knownNames = @('Connector.Desktop.Setup.msi', 'Platform.Connector.Desktop.Setup.msi',
        'legacy-msi.lock.json', $pin.Name, '.netbird-copy.partial')
    $published = $false
    $temporaryCreated = $false
    try {
        New-Item -ItemType Directory -Path $temporary -ErrorAction Stop | Out-Null
        $temporaryCreated = $true
        Assert-LegacyRollbackNoReparseAncestors $temporary
        $copy = if ($null -ne $CopyOperation) { $CopyOperation } else { ${function:Copy-HelperBundleFileCreateNew} }
        [void](Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $structura -PlatformMsiPath $platform `
            -StagingDirectory $temporary -LockPath $legacyLockItem.FullName -FixtureMode $FixtureMode `
            -MsiPropertyReader $MsiPropertyReader -CopyOperation $copy)

        $partial = Join-Path $temporary '.netbird-copy.partial'
        $destination = Join-Path $temporary $pin.Name
        if (Test-Path -LiteralPath $destination) { throw 'The NetBird destination filename collides with a legacy helper asset.' }
        Assert-LegacyRollbackNoReparseAncestors $verifiedNetBird.SourcePath
        & $copy $verifiedNetBird.SourcePath $partial
        $copied = Get-Item -LiteralPath $partial -Force -ErrorAction Stop
        if ($copied.PSIsContainer -or (Test-LegacyRollbackReparsePoint $copied) -or
            $copied.Length -ne $verifiedNetBird.Pin.Bytes -or
            ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash).ToUpperInvariant() -cne $verifiedNetBird.Hash) {
            throw 'Copied NetBird MSI failed post-copy verification.'
        }
        [IO.File]::Move($partial, $destination)
        Assert-LegacyRollbackNoReparseAncestors $temporary
        Assert-LegacyRollbackNoReparseAncestors $destination

        $expectedNames = @('Connector.Desktop.Setup.msi', 'Platform.Connector.Desktop.Setup.msi',
            'legacy-msi.lock.json', $pin.Name) | Sort-Object
        $actualNames = @(Get-ChildItem -LiteralPath $temporary -Force | ForEach-Object { $_.Name } | Sort-Object)
        if ($actualNames.Count -ne $expectedNames.Count -or
            [string]::Join('|', $actualNames) -cne [string]::Join('|', $expectedNames)) {
            throw 'The complete helper bundle did not contain exactly the pinned asset set.'
        }

        # Same-parent directory rename publishes the complete bundle in one filesystem operation.
        # The explicit target is rechecked empty and only that empty directory is removed first.
        if (@(Get-ChildItem -LiteralPath $target -Force).Count -ne 0) {
            throw 'The target helper bundle directory became occupied before publication.'
        }
        Assert-LegacyRollbackNoReparseAncestors $target
        [IO.Directory]::Delete($target, $false)
        try {
            [IO.Directory]::Move($temporary, $target)
            $published = $true
        } catch {
            if (!(Test-Path -LiteralPath $target)) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
            }
            throw
        }
        return [pscustomobject]@{
            schemaVersion = 1
            bundled = $true
            fixture = $FixtureMode
            trustLevel = 'unprivileged-build-artifact'
            targetDirectory = $target
            assets = @($expectedNames)
        }
    } finally {
        if ($temporaryCreated -and !$published) { Remove-HelperBundleOwnedTemporary $temporary $parent $knownNames }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-MigrationHelperAssetBundle -StructuraMsiPath $StructuraMsiPath `
        -PlatformMsiPath $PlatformMsiPath -NetBirdMsiPath $NetBirdMsiPath `
        -TargetBundleDirectory $TargetBundleDirectory
}
