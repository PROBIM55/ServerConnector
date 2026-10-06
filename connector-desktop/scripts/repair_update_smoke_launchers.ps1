param([Parameter(Mandatory = $true)][string]$ExpectedOldInstallRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts\installed-update-smoke'))
$oldInstall = [IO.Path]::GetFullPath($ExpectedOldInstallRoot).TrimEnd('\')
if (-not $oldInstall.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $oldInstall) -ne 'installed') { throw 'Only an explicitly named prior smoke installation can be repaired.' }
$productionExe = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Structura Connector\Connector.Desktop.exe'
$productionLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Structura Connector\Structura Connector.lnk'
if (-not (Test-Path -LiteralPath $productionExe -PathType Leaf) -or -not (Test-Path -LiteralPath $productionLink -PathType Leaf)) {
    throw 'Existing MSI executable and original Start Menu shortcut are required.'
}
$shell = New-Object -ComObject WScript.Shell
$original = $shell.CreateShortcut($productionLink)
if (-not [string]::Equals($original.TargetPath, $productionExe, [StringComparison]::OrdinalIgnoreCase)) { throw 'Original production shortcut target is unexpected.' }
$originalHash = (Get-FileHash -LiteralPath $productionLink -Algorithm SHA256).Hash
$backup = Join-Path $artifactRoot ('launcher-repair-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup | Out-Null
$repaired = @()
foreach ($entry in @(
    @{ location = 'desktop'; path = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Structura Connector.lnk') },
    @{ location = 'programs'; path = (Join-Path ([Environment]::GetFolderPath('Programs')) 'Structura Connector.lnk') }
)) {
    if (-not (Test-Path -LiteralPath $entry.path -PathType Leaf)) { continue }
    $link = $shell.CreateShortcut($entry.path)
    $expectedTarget = Join-Path $oldInstall 'current\UpdateSmokeProbe.exe'
    if (-not [string]::Equals($link.TargetPath, $expectedTarget, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A launcher no longer belongs to the named prior smoke; no overwrite is allowed.'
    }
    Copy-Item -LiteralPath $entry.path -Destination (Join-Path $backup ($entry.location + '-before.lnk'))
    Copy-Item -LiteralPath $productionLink -Destination $entry.path -Force
    if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash -ne $originalHash) { throw 'Production launcher restoration failed.' }
    $repaired += $entry.location
}
$registryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\Structura.Connector.UpdateSmoke'
$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath)
$registrationArchived = $false
if ($key) {
    try {
        if (-not [string]::Equals($key.GetValue('InstallLocation'), $oldInstall, [StringComparison]::OrdinalIgnoreCase) -or
            $key.GetValue('Publisher') -ne 'Structura.Connector.UpdateSmoke') { throw 'Test registration does not belong to the named prior smoke.' }
        $values = @{}
        foreach ($name in $key.GetValueNames()) {
            $values[$name] = @{ value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); kind = $key.GetValueKind($name).ToString() }
        }
        @{ key = $registryPath; values = $values } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'test-registration-before.json') -Encoding utf8
    } finally { $key.Dispose() }
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($registryPath, $false)
    $registrationArchived = $true
}
if ((Get-FileHash -LiteralPath $productionLink -Algorithm SHA256).Hash -ne $originalHash) { throw 'Original MSI shortcut changed.' }
@{ ok = $true; repaired = $repaired; originalMsiShortcutPreserved = $true; testRegistrationArchived = $registrationArchived; backup = $backup } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'results.json') -Encoding utf8
Write-Host "PASS: previous smoke launcher collision repaired; backup: $backup"
