#Requires -Version 7.4
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]*$')][string]$Channel,
    [Parameter(Mandatory = $true)][string]$PrivateKeyPemPath
)

$ErrorActionPreference = 'Stop'
$applicationId = 'Structura.Connector.Desktop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$embeddedPublicKeyPath = Join-Path $repoRoot 'Connector.Desktop\Assets\update-public-key.pem'
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$keyPath = [IO.Path]::GetFullPath($PrivateKeyPemPath)
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($keyPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Private signing key must be stored outside the repository.'
}
if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) { throw 'Private signing key file was not found.' }
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { throw 'Velopack package directory was not found.' }

$manifestName = "connector-release.$Channel.json"
$manifestPath = Join-Path $packageRoot $manifestName
$signaturePath = $manifestPath + '.sig'
if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $signaturePath)) {
    throw 'Refusing to overwrite an existing signed release manifest.'
}

$files = @(Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.nupkg' | Sort-Object Name)
if ($files.Count -eq 0) { throw 'Velopack package directory contains no .nupkg assets.' }
$catalogPath = Join-Path $packageRoot "releases.$Channel.json"
$indexPath = Join-Path $packageRoot "RELEASES-$Channel"
if (!(Test-Path -LiteralPath $catalogPath -PathType Leaf) -or !(Test-Path -LiteralPath $indexPath -PathType Leaf)) {
    throw 'Native Velopack releases catalog and RELEASES index are required to establish canonical package identity.'
}
$catalogDocument = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new([IO.File]::ReadAllBytes($catalogPath)))
try {
    $catalogAssets = @($catalogDocument.RootElement.GetProperty('Assets').EnumerateArray() | ForEach-Object { $_.Clone() })
} finally { $catalogDocument.Dispose() }
if ($catalogAssets.Count -ne $files.Count) { throw 'Native catalog asset count differs from package directory.' }
$assets = [Collections.Generic.List[object]]::new()
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    if (-not $names.Add($file.Name) -or $file.Name.Contains('/') -or $file.Name.Contains('\')) { throw 'Duplicate or unsafe package filename.' }
    $catalogMatches = @($catalogAssets | Where-Object { $_.GetProperty('FileName').GetString() -ceq $file.Name })
    if ($catalogMatches.Count -ne 1) { throw "Package is not uniquely listed in native catalog: $($file.Name)" }
    $entry = $catalogMatches[0]
    $version = $entry.GetProperty('Version').GetString()
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Invalid catalog package version: $version" }
    $match = [regex]::Match($file.Name, '^Structura\.Connector\.Desktop-' + [regex]::Escape($version) + '-' + [regex]::Escape($Channel) + '-(?<type>full|delta(?:\.\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)?)\.nupkg$', 'CultureInvariant')
    if (-not $match.Success) { throw "Unexpected Velopack package filename for catalog version/channel: $($file.Name)" }
    $assetType = if ($match.Groups['type'].Value.StartsWith('delta', [StringComparison]::OrdinalIgnoreCase)) { 'Delta' } else { 'Full' }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($entry.GetProperty('PackageId').GetString() -cne $applicationId -or
        $entry.GetProperty('Type').GetString() -cne $assetType -or
        $entry.GetProperty('SHA256').GetString() -ine $hash -or
        $entry.GetProperty('Size').GetInt64() -ne $file.Length) { throw "Native catalog mismatch: $($file.Name)" }
    $assets.Add([ordered]@{ fileName = $file.Name; version = $version; type = $assetType; size = [long]$file.Length; sha256 = $hash })
}
$indexLines = [IO.File]::ReadAllLines($indexPath)
if ($indexLines.Count -ne $assets.Count) { throw 'RELEASES index asset count differs from signed assets.' }
$indexedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in $indexLines) {
    $parts = $line -split ' '
    if ($parts.Count -ne 3 -or !$indexedNames.Add($parts[1])) { throw 'RELEASES index has a malformed or duplicate asset entry.' }
    $match = @($assets | Where-Object { $_.fileName -ceq $parts[1] })
    if ($parts.Count -ne 3 -or $match.Count -ne 1 -or $parts[0] -ine (Get-FileHash -LiteralPath (Join-Path $packageRoot $parts[1]) -Algorithm SHA1).Hash -or $parts[2] -cne [string]$match[0].size) {
        throw 'RELEASES index SHA1/size does not match exact package assets.'
    }
}

$manifest = [ordered]@{ schemaVersion = 1; applicationId = $applicationId; channel = $Channel; assets = @($assets.ToArray()) }
$json = ConvertTo-Json -InputObject $manifest -Depth 5 -Compress
$encoding = [Text.UTF8Encoding]::new($false)
$manifestBytes = $encoding.GetBytes($json)
$key = [Security.Cryptography.ECDsa]::Create()
$temporaryManifest = Join-Path $packageRoot ('.' + [Guid]::NewGuid().ToString('N') + '.manifest.tmp')
$temporarySignature = Join-Path $packageRoot ('.' + [Guid]::NewGuid().ToString('N') + '.signature.tmp')
$publishedSignature = $false
try {
    $key.ImportFromPem([IO.File]::ReadAllText($keyPath))
    if ($key.KeySize -ne 256) { throw 'Update manifest signing requires a P-256 private key.' }
    $signature = $key.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if ($signature.Length -ne 64) { throw 'P-256 signature has an unexpected length.' }
    if (-not (Test-Path -LiteralPath $embeddedPublicKeyPath -PathType Leaf)) { throw 'Embedded update public key is missing.' }
    $verifier = [Security.Cryptography.ECDsa]::Create()
    try {
        $verifier.ImportFromPem([IO.File]::ReadAllText($embeddedPublicKeyPath))
        if ($verifier.KeySize -ne 256 -or -not $verifier.VerifyData($manifestBytes, $signature,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
            throw 'Signing key does not match the embedded Connector.Desktop update public key.'
        }
    }
    finally { $verifier.Dispose() }
    [IO.File]::WriteAllBytes($temporaryManifest, $manifestBytes)
    [IO.File]::WriteAllBytes($temporarySignature, $signature)
    if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $signaturePath)) {
        throw 'Signed release manifest appeared concurrently; refusing to overwrite it.'
    }
    [IO.File]::Move($temporarySignature, $signaturePath)
    $publishedSignature = $true
    [IO.File]::Move($temporaryManifest, $manifestPath)
}
catch {
    if ($publishedSignature -and -not (Test-Path -LiteralPath $manifestPath)) { Remove-Item -LiteralPath $signaturePath -Force }
    throw
}
finally {
    $key.Dispose()
    foreach ($temporary in @($temporaryManifest, $temporarySignature)) {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

Write-Host "Signed update feed manifest: $manifestPath"
