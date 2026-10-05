$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'netbird_windows_preflight.ps1'
. $scriptPath

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message Expected '$Expected', got '$Actual'." }
}

function New-Expected {
    [pscustomobject]@{
        version = '0.79.0'
        productCode = '{463D0C9D-ED41-451E-A44F-937932C8B267}'
        upgradeCode = '{6456EC4E-3AD6-4B9B-A2BE-98E81CB21CCF}'
        manufacturer = 'NetBird GmbH'
        productName = 'NetBird'
        installerSha256 = '50f822c0f5f6e54e7618096cdd36b63bf4b169e125c9da06052fd4fd96115210'
        signerSubjectPrefix = 'CN=NetBird GmbH,'
        signerThumbprint = '7B41FCCAFCB794720FE07D381F9CBDF18AB5900F'
        cliPath = 'C:\Program Files\NetBird\netbird.exe'
        serviceName = 'NetBird'
        serviceStartType = 2
        markerPath = 'C:\ProgramData\Structura Connector\Network\netbird-install-owner.json'
    }
}

function New-Snapshot {
    param([string]$Scenario)
    $expected = New-Expected
    $entry = [pscustomobject]@{
        view = 'Registry64'; key = $expected.productCode; productCode = $expected.productCode
        displayName = 'NetBird'; displayVersion = $expected.version; publisher = $expected.manufacturer
        installLocation = 'C:\Program Files\NetBird'; windowsInstaller = 1
    }
    $registry = [pscustomobject]@{
        querySucceeded = $true; entries = @(); productKeyPresent = $false; upgradeMembers = @()
        expectedUpgradeMembership = $false; errors = @()
    }
    $service = [pscustomobject]@{
        querySucceeded = $true; present = $false; name = 'NetBird'; imagePath = $null
        executablePath = $null; pathMatch = $false; startType = $null; errors = @()
    }
    $cli = [pscustomobject]@{
        querySucceeded = $true; present = $false; path = $expected.cliPath; pathMatch = $true
        pathSecure = $false; version = $null; versionMatch = $false; signatureStatus = 'NotFound'
        signerThumbprint = $null; signatureMatch = $false; errors = @()
    }
    $marker = [pscustomobject]@{
        querySucceeded = $true; present = $false; path = $expected.markerPath
        contentValid = $false; pathSecure = $false; valid = $false; errors = @()
    }
    if ($Scenario -in @('owned', 'mismatch', 'multiple', 'untrusted')) {
        $registry.entries = @($entry)
        $registry.productKeyPresent = $true
        $registry.upgradeMembers = @((ConvertTo-PackedMsiGuid $expected.productCode))
        $registry.expectedUpgradeMembership = $true
        $service.present = $true; $service.imagePath = '"C:\Program Files\NetBird\netbird.exe" service run'
        $service.executablePath = $expected.cliPath; $service.pathMatch = $true; $service.startType = 2
        $cli.present = $true; $cli.pathSecure = $true; $cli.version = '0.79.0'; $cli.versionMatch = $true
        $cli.signatureStatus = 'Valid'; $cli.signerThumbprint = $expected.signerThumbprint; $cli.signatureMatch = $true
        $marker.present = $true; $marker.contentValid = $true; $marker.pathSecure = $true; $marker.valid = $true
    }
    switch ($Scenario) {
        'foreign' {
            $registry.entries = @([pscustomobject]@{
                view = 'Registry64'; key = '{11111111-1111-1111-1111-111111111111}'
                productCode = '{11111111-1111-1111-1111-111111111111}'; displayName = 'NetBird'
                displayVersion = '0.78.0'; publisher = 'NetBird GmbH'; installLocation = 'C:\Program Files\NetBird'; windowsInstaller = 1
            })
        }
        'mismatch' { $service.startType = 3 }
        'multiple' {
            $registry.entries += [pscustomobject]@{
                view = 'Registry32'; key = '{11111111-1111-1111-1111-111111111111}'
                productCode = '{11111111-1111-1111-1111-111111111111}'; displayName = 'NetBird'
                displayVersion = '0.78.0'; publisher = 'NetBird GmbH'; installLocation = ''; windowsInstaller = 1
            }
        }
        'service-only' { $service.present = $true; $service.imagePath = 'C:\Foreign\netbird.exe service run'; $service.startType = 2 }
        'untrusted' { $marker.pathSecure = $false; $marker.valid = $false }
        'unknown' { $registry.querySucceeded = $false; $registry.errors = @('registry-unavailable') }
    }
    [pscustomobject]@{ expected = $expected; snapshot = [pscustomobject]@{ registry = $registry; service = $service; cli = $cli; marker = $marker } }
}

