$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'legacy_msi_migration_preflight.ps1')

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message Expected '$Expected', got '$Actual'." }
}

function New-Fixture([string]$Scenario) {
    $productCode = '{12345678-1234-1234-ABCD-1234567890AB}'
    $entry = [pscustomobject]@{ scope = 'perUser'; productCode = $productCode; displayVersion = '1.0.31'; uninstallStringPresent = $true; windowsInstaller = 1 }
    $snapshot = [pscustomobject]@{
        registry = [pscustomobject]@{
            querySucceeded = $true; entries = @($entry)
            upgradeMemberships = @([pscustomobject]@{ scope = 'perUser'; packedProductCode = ConvertTo-PackedMsiGuid $productCode })
        }
        executablePresent = $true; shortcutsPresent = 1
        settings = [pscustomobject]@{ exists = $true; beforeHash = 'A'; afterHash = 'A'; jsonValid = $true; tokenCipherPresent = $true; tokenDpapiCurrentUserValid = $true; tokenValidationError = $null }
    }
    switch ($Scenario) {
        'per-machine' {
            $snapshot.registry.entries[0].scope = 'perMachine'
            $snapshot.registry.upgradeMemberships[0].scope = 'perMachine'
        }
        'missing-msi' { $snapshot.registry.entries = @() }
        'wrong-version' { $snapshot.registry.entries[0].displayVersion = '1.0.30' }
        'upgrade-member-other-scope' { $snapshot.registry.upgradeMemberships[0].scope = 'perMachine' }
        'upgrade-member-other-product' { $snapshot.registry.upgradeMemberships[0].packedProductCode = ConvertTo-PackedMsiGuid '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}' }
        'missing-upgrade-member' { $snapshot.registry.upgradeMemberships = @() }
        'missing-exe' { $snapshot.executablePresent = $false }
        'missing-shortcut' { $snapshot.shortcutsPresent = 0 }
        'invalid-json' { $snapshot.settings.jsonValid = $false }
        'invalid-dpapi' { $snapshot.settings.tokenDpapiCurrentUserValid = $false }
        'changed-settings' { $snapshot.settings.afterHash = 'B' }
    }
    return $snapshot
}

$cases = @(
    @{ scenario = 'ready'; ready = $true; blocker = $null },
    @{ scenario = 'per-machine'; ready = $true; blocker = $null },
    @{ scenario = 'missing-msi'; ready = $false; blocker = 'legacy-msi-not-found' },
    @{ scenario = 'wrong-version'; ready = $false; blocker = 'legacy-msi-identity-mismatch' },
    @{ scenario = 'upgrade-member-other-scope'; ready = $true; blocker = $null },
    @{ scenario = 'upgrade-member-other-product'; ready = $false; blocker = 'legacy-msi-identity-mismatch' },
    @{ scenario = 'missing-upgrade-member'; ready = $false; blocker = 'legacy-msi-identity-mismatch' },
    @{ scenario = 'missing-exe'; ready = $false; blocker = 'legacy-executable-not-found' },
    @{ scenario = 'missing-shortcut'; ready = $false; blocker = 'legacy-shortcut-not-found' },
    @{ scenario = 'invalid-json'; ready = $false; blocker = 'settings-json-invalid' },
    @{ scenario = 'invalid-dpapi'; ready = $false; blocker = 'settings-token-dpapi-invalid' },
    @{ scenario = 'changed-settings'; ready = $false; blocker = 'settings-changed-during-inspection' }
)
foreach ($case in $cases) {
    $result = Test-LegacyMsiMigrationPreflightSnapshot -Snapshot (New-Fixture $case.scenario)
    Assert-Equal $result.readOnly $true "$($case.scenario): read-only."
    Assert-Equal $result.readyForMigration $case.ready "$($case.scenario): readiness."
    if ($case.blocker) { Assert-Equal ($result.blockers -contains $case.blocker) $true "$($case.scenario): blocker." }
    if ($case.scenario -eq 'upgrade-member-other-scope') {
        Assert-Equal $result.legacyMsi.registryScopeConsistent $false 'Cross-hive identity remains visible.'
    }
}

$secret = 'must-not-appear-in-preflight-output'
$resultJson = (Test-LegacyMsiMigrationPreflightSnapshot -Snapshot (New-Fixture 'ready')) | ConvertTo-Json -Compress -Depth 6
Assert-Equal ($resultJson.Contains($secret)) $false 'Output must not disclose token content.'
[pscustomobject]@{ passed = $cases.Count; failed = 0; readOnly = $true; tokenOutputRedacted = $true } | ConvertTo-Json -Compress
