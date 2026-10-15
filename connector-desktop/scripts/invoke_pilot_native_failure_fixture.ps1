[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousInstaller,
    [Parameter(Mandatory)][string]$CandidateInstaller,
    [Parameter(Mandatory)][ValidatePattern('^\{[A-Fa-f0-9-]{36}\}$')][string]$CandidateProductCode,
    [Parameter(Mandatory)][string]$ExpectedBootstrapperDirectory,
    [Parameter(Mandatory)][string]$ExpectedHelperDirectory,
    [Parameter(Mandatory)][string]$ExpectedConfiguration,
    [string]$ProductionPreflightProbeProject,
    [string]$StandaloneInstallProbeExecutable,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'This intentionally failing install fixture requires disposable hosted Windows.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (![Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The fixture must exercise the bridge rejection of an elevated original token.'
    }
} finally { $identity.Dispose() }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
function Invoke-FixtureBundle([string]$Path, [string]$Action, [string]$Log) {
    $arguments = @(('/' + $Action), '/quiet', '/norestart', '/log', ('"' + $Log + '"'))
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        if (!$process.WaitForExit(180000)) { $process.Kill($true); throw 'Native bundle fixture timed out.' }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Registration([string]$Code) { return 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $Code }
$previousProduct = '{5490E9F5-4C12-4797-A497-28D4EB309718}'
try {
    $previousLog = Join-Path $Output 'previous-failure.log'
    $previousExit = Invoke-FixtureBundle $PreviousInstaller 'install' $previousLog
    if ($previousExit -ne 1 -or !(Test-Path -LiteralPath (Registration $previousProduct))) {
        throw 'Previous failed bundle fixture did not leave its committed machine stage.'
    }
    $previousSetup = Join-Path $Output 'previous-desktop-Setup.exe'
    if ($StandaloneInstallProbeExecutable) {
        Copy-Item -LiteralPath (Join-Path $env:ProgramData 'StructuraConnectorInstaller/Helper/Setup.exe') -Destination $previousSetup
    }
    $candidateLog = Join-Path $Output 'candidate-failure.log'
    $candidateExit = Invoke-FixtureBundle $CandidateInstaller 'install' $candidateLog
    if ($candidateExit -ne 1 -or !(Test-Path -LiteralPath (Registration $CandidateProductCode)) -or
        (Test-Path -LiteralPath (Registration $previousProduct))) {
        throw 'A user-stage failure did not retain the upgraded machine stage.'
    }
    $log = [IO.File]::ReadAllText($candidateLog)
    if ($log.Contains('Applying rollback package:') -or
        !$log.Contains('OriginalUserUpgradeBridge exited with code: 0x1')) {
        throw 'Native failure recovery retried a related bundle or did not exercise the intended bridge rejection.'
    }
    # Elevated bridge rejection happens before the installed caller starts. Verify
    # the complete runtime after the EXE chain, not just the standalone MSI test.
    $installedCaller = "$env:ProgramFiles/StructuraConnectorInstaller/Bootstrapper/Connector.Upgrade.Bootstrapper.exe"
    $installedCount = 0
    foreach ($pair in @(
        @{Source=$ExpectedBootstrapperDirectory; Target=(Split-Path $installedCaller -Parent)},
        @{Source=$ExpectedHelperDirectory; Target=(Join-Path $env:ProgramData 'StructuraConnectorInstaller/Helper')}
    )) {
        foreach ($file in Get-ChildItem -LiteralPath $pair.Source -Recurse -File) {
            $target = Join-Path $pair.Target ([IO.Path]::GetRelativePath($pair.Source, $file.FullName))
            if (!(Test-Path -LiteralPath $target -PathType Leaf) -or
                (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Bundle upgrade omitted or changed runtime file: $($file.Name)"
            }
            $installedCount++
        }
    }
    $installedConfiguration = "$env:ProgramFiles/Structura Connector/NetBird/connector-access.json"
    if ((Get-FileHash -LiteralPath $installedConfiguration).Hash -ne
        (Get-FileHash -LiteralPath $ExpectedConfiguration).Hash) { throw 'Bundle installed a different API configuration.' }
    $callerProbe = Start-Process -FilePath $installedCaller -PassThru -WindowStyle Hidden
    try {
        if ($callerProbe.WaitForExit(5000)) { throw "Caller failed after bundle upgrade: $($callerProbe.ExitCode)" }
    } finally {
        if (!$callerProbe.HasExited) { $callerProbe.Kill($true); [void]$callerProbe.WaitForExit(5000) }
        $callerProbe.Dispose()
    }
    $productionPreflightVerified = $false
    if ($ProductionPreflightProbeProject) {
        & dotnet run --project $ProductionPreflightProbeProject --configuration Release `
            -p:UseSharedCompilation=false -p:BuildInParallel=false
        if ($LASTEXITCODE -ne 0) { throw 'Actual production deployment preflight failed.' }
        $productionPreflightVerified = $true
    }
    $defaultInstallationVerified = $false
    if ($StandaloneInstallProbeExecutable) {
        & (Join-Path $PSScriptRoot 'invoke_hosted_token_free_install_fixture.ps1') -ProbeExecutable $StandaloneInstallProbeExecutable `
            -PreviousSetup $previousSetup -Output (Join-Path $Output 'token-free-install-proof.json')
        if (!$?) { throw 'Default no-token installation verification failed.' }
        $defaultInstallationVerified = $true
    }
    [ordered]@{expectedBridgeFailure=1;machineStageRetained=$true;relatedBundleRollbackNotInvoked=$true;
        installedRuntimePayloadsVerified=$true;installedRuntimePayloadCount=$installedCount;nativeCallerStarted=$true;
        deploymentConfigurationVerified=$true;productionPreflightVerified=$productionPreflightVerified;
        defaultInstallationVerified=$defaultInstallationVerified;netBirdFirstLoginVerified=$defaultInstallationVerified} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'failure-proof.json') -Encoding utf8
} finally {
    foreach ($path in @($CandidateInstaller,$PreviousInstaller)) {
        $cleanupLog = Join-Path $Output (([Guid]::NewGuid().ToString('N')) + '-cleanup.log')
        $cleanupExit = Invoke-FixtureBundle $path 'uninstall' $cleanupLog
        if ($cleanupExit -notin @(0,3010,1605)) { throw "Fixture bundle cleanup failed: $cleanupExit" }
    }
    foreach ($code in @($CandidateProductCode,$previousProduct)) {
        if (Test-Path -LiteralPath (Registration $code)) {
            $process = Start-Process -FilePath "$env:WINDIR/System32/msiexec.exe" -ArgumentList @('/x',$code,'/qn','/norestart','REBOOT=ReallySuppress') -Wait -PassThru -WindowStyle Hidden
            if ($process.ExitCode -notin @(0,3010)) { throw 'Fixture MSI cleanup failed.' }
        }
    }
}
