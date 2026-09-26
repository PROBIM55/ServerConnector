param(
    [string]$TargetDir = "",
    [switch]$Force
)

# Downloads the official gltfpack (zeux/meshoptimizer v1.2, gltfpack-windows.zip) and extracts
# gltfpack.exe into $TargetDir for bundling into the connector. Mirrors scripts/ensure_bundled_amneziawg.ps1:
# pinned version, SHA-256 of the zip and of the exe checked before use, version marker for re-runs.
#
# The AGR model converter (Connector.AgrConversion, Converter tab) cannot work without gltfpack, so
# build_msi.ps1 treats any failure here as a build error. build_msi.ps1 copies gltfpack.exe next to
# Connector.Desktop.exe: AgrGltfpackPartConverter.DefaultGltfpackPath = AppContext.BaseDirectory\gltfpack.exe.

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    throw "TargetDir is required"
}

$version = '1.2'
$assetName = 'gltfpack-windows.zip'
$assetUrl = "https://github.com/zeux/meshoptimizer/releases/download/v$version/$assetName"
$expectedZipSha256 = '52e0c061d8b42f1c6bd8fe1cbc1e26a9da579ad5a4f5dd30a8ee0d599062f6c4'
$expectedExeSha256 = 'ff64f45e84aac9a1f58880e40934b3f29277413e2d0b3ed257322261ec021d2b'

$targetExe = Join-Path $TargetDir 'gltfpack.exe'
$markerPath = Join-Path $TargetDir '.structura-bundle-version'
$expectedMarker = "$assetName v$version sha256:$expectedZipSha256 exe-sha256:$expectedExeSha256"

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (-not $Force -and
    (Test-Path $targetExe) -and
    (Test-Path $markerPath) -and
    ((Get-Content -LiteralPath $markerPath -Raw).Trim() -eq $expectedMarker) -and
    ((Get-Sha256 $targetExe) -eq $expectedExeSha256)) {
    Write-Host "Bundled gltfpack already exists at $TargetDir"
    exit 0
}

if (Test-Path $TargetDir) {
    Remove-Item -Path $TargetDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) ("gltfpack_" + [Guid]::NewGuid().ToString('N') + ".zip")
$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gltfpack_extract_" + [Guid]::NewGuid().ToString('N'))
try {
    Write-Host "Downloading pinned $assetName (meshoptimizer v$version)"
    Invoke-WebRequest -Uri $assetUrl -OutFile $zipPath -TimeoutSec 180

    $actualZipSha256 = Get-Sha256 $zipPath
    if ($actualZipSha256 -ne $expectedZipSha256) {
        throw "gltfpack zip SHA-256 mismatch: expected $expectedZipSha256, got $actualZipSha256"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractDir)

    $found = Get-ChildItem -Path $extractDir -Recurse -Filter 'gltfpack.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) {
        throw "gltfpack zip unpacked, but gltfpack.exe was not found under $extractDir"
    }

    $actualExeSha256 = Get-Sha256 $found.FullName
    if ($actualExeSha256 -ne $expectedExeSha256) {
        throw "gltfpack.exe SHA-256 mismatch: expected $expectedExeSha256, got $actualExeSha256"
    }

    Copy-Item -Path $found.FullName -Destination $targetExe -Force
}
catch {
    if (Test-Path $TargetDir) { Remove-Item -Path $TargetDir -Recurse -Force -ErrorAction SilentlyContinue }
    throw
}
finally {
    if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue }
    if (Test-Path $extractDir) { Remove-Item -Path $extractDir -Recurse -Force -ErrorAction SilentlyContinue }
}

if (-not (Test-Path $targetExe)) {
    throw "gltfpack bundling failed: $targetExe not present"
}

$expectedMarker | Set-Content -LiteralPath $markerPath -Encoding ASCII
Write-Host "Bundled gltfpack prepared at $TargetDir (meshoptimizer v$version)"
