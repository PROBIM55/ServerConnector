$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted'){throw 'Run this process fixture only on a disposable hosted worker'}
. (Join-Path $PSScriptRoot 'cloud-build-supervision.ps1')
$fixture=Join-Path $env:RUNNER_TEMP ('cloud-supervision-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
$parent=$null
try {
    $childFile=Join-Path $fixture 'child.ps1'
    '$PID|Set-Content -LiteralPath (Join-Path $PSScriptRoot "child.pid"); Start-Sleep -Seconds 300'|Set-Content $childFile -Encoding utf8
    $parentFile=Join-Path $fixture 'parent.ps1'
    @'
$file=[IO.File]::Open((Join-Path $PSScriptRoot 'private-build.log'),[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try {
    Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile','-NonInteractive','-File',('"'+(Join-Path $PSScriptRoot 'child.ps1')+'"')) -WindowStyle Hidden|Out-Null
    Start-Sleep -Seconds 300
} finally {$file.Dispose()}
'@|Set-Content $parentFile -Encoding utf8
    $parent=Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile','-NonInteractive','-File',('"'+$parentFile+'"')) -WindowStyle Hidden -PassThru
    $pidFile=Join-Path $fixture 'child.pid'
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    $childId=0
    while(!$childId) {
        if([DateTime]::UtcNow -gt $deadline){throw 'Fixture child did not start'}
        if(Test-Path -LiteralPath $pidFile){[void][int]::TryParse([string](Get-Content -LiteralPath $pidFile -Raw),[ref]$childId)}
        if(!$childId){Start-Sleep -Milliseconds 100}
    }
    $lockedLog=Join-Path $fixture 'private-build.log'
    $writeRejected=$false
    try {[IO.File]::AppendAllText($lockedLog,'must-not-write')}catch [IO.IOException] {$writeRejected=$true}
    if(!$writeRejected){throw 'Fixture did not reproduce the active builder log lock'}
    Stop-PlatformCloudBuilderTree -Process $parent -TerminationLog (Join-Path $fixture 'termination.log')
    if(Get-Process -Id $childId -ErrorAction SilentlyContinue){throw 'A child process survived tree termination'}
    if((Get-Item -LiteralPath $lockedLog).Length -ne 0){throw 'Supervisor wrote to the builder-owned log'}
    Write-Output 'CLOUD_BUILD_SUPERVISION_OK: parent and child terminated with the build log locked'
} finally {
    if($parent){try {Stop-PlatformCloudBuilderTree -Process $parent -TerminationLog (Join-Path $fixture 'cleanup.log')}finally{$parent.Dispose()}}
    $resolved=[IO.Path]::GetFullPath($fixture)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'cloud-supervision-*'){throw 'Fixture cleanup path differs'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
