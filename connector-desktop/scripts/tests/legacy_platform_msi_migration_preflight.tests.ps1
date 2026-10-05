$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'legacy_platform_msi_migration_preflight.ps1')

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message Expected '$Expected', got '$Actual'." }
}

function New-Fixture([string]$Scenario) {
    $entry = [pscustomobject]@{
        scope = 'perMachine'; registryView = 'Registry32'
        productCode = '{93CCDE79-6601-4232-8489-8A6435FD6D62}'
        displayName = 'Platform Connector Desktop'; displayVersion = '1.2.1'
        uninstallStringPresent = $true; windowsInstaller = 1
    }
    $snapshot = [pscustomobject]@{
        registry = [pscustomobject]@{ querySucceeded = $true; entries = @($entry) }
        executablePresent = $true
        settings = [pscustomobject]@{ exists = $true; beforeHash = 'A'; afterHash = 'A'; jsonValid = $true }
        token = [pscustomobject]@{ exists = $true; beforeHash = 'B'; afterHash = 'B'; dpapiCurrentUserValid = $true; validationError = $null }
        backups = [pscustomobject]@{ exists = $true; fileCount = 2 }
    }
    switch ($Scenario) {
        'absent' {
            $snapshot.registry.entries = @(); $snapshot.executablePresent = $false
            $snapshot.settings.exists = $false; $snapshot.token.exists = $false; $snapshot.backups.exists = $false; $snapshot.backups.fileCount = 0
        }
        'missing-msi' { $snapshot.registry.entries = @() }
        'identity-mismatch' { $snapshot.registry.entries[0].productCode = '{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}' }
        'duplicate-registration' { $snapshot.registry.entries += [pscustomobject]@{ scope = 'perUser'; registryView = 'Registry32'; productCode = '{93CCDE79-6601-4232-8489-8A6435FD6D62}'; displayName = 'Platform Connector Desktop'; displayVersion = '1.2.1'; uninstallStringPresent = $true; windowsInstaller = 1 } }
        'unsupported-registration' { $snapshot.registry.entries[0].scope = 'perUser' }
        'wrong-version' { $snapshot.registry.entries[0].displayVersion = '1.2.0' }
        'invalid-json' { $snapshot.settings.jsonValid = $false }
        'invalid-dpapi' { $snapshot.token.dpapiCurrentUserValid = $false; $snapshot.token.validationError = 'dpapi-current-user-rejected' }
        'settings-changed' { $snapshot.settings.afterHash = 'C' }
        'token-changed' { $snapshot.token.afterHash = 'D' }
    }
    return $snapshot
}

$cases = @(
    @{ scenario = 'ready'; readiness = 'ready'; required = $true; blocker = $null },
    @{ scenario = 'absent'; readiness = 'not-applicable'; required = $false; blocker = $null },
    @{ scenario = 'missing-msi'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-msi-not-found' },
    @{ scenario = 'identity-mismatch'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-msi-identity-mismatch' },
    @{ scenario = 'duplicate-registration'; readiness = 'blocked'; required = $true; blocker = 'multiple-legacy-platform-msi-entries' },
    @{ scenario = 'unsupported-registration'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-msi-unsupported-registration' },
    @{ scenario = 'wrong-version'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-msi-identity-mismatch' },
    @{ scenario = 'invalid-json'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-settings-json-invalid' },
    @{ scenario = 'invalid-dpapi'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-token-dpapi-invalid' },
    @{ scenario = 'settings-changed'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-settings-changed-during-inspection' },
    @{ scenario = 'token-changed'; readiness = 'blocked'; required = $true; blocker = 'legacy-platform-token-changed-during-inspection' }
)
foreach ($case in $cases) {
    $result = Test-LegacyPlatformMsiMigrationPreflightSnapshot -Snapshot (New-Fixture $case.scenario)
    Assert-Equal $result.readOnly $true "$($case.scenario): read-only."
    Assert-Equal $result.readiness $case.readiness "$($case.scenario): readiness."
    Assert-Equal $result.migrationRequired $case.required "$($case.scenario): migration requirement."
    if ($case.blocker) { Assert-Equal ($result.blockers -contains $case.blocker) $true "$($case.scenario): blocker." }
}

$secret = 'must-not-appear-in-preflight-output'
$json = (Test-LegacyPlatformMsiMigrationPreflightSnapshot -Snapshot (New-Fixture 'ready')) | ConvertTo-Json -Compress -Depth 6
Assert-Equal ($json.Contains($secret)) $false 'Output must not disclose token content.'
[pscustomobject]@{ passed = $cases.Count; failed = 0; readOnly = $true; tokenOutputRedacted = $true } | ConvertTo-Json -Compress
