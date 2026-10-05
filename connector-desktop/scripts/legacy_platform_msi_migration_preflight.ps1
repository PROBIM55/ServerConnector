param(
    [string]$FixturePath,
    [ValidateSet('Json', 'Object')][string]$OutputFormat = 'Json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    Add-Type -AssemblyName System.Security
}

$script:LegacyProductCode = '{93CCDE79-6601-4232-8489-8A6435FD6D62}'
$script:LegacyVersion = '1.2.1'
$script:LegacyDisplayName = 'Platform Connector Desktop'

function Get-LegacyPlatformMsiRegistrySnapshot {
    try {
        $entries = [Collections.Generic.List[object]]::new()
        $scopes = @(
            [pscustomobject]@{ name = 'perMachine'; hive = [Microsoft.Win32.RegistryHive]::LocalMachine },
            [pscustomobject]@{ name = 'perUser'; hive = [Microsoft.Win32.RegistryHive]::CurrentUser }
        )
        foreach ($scope in $scopes) {
            foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
                $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($scope.hive, $view)
                try {
                    $uninstall = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', $false)
                    if (!$uninstall) { continue }
                    try {
                foreach ($name in $uninstall.GetSubKeyNames()) {
                    $key = $uninstall.OpenSubKey($name, $false)
                    if (!$key) { continue }
                    try {
                        if ([string]$key.GetValue('DisplayName') -ceq $script:LegacyDisplayName -or
                            $name -ceq $script:LegacyProductCode) {
                            $entries.Add([pscustomobject]@{
                                scope = $scope.name
                                registryView = $view.ToString()
                                productCode = $name
                                displayName = [string]$key.GetValue('DisplayName')
                                displayVersion = [string]$key.GetValue('DisplayVersion')
                                uninstallStringPresent = -not [string]::IsNullOrWhiteSpace([string]$key.GetValue('UninstallString'))
                                windowsInstaller = [int]$key.GetValue('WindowsInstaller', 0)
                            })
                        }
                    } finally { $key.Dispose() }
                }
                    } finally { $uninstall.Dispose() }
                } finally { $base.Dispose() }
            }
        }
        return [pscustomobject]@{ querySucceeded = $true; entries = @($entries) }
    } catch {
        return [pscustomobject]@{ querySucceeded = $false; entries = @() }
    }
}

function Get-FileInspection {
    param([Parameter(Mandatory = $true)][string]$Path)
    $result = [pscustomobject]@{
        exists = (Test-Path -LiteralPath $Path -PathType Leaf)
        beforeHash = $null; afterHash = $null; dpapiCurrentUserValid = $false; validationError = $null
    }
    if (!$result.exists) { return $result }
    try {
        $result.beforeHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        $raw = [IO.File]::ReadAllText($Path).Trim()
        if ([string]::IsNullOrWhiteSpace($raw)) {
            $result.validationError = 'empty-token-file'
        } elseif ($raw.StartsWith('plain:', [StringComparison]::Ordinal)) {
            $result.validationError = 'token-not-dpapi-protected'
        } else {
            try {
                [void][Security.Cryptography.ProtectedData]::Unprotect(
                    [Convert]::FromBase64String($raw), $null,
                    [Security.Cryptography.DataProtectionScope]::CurrentUser)
                $result.dpapiCurrentUserValid = $true
            } catch [FormatException] {
                $result.validationError = 'invalid-base64'
            } catch [Security.Cryptography.CryptographicException] {
                $result.validationError = 'dpapi-current-user-rejected'
            } catch {
                $result.validationError = 'token-check-failed'
            }
        }
    } catch {
        $result.validationError = 'token-read-failed'
    }
    try { $result.afterHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash } catch { }
    return $result
}

function Get-LegacyPlatformMsiMigrationPreflightSnapshot {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    $settingsPath = Join-Path $localAppData 'Platform\Connector\desktop\settings.json'
    $tokenPath = Join-Path $localAppData 'Platform\Connector\secrets\device-token.dat'
    $exePath = Join-Path $localAppData 'Platform Connector\Platform.Connector.Desktop.Ui.exe'
    $backupRoot = Join-Path $localAppData 'Platform\Connector\backups'
    $settings = [pscustomobject]@{ exists = (Test-Path -LiteralPath $settingsPath -PathType Leaf); beforeHash = $null; afterHash = $null; jsonValid = $false }
    if ($settings.exists) {
        try {
            $settings.beforeHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
            [void](Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json)
            $settings.jsonValid = $true
        } catch { }
        try { $settings.afterHash = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash } catch { }
    }
    $backupFiles = if (Test-Path -LiteralPath $backupRoot -PathType Container) {
        @(Get-ChildItem -LiteralPath $backupRoot -File -Recurse -ErrorAction Stop).Count
    } else { 0 }
    return [pscustomobject]@{
        registry = Get-LegacyPlatformMsiRegistrySnapshot
        executablePresent = (Test-Path -LiteralPath $exePath -PathType Leaf)
        settings = $settings
        token = Get-FileInspection -Path $tokenPath
        backups = [pscustomobject]@{ exists = (Test-Path -LiteralPath $backupRoot -PathType Container); fileCount = [int]$backupFiles }
    }
}

