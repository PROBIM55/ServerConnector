param(
    [Parameter(Mandatory = $true)]
    [string]$TargetDir,
    [Parameter(Mandatory = $true)]
    [string]$IfcWorkerPath,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$IfcWorkerSha256,
    [Parameter(Mandatory = $true)]
    [string]$GltfpackPath
)

$ErrorActionPreference = 'Stop'

$acceptedEnginesPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'workers\accepted-engines.json'

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (-not (Test-Path -LiteralPath $acceptedEnginesPath -PathType Leaf)) {
    throw "Accepted engines manifest was not found: $acceptedEnginesPath"
}

try {
    $acceptedEngines = Get-Content -LiteralPath $acceptedEnginesPath -Raw | ConvertFrom-Json
}
catch {
    throw "Accepted engines manifest is not valid JSON: $acceptedEnginesPath"
}

if ($acceptedEngines.schemaVersion -ne 1 -or
    [string]::IsNullOrWhiteSpace($acceptedEngines.ifcOptimizer.version) -or
    [string]::IsNullOrWhiteSpace($acceptedEngines.ifcOptimizer.protocol) -or
    $acceptedEngines.ifcOptimizer.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
    [string]::IsNullOrWhiteSpace($acceptedEngines.gltfpack.version) -or
    $acceptedEngines.gltfpack.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw "Accepted engines manifest has an unsupported schema: $acceptedEnginesPath"
}

$acceptedIfcSha256 = $acceptedEngines.ifcOptimizer.sha256.ToLowerInvariant()
$acceptedGltfpackSha256 = $acceptedEngines.gltfpack.sha256.ToLowerInvariant()

foreach ($entry in @(
    @{ Name = 'TargetDir'; Path = $TargetDir; Directory = $true },
    @{ Name = 'IfcWorkerPath'; Path = $IfcWorkerPath; Directory = $false },
    @{ Name = 'GltfpackPath'; Path = $GltfpackPath; Directory = $false }
)) {
    if ($entry.Directory) {
        if (-not (Test-Path -LiteralPath $entry.Path -PathType Container)) { throw "$($entry.Name) directory was not found: $($entry.Path)" }
    }
    elseif (-not (Test-Path -LiteralPath $entry.Path -PathType Leaf)) { throw "$($entry.Name) file was not found: $($entry.Path)" }
}

$actualIfcSha256 = Get-Sha256 $IfcWorkerPath
if ($actualIfcSha256 -ne $acceptedIfcSha256) {
    throw "IFC worker SHA-256 mismatch: expected accepted $acceptedIfcSha256, got $actualIfcSha256"
}
if ($IfcWorkerSha256 -and $IfcWorkerSha256.ToLowerInvariant() -ne $acceptedIfcSha256) {
    throw "IfcWorkerSha256 does not match accepted engines manifest: expected $acceptedIfcSha256, got $($IfcWorkerSha256.ToLowerInvariant())"
}

$actualGltfpackSha256 = Get-Sha256 $GltfpackPath
if ($actualGltfpackSha256 -ne $acceptedGltfpackSha256) {
    throw "gltfpack.exe SHA-256 mismatch: expected accepted $acceptedGltfpackSha256, got $actualGltfpackSha256"
}

$ifcTargetDir = Join-Path $TargetDir 'workers\ifc-optimizer'
New-Item -ItemType Directory -Path $ifcTargetDir -Force | Out-Null
$ifcTarget = Join-Path $ifcTargetDir 'structura-ifc-optimizer.exe'
$gltfpackTarget = Join-Path $TargetDir 'gltfpack.exe'
Copy-Item -LiteralPath $IfcWorkerPath -Destination $ifcTarget -Force
Copy-Item -LiteralPath $GltfpackPath -Destination $gltfpackTarget -Force

if ((Get-Sha256 $ifcTarget) -ne $acceptedIfcSha256) { throw 'Copied IFC worker hash did not match the accepted artifact.' }
if ((Get-Sha256 $gltfpackTarget) -ne $acceptedGltfpackSha256) { throw 'Copied gltfpack hash did not match the accepted artifact.' }

[ordered]@{
    schemaVersion = 1
    ifcOptimizer = [ordered]@{
        path = $acceptedEngines.ifcOptimizer.path
        version = $acceptedEngines.ifcOptimizer.version
        protocol = $acceptedEngines.ifcOptimizer.protocol
        sha256 = $acceptedIfcSha256
    }
    gltfpack = [ordered]@{
        path = $acceptedEngines.gltfpack.path
        version = $acceptedEngines.gltfpack.version
        sha256 = $acceptedGltfpackSha256
    }
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $TargetDir 'workers\engine-manifest.json') -Encoding utf8

Write-Host "Bundled verified private engines into $TargetDir"
