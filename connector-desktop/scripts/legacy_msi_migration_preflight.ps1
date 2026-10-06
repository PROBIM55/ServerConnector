param(
    [string]$FixturePath,
    [ValidateSet('Json', 'Object')][string]$OutputFormat = 'Json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    Add-Type -AssemblyName System.Security
}

$script:LegacyVersion = '1.0.31'
$script:LegacyUpgradeCode = '{0E67CBE8-8F77-45EA-B89D-E58C8C554B37}'
$script:LegacyDisplayName = 'Structura Connector'

function ConvertTo-PackedMsiGuid {
    param([Parameter(Mandatory = $true)][string]$Guid)
    $hex = $Guid.Trim('{}').Replace('-', '').ToUpperInvariant()
    if ($hex -notmatch '^[0-9A-F]{32}$') { throw 'Invalid MSI GUID.' }
    $packed = -join $hex.Substring(0, 8).ToCharArray()[7..0]
    $packed += -join $hex.Substring(8, 4).ToCharArray()[3..0]
    $packed += -join $hex.Substring(12, 4).ToCharArray()[3..0]
    for ($index = 16; $index -lt 32; $index += 2) {
        $packed += $hex[$index + 1]
        $packed += $hex[$index]
    }
    return $packed
}

function Get-LegacyMsiRegistrySnapshot {
    $entries = [Collections.Generic.List[object]]::new()
    $upgradeMemberships = [Collections.Generic.List[object]]::new()
    $entryKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        $registryScopes = @(
            [pscustomobject]@{ name = 'perMachine'; hive = [Microsoft.Win32.RegistryHive]::LocalMachine; upgradePath = 'SOFTWARE\Classes\Installer\UpgradeCodes' },
            [pscustomobject]@{ name = 'perUser'; hive = [Microsoft.Win32.RegistryHive]::CurrentUser; upgradePath = 'SOFTWARE\Microsoft\Installer\UpgradeCodes' }
        )
        foreach ($scope in $registryScopes) {
            foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($scope.hive, $view)
                try {
                $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', $false)
                if ($uninstall) {
                    try {
                        foreach ($name in $uninstall.GetSubKeyNames()) {
                            $key = $uninstall.OpenSubKey($name, $false)
                            if (!$key) { continue }
                            try {
                                if ([string]$key.GetValue('DisplayName') -ceq $script:LegacyDisplayName) {
                                    $entryKey = "$($scope.name)|$name"
                                    if ($entryKeys.Add($entryKey)) {
                                        $entries.Add([pscustomobject]@{
                                            scope = $scope.name
                                            productCode = $name
                                            displayVersion = [string]$key.GetValue('DisplayVersion')
                                            uninstallStringPresent = -not [string]::IsNullOrWhiteSpace([string]$key.GetValue('UninstallString'))
                                            installLocation = [string]$key.GetValue('InstallLocation')
                                            windowsInstaller = [int]$key.GetValue('WindowsInstaller', 0)
                                        })
                                    }
                                }
                            } finally { $key.Dispose() }
                        }
                    } finally { $uninstall.Dispose() }
                }
                $upgrade = $base.OpenSubKey($scope.upgradePath + '\\' + (ConvertTo-PackedMsiGuid $script:LegacyUpgradeCode), $false)
                if ($upgrade) {
                    try {
                        foreach ($valueName in $upgrade.GetValueNames()) {
                            $upgradeMemberships.Add([pscustomobject]@{ scope = $scope.name; packedProductCode = $valueName })
                        }
                    }
                    finally { $upgrade.Dispose() }
                }
                } finally { $base.Dispose() }
            }
        }
        return [pscustomobject]@{ querySucceeded = $true; entries = @($entries); upgradeMemberships = @($upgradeMemberships) }
    } catch {
        return [pscustomobject]@{ querySucceeded = $false; entries = @(); upgradeMemberships = @() }
    }
}

function Get-LegacyMsiMigrationPreflightSnapshot {
    $settingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ConnectorAgentDesktop\settings.json'
    $exePath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Structura Connector\Connector.Desktop.exe'
    $shortcutPaths = @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Structura Connector.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Structura Connector.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'Structura Connector\Structura Connector.lnk')
    )
    $settings = [pscustomobject]@{ exists = (Test-Path -LiteralPath $settingsPath -PathType Leaf); beforeHash = $null; afterHash = $null; jsonValid = $false; tokenCipherPresent = $false; tokenDpapiCurrentUserValid = $false; tokenValidationError = $null }
    if ($settings.exists) {
        try {
            $settings.beforeHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
            $document = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
            $settings.jsonValid = $true
            $cipher = [string]$document.TokenCipherBase64
            $settings.tokenCipherPresent = -not [string]::IsNullOrWhiteSpace($cipher)
            if ($settings.tokenCipherPresent) {
                try {
                    [void][Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($cipher), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
                    $settings.tokenDpapiCurrentUserValid = $true
                } catch [FormatException] {
                    $settings.tokenValidationError = 'invalid-base64'
                } catch [Security.Cryptography.CryptographicException] {
                    $settings.tokenValidationError = 'dpapi-current-user-rejected'
                } catch {
                    $settings.tokenValidationError = 'token-check-failed'
                }
            }
        } catch { }
        try { $settings.afterHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } catch { }
    }
    return [pscustomobject]@{
        registry = Get-LegacyMsiRegistrySnapshot
        executablePresent = Test-Path -LiteralPath $exePath -PathType Leaf
        shortcutsPresent = @($shortcutPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }).Count
        settings = $settings
    }
}

