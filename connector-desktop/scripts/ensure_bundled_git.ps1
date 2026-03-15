param(
    [string]$TargetDir = "",
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    throw "TargetDir is required"
}

$targetBin = Join-Path $TargetDir 'bin/git.exe'
$targetCmd = Join-Path $TargetDir 'cmd/git.exe'
if (-not $Force -and ((Test-Path $targetBin) -or (Test-Path $targetCmd))) {
    Write-Host "Bundled git already exists at $TargetDir"
    exit 0
}

if (Test-Path $TargetDir) {
    Remove-Item -Path $TargetDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

$apiUrl = 'https://api.github.com/repos/git-for-windows/git/releases/latest'
Write-Host "Resolving latest MinGit release from $apiUrl"
$release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'StructuraConnectorBuild' } -TimeoutSec 30

$asset = $release.assets |
    Where-Object { $_.name -match '^MinGit-.*-64-bit\.zip$' } |
    Select-Object -First 1

if (-not $asset) {
    throw "Could not find MinGit 64-bit asset in latest git-for-windows release"
}

$zipPath = Join-Path ([System.IO.Path]::GetTempPath()) ("mingit_" + [Guid]::NewGuid().ToString('N') + ".zip")
Write-Host "Downloading $($asset.name)"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -TimeoutSec 120

try {
    Expand-Archive -Path $zipPath -DestinationPath $TargetDir -Force
}
finally {
    if (Test-Path $zipPath) {
        Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue
    }
}

if (-not ((Test-Path $targetBin) -or (Test-Path $targetCmd))) {
    throw "Bundled git extracted, but git.exe not found in bin/cmd"
}

Write-Host "Bundled git prepared at $TargetDir"
