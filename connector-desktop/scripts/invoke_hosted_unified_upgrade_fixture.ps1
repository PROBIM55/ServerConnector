[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$ExpectedConfiguration
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Upgrade verification requires disposable hosted Windows.'
}
$output=Join-Path $RunDirectory 'native-upgrade-fixture'
$previous=Join-Path $output 'previous'
$source=gh api "repos/$env:GITHUB_REPOSITORY/actions/runs/38548514207" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $source.conclusion -cne 'success' -or
    $source.head_sha -cne '91de098ef9f50905275be25ab2955baeb3c17d74') { throw 'Previous fixture identity differs.' }
gh run download 38548514207 --repo $env:GITHUB_REPOSITORY --name verified-unified-installer --dir $previous
if ($LASTEXITCODE -ne 0) { throw 'Previous verified fixture unavailable.' }
$previousExe=Join-Path $previous 'Structura.Connector.Installer.exe'
if ((Get-FileHash -LiteralPath $previousExe).Hash -cne '44E4F750463B6FC5B48352FFA0F4B0FE3BE5730012DCB5AE5B89A008AF325CF7') {
    throw 'Previous fixture bytes differ.'
}
$msi=Join-Path $RunDirectory 'unified-setup/Connector.Unified.Setup.msi'
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$installer.OpenDatabase($msi,0)
$view=$database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductCode''')
[void]$view.Execute()
$productCode=$view.Fetch().StringData(1)
[void]$view.Close()
$probeOutput = Join-Path $RunDirectory 'standalone-install-probe'
& dotnet publish (Join-Path $PSScriptRoot '../installer/Connector.Upgrade.StandaloneInstallProbe/Connector.Upgrade.StandaloneInstallProbe.csproj') `
    --configuration Release --runtime win-x64 --self-contained true -o $probeOutput `
    --artifacts-path (Join-Path $RunDirectory 'standalone-install-probe-artifacts') `
    -p:UseSharedCompilation=false -p:BuildInParallel=false -p:UnifiedReleaseBuild=true `
    "-p:HelperReleasePublicKeyFile=$([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../installer/release-public-key.pem')))"
if ($LASTEXITCODE -ne 0) { throw 'Native install probe build failed.' }
& (Join-Path $PSScriptRoot 'invoke_pilot_native_failure_fixture.ps1') -PreviousInstaller $previousExe `
    -CandidateInstaller (Join-Path $RunDirectory 'unified-installer/Structura.Connector.Installer.exe') `
    -CandidateProductCode $productCode -ExpectedBootstrapperDirectory (Join-Path $RunDirectory 'bootstrapper') `
    -ExpectedHelperDirectory (Join-Path $RunDirectory 'machine-helper') `
    -ExpectedConfiguration $ExpectedConfiguration -Output $output `
    -ProductionPreflightProbeProject (Join-Path $PSScriptRoot '../installer/Connector.Upgrade.DeploymentProbe/Connector.Upgrade.DeploymentProbe.csproj') `
    -StandaloneInstallProbeExecutable (Join-Path $probeOutput 'Connector.Upgrade.StandaloneInstallProbe.exe')
if (!$?) { throw 'Native upgrade fixture failed.' }
$proof=Get-Content -LiteralPath (Join-Path $output 'failure-proof.json') -Raw | ConvertFrom-Json
if (!$proof.installedRuntimePayloadsVerified -or !$proof.nativeCallerStarted -or
    !$proof.deploymentConfigurationVerified -or !$proof.relatedBundleRollbackNotInvoked -or !$proof.productionPreflightVerified -or
    !$proof.defaultInstallationVerified) {
    throw 'Native upgrade evidence is incomplete.'
}
$proof | Add-Member -MemberType NoteProperty -Name sourceSha -Value $env:GITHUB_SHA
$proof | Add-Member -MemberType NoteProperty -Name productCode -Value $productCode
$proof | Add-Member -MemberType NoteProperty -Name installerSha256 -Value (
    (Get-FileHash -LiteralPath (Join-Path $RunDirectory 'unified-installer/Structura.Connector.Installer.exe')).Hash.ToLowerInvariant())
$proof | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunDirectory 'native-upgrade-proof.json') -Encoding utf8
