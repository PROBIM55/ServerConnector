[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$IfcWorkerPath,
    [Parameter(Mandatory)][string]$NetBirdMsiPath,
    [Parameter(Mandatory)][string]$WebView2InstallerPath,
    [Parameter(Mandatory)][string]$ConnectorAccessConfigurationPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This release is built only on disposable GitHub-hosted Windows.'
}
$desktopRoot=Split-Path -Parent $PSScriptRoot
$repo=Split-Path -Parent $desktopRoot
$out=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $out) { throw 'OutputDirectory must be new.' }
New-Item -ItemType Directory -Path $out | Out-Null
$configuration=Get-Content -LiteralPath $ConnectorAccessConfigurationPath -Raw | ConvertFrom-Json
$origin=[Uri]$configuration.serviceBaseUri
if (!$configuration.enabled -or $origin.Scheme -cne 'https' -or $origin.UserInfo -or $origin.Query -or $origin.Fragment -or
    $origin.AbsolutePath -cne '/' -or $configuration.issuerCertificateSha256 -cnotmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid protected access configuration.' }
if ((Get-Item -LiteralPath $NetBirdMsiPath).Length -ne 23543808 -or
    (Get-FileHash -LiteralPath $NetBirdMsiPath).Hash -cne '50F822C0F5F6E54E7618096CDD36B63BF4B169E125C9DA06052FD4FD96115210') { throw 'NetBird package differs from the accepted identity.' }
$webSignature=Get-AuthenticodeSignature -LiteralPath $WebView2InstallerPath
if ($webSignature.Status -ne 'Valid' -or $webSignature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)' -or
    (Get-Item -LiteralPath $WebView2InstallerPath).Length -lt 50MB) { throw 'Microsoft WebView2 signature is invalid.' }
$assetInputs=@(
    @('scripts/netbird_windows_preflight.ps1','B549546E97D6EE68B415DAD4789FCF19D979939CA49211B3F0DCCCD4E56BC0BB'),
    @('scripts/verify_netbird_installer.ps1','DFFFA2EC3BBDFB57583F66AB651F12259AFFFBCF39AA014B11411F9C152EA834'))
foreach ($asset in $assetInputs) {
    if ((Get-FileHash -LiteralPath (Join-Path $desktopRoot $asset[0])).Hash -cne $asset[1]) { throw 'Protected NetBird source changed.' }
}
$lock=Join-Path $desktopRoot 'infra/netbird/v0.79.0-windows-x64.lock.json'
if ((Get-FileHash -LiteralPath $lock).Hash -cne 'EE48F195FAE2A08B83C0A44F238C237C79F05F27BCA8DBC995542B1FCD23FA96') { throw 'NetBird lock changed.' }
$imageOutput=Join-Path $out 'image'
& (Join-Path $PSScriptRoot 'build_package_update.ps1') -LocalOnly -PublishOnly -PackVersion $Version -Channel stable -IfcWorkerPath $IfcWorkerPath -IfcWorkerSha256 (Get-Content (Join-Path $desktopRoot 'workers/accepted-engines.json') -Raw | ConvertFrom-Json).ifcOptimizer.sha256 -PackageOutputDir $imageOutput
$image=Get-Content -LiteralPath (Join-Path $imageOutput 'desktop-image.json') -Raw | ConvertFrom-Json
$publish=[string]$image.publishDirectory
@{schemaVersion=1;installation='machine-msi';version=$Version;legacyImport=$false} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $publish 'connector-install.json') -Encoding utf8
$config=Join-Path $out 'connector-access.json'
Copy-Item -LiteralPath $ConnectorAccessConfigurationPath -Destination $config
function Invoke-Build([string[]]$Arguments) {
    & dotnet @Arguments -m:1 -nodeReuse:false -p:BuildInParallel=false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Cloud installer build failed.' }
}
$msiDir=Join-Path $out 'msi'
Invoke-Build -Arguments @('build',(Join-Path $desktopRoot 'Connector.Clean.Setup/Connector.Clean.Setup.wixproj'),'-c','Release','-o',$msiDir,
    "-p:ProductVersion=$Version","-p:DesktopPublishDir=$publish","-p:ConnectorAccessConfigurationPath=$config",
    "-p:NetBirdMsiPath=$NetBirdMsiPath","-p:NetBirdPreflightScriptPath=$(Join-Path $PSScriptRoot 'netbird_windows_preflight.ps1')",
    "-p:NetBirdVerifierScriptPath=$(Join-Path $PSScriptRoot 'verify_netbird_installer.ps1')","-p:NetBirdIdentityLockPath=$lock")
$msis=@(Get-ChildItem -LiteralPath $msiDir -File -Filter '*.msi')
if ($msis.Count -ne 1) { throw 'Expected exactly one fresh application MSI.' }
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$installer.OpenDatabase($NetBirdMsiPath,0)
$view=$database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''')
$view.Execute(); $upgrade=$view.Fetch().StringData(1); $view.Close()
if ($upgrade -notmatch '^\{[A-Fa-f0-9-]{36}\}$') { throw 'Invalid NetBird MSI UpgradeCode.' }
$bundleDir=Join-Path $out 'bundle'
Invoke-Build -Arguments @('build',(Join-Path $desktopRoot 'Connector.Clean.Bundle/Connector.Clean.Bundle.wixproj'),'-c','Release','-o',$bundleDir,
    "-p:ProductVersion=$Version","-p:SetupMsi=$($msis[0].FullName)","-p:NetBirdMsi=$NetBirdMsiPath",
    "-p:NetBirdUpgradeCode=$upgrade","-p:WebView2Installer=$WebView2InstallerPath")
$exe=Join-Path $bundleDir 'PlatformConnector.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Final EXE missing.' }
$release=Join-Path $out 'release'
New-Item -ItemType Directory -Path $release | Out-Null
$final=Join-Path $release "PlatformConnector-$Version.exe"
Copy-Item -LiteralPath $exe -Destination $final
if ((Get-AuthenticodeSignature -LiteralPath $final).Status -ne 'NotSigned' -or
    (Get-AuthenticodeSignature -LiteralPath $msis[0].FullName).Status -ne 'NotSigned') { throw 'Unsigned candidate contains an unexpected signature state.' }
@{schemaVersion=1;version=$Version;sourceSha=$env:GITHUB_SHA;sourceRun=$env:GITHUB_RUN_ID;
    fileName=[IO.Path]::GetFileName($final);bytes=(Get-Item $final).Length;sha256=(Get-FileHash $final).Hash.ToLowerInvariant();
    artifactKind='unsigned-cloud-candidate';authenticodeStatus=[string](Get-AuthenticodeSignature -LiteralPath $final).Status;
    freshInstallation=$true;legacyPayloads=$false;legacyImport=$false;blenderIncluded=$false;
    netBirdSha256=(Get-FileHash $NetBirdMsiPath).Hash.ToLowerInvariant();webView2Sha256=(Get-FileHash $WebView2InstallerPath).Hash.ToLowerInvariant()} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'release.json') -Encoding utf8
Write-Host "CLEAN_RELEASE_EXE=$final"
