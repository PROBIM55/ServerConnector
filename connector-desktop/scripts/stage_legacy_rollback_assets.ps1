[CmdletBinding()]
param(
    [string]$StructuraMsiPath, [string]$PlatformMsiPath, [string]$StagingDirectory,
    [string]$LockPath,
    [switch]$FixtureMode, [scriptblock]$MsiPropertyReader, [scriptblock]$CopyOperation
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'infra/migration/legacy-msi.lock.json'
}
$script:ProductionLegacyAssets = @(
    [pscustomobject]@{ id='structura-connector'; installerName='Connector.Desktop.Setup.msi'; version='1.0.31'; productCode='{8C16FFEC-F35D-45AF-BE71-65BB07533BF8}'; upgradeCode='{0E67CBE8-8F77-45EA-B89D-E58C8C554B37}'; installerBytes=114074894; installerSha256='47B5B3434CED41C3B9A71615906CBAF835B6CB45433554BE612E7BF2D88EF515' },
    [pscustomobject]@{ id='platform-connector'; installerName='Platform.Connector.Desktop.Setup.msi'; version='1.2.1'; productCode='{93CCDE79-6601-4232-8489-8A6435FD6D62}'; upgradeCode='{A7E5408C-1238-4A45-8A84-E3AE45107D7A}'; installerBytes=59929448; installerSha256='D6666FD1613B16879AB6FDA9912D3476D0BA0BE2A782A7F934772D9F89DEDC3D' }
)
function Test-LegacyRollbackReparsePoint([System.IO.FileSystemInfo]$Item) { return (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) }
function Assert-LegacyRollbackNoReparseAncestors([string]$Path) {
    $current = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force -ErrorAction Stop
    while ($null -ne $current) { if (Test-LegacyRollbackReparsePoint $current) { throw "Rollback path '$Path' contains a reparse-point ancestor." }; $current = [IO.Directory]::GetParent($current.FullName) }
}
function Get-LegacyRollbackMsiProperties([string]$Path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer; $database = $installer.OpenDatabase($Path, 0)
    try {
        $values = @{}
        foreach ($name in @('ProductCode','UpgradeCode','ProductVersion')) {
            $view = $database.OpenView("SELECT `Value` FROM `Property` WHERE `Property` = '$name'")
            try { [void]$view.Execute(); $record = $view.Fetch(); if ($null -eq $record) { throw "MSI property '$name' is missing." }; $values[$name] = $record.StringData(1) }
            finally { if ($null -ne $view) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view) } }
        }
        return [pscustomobject]@{ productCode=$values.ProductCode; upgradeCode=$values.UpgradeCode; version=$values.ProductVersion }
    } finally { if ($null -ne $database) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($database) } }
}
function Test-LegacyRollbackLock($Lock, [bool]$FixtureMode) {
    if ($Lock.schemaVersion -ne 1 -or @($Lock.assets).Count -ne 2) { throw 'Rollback lock schema is unsupported.' }
    $structura = @($Lock.assets | Where-Object { $_.id -ceq 'structura-connector' }); $platform = @($Lock.assets | Where-Object { $_.id -ceq 'platform-connector' })
    if ($structura.Count -ne 1 -or $platform.Count -ne 1) { throw 'Rollback lock must contain both exact legacy assets.' }
    if (!$FixtureMode) { foreach ($expected in $script:ProductionLegacyAssets) { $actual = @($Lock.assets | Where-Object { $_.id -ceq $expected.id })[0]; foreach ($property in @('installerName','version','productCode','upgradeCode','installerBytes','installerSha256')) { if ([string]$actual.$property -cne [string]$expected.$property) { throw "Rollback lock did not match the production allowlist for '$($expected.id)'." } } } }
    return @($structura[0], $platform[0])
}
function Get-LegacyRollbackAsset([string]$SourcePath, $Expected, [scriptblock]$PropertyReader) {
    $resolved = (Resolve-Path -LiteralPath $SourcePath -ErrorAction Stop).Path; $item = Get-Item -LiteralPath $resolved -Force
    if ($item.PSIsContainer -or (Test-LegacyRollbackReparsePoint $item)) { throw "Rollback source '$SourcePath' must be a regular file." }
    if ($item.Name -cne $Expected.installerName -or $item.Length -ne [int64]$Expected.installerBytes) { throw "Rollback source '$SourcePath' name or size did not match its lock entry." }
    $hash = ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash).ToUpperInvariant()
    if ($hash -cne $Expected.installerSha256) { throw "Rollback source '$SourcePath' SHA-256 did not match its lock entry." }
    $properties = & $PropertyReader $resolved
    if ($null -eq $properties -or $properties.productCode -cne $Expected.productCode -or $properties.upgradeCode -cne $Expected.upgradeCode -or $properties.version -cne $Expected.version) { throw "Rollback source '$SourcePath' MSI properties did not match its lock entry." }
    return [pscustomobject]@{ Path=$resolved; Hash=$hash; Expected=$Expected }
}
function Test-LegacyRollbackStagingDirectory([string]$Path, [bool]$AllowFixture) {
    $full = [IO.Path]::GetFullPath($Path); $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop; Assert-LegacyRollbackNoReparseAncestors $full
    if (!$item.PSIsContainer -or @(Get-ChildItem -LiteralPath $full -Force).Count -ne 0) { throw 'StagingDirectory must be an existing, empty, non-reparse directory.' }
    $root = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = $full.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($AllowFixture) {
        if (!$candidate.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture staging must be under the system temporary path.' }
    } elseif (!$candidate.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Production staging must be under connector-desktop/artifacts.'
    }
    return $full
}
function Remove-LegacyRollbackOwnedTemporary([string]$Temporary, [string[]]$KnownFiles) {
    if (!(Test-Path -LiteralPath $Temporary)) { return }; Assert-LegacyRollbackNoReparseAncestors $Temporary
    foreach ($file in $KnownFiles) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force -ErrorAction Stop } }
    if (@(Get-ChildItem -LiteralPath $Temporary -Force).Count -eq 0) { Remove-Item -LiteralPath $Temporary -Force -ErrorAction Stop }
}
function Invoke-LegacyRollbackAssetStagingIntoPrivateDirectory {
    param([string]$StructuraMsiPath, [string]$PlatformMsiPath, [string]$StagingDirectory, [string]$LockPath, [bool]$FixtureMode, [scriptblock]$MsiPropertyReader, [scriptblock]$CopyOperation)
    if ($null -eq $MsiPropertyReader) { $MsiPropertyReader = ${function:Get-LegacyRollbackMsiProperties} }; if ($null -eq $CopyOperation) { $CopyOperation = { param($source,$destination) Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop } }
    $lockItem = Get-Item -LiteralPath $LockPath -Force -ErrorAction Stop; Assert-LegacyRollbackNoReparseAncestors $lockItem.FullName
    if (Test-LegacyRollbackReparsePoint $lockItem) { throw 'Rollback lock must not be a reparse point.' }
    $lock = Get-Content -LiteralPath $lockItem.FullName -Raw | ConvertFrom-Json; $expected = Test-LegacyRollbackLock $lock $FixtureMode; $destination = Test-LegacyRollbackStagingDirectory $StagingDirectory $FixtureMode
    $assets = @(Get-LegacyRollbackAsset $StructuraMsiPath $expected[0] $MsiPropertyReader; Get-LegacyRollbackAsset $PlatformMsiPath $expected[1] $MsiPropertyReader)
    $lockHash = ((Get-FileHash -LiteralPath $lockItem.FullName -Algorithm SHA256).Hash).ToUpperInvariant(); $lockBytes = $lockItem.Length
    $temporary = Join-Path $destination ('.rollback-staging-' + [guid]::NewGuid().ToString('N')); $temporaryFiles = New-Object System.Collections.Generic.List[string]; $created = New-Object System.Collections.Generic.List[string]
    try {
        New-Item -ItemType Directory -Path $temporary -ErrorAction Stop | Out-Null; Assert-LegacyRollbackNoReparseAncestors $temporary
        foreach ($asset in $assets) {
            $copy = Join-Path $temporary $asset.Expected.installerName; $temporaryFiles.Add($copy); & $CopyOperation $asset.Path $copy; $copied = Get-Item -LiteralPath $copy -Force
            if ((Test-LegacyRollbackReparsePoint $copied) -or $copied.Length -ne [int64]$asset.Expected.installerBytes -or ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash).ToUpperInvariant() -cne $asset.Hash) { throw "Copied rollback asset '$($asset.Expected.id)' failed post-copy verification." }
            Move-Item -LiteralPath $copy -Destination (Join-Path $destination $asset.Expected.installerName) -ErrorAction Stop; $created.Add((Join-Path $destination $asset.Expected.installerName))
        }
        $lockTemporary = Join-Path $temporary '.legacy-msi.lock.json.partial'; $temporaryFiles.Add($lockTemporary); & $CopyOperation $lockItem.FullName $lockTemporary; $copiedLock = Get-Item -LiteralPath $lockTemporary -Force
        if ((Test-LegacyRollbackReparsePoint $copiedLock) -or $copiedLock.Length -ne $lockBytes -or ((Get-FileHash -LiteralPath $lockTemporary -Algorithm SHA256).Hash).ToUpperInvariant() -cne $lockHash) { throw 'Copied rollback lock failed post-copy verification.' }
        $lockCopy = Join-Path $destination 'legacy-msi.lock.json'; Move-Item -LiteralPath $lockTemporary -Destination $lockCopy -ErrorAction Stop; $created.Add($lockCopy)
        return [pscustomobject]@{ schemaVersion=1; staged=$true; fixture=[bool]$FixtureMode; trustLevel='unprivileged-build-artifact'; stagingDirectory=$destination; assets=@($assets | ForEach-Object { [pscustomobject]@{ id=$_.Expected.id; installerName=$_.Expected.installerName; bytes=$_.Expected.installerBytes; sha256=$_.Hash } }) }
    } catch {
        foreach ($path in $created) { if (Test-Path -LiteralPath $path) { Assert-LegacyRollbackNoReparseAncestors $path; Remove-Item -LiteralPath $path -Force -ErrorAction Stop } }; throw
    } finally { Remove-LegacyRollbackOwnedTemporary $temporary @($temporaryFiles) }
}
function Invoke-LegacyRollbackAssetStaging {
    param([string]$StructuraMsiPath, [string]$PlatformMsiPath, [string]$StagingDirectory, [string]$LockPath, [bool]$FixtureMode, [scriptblock]$MsiPropertyReader, [scriptblock]$CopyOperation, [scriptblock]$BeforePublish)
    $destination = Test-LegacyRollbackStagingDirectory $StagingDirectory $FixtureMode
    $parent = Split-Path -Parent $destination
    Assert-LegacyRollbackNoReparseAncestors $parent
    $private = Join-Path $parent ('.rollback-publish-' + [guid]::NewGuid().ToString('N'))
    $privateCreated = $false
    try {
        New-Item -ItemType Directory -Path $private -ErrorAction Stop | Out-Null
        $privateCreated = $true
        Assert-LegacyRollbackNoReparseAncestors $private
        $result = Invoke-LegacyRollbackAssetStagingIntoPrivateDirectory `
            -StructuraMsiPath $StructuraMsiPath -PlatformMsiPath $PlatformMsiPath `
            -StagingDirectory $private -LockPath $LockPath -FixtureMode $FixtureMode `
            -MsiPropertyReader $MsiPropertyReader -CopyOperation $CopyOperation
        if ($null -ne $BeforePublish) { & $BeforePublish $private $destination }

        # The public location is never populated file-by-file. A crash before the rename
        # leaves it empty (at worst an unpublished private directory remains); after the
        # same-volume directory rename it contains the complete, verified set.
        $current = Test-LegacyRollbackStagingDirectory $destination $FixtureMode
        [IO.Directory]::Delete($current, $false)
        try {
            [IO.Directory]::Move($private, $destination)
            $privateCreated = $false
        } catch {
            if (!(Test-Path -LiteralPath $destination)) {
                [IO.Directory]::CreateDirectory($destination) | Out-Null
            }
            throw
        }
        $result.stagingDirectory = $destination
        return $result
    } finally {
        if ($privateCreated -and (Test-Path -LiteralPath $private)) {
            Assert-LegacyRollbackNoReparseAncestors $private
            $known = @('Connector.Desktop.Setup.msi', 'Platform.Connector.Desktop.Setup.msi', 'legacy-msi.lock.json')
            foreach ($name in $known) {
                $path = Join-Path $private $name
                if (Test-Path -LiteralPath $path) {
                    $item = Get-Item -LiteralPath $path -Force
                    if ($item.PSIsContainer -or (Test-LegacyRollbackReparsePoint $item)) { throw 'Refusing to clean an unexpected rollback staging entry.' }
                    Remove-Item -LiteralPath $path -Force -ErrorAction Stop
                }
            }
            if (@(Get-ChildItem -LiteralPath $private -Force).Count -eq 0) { [IO.Directory]::Delete($private, $false) }
        }
    }
}
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $StructuraMsiPath `
        -PlatformMsiPath $PlatformMsiPath -StagingDirectory $StagingDirectory `
        -LockPath $LockPath -FixtureMode ([bool]$FixtureMode) `
        -MsiPropertyReader $MsiPropertyReader -CopyOperation $CopyOperation
}
