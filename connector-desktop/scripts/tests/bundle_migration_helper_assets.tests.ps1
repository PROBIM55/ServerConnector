$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'bundle_migration_helper_assets.ps1')

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message Expected '$Expected', got '$Actual'." }
}
function Assert-Throws([scriptblock]$Action, [string]$Fragment) {
    try { & $Action } catch { if ($_.Exception.Message.Contains($Fragment)) { return }; throw }
    throw "Expected error containing '$Fragment'."
}
function Assert-EmptyDirectory([string]$Path, [string]$Message) {
    if (!(Test-Path -LiteralPath $Path -PathType Container) -or @(Get-ChildItem -LiteralPath $Path -Force).Count -ne 0) {
        throw $Message
    }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('bundle_migration_helper_assets.tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$junction = $null
try {
    $productionLockPath = Join-Path $script:MigrationHelperRepositoryRoot 'infra/netbird/v0.79.0-windows-x64.lock.json'
    $productionPin = Get-HelperBundleNetBirdPin $productionLockPath $false
    Assert-Equal $productionPin.Name 'netbird_installer_0.79.0_windows_amd64.msi' 'Official NetBird fixed basename.'
    Assert-Equal $productionPin.Sha256 $script:ExpectedNetBirdInstallerSha256 'Official NetBird hash pin.'
    $productionVerifierPath = Resolve-HelperBundleInput (Join-Path $script:MigrationHelperScriptsRoot 'verify_netbird_installer.ps1') 'NetBird verifier'
    Assert-Equal ((Get-FileHash $productionVerifierPath -Algorithm SHA256).Hash).ToUpperInvariant() $script:ExpectedNetBirdVerifierSha256 'Official verifier script pin.'

    # Exercise the real -File entrypoint. This deliberately stops at the production
    # target-path guard using a temp target; it proves dot-sourced legacy params did
    # not erase the CLI inputs without touching artifacts or a production MSI.
    $cliStructura = Join-Path $root 'cli-structura.msi'
    $cliPlatform = Join-Path $root 'cli-platform.msi'
    $cliNetBird = Join-Path $root $productionPin.Name
    [IO.File]::WriteAllBytes($cliStructura, [byte[]](1)); [IO.File]::WriteAllBytes($cliPlatform, [byte[]](2)); [IO.File]::WriteAllBytes($cliNetBird, [byte[]](3))
    $cliTarget = Join-Path $root 'cli-target'; New-Item -ItemType Directory -Path $cliTarget | Out-Null
    $cliScript = Join-Path $script:MigrationHelperScriptsRoot 'bundle_migration_helper_assets.ps1'
    $cliOutput = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $cliScript `
        -StructuraMsiPath $cliStructura -PlatformMsiPath $cliPlatform -NetBirdMsiPath $cliNetBird `
        -TargetBundleDirectory $cliTarget 2>&1
    $cliExitCode = $LASTEXITCODE
    if ($cliExitCode -eq 0 -or [string]::Join("`n", @($cliOutput)) -notmatch 'Production staging must be under connector-desktop/artifacts') {
        throw "The -File CLI did not preserve its arguments through dot-sourcing; exit=$cliExitCode output=$cliOutput"
    }
    Assert-EmptyDirectory $cliTarget 'CLI regression must not mutate its temporary target.'

    $structuraSource = Join-Path $root 'Connector.Desktop.Setup.msi'
    $platformSource = Join-Path $root 'Platform.Connector.Desktop.Setup.msi'
    $netbirdPin = Get-HelperBundleNetBirdPin (Join-Path $script:MigrationHelperRepositoryRoot 'infra/netbird/v0.79.0-windows-x64.lock.json') $true
    $netbirdSource = Join-Path $root $netbirdPin.Name
    [IO.File]::WriteAllBytes($structuraSource, [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes($platformSource, [byte[]](4, 5, 6, 7))
    [IO.File]::WriteAllBytes($netbirdSource, [byte[]](8, 9, 10, 11, 12))

    $lock = [pscustomobject]@{ schemaVersion = 1; assets = @(
        [pscustomobject]@{ id = 'structura-connector'; installerName = 'Connector.Desktop.Setup.msi'; version = '1.0.31'; productCode = '{8C16FFEC-F35D-45AF-BE71-65BB07533BF8}'; upgradeCode = '{0E67CBE8-8F77-45EA-B89D-E58C8C554B37}'; installerBytes = 3; installerSha256 = ((Get-FileHash $structuraSource -Algorithm SHA256).Hash).ToUpperInvariant() },
        [pscustomobject]@{ id = 'platform-connector'; installerName = 'Platform.Connector.Desktop.Setup.msi'; version = '1.2.1'; productCode = '{93CCDE79-6601-4232-8489-8A6435FD6D62}'; upgradeCode = '{A7E5408C-1238-4A45-8A84-E3AE45107D7A}'; installerBytes = 4; installerSha256 = ((Get-FileHash $platformSource -Algorithm SHA256).Hash).ToUpperInvariant() }
    ) }
    $legacyLockPath = Join-Path $root 'fixture-legacy.lock.json'
    $lock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $legacyLockPath -NoNewline
    $reader = {
        param($path)
        if ((Split-Path -Leaf $path) -ceq 'Connector.Desktop.Setup.msi') {
            [pscustomobject]@{ productCode = '{8C16FFEC-F35D-45AF-BE71-65BB07533BF8}'; upgradeCode = '{0E67CBE8-8F77-45EA-B89D-E58C8C554B37}'; version = '1.0.31' }
        } else {
            [pscustomobject]@{ productCode = '{93CCDE79-6601-4232-8489-8A6435FD6D62}'; upgradeCode = '{A7E5408C-1238-4A45-8A84-E3AE45107D7A}'; version = '1.2.1' }
        }
    }
    $verifier = {
        param($path)
        [pscustomobject]@{
            verified = $true; version = $netbirdPin.Version; bytes = (Get-Item -LiteralPath $path).Length
            # The official checker emits lowercase hex; compare hashes canonically.
            sha256 = ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash).ToLowerInvariant()
            authenticodeStatus = 'Fixture'; signerThumbprint = 'fixture'
        }
    }
    $newTarget = { param($name) $path = Join-Path $root $name; New-Item -ItemType Directory -Path $path | Out-Null; return $path }
    $common = @{
        StructuraMsiPath = $structuraSource; PlatformMsiPath = $platformSource; NetBirdMsiPath = $netbirdSource
        LegacyLockPath = $legacyLockPath; FixtureMode = $true; MsiPropertyReader = $reader; NetBirdVerifier = $verifier
    }

    $target = & $newTarget 'success-bundle'
    $result = Invoke-MigrationHelperAssetBundle @common -TargetBundleDirectory $target
    Assert-Equal $result.bundled $true 'Bundle publication succeeds.'
    Assert-Equal $result.fixture $true 'Fixture bundle is marked untrusted.'
    Assert-Equal @(Get-ChildItem -LiteralPath $target -File).Count 4 'Full bundle contains three MSI files and legacy lock.'
    Assert-Equal ((Get-FileHash (Join-Path $target $netbirdPin.Name) -Algorithm SHA256).Hash).ToUpperInvariant() ((Get-FileHash $netbirdSource -Algorithm SHA256).Hash).ToUpperInvariant() 'NetBird post-copy hash.'
    $verifiedLowercase = Invoke-HelperBundleNetBirdVerification $netbirdSource $netbirdPin '' $true $verifier
    Assert-Equal $verifiedLowercase.Hash ((Get-FileHash $netbirdSource -Algorithm SHA256).Hash).ToUpperInvariant() 'Lowercase verifier hash is canonicalized.'
    $wrongVersionVerifier = { param($path) [pscustomobject]@{ verified=$true; version='0.0.0'; bytes=(Get-Item $path).Length; sha256=((Get-FileHash $path -Algorithm SHA256).Hash).ToLowerInvariant() } }
    Assert-Throws { Invoke-HelperBundleNetBirdVerification $netbirdSource $netbirdPin '' $true $wrongVersionVerifier } 'version, size, hash, or signature pin'
    $wrongBytesVerifier = { param($path) [pscustomobject]@{ verified=$true; version=$netbirdPin.Version; bytes=((Get-Item $path).Length + 1); sha256=((Get-FileHash $path -Algorithm SHA256).Hash).ToLowerInvariant() } }
    Assert-Throws { Invoke-HelperBundleNetBirdVerification $netbirdSource $netbirdPin '' $true $wrongBytesVerifier } 'version, size, hash, or signature pin'

    $badNetBird = Join-Path $root 'bad-netbird'; New-Item -ItemType Directory -Path $badNetBird | Out-Null
    $badVerifier = { param($path) [pscustomobject]@{ verified = $true; version = $netbirdPin.Version; bytes = (Get-Item $path).Length; sha256 = ('0' * 64); authenticodeStatus = 'Fixture'; signerThumbprint = 'fixture' } }
    $badNetBirdArgs = @{} + $common; $badNetBirdArgs.NetBirdVerifier = $badVerifier
    Assert-Throws { Invoke-MigrationHelperAssetBundle @badNetBirdArgs -TargetBundleDirectory $badNetBird } 'official version, size, hash'
    Assert-EmptyDirectory $badNetBird 'NetBird pin mismatch must leave the target empty.'

    $legacyMismatch = Join-Path $root 'legacy-mismatch'; New-Item -ItemType Directory -Path $legacyMismatch | Out-Null
    $badReader = { param($path) [pscustomobject]@{ productCode = 'bad'; upgradeCode = 'bad'; version = 'bad' } }
    $badReaderArgs = @{} + $common; $badReaderArgs.MsiPropertyReader = $badReader
    Assert-Throws { Invoke-MigrationHelperAssetBundle @badReaderArgs -TargetBundleDirectory $legacyMismatch } 'MSI properties'
    Assert-EmptyDirectory $legacyMismatch 'Legacy identity mismatch must leave the target empty.'

    $faultTarget = Join-Path $root 'copy-fault'; New-Item -ItemType Directory -Path $faultTarget | Out-Null
    $faultCopy = {
        param($source, $destination)
        if ((Split-Path -Leaf $destination) -ceq '.netbird-copy.partial') {
            [IO.File]::WriteAllBytes($destination, [byte[]](0xFA, 0xCE))
            throw 'fixture NetBird copy fault'
        }
        Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop
    }
    Assert-Throws { Invoke-MigrationHelperAssetBundle @common -TargetBundleDirectory $faultTarget -CopyOperation $faultCopy } 'fixture NetBird copy fault'
    Assert-EmptyDirectory $faultTarget 'Copy fault must leave the target empty and remove only owned temporary files.'
    if (@(Get-ChildItem -LiteralPath $root -Directory -Filter '.migration-helper-assets-*').Count -ne 0) {
        throw 'Owned temporary helper bundle directory was not cleaned after copy fault.'
    }

    $occupied = Join-Path $root 'occupied'; New-Item -ItemType Directory -Path $occupied | Out-Null
    $sentinel = Join-Path $occupied 'keep.bin'; [IO.File]::WriteAllBytes($sentinel, [byte[]](0xAA))
    $sentinelHash = (Get-FileHash $sentinel -Algorithm SHA256).Hash
    Assert-Throws { Invoke-MigrationHelperAssetBundle @common -TargetBundleDirectory $occupied } 'existing, empty'
    Assert-Equal (Get-FileHash $sentinel -Algorithm SHA256).Hash $sentinelHash 'Occupied target is preserved.'

    $junctionTarget = Join-Path $root 'junction-target'; New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    $junction = Join-Path $root 'junction'; New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
    $junctionBundle = Join-Path $junction 'bundle'; New-Item -ItemType Directory -Path $junctionBundle | Out-Null
    Assert-Throws { Invoke-MigrationHelperAssetBundle @common -TargetBundleDirectory $junctionBundle } 'reparse-point ancestor'

    [pscustomobject]@{ passed = 7; failed = 0; fixtureBytes = 12; productionMsiCopied = $false } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $root) {
        $rootFull = [IO.Path]::GetFullPath($root)
        $tempFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (!$rootFull.StartsWith($tempFull, [StringComparison]::OrdinalIgnoreCase) -or
            !(Split-Path -Leaf $rootFull).StartsWith('bundle_migration_helper_assets.tests-', [StringComparison]::Ordinal)) {
            throw 'Refusing to remove a test root outside the expected temporary directory.'
        }
        Assert-LegacyRollbackNoReparseAncestors $rootFull
        if ($null -ne $junction -and (Test-Path -LiteralPath $junction)) {
            $item = Get-Item -LiteralPath $junction -Force
            if (!(Test-LegacyRollbackReparsePoint $item)) { throw 'Refusing to remove a non-junction test path.' }
            [IO.Directory]::Delete($junction, $false)
        }
        Remove-Item -LiteralPath $rootFull -Recurse -Force -ErrorAction Stop
    }
}