function Test-LegacyPlatformMsiMigrationPreflightSnapshot {
    param([Parameter(Mandatory = $true)]$Snapshot)
    $blockers = [Collections.Generic.List[string]]::new()
    $checks = [Collections.Generic.List[object]]::new()
    $entries = @($Snapshot.registry.entries)
    $hasArtifacts = [bool]$Snapshot.executablePresent -or [bool]$Snapshot.settings.exists -or [bool]$Snapshot.token.exists -or [bool]$Snapshot.backups.exists
    $absent = $Snapshot.registry.querySucceeded -and $entries.Count -eq 0 -and !$hasArtifacts
    $entry = if ($entries.Count -eq 1) { $entries[0] } else { $null }
    $identityOk = $Snapshot.registry.querySucceeded -and $entry -and
        $entry.scope -ceq 'perMachine' -and $entry.registryView -ceq 'Registry32' -and
        $entry.productCode -ceq $script:LegacyProductCode -and
        $entry.displayName -ceq $script:LegacyDisplayName -and
        $entry.displayVersion -ceq $script:LegacyVersion -and
        $entry.uninstallStringPresent -and $entry.windowsInstaller -eq 1
    $settingsHashStable = $Snapshot.settings.exists -and $null -ne $Snapshot.settings.beforeHash -and
        $Snapshot.settings.beforeHash -ceq $Snapshot.settings.afterHash
    $tokenHashStable = $Snapshot.token.exists -and $null -ne $Snapshot.token.beforeHash -and
        $Snapshot.token.beforeHash -ceq $Snapshot.token.afterHash
    $checks.Add([pscustomobject]@{ id = 'legacy-platform-connector-absent'; ok = [bool]$absent })
    $checks.Add([pscustomobject]@{ id = 'msi-registry-identity'; ok = [bool]$identityOk })
    $checks.Add([pscustomobject]@{ id = 'legacy-executable'; ok = [bool]$Snapshot.executablePresent })
    $checks.Add([pscustomobject]@{ id = 'settings-json'; ok = [bool]$Snapshot.settings.jsonValid })
    $checks.Add([pscustomobject]@{ id = 'settings-hash-stable'; ok = [bool]$settingsHashStable })
    $checks.Add([pscustomobject]@{ id = 'token-dpapi-current-user'; ok = [bool]$Snapshot.token.dpapiCurrentUserValid })
    $checks.Add([pscustomobject]@{ id = 'token-hash-stable'; ok = [bool]$tokenHashStable })
    if (!$Snapshot.registry.querySucceeded) { $blockers.Add('legacy-platform-msi-registry-inspection-failed') }
    elseif (!$absent -and $entries.Count -eq 0) { $blockers.Add('legacy-platform-msi-not-found') }
    elseif ($entries.Count -gt 1) { $blockers.Add('multiple-legacy-platform-msi-entries') }
    elseif ($entry -and ($entry.scope -cne 'perMachine' -or $entry.registryView -cne 'Registry32')) { $blockers.Add('legacy-platform-msi-unsupported-registration') }
    elseif (!$absent -and !$identityOk) { $blockers.Add('legacy-platform-msi-identity-mismatch') }
    if (!$absent) {
        if (!$Snapshot.executablePresent) { $blockers.Add('legacy-platform-executable-not-found') }
        if (!$Snapshot.settings.exists) { $blockers.Add('legacy-platform-settings-not-found') }
        elseif (!$Snapshot.settings.jsonValid) { $blockers.Add('legacy-platform-settings-json-invalid') }
        elseif (!$settingsHashStable) { $blockers.Add('legacy-platform-settings-changed-during-inspection') }
        if (!$Snapshot.token.exists) { $blockers.Add('legacy-platform-token-not-found') }
        elseif (!$Snapshot.token.dpapiCurrentUserValid) { $blockers.Add('legacy-platform-token-dpapi-invalid') }
        elseif (!$tokenHashStable) { $blockers.Add('legacy-platform-token-changed-during-inspection') }
    }
    $ready = $blockers.Count -eq 0
    return [pscustomobject]@{
        schemaVersion = 1; readOnly = $true
        readiness = if ($absent) { 'not-applicable' } elseif ($ready) { 'ready' } else { 'blocked' }
        readyForMigration = [bool]$ready; migrationRequired = -not $absent
        legacyPlatformConnector = [pscustomobject]@{ found = $entries.Count -gt 0; entryCount = $entries.Count; identityVerified = [bool]$identityOk; productCode = if ($identityOk) { $script:LegacyProductCode } else { $null } }
        executable = [pscustomobject]@{ present = [bool]$Snapshot.executablePresent }
        settings = [pscustomobject]@{ present = [bool]$Snapshot.settings.exists; jsonValid = [bool]$Snapshot.settings.jsonValid; hashStableDuringPreflight = [bool]$settingsHashStable }
        token = [pscustomobject]@{ present = [bool]$Snapshot.token.exists; dpapiCurrentUserValid = [bool]$Snapshot.token.dpapiCurrentUserValid; validationError = $Snapshot.token.validationError; hashStableDuringPreflight = [bool]$tokenHashStable }
        backups = [pscustomobject]@{ present = [bool]$Snapshot.backups.exists; fileCount = [int]$Snapshot.backups.fileCount }
        checks = @($checks); blockers = @($blockers)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $snapshot = if ($FixturePath) { Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json } else { Get-LegacyPlatformMsiMigrationPreflightSnapshot }
        $result = Test-LegacyPlatformMsiMigrationPreflightSnapshot -Snapshot $snapshot
    } catch {
        $result = [pscustomobject]@{ schemaVersion = 1; readOnly = $true; readiness = 'blocked'; readyForMigration = $false; migrationRequired = $null; checks = @(); blockers = @('legacy-platform-preflight-inspection-failed') }
    }
    if ($OutputFormat -eq 'Object') { $result } else { $result | ConvertTo-Json -Compress -Depth 6 }
}