function Test-LegacyMsiMigrationPreflightSnapshot {
    param([Parameter(Mandatory = $true)]$Snapshot)
    $blockers = [Collections.Generic.List[string]]::new()
    $checks = [Collections.Generic.List[object]]::new()
    $entries = @($Snapshot.registry.entries)
    $registryOk = $Snapshot.registry.querySucceeded
    $legacyEntry = if ($entries.Count -eq 1) { $entries[0] } else { $null }
    $expectedPackedProductCode = $null
    if ($legacyEntry) {
        try { $expectedPackedProductCode = ConvertTo-PackedMsiGuid $legacyEntry.productCode } catch { }
    }
    # Windows Installer can publish the uninstall entry and UpgradeCode membership
    # in different registry hives for the same product. Identity is the exact
    # packed ProductCode under our UpgradeCode, not equality of the hive names.
    $matchingMembers = @($Snapshot.registry.upgradeMemberships | Where-Object {
        $_.packedProductCode -ceq $expectedPackedProductCode
    })
    $matchingUpgradeMembership = $expectedPackedProductCode -and $matchingMembers.Count -gt 0
    $registryScopeConsistent = $matchingMembers.Count -gt 0 -and @($matchingMembers | Where-Object {
        $_.scope -ceq $legacyEntry.scope
    }).Count -gt 0
    $identityOk = $registryOk -and $legacyEntry -and $legacyEntry.displayVersion -ceq $script:LegacyVersion -and
        $legacyEntry.uninstallStringPresent -and $legacyEntry.windowsInstaller -eq 1 -and $matchingUpgradeMembership
    $settingsHashStable = $Snapshot.settings.exists -and $null -ne $Snapshot.settings.beforeHash -and
        $Snapshot.settings.beforeHash -ceq $Snapshot.settings.afterHash
    $checks.Add([pscustomobject]@{ id = 'msi-registry-identity'; ok = [bool]$identityOk })
    $checks.Add([pscustomobject]@{ id = 'legacy-executable'; ok = [bool]$Snapshot.executablePresent })
    $checks.Add([pscustomobject]@{ id = 'legacy-shortcut'; ok = ($Snapshot.shortcutsPresent -gt 0) })
    $checks.Add([pscustomobject]@{ id = 'settings-json'; ok = [bool]$Snapshot.settings.jsonValid })
    $checks.Add([pscustomobject]@{ id = 'settings-dpapi-current-user'; ok = [bool]$Snapshot.settings.tokenDpapiCurrentUserValid })
    $checks.Add([pscustomobject]@{ id = 'settings-hash-stable'; ok = [bool]$settingsHashStable })
    if (!$registryOk) { $blockers.Add('msi-registry-inspection-failed') }
    elseif ($entries.Count -eq 0) { $blockers.Add('legacy-msi-not-found') }
    elseif ($entries.Count -gt 1) { $blockers.Add('multiple-legacy-msi-entries') }
    elseif (!$identityOk) { $blockers.Add('legacy-msi-identity-mismatch') }
    if (!$Snapshot.executablePresent) { $blockers.Add('legacy-executable-not-found') }
    if ($Snapshot.shortcutsPresent -eq 0) { $blockers.Add('legacy-shortcut-not-found') }
    if (!$Snapshot.settings.exists) { $blockers.Add('settings-not-found') }
    elseif (!$Snapshot.settings.jsonValid) { $blockers.Add('settings-json-invalid') }
    elseif (!$Snapshot.settings.tokenCipherPresent) { $blockers.Add('settings-token-missing') }
    elseif (!$Snapshot.settings.tokenDpapiCurrentUserValid) { $blockers.Add('settings-token-dpapi-invalid') }
    elseif (!$settingsHashStable) { $blockers.Add('settings-changed-during-inspection') }
    $ready = $blockers.Count -eq 0
    [pscustomobject]@{
        schemaVersion = 1; readOnly = $true; readiness = if ($ready) { 'ready' } else { 'blocked' }; readyForMigration = $ready
        legacyMsi = [pscustomobject]@{ found = $entries.Count -gt 0; entryCount = $entries.Count; identityVerified = [bool]$identityOk; registryScopeConsistent = [bool]$registryScopeConsistent }
        executable = [pscustomobject]@{ present = [bool]$Snapshot.executablePresent }
        shortcuts = [pscustomobject]@{ presentCount = [int]$Snapshot.shortcutsPresent }
        settings = [pscustomobject]@{ present = [bool]$Snapshot.settings.exists; jsonValid = [bool]$Snapshot.settings.jsonValid; tokenCipherPresent = [bool]$Snapshot.settings.tokenCipherPresent; tokenDpapiCurrentUserValid = [bool]$Snapshot.settings.tokenDpapiCurrentUserValid; tokenValidationError = $Snapshot.settings.tokenValidationError; hashStableDuringPreflight = [bool]$settingsHashStable }
        checks = @($checks); blockers = @($blockers)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $snapshot = if ($FixturePath) { Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json } else { Get-LegacyMsiMigrationPreflightSnapshot }
        $result = Test-LegacyMsiMigrationPreflightSnapshot -Snapshot $snapshot
    } catch {
        $result = [pscustomobject]@{ schemaVersion = 1; readOnly = $true; readiness = 'blocked'; readyForMigration = $false; legacyMsi = $null; executable = $null; shortcuts = $null; settings = $null; checks = @(); blockers = @('preflight-inspection-failed') }
    }
    if ($OutputFormat -eq 'Object') { $result } else { $result | ConvertTo-Json -Compress -Depth 6 }
}