Assert-Equal (ConvertTo-PackedMsiGuid '{463D0C9D-ED41-451E-A44F-937932C8B267}') 'D9C0D36414DEE1544AF43997238C2B76' 'MSI packed ProductCode.'
Assert-Equal (Test-NetBirdVersionText '0.79.0.35374398338' '0.79.0') $true 'Numeric vendor build version.'
Assert-Equal (Test-NetBirdVersionText '0.79.0-rc1' '0.79.0') $false 'Prerelease version must not match pinned release.'

$cases = @(
    @{ scenario = 'absent'; state = 'absent'; install = $true; use = $false },
    @{ scenario = 'foreign'; state = 'foreign'; install = $false; use = $false },
    @{ scenario = 'mismatch'; state = 'mismatch'; install = $false; use = $false },
    @{ scenario = 'owned'; state = 'owned'; install = $false; use = $true },
    @{ scenario = 'multiple'; state = 'multiple'; install = $false; use = $false },
    @{ scenario = 'service-only'; state = 'service-only'; install = $false; use = $false },
    @{ scenario = 'untrusted'; state = 'untrusted'; install = $false; use = $false },
    @{ scenario = 'unknown'; state = 'unknown'; install = $false; use = $false }
)

foreach ($case in $cases) {
    $fixture = New-Snapshot $case.scenario
    $result = Test-NetBirdPreflightSnapshot -Expected $fixture.expected -Snapshot $fixture.snapshot
    Assert-Equal $result.state $case.state "$($case.scenario): state."
    Assert-Equal $result.safeToInstall $case.install "$($case.scenario): safeToInstall."
    Assert-Equal $result.safeToUse $case.use "$($case.scenario): safeToUse."
    Assert-Equal $result.requiresPrivilegedRecheck $true "$($case.scenario): recheck."
}

$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stagedMsi = Join-Path $projectRoot 'artifacts/netbird-staging/v0.79.0/netbird_installer_0.79.0_windows_amd64.msi'
$realMsiTested = $false
if (Test-Path -LiteralPath $stagedMsi -PathType Leaf) {
    $actualExpected = Get-NetBirdExpectedIdentity -Path $stagedMsi
    Assert-Equal $actualExpected.version '0.79.0' 'Staged MSI version.'
    Assert-Equal $actualExpected.productCode '{463D0C9D-ED41-451E-A44F-937932C8B267}' 'Staged MSI ProductCode.'
    Assert-Equal $actualExpected.upgradeCode '{6456EC4E-3AD6-4B9B-A2BE-98E81CB21CCF}' 'Staged MSI UpgradeCode.'
    Assert-Equal $actualExpected.installerSha256 '50f822c0f5f6e54e7618096cdd36b63bf4b169e125c9da06052fd4fd96115210' 'Staged MSI SHA-256.'
    $actualResult = Test-NetBirdPreflightSnapshot -Expected $actualExpected -Snapshot (Get-NetBirdWindowsSnapshot $actualExpected)
    if ($actualResult.state -eq 'unknown') { throw 'Staged MSI live read-only preflight returned unknown.' }
    $realMsiTested = $true
}

[pscustomobject]@{ passed = $cases.Count; failed = 0; scenarios = @($cases.scenario); realMsiTested = $realMsiTested } | ConvertTo-Json -Compress
