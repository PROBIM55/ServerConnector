param(
    [ValidatePattern('^https://')]
    [string]$FeedUrl,
    [switch]$LocalOnly,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string]$PackVersion,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]*$')]
    [string]$Channel,
    [Parameter(Mandatory = $true)]
    [string]$IfcWorkerPath,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$IfcWorkerSha256,
    [ValidatePattern('^Structura\.Connector\.UpdateSmoke$')]
    [string]$TestAppId,
    [switch]$SmokeProbe,
    [string]$PreviousReleasesDir,
    [string]$UpdateSigningKeyPemPath,
    [string]$PackageOutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Connector.Desktop\Connector.Desktop.csproj'
$buildId = [Guid]::NewGuid().ToString('N')
$publishDir = Join-Path $root (Join-Path 'artifacts\package-update-build' $buildId)
$buildArtifactsDir = Join-Path $root (Join-Path 'artifacts\package-update-obj' $buildId)
$outputDir = if ($PackageOutputDir) { [IO.Path]::GetFullPath($PackageOutputDir) } else {
    Join-Path $root (Join-Path (Join-Path 'artifacts\package-update' $Channel) ([Guid]::NewGuid().ToString('N')))
}
$toolPath = Join-Path $root '.tools\vpk-1.2.161'
$vpk = Join-Path $toolPath 'vpk.exe'
$feedUri = if ($FeedUrl) { [Uri]$FeedUrl } else { $null }
$bundledGltfpackDir = Join-Path $root 'Connector.Desktop\tools\gltfpack'
$ensureGltfpackScript = Join-Path $PSScriptRoot 'ensure_bundled_gltfpack.ps1'
$bundleEnginesScript = Join-Path $PSScriptRoot 'bundle_connector_engines.ps1'
$checkNativeImportsScript = Join-Path $PSScriptRoot 'check_native_imports.ps1'
$appId = if ($TestAppId) { $TestAppId } else { 'Structura.Connector.Desktop' }
$mainExe = 'Connector.Desktop.exe'

if (-not $feedUri -and -not $LocalOnly) {
    throw 'Specify a published HTTPS FeedUrl, or LocalOnly for an unpublished local acceptance package.'
}
if ($LocalOnly -and $feedUri) { throw 'LocalOnly cannot be combined with FeedUrl.' }
if ($feedUri -and -not $UpdateSigningKeyPemPath) {
    throw 'A published update feed requires UpdateSigningKeyPemPath for the signed package manifest.'
}
if (Test-Path -LiteralPath $outputDir) {
    if (-not (Test-Path -LiteralPath $outputDir -PathType Container) -or
        @(Get-ChildItem -LiteralPath $outputDir -Force).Count -ne 0) {
        throw 'PackageOutputDir must be a fresh empty directory.'
    }
}
if ($feedUri -and (-not $feedUri.IsAbsoluteUri -or $feedUri.Scheme -ne 'https' -or $feedUri.UserInfo)) {
    throw 'FeedUrl must use HTTPS without embedded credentials.'
}

& (Join-Path $PSScriptRoot 'verify_source_boundaries.ps1')
& $ensureGltfpackScript -TargetDir $bundledGltfpackDir
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

if (-not (Test-Path $vpk)) {
    dotnet tool install vpk --tool-path $toolPath --version 1.2.161
    if ($LASTEXITCODE -ne 0) { throw 'Unable to install pinned vpk 1.2.161.' }
}

dotnet publish $project -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true "-p:Version=$PackVersion" `
    --artifacts-path $buildArtifactsDir -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if ($SmokeProbe) {
    if (-not $TestAppId) { throw 'SmokeProbe requires TestAppId.' }
    dotnet publish (Join-Path $PSScriptRoot 'installed-update-probe\UpdateSmokeProbe.csproj') -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true "-p:Version=$PackVersion" -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Smoke probe publish failed.' }
    $mainExe = 'UpdateSmokeProbe.exe'
}

$bundleEnginesArguments = @{
    TargetDir = $publishDir
    IfcWorkerPath = $IfcWorkerPath
    GltfpackPath = Join-Path $bundledGltfpackDir 'gltfpack.exe'
}
if ($IfcWorkerSha256) { $bundleEnginesArguments.IfcWorkerSha256 = $IfcWorkerSha256 }
& $bundleEnginesScript @bundleEnginesArguments
& (Join-Path $PSScriptRoot 'copy_vc_runtime.ps1') -TargetDir $publishDir

# Match the MSI gate: an update package without the AGR managed/native set is incomplete.
foreach ($name in @('Connector.AgrConversion.dll', 'Assimp64.dll', 'Silk.NET.Assimp.dll', 'Silk.NET.Core.dll',
                    'SharpGLTF.Core.dll', 'SharpGLTF.Toolkit.dll', 'THIRD_PARTY_NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $name))) {
        throw "AGR converter file $name was not published to $publishDir"
    }
}
& $checkNativeImportsScript -Dir $publishDir -Files @('Assimp64.dll', 'gltfpack.exe')

@{ feedUrl = $(if ($feedUri) { $feedUri.AbsoluteUri.TrimEnd('/') } else { '' }); channel = $Channel } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDir 'connector-update.json') -Encoding utf8

if ($PreviousReleasesDir) {
    if (-not (Test-Path $PreviousReleasesDir)) { throw "Previous releases directory was not found: $PreviousReleasesDir" }
    Get-ChildItem -LiteralPath $PreviousReleasesDir -File |
        Where-Object { $_.Name -notmatch '^connector-release\..+\.json(\.sig)?$' } |
        Copy-Item -Destination $outputDir -Force
}

$packTitle = if ($TestAppId) { 'Structura Connector Update Smoke' } else { 'Structura Connector' }
$shortcutLocations = if ($TestAppId) { 'None' } else { 'Desktop,StartMenuRoot' }
& $vpk pack --packId $appId --packTitle $packTitle --packVersion $PackVersion `
    --shortcuts $shortcutLocations `
    --packDir $publishDir --mainExe $mainExe --runtime 'win-x64' `
    --icon (Join-Path $root 'Connector.Desktop\Assets\structura_connector.ico') --channel $Channel --outputDir $outputDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

if ($UpdateSigningKeyPemPath) {
    & (Join-Path $PSScriptRoot 'sign_update_feed.ps1') `
        -PackageDirectory $outputDir -Channel $Channel -PrivateKeyPemPath $UpdateSigningKeyPemPath
    if ($LASTEXITCODE -ne 0) { throw 'Signing the update feed failed.' }
}

Get-ChildItem -LiteralPath $outputDir -File | Where-Object { $_.Name -match '^(releases\.|assets\.|Structura\.Connector\.Desktop-)' } |
    Select-Object FullName, Length, LastWriteTime
@{ schemaVersion = 1; applicationId = $appId; version = $PackVersion; channel = $Channel; feedPublished = -not $LocalOnly;
   packTitle = $packTitle; shortcuts = $shortcutLocations;
   publishDirectory = $publishDir; packageDirectory = $outputDir } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDir 'local-package.json') -Encoding utf8
Write-Host "Local application directory: $publishDir"
if ($LocalOnly) { Write-Host 'Package feed is unpublished; managed update checks fail closed until a feed is configured.' }
