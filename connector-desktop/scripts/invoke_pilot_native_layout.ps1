[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Layout,
    [Parameter(Mandatory)][string]$Log
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Native layout recovery is restricted to the hosted CI runner.'
}
# Burn hands off to a clean-room engine. Start-Process -Wait waits for the process
# tree; keeping this supervisor alive prevents the guard from killing that handoff.
$arguments = @('/layout', ('"' + $Layout + '"'), '/quiet', '/norestart', '/log', ('"' + $Log + '"'))
$process = Start-Process -FilePath $Executable -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if (Test-Path -LiteralPath $Log) { Get-Content -LiteralPath $Log | Select-Object -Last 35 | Write-Host }
exit $process.ExitCode
