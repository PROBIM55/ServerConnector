# Repackage the existing signed pilot components; never compile the desktop client.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^pilot-packaging-20261014-[a-f0-9]{8}$')][string]$InputRelease)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This packaging recovery runs only on a hosted Windows runner; local memory policy must not be lowered.'
}
$root = Join-Path $env:GITHUB_WORKSPACE 'pilot-repack'
$inputs = Join-Path $root 'inputs'
$output = Join-Path $root 'output'
New-Item -ItemType Directory -Path $inputs,$output -Force | Out-Null
$pins = [ordered]@{
    'Connector.Unified.Setup.msi' = 'd8302ffe7c7176417c5a54d32830751804ccd9cf1952c6f41ff6e3b4e5898c2c'
    'Connector.Upgrade.LaunchBridge.exe' = 'ba1e0528b14b65229df00785859ff9683ffc8e978256d03ccd25c68eb1462a01'
    'platform-build-guard.cs' = '99f6d32467f46042a367fad6383d088432c54a9e181e72a92d397a314a1a4c09'
}
$release = gh release view $InputRelease --repo $env:GITHUB_REPOSITORY --json isDraft | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $release.isDraft) { throw 'Inputs must remain in an unpublished draft.' }
foreach ($name in $pins.Keys) {
    gh release download $InputRelease --repo $env:GITHUB_REPOSITORY --pattern $name --dir $inputs
    if ($LASTEXITCODE -ne 0) { throw "Input download failed: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $inputs $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pins[$name]) {
        throw "Verified pilot input changed: $name"
    }
}
Add-Type -Path (Join-Path $inputs 'platform-build-guard.cs')
function Invoke-HostedGuard([string]$Executable, [string[]]$Arguments, [string]$Label) {
    # This reserve applies to the disposable CI machine, never to the user's PC.
    $result = [PlatformBuild.Guard]::RunWithPolicy($Executable, $Arguments, $env:GITHUB_WORKSPACE,
        $Label, 0, $null, [uint64](2GB), [uint64](2GB), 25, [PlatformBuild.Guard]::MutexName,
        [Func[PlatformBuild.Guard+Memory]]{ [PlatformBuild.Guard]::ReadMemory() })
    if ($result -ne 0) { throw "$Label failed under resource protection (exit $result); no automatic retry." }
}
$stagingOutput = Join-Path $root 'staging-output'
$stagingWorker = Join-Path $PSScriptRoot 'repack_pilot_staging_msi.ps1'
Invoke-HostedGuard (Get-Command pwsh).Source @('-NoProfile', '-File', $stagingWorker,
    '-InputMsi', (Join-Path $inputs 'Connector.Unified.Setup.msi'), '-Output', $stagingOutput) 'pilot-staging-repack'
$stagingProof = Get-Content -LiteralPath (Join-Path $stagingOutput 'staging-proof.json') -Raw | ConvertFrom-Json
if (-not $stagingProof.sameVersionUpgradeVerified -or -not $stagingProof.runtimePayloadsUnchanged) {
    throw 'The existing pilot staging must upgrade without changing runtime bytes.'
}
$finalMsi = Join-Path $stagingOutput 'Connector.Unified.Setup.msi'
$buildArgs = @('build', 'connector-desktop/Connector.Unified.Bundle/Connector.Unified.Bundle.wixproj',
    '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:BuildInParallel=false',
    '-p:ProductVersion=1.1.0', "-p:UnifiedSetupMsiPath=$finalMsi",
    "-p:LaunchBridgePath=$(Join-Path $inputs 'Connector.Upgrade.LaunchBridge.exe')", '-o', $output, '--nologo')
