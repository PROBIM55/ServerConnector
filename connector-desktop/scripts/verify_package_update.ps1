param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [ValidatePattern('^Structura\.Connector\.UpdateSmoke$')][string]$TestAppId
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$metadata = Get-Content -Raw -LiteralPath (Join-Path $packageRoot 'local-package.json') | ConvertFrom-Json
$applicationId = if ($TestAppId) { $TestAppId } else { 'Structura.Connector.Desktop' }
if ($metadata.applicationId -and $metadata.applicationId -ne $applicationId) {
    throw "Package application id mismatch: expected $applicationId, got $($metadata.applicationId)"
}
$pin = Get-Content -Raw -LiteralPath (Join-Path $root 'workers/accepted-engines.json') | ConvertFrom-Json
Add-Type -AssemblyName System.IO.Compression.FileSystem
$portable = Join-Path $packageRoot ($applicationId + '-' + $metadata.channel + '-Portable.zip')
$archive = [IO.Compression.ZipFile]::OpenRead($portable)
function Find-Entry([string]$RelativePath) {
    $entry = $archive.GetEntry('current/' + $RelativePath.Replace('\','/'))
    if (-not $entry) { throw "Missing portable file: $RelativePath" }
    return $entry
}
function Get-EntryHash([string]$RelativePath) {
    $entry = Find-Entry $RelativePath
    $stream = $entry.Open()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $hasher.Dispose() }
}
try {
    $engines = foreach ($engine in @($pin.ifcOptimizer, $pin.gltfpack)) {
        $actual = Get-EntryHash $engine.path
        if ($actual -ne $engine.sha256) { throw "Engine pin mismatch: $($engine.path)" }
        @{ path = $engine.path; version = $engine.version; sha256 = $actual }
    }
    $ui = foreach ($relative in @('desktop.html','desktop-bridge.js','app.js','review-pages.js','style.css','kit-tokens.js','icons.js','brand/structura-logo.png','brand/platform-field-refined-v3.png')) {
        $actual = Get-EntryHash ('ui/' + $relative)
        $expected = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $root ('design/' + $relative))).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "UI differs from accepted source: $relative" }
        @{ path = $relative; sha256 = $actual }
    }
    foreach ($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll','coreclr.dll','hostfxr.dll','System.Private.CoreLib.dll')) {
        $null = Find-Entry $name
    }
    $reader = [IO.StreamReader]::new((Find-Entry 'Connector.Desktop.runtimeconfig.json').Open())
    try { $runtime = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($runtime.runtimeOptions.includedFrameworks.Count -ne 2) { throw 'Package is not self-contained.' }
    $reader = [IO.BinaryReader]::new((Find-Entry 'Connector.Desktop.exe').Open())
    try {
        $bytes = $reader.ReadBytes(4096)
        $offset = [BitConverter]::ToInt32($bytes, 60)
        if ([BitConverter]::ToUInt16($bytes, $offset + 4) -ne 0x8664) { throw 'Desktop apphost is not x64.' }
    } finally { $reader.Dispose() }
    $files = Get-ChildItem -LiteralPath $packageRoot -File | Where-Object Extension -In '.exe','.zip','.nupkg' | ForEach-Object {
        @{ name = $_.Name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
@{ schemaVersion = 1; applicationId = $applicationId; version = $metadata.version; channel = $metadata.channel;
       feedPublished = $metadata.feedPublished; signed = $false; runtime = 'win-x64';
       applicationDirectory = $metadata.publishDirectory; files = @($files); engines = @($engines);
       ui = @($ui); includedFrameworks = $runtime.runtimeOptions.includedFrameworks; entryCount = $archive.Entries.Count } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'verified-package.json') -Encoding utf8
    Write-Host 'PASS: portable x64 apphost, pinned engines, accepted UI, branding, native CRT and self-contained frameworks.'
} finally { $archive.Dispose() }
