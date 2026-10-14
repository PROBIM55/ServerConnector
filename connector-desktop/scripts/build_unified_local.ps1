#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackVersion,
    [Parameter(Mandatory)][string]$InstallerVersion,
    [Parameter(Mandatory)][string]$Channel,
    [Parameter(Mandatory)][string]$FeedUrl,
    [Parameter(Mandatory)][string]$IfcWorkerPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$HostedSigningRef,
    [Parameter(Mandatory)][string]$HelperReleasePublicKeyFile,
    [Parameter(Mandatory)][string]$ConnectorAccessConfigurationPath,
    [Parameter(Mandatory)][string]$ProtectedOutputRoot,
    [string]$PinnedAssetBundleDirectory = (Join-Path $PSScriptRoot '..\artifacts\migration-helper-bundle-20261003'),
    [Parameter(Mandatory)][string]$SharedGuardSourcePath,
    [string]$CancelFile,
    [string]$VcRuntimeSourceDir,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$builder = Join-Path $PSScriptRoot 'build_unified_release.ps1'
$guardPin = '99F6D32467F46042A367FAD6383D088432C54A9E181E72A92D397A314A1A4C09'
$guardFile = Get-Item -LiteralPath $SharedGuardSourcePath -ErrorAction Stop
if ($guardFile.PSIsContainer -or ($guardFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Shared guard must be a regular file.'
}
# Compile exactly the bytes whose hash was verified; do not reopen a mutable path.
$guardBytes = [IO.File]::ReadAllBytes($guardFile.FullName)
if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($guardBytes)) -cne $guardPin) {
    throw 'Shared build controller differs from the coordinated version.'
}
Push-Location -LiteralPath $repository
try {
    $branch = (& git branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne 'main') { throw 'Use the primary main checkout for the local build.' }
    $head = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $HostedSigningRef) { throw 'Hosted signing ref must match the local build source.' }
    $remote = (& git ls-remote origin refs/heads/master) -split '\s+'
    if ($LASTEXITCODE -ne 0 -or $remote[0] -cne $HostedSigningRef) { throw 'Push the reviewed build source to origin/master before signing.' }
} finally { Pop-Location }
. (Join-Path $PSScriptRoot 'connector_local_build_policy.ps1')
$os = Get-CimInstance Win32_OperatingSystem
$policy = Get-ConnectorLocalBuildPolicy -AvailablePhysical ([ulong]$os.FreePhysicalMemory * 1024) `
    -AvailableCommit ([ulong]$os.FreeVirtualMemory * 1024)
if ($PlanOnly) {
    [pscustomobject]@{
        Source = $head; SharedGuardSha256 = $guardPin; Mutex = 'Global\Platform.HeavyBuild.v1'
        JobMemoryGiB = $policy.JobMemoryBytes / 1GB; CpuPercent = $policy.CpuPercent
        MinimumFreeGiB = $policy.MinimumFreeBytes / 1GB; EarlyStopFreeGiB = $policy.EarlyStopFreeBytes / 1GB
        FreeGiB = [math]::Round($os.FreePhysicalMemory / 1MB, 2); MemoryAdmits = $policy.CanStart
        QueueChecked = $false; ChildStarted = $false
        Steps = @('desktop publish', 'Velopack pack and signed feed', 'helper publish', 'bootstrapper publish',
                  'launch bridge publish', 'signed helper manifest', 'machine MSI', 'Burn installer')
    }
    return
}
if (-not $policy.CanStart) {
    Write-Host 'BUILD_GUARD admission-refused no-child-started; Connector needs 8.5 GiB free and 6 GiB available commit.'
    exit 75
}
if ('PlatformBuild.Guard' -as [type]) { throw 'Run this entry point in a fresh pwsh process.' }
Add-Type -TypeDefinition ([Text.Encoding]::UTF8.GetString($guardBytes))
if ([PlatformBuild.Guard]::MutexName -cne 'Global\Platform.HeavyBuild.v1') { throw 'Unexpected shared build mutex.' }
$memory = [PlatformBuild.Guard]::ReadMemory()
$policy = Get-ConnectorLocalBuildPolicy $memory.AvailablePhysical $memory.AvailableCommit
if (-not $policy.CanStart) {
    Write-Host 'BUILD_GUARD admission-refused-after-controller-load no-child-started.'
    exit 75
}
$arguments = [Collections.Generic.List[string]]::new()
foreach ($argument in @('-NoProfile', '-File', $builder)) { $arguments.Add($argument) }
foreach ($name in @('PackVersion','InstallerVersion','Channel','FeedUrl','IfcWorkerPath','HostedSigningRef',
                    'HelperReleasePublicKeyFile','ConnectorAccessConfigurationPath','ProtectedOutputRoot','PinnedAssetBundleDirectory')) {
    $arguments.Add('-' + $name)
    $arguments.Add([string](Get-Variable -Name $name -ValueOnly))
}
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
if ($VcRuntimeSourceDir) {
    $arguments.Add('-VcRuntimeSourceDir')
    $arguments.Add($VcRuntimeSourceDir)
}
$env:MSBUILDDISABLENODEREUSE = '1'
# One shared mutex/Job covers the existing sequential pipeline, including every
# descendant. A denied admission or memory stop never launches an automatic retry.
$exitCode = [PlatformBuild.Guard]::RunWithPolicy((Get-Process -Id $PID).Path,$arguments.ToArray(),$repository,
    'connector-unified-local',0,$CancelFile,[ulong]$policy.JobMemoryBytes,[ulong]$policy.ReserveBytes,
    [uint]$policy.CpuPercent,[PlatformBuild.Guard]::MutexName,
    [Func[PlatformBuild.Guard+Memory]]{ [PlatformBuild.Guard]::ReadMemory() })
exit $exitCode
