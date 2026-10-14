[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackVersion,
    [Parameter(Mandatory)][string]$InstallerVersion,
    [Parameter(Mandatory)][string]$Channel,
    [Parameter(Mandatory)][string]$FeedUrl,
    [Parameter(Mandatory)][string]$IfcWorkerPath,
    [Parameter(Mandatory)][string]$UpdateSigningKeyPemPath,
    [Parameter(Mandatory)][string]$HelperManifestSigningKeyPemPath,
    [Parameter(Mandatory)][string]$HelperReleasePublicKeyFile,
    [Parameter(Mandatory)][string]$ConnectorAccessConfigurationPath,
    [Parameter(Mandatory)][string]$ProtectedOutputRoot,
    [Parameter(Mandatory)][string]$PinnedAssetBundleDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This profile is restricted to disposable hosted Windows CI; workstation reserves must not be lowered.'
}
$guard = Join-Path $PSScriptRoot 'resource-guard/platform-build-guard.cs'
if ((Get-FileHash -LiteralPath $guard).Hash.ToLowerInvariant() -ne '99f6d32467f46042a367fad6383d088432c54a9e181e72a92d397a314a1a4c09') {
    throw 'The verified process-tree build guard changed.'
}
Add-Type -Path $guard
function Invoke-GuardedHostedProcess([string[]]$Arguments, [string]$Label) {
    # The 2 GiB reserve applies only to this isolated hosted VM. Local policy keeps 6 GiB.
    $result = [PlatformBuild.Guard]::RunWithPolicy((Get-Command pwsh).Source, $Arguments,
        $env:GITHUB_WORKSPACE, $Label, 0, $null, [uint64](2GB), [uint64](2GB), 100,
        [PlatformBuild.Guard]::MutexName,
        [Func[PlatformBuild.Guard+Memory]]{ [PlatformBuild.Guard]::ReadMemory() })
    if ($result -ne 0) { throw "$Label failed under resource protection (exit $result); no automatic retry." }
}
$arguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'build_unified_release.ps1'))
foreach ($entry in $PSBoundParameters.GetEnumerator()) {
    $arguments += @(('-' + $entry.Key), [string]$entry.Value)
}
Invoke-GuardedHostedProcess $arguments 'unified-candidate-build'
$runs = @(Get-ChildItem -LiteralPath $ProtectedOutputRoot -Directory -Filter "unified-$PackVersion-*")
if ($runs.Count -ne 1) { throw 'Expected exactly one fresh candidate output directory.' }
$installer = Join-Path $runs[0].FullName 'unified-installer/Structura.Connector.Installer.exe'
$layoutArguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'invoke_pilot_native_layout.ps1'),
    '-Executable', $installer, '-Layout', (Join-Path $runs[0].FullName 'native-layout'),
    '-Log', (Join-Path $runs[0].FullName 'native-layout.log'))
Invoke-GuardedHostedProcess $layoutArguments 'unified-native-layout'
$upgradeArguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'invoke_hosted_unified_upgrade_fixture.ps1'),
    '-RunDirectory', $runs[0].FullName, '-ExpectedConfiguration', $ConnectorAccessConfigurationPath)
Invoke-GuardedHostedProcess $upgradeArguments 'unified-native-upgrade'
