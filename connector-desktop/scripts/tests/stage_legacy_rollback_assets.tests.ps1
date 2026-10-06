$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'stage_legacy_rollback_assets.ps1')

function Assert-Equal($Actual, $Expected, [string]$Message) { if ($Actual -cne $Expected) { throw "$Message Expected '$Expected', got '$Actual'." } }
function Assert-Throws([scriptblock]$Action, [string]$Fragment) { try { & $Action } catch { if ($_.Exception.Message.Contains($Fragment)) { return }; throw }; throw "Expected error containing '$Fragment'." }

$root = Join-Path ([IO.Path]::GetTempPath()) ('stage_legacy_rollback_assets.tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $structuraSource = Join-Path $root 'Connector.Desktop.Setup.msi'
    $platformSource = Join-Path $root 'Platform.Connector.Desktop.Setup.msi'
    [IO.File]::WriteAllBytes($structuraSource, [byte[]](1,2,3)); [IO.File]::WriteAllBytes($platformSource, [byte[]](4,5,6,7))
    $makeLock = {
        param($structuraPath, $platformPath)
        [pscustomobject]@{ schemaVersion = 1; assets = @(
            [pscustomobject]@{ id = 'structura-connector'; installerName = 'Connector.Desktop.Setup.msi'; version = '1.0.31'; productCode = '{8C16FFEC-F35D-45AF-BE71-65BB07533BF8}'; upgradeCode = '{0E67CBE8-8F77-45EA-B89D-E58C8C554B37}'; installerBytes = (Get-Item $structuraPath).Length; installerSha256 = ((Get-FileHash $structuraPath -Algorithm SHA256).Hash).ToUpperInvariant() },
            [pscustomobject]@{ id = 'platform-connector'; installerName = 'Platform.Connector.Desktop.Setup.msi'; version = '1.2.1'; productCode = '{93CCDE79-6601-4232-8489-8A6435FD6D62}'; upgradeCode = '{A7E5408C-1238-4A45-8A84-E3AE45107D7A}'; installerBytes = (Get-Item $platformPath).Length; installerSha256 = ((Get-FileHash $platformPath -Algorithm SHA256).Hash).ToUpperInvariant() }
        ) }
    }
    $lock = & $makeLock $structuraSource $platformSource
    Assert-Equal $lock.assets[0].installerSha256 (((Get-FileHash -LiteralPath $structuraSource -Algorithm SHA256).Hash).ToUpperInvariant()) 'Fixture lock hash.'
    $lockPath = Join-Path $root 'fixture.lock.json'; $lock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $lockPath -NoNewline
    $reader = { param($path) if ((Split-Path -Leaf $path) -ceq 'Connector.Desktop.Setup.msi') { [pscustomobject]@{ productCode = '{8C16FFEC-F35D-45AF-BE71-65BB07533BF8}'; upgradeCode = '{0E67CBE8-8F77-45EA-B89D-E58C8C554B37}'; version = '1.0.31' } } else { [pscustomobject]@{ productCode = '{93CCDE79-6601-4232-8489-8A6435FD6D62}'; upgradeCode = '{A7E5408C-1238-4A45-8A84-E3AE45107D7A}'; version = '1.2.1' } } }
    $stage = Join-Path $root 'stage'; New-Item -ItemType Directory -Path $stage | Out-Null
    $result = Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $structuraSource -PlatformMsiPath $platformSource -StagingDirectory $stage -LockPath $lockPath -FixtureMode $true -MsiPropertyReader $reader
    Assert-Equal $result.staged $true 'Fixture payload stages.'
    Assert-Equal $result.fixture $true 'Fixture payload is marked.'
    Assert-Equal @(Get-ChildItem -LiteralPath $stage -File).Count 3 'Payload contains two MSI assets and lock.'
    Assert-Equal ((Get-FileHash (Join-Path $stage 'Connector.Desktop.Setup.msi') -Algorithm SHA256).Hash).ToUpperInvariant() $lock.assets[0].installerSha256 'Structura post-copy hash.'
    $publishFaultStage = Join-Path $root 'publish-fault-stage'; New-Item -ItemType Directory -Path $publishFaultStage | Out-Null
    $beforePublishFault = { param($privatePath, $publicPath) if (@(Get-ChildItem -LiteralPath $publicPath -Force).Count -ne 0) { throw 'Public staging became visible before publication.' }; throw 'fixture publication interruption' }
    Assert-Throws { Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $structuraSource -PlatformMsiPath $platformSource -StagingDirectory $publishFaultStage -LockPath $lockPath -FixtureMode $true -MsiPropertyReader $reader -BeforePublish $beforePublishFault } 'fixture publication interruption'
    Assert-Equal @(Get-ChildItem -LiteralPath $publishFaultStage -Force).Count 0 'Interrupted publication leaves public staging empty.'
    Assert-Equal @(Get-ChildItem -LiteralPath $root -Directory -Filter '.rollback-publish-*').Count 0 'Owned private publication directory is cleaned after interruption.'
    $occupied = Join-Path $root 'occupied'; New-Item -ItemType Directory -Path $occupied | Out-Null; Set-Content -LiteralPath (Join-Path $occupied 'already-there') -Value 'x'
    Assert-Throws { Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $structuraSource -PlatformMsiPath $platformSource -StagingDirectory $occupied -LockPath $lockPath -FixtureMode $true -MsiPropertyReader $reader } 'existing, empty'
    $badLock = & $makeLock $structuraSource $platformSource; $badLock.assets[0].installerSha256 = '00'; $badLockPath = Join-Path $root 'bad.lock.json'; $badLock | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $badLockPath -NoNewline
    $badStage = Join-Path $root 'bad-stage'; New-Item -ItemType Directory -Path $badStage | Out-Null
    Assert-Throws { Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $structuraSource -PlatformMsiPath $platformSource -StagingDirectory $badStage -LockPath $badLockPath -FixtureMode $true -MsiPropertyReader $reader } 'SHA-256'
    Assert-Equal @(Get-ChildItem -LiteralPath $badStage -Force).Count 0 'Invalid sources leave no partial output.'
    $propertyStage = Join-Path $root 'property-stage'; New-Item -ItemType Directory -Path $propertyStage | Out-Null
    Assert-Throws { Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $structuraSource -PlatformMsiPath $platformSource -StagingDirectory $propertyStage -LockPath $lockPath -FixtureMode $true -MsiPropertyReader { param($path) [pscustomobject]@{ productCode = 'bad'; upgradeCode = 'bad'; version = 'bad' } } } 'MSI properties'
    Assert-Equal @(Get-ChildItem -LiteralPath $propertyStage -Force).Count 0 'Property mismatch leaves no partial output.'
    $maliciousLock = & $makeLock $structuraSource $platformSource; $maliciousLock.assets[1].productCode = '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}'
    Assert-Throws { Test-LegacyRollbackLock $maliciousLock $false } 'production allowlist'
    $junctionTarget = Join-Path $root 'junction-target'; New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    $junction = Join-Path $root 'junction'; New-Item -ItemType Junction -Path $junction -Target $junctionTarget | Out-Null
    $junctionStage = Join-Path $junction 'stage'; New-Item -ItemType Directory -Path $junctionStage | Out-Null
    Assert-Throws { Test-LegacyRollbackStagingDirectory $junctionStage $true } 'reparse-point ancestor'
    $artifactFixture = Join-Path (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts') ('fixture-reject-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $artifactFixture -ErrorAction Stop | Out-Null
    try { Assert-Throws { Test-LegacyRollbackStagingDirectory $artifactFixture $true } 'Fixture staging must be under' }
    finally { if (Test-Path -LiteralPath $artifactFixture) { Remove-Item -LiteralPath $artifactFixture -Force -ErrorAction Stop } }
    $lockFaultStage = Join-Path $root 'lock-fault-stage'; New-Item -ItemType Directory -Path $lockFaultStage | Out-Null
    $lockFaultCopier = { param($source, $destination) if ((Split-Path -Leaf $destination) -ceq '.legacy-msi.lock.json.partial') { throw 'fixture lock copy fault' }; Copy-Item -LiteralPath $source -Destination $destination -ErrorAction Stop }
    Assert-Throws { Invoke-LegacyRollbackAssetStaging -StructuraMsiPath $structuraSource -PlatformMsiPath $platformSource -StagingDirectory $lockFaultStage -LockPath $lockPath -FixtureMode $true -MsiPropertyReader $reader -CopyOperation $lockFaultCopier } 'fixture lock copy fault'
    Assert-Equal @(Get-ChildItem -LiteralPath $lockFaultStage -Force).Count 0 'Lock-copy fault leaves no partial output.'
    [pscustomobject]@{ passed = 13; failed = 0; fixtureBytes = 7; realMsiCopied = $false } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $root) {
        $rootFull = [IO.Path]::GetFullPath($root)
        $tempFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (!$rootFull.StartsWith($tempFull, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path -Leaf $rootFull).StartsWith('stage_legacy_rollback_assets.tests-', [StringComparison]::Ordinal)) { throw 'Refusing to remove a test root outside the expected temporary directory.' }
        Assert-LegacyRollbackNoReparseAncestors $rootFull
        if (Test-Path -LiteralPath $junction) {
            $junctionItem = Get-Item -LiteralPath $junction -Force -ErrorAction Stop
            if (!(Test-LegacyRollbackReparsePoint $junctionItem)) { throw 'Refusing to remove a non-junction test path.' }
            [IO.Directory]::Delete($junction, $false)
        }
        Remove-Item -LiteralPath $rootFull -Recurse -Force -ErrorAction Stop
    }
}
