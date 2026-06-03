param(
    [string]$TargetDir = "",
    [switch]$Force
)

# Downloads the official AmneziaWG (amnezia-vpn/amneziawg-windows-client) MSI and extracts the
# CLI binaries (amneziawg.exe, awg.exe, wintun.dll) into $TargetDir for bundling into the connector.
# Mirrors scripts/ensure_bundled_git.ps1. Uses an MSI administrative install (msiexec /a) which only
# unpacks files (no system install, no driver load, no admin required at build time).
#
# Stage 2 (VPN-in-connector). Wired into build_msi.ps1 (best-effort: a failure there is a
# warning, not a hard build break, since the connector degrades gracefully without the VPN client).

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    throw "TargetDir is required"
}

$targetExe = Join-Path $TargetDir 'amneziawg.exe'
if (-not $Force -and (Test-Path $targetExe)) {
    Write-Host "Bundled AmneziaWG already exists at $TargetDir"
    exit 0
}

if (Test-Path $TargetDir) {
    Remove-Item -Path $TargetDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$apiUrl = 'https://api.github.com/repos/amnezia-vpn/amneziawg-windows-client/releases/latest'
Write-Host "Resolving latest AmneziaWG release from $apiUrl"
$release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'StructuraConnectorBuild' } -TimeoutSec 30

$asset = $release.assets |
    Where-Object { $_.name -match 'amd64.*\.msi$' } |
    Select-Object -First 1
if (-not $asset) {
    $asset = $release.assets | Where-Object { $_.name -match '\.msi$' } | Select-Object -First 1
}
if (-not $asset) {
    throw "Could not find an AmneziaWG MSI asset in the latest release"
}

$msiPath = Join-Path ([System.IO.Path]::GetTempPath()) ("awg_" + [Guid]::NewGuid().ToString('N') + ".msi")
Write-Host "Downloading $($asset.name)"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $msiPath -TimeoutSec 180

# Administrative install: unpack only (no system change, no driver).
$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("awg_extract_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
$adminLog = Join-Path ([System.IO.Path]::GetTempPath()) ("awg_admin_" + [Guid]::NewGuid().ToString('N') + ".log")
try {
    $p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/a', "`"$msiPath`"", '/qn', "TARGETDIR=`"$extractDir`"", '/L*v', "`"$adminLog`""
    if ($p.ExitCode -ne 0) {
        throw "msiexec administrative install failed with exit code $($p.ExitCode); see $adminLog"
    }

    $wanted = @('amneziawg.exe', 'awg.exe', 'wintun.dll')
    foreach ($name in $wanted) {
        $found = Get-ChildItem -Path $extractDir -Recurse -Filter $name -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $found) {
            throw "AmneziaWG MSI unpacked, but $name was not found under $extractDir"
        }
        Copy-Item -Path $found.FullName -Destination (Join-Path $TargetDir $name) -Force
    }
}
finally {
    if (Test-Path $msiPath) { Remove-Item -Path $msiPath -Force -ErrorAction SilentlyContinue }
    if (Test-Path $extractDir) { Remove-Item -Path $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
}

if (-not (Test-Path $targetExe)) {
    throw "AmneziaWG bundling failed: $targetExe not present"
}

Write-Host "Bundled AmneziaWG prepared at $TargetDir (version $($release.tag_name))"
