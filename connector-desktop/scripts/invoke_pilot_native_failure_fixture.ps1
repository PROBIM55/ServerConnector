[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousInstaller,
    [Parameter(Mandatory)][string]$CandidateInstaller,
    [Parameter(Mandatory)][ValidatePattern('^\{[A-Fa-f0-9-]{36}\}$')][string]$CandidateProductCode,
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
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    return $process.ExitCode
}
function Registration([string]$Code) { return 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $Code }
$previousProduct = '{5490E9F5-4C12-4797-A497-28D4EB309718}'
try {
    $previousLog = Join-Path $Output 'previous-failure.log'
    $previousExit = Invoke-FixtureBundle $PreviousInstaller 'install' $previousLog
    if ($previousExit -ne 1 -or !(Test-Path -LiteralPath (Registration $previousProduct))) {
        throw 'Previous failed bundle fixture did not leave its committed machine stage.'
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
    [ordered]@{expectedBridgeFailure=1;machineStageRetained=$true;relatedBundleRollbackNotInvoked=$true} |
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