Invoke-HostedGuard (Get-Command dotnet).Source $buildArgs 'pilot-burn-repack'
$pdb = Join-Path $output 'Structura.Connector.Installer.wixpdb'
$zip = [IO.Compression.ZipFile]::OpenRead($pdb)
try {
    $entry = $zip.GetEntry('wix-badata.xml')
    if ($null -eq $entry) { throw 'Bootstrapper application metadata missing.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $info = $metadata.SelectSingleNode('//*[local-name()="WixStdbaInformation"]')
    if ($null -eq $info -or $info.GetAttribute('LicenseUrl') -ne '') {
        throw 'WixStdbaInformation with empty LicenseUrl is required for the custom installer UI.'
    }
} finally { $zip.Dispose() }
$exe = Join-Path $output 'Structura.Connector.Installer.exe'
$layout = Join-Path $root 'native-layout'
$log = Join-Path $output 'native-layout.log'
# Burn layout loads the actual bootstrapper and extracts the chain without running any MSI/EXE package.
$layoutWorker = Join-Path $PSScriptRoot 'invoke_pilot_native_layout.ps1'
Invoke-HostedGuard (Get-Command pwsh).Source @('-NoProfile', '-File', $layoutWorker,
    '-Executable', $exe, '-Layout', $layout, '-Log', $log) 'pilot-native-layout'
$layoutExe = Join-Path $layout 'Structura.Connector.Installer.exe'
if ((Get-FileHash -LiteralPath $layoutExe -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash) {
    throw 'Native layout did not preserve the complete embedded bundle.'
}
# A compressed bundle lays out the EXE with its containers intact. Extract those
# containers separately to verify the MSI, bridge and custom UI byte for byte.
$sdk = Join-Path $env:USERPROFILE '.nuget/packages/wixtoolset.sdk/6.0.2'
$wix = @(Get-ChildItem -LiteralPath $sdk -Recurse -File -Filter wix.dll)
if ($wix.Count -ne 1) { throw 'Expected the exact WiX 6.0.2 CLI used by the bundle SDK.' }
$extracted = Join-Path $root 'extracted'
$ba = Join-Path $root 'bootstrapper-application'
Invoke-HostedGuard (Get-Command dotnet).Source @($wix[0].FullName, 'burn', 'extract', $exe,
    '-o', $extracted, '-oba', $ba) 'pilot-payload-extraction'
foreach ($name in @('Connector.Unified.Setup.msi','Connector.Upgrade.LaunchBridge.exe')) {
    $files = @(Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter $name)
    $files | Select-Object FullName,Length | Format-Table | Out-String | Write-Host
    $expected = if ($name -eq 'Connector.Unified.Setup.msi') { $stagingProof.sha256 } else { $pins[$name] }
    if ($files.Count -ne 1 -or (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw "Native layout did not preserve the verified payload: $name"
    }
}
foreach ($pair in @(@('thm.xml','InstallerTheme.xml'), @('thm.wxl','Installer.ru-ru.wxl'))) {
    $embedded = Join-Path $ba $pair[0]
    $source = Join-Path $env:GITHUB_WORKSPACE ("connector-desktop/Connector.Unified.Bundle/" + $pair[1])
    if ((Get-FileHash -LiteralPath $embedded).Hash -ne (Get-FileHash -LiteralPath $source).Hash) {
        throw "Custom installer UI payload changed: $($pair[0])"
    }
}
if (Select-String -LiteralPath $log -SimpleMatch 'manifest is missing wixstdba information') {
    throw 'The original native bootstrapper failure is still present.'
}
[ordered]@{
    sourceSha = $env:GITHUB_SHA
    runtimeSourceSha = '64760620a93365e3d0809dc7f84d59b51069b4a3'
    inputRelease = $InputRelease
    inputHashes = $pins
    staging = $stagingProof
    installerSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    installerBytes = (Get-Item -LiteralPath $exe).Length
    wixStdbaInformationVerified = $true
    nativeLayoutVerified = $true
    embeddedPayloadsVerified = $true
    customThemeVerified = $true
    installedFunctionalVerified = $false
    localUiVerified = $false
    guard = @{ location = 'github-hosted'; aggregateMemoryGiB = 2; reserveGiB = 2; cpuPercent = 25 }
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'repack-proof.json') -Encoding utf8
