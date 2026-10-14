# Repackage the existing signed pilot components; never compile the desktop client.
[CmdletBinding()]
param()
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
    'Connector.Unified.Setup.msi' = 'ab139abdd3783b6d18c30ad370916ca5dfaa8a9f45725439f6940635ecf246de'
    'Connector.Upgrade.LaunchBridge.exe' = 'f59635eccea98dd93ea14bb55687fcb9248886f3ed444fad633fc8ac81cb51eb'
    'platform-build-guard.cs' = '99f6d32467f46042a367fad6383d088432c54a9e181e72a92d397a314a1a4c09'
}
foreach ($item in @(
    @{Run=38557543241; Sha='d13c709f0e639a053d5609b800907fa7cf8db174'; Name='unified-connector-installer-1.1.0'; Directory='source'; Hash='48e991842b1ed7ed08b6b6bb631dd6049dbc18c0ba3220e53ba75e9c0785cca6'},
    @{Run=38548514207; Sha='91de098ef9f50905275be25ab2955baeb3c17d74'; Name='verified-unified-installer'; Directory='previous'; Hash='44e4f750463b6fc5b48352ffa0f4b0fe3be5730012dcb5ae5b89a008af325cf7'}
)) {
    $run = gh api "repos/$env:GITHUB_REPOSITORY/actions/runs/$($item.Run)" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $run.conclusion -cne 'success' -or $run.head_sha -cne $item.Sha) { throw 'Verified input run identity differs.' }
    $directory = Join-Path $inputs $item.Directory
    gh run download $item.Run --repo $env:GITHUB_REPOSITORY --name $item.Name --dir $directory
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath (Join-Path $directory 'Structura.Connector.Installer.exe')).Hash.ToLowerInvariant() -cne $item.Hash) {
        throw 'Verified installer input changed.'
    }
}
$guard = Join-Path $PSScriptRoot 'resource-guard/platform-build-guard.cs'
if ((Get-FileHash -LiteralPath $guard).Hash.ToLowerInvariant() -cne $pins['platform-build-guard.cs']) { throw 'Build guard changed.' }
Add-Type -Path $guard
function Invoke-HostedGuard([string]$Executable, [string[]]$Arguments, [string]$Label) {
    # This reserve applies to the disposable CI machine, never to the user's PC.
    $result = [PlatformBuild.Guard]::RunWithPolicy($Executable, $Arguments, $env:GITHUB_WORKSPACE,
        $Label, 0, $null, [uint64](2GB), [uint64](2GB), 100, [PlatformBuild.Guard]::MutexName,
        [Func[PlatformBuild.Guard+Memory]]{ [PlatformBuild.Guard]::ReadMemory() })
    if ($result -ne 0) { throw "$Label failed under resource protection (exit $result); no automatic retry." }
}
Invoke-HostedGuard (Get-Command dotnet).Source @('restore',
    'connector-desktop/Connector.Unified.Bundle/Connector.Unified.Bundle.wixproj', '-m:1') 'pilot-wix-restore'
$sdk = Join-Path $env:USERPROFILE '.nuget/packages/wixtoolset.sdk/6.0.2'
$wix = @(Get-ChildItem -LiteralPath $sdk -Recurse -File -Filter wix.dll)
if ($wix.Count -ne 1) { throw 'Expected the exact WiX 6.0.2 CLI.' }
foreach ($kind in @('source','previous')) {
    Invoke-HostedGuard (Get-Command dotnet).Source @($wix[0].FullName, 'burn', 'extract',
        (Join-Path $inputs "$kind/Structura.Connector.Installer.exe"),
        '-o', (Join-Path $inputs "$kind/extracted"), '-oba', (Join-Path $inputs "$kind/ba")) "pilot-$kind-input-extraction"
}
$sourcePayloads = Join-Path $inputs 'source/extracted/WixAttachedContainer'
$previousMsi = Join-Path $inputs 'previous/extracted/WixAttachedContainer/Connector.Unified.Setup.msi'
foreach ($name in @('Connector.Unified.Setup.msi','Connector.Upgrade.LaunchBridge.exe')) {
    if ((Get-FileHash -LiteralPath (Join-Path $sourcePayloads $name)).Hash.ToLowerInvariant() -cne $pins[$name]) { throw "Input payload changed: $name" }
}
$stagingOutput = Join-Path $root 'staging-output'
$stagingWorker = Join-Path $PSScriptRoot 'repack_pilot_staging_msi.ps1'
Invoke-HostedGuard (Get-Command pwsh).Source @('-NoProfile', '-File', $stagingWorker,
    '-InputMsi', (Join-Path $sourcePayloads 'Connector.Unified.Setup.msi'), '-PreviousMsi', $previousMsi,
    '-Output', $stagingOutput) 'pilot-staging-repack'
$stagingProof = Get-Content -LiteralPath (Join-Path $stagingOutput 'staging-proof.json') -Raw | ConvertFrom-Json
if (-not $stagingProof.sameVersionUpgradeVerified -or -not $stagingProof.runtimePayloadsUnchanged -or
    -not $stagingProof.installedRuntimePayloadsVerified -or -not $stagingProof.nativeCallerStarted) {
    throw 'The existing pilot staging must upgrade without changing runtime bytes.'
}
$finalMsi = Join-Path $stagingOutput 'Connector.Unified.Setup.msi'
$buildArgs = @('build', 'connector-desktop/Connector.Unified.Bundle/Connector.Unified.Bundle.wixproj',
    '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:BuildInParallel=false',
    '-p:ProductVersion=1.1.0', "-p:UnifiedSetupMsiPath=$finalMsi",
    "-p:LaunchBridgePath=$(Join-Path $sourcePayloads 'Connector.Upgrade.LaunchBridge.exe')", '-o', $output, '--nologo')
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
if ((Get-FileHash -LiteralPath (Join-Path $ba 'platform_connector.ico')).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $env:GITHUB_WORKSPACE 'connector-desktop/Connector.Desktop/Assets/platform_connector.ico')).Hash) {
    throw 'Window icon payload differs.'
}
Invoke-HostedGuard (Get-Command pwsh).Source @('-NoProfile','-File',
    (Join-Path $PSScriptRoot 'invoke_pilot_native_failure_fixture.ps1'),
    '-PreviousInstaller', (Join-Path $inputs 'previous/Structura.Connector.Installer.exe'),
    '-CandidateInstaller', $exe, '-CandidateProductCode', $stagingProof.productCode,
    '-ExpectedBootstrapperDirectory', (Join-Path $stagingOutput $stagingProof.fixtureBootstrapperDirectory),
    '-ExpectedHelperDirectory', (Join-Path $stagingOutput $stagingProof.fixtureHelperDirectory),
    '-ExpectedConfiguration', (Join-Path $stagingOutput 'connector-access.json'),
    '-Output', (Join-Path $root 'failure-fixture')) 'pilot-native-failure-recovery'
$bundleProof = Get-Content -LiteralPath (Join-Path $root 'failure-fixture/failure-proof.json') -Raw | ConvertFrom-Json
if (!$bundleProof.installedRuntimePayloadsVerified -or !$bundleProof.nativeCallerStarted -or
    !$bundleProof.deploymentConfigurationVerified) { throw 'Native EXE upgrade verification is incomplete.' }
[ordered]@{
    sourceSha = $env:GITHUB_SHA
    runtimeSourceSha = 'd13c709f0e639a053d5609b800907fa7cf8db174'
    sourceInstallerRun = 38557543241
    previousInstallerRun = 38548514207
    inputHashes = $pins
    staging = $stagingProof
    bundleUpgrade = $bundleProof
    installerSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    installerBytes = (Get-Item -LiteralPath $exe).Length
    wixStdbaInformationVerified = $true
    nativeLayoutVerified = $true
    embeddedPayloadsVerified = $true
    customThemeVerified = $true
    failedRelatedBundleRollbackDisabledVerified = $true
    installedFunctionalVerified = $false
    localUiVerified = $false
    guard = @{ location = 'github-hosted'; aggregateMemoryGiB = 2; reserveGiB = 2; cpuPercent = 100 }
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'repack-proof.json') -Encoding utf8
