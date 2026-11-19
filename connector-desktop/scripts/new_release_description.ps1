#Requires -Version 7.4
# Release description of publish-connector-release.yml, computed with the job token and never with the key.
#   -Mode Plan   job "plan" (no environment): finds the installer and the channel state, applies every rule and shows
#                the owner what will be signed (GITHUB_STEP_SUMMARY, job outputs) before the signing job is approved.
#   -Mode Build  job "sign", step without the key: computes everything again by itself, refuses when it differs from
#                what "plan" showed (PLAN_* environment), and writes the exact bytes of connector-release.json.
# Installer identity: first publication — the cloud-tested candidate of source_run_id; later — an earlier signed
# description of the same version.
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Plan', 'Build')][string]$Mode, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'connector_release_description.ps1')
. (Join-Path $PSScriptRoot 'connector_release_github.ps1')

Assert-ConnectorReleaseRunner
$request = Get-ConnectorReleaseRequestFromEnvironment
if ($Mode -ceq 'Build' -and (-not $OutputDirectory -or (Test-Path -LiteralPath $OutputDirectory))) { throw 'A fresh description output directory is required.' }
$work = Join-Path $env:RUNNER_TEMP ('release-description-work-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$publicKey = Import-PinnedReleaseKey (Get-PinnedReleaseKeyPath)
try {
    $versionTag = "platform-connector-v$($request.Version)"
    $channelTag = "platform-connector-channel-$($request.Channel)"
    $versionRelease = Get-ConnectorGhRelease $versionTag
    $versionDescriptions = Get-ConnectorVersionDescriptions $versionRelease $request.Version $publicKey $work
    $channelState = Read-ConnectorChannelState $request.Channel $publicKey $work
    $current = $channelState.Description
    # Releases that the publish job will create must not bind to a tag outside the history of this commit.
    if ($null -eq $versionRelease) { Assert-ConnectorReleaseTag $versionTag }
    if ($null -eq $channelState.Release) { Assert-ConnectorReleaseTag $channelTag }
    $installerAsset = Get-ConnectorGhAsset $versionRelease "PlatformConnector-$($request.Version).exe"

    if ($request.SourceRunId -cne '0') {
        $candidate = Get-ConnectorCandidate $request.SourceRunId $request.Version $work
        $installer = [pscustomobject]@{ FileName = $candidate.FileName; Size = $candidate.Size; Sha256 = $candidate.Sha256 }
        $source = "run $($request.SourceRunId)"
    } else {
        if ($versionDescriptions.Count -eq 0) {
            Stop-ConnectorRelease 'VERSION_NOT_PUBLISHED' "Version $($request.Version) has no signed description yet; its first publication needs source_run_id."
        }
        if ($null -eq $installerAsset) { Stop-ConnectorRelease 'INSTALLER_MISSING' "$versionTag has no installer; its first publication needs source_run_id." }
        $installer = $versionDescriptions[0].Installer
        $source = "signed $($versionDescriptions[0].Channel) description of $versionTag"
    }

    $plan = New-ConnectorReleasePlan -Request $request -Installer $installer -Now ([DateTimeOffset]::UtcNow) -CurrentChannel $current `
        -VersionDescriptions $versionDescriptions -InstallerAsset $installerAsset
    $facts = Get-ReleasePlanFacts -Request $request -Installer $installer -Source $source -CurrentChannel $current

    if ($Mode -ceq 'Plan') {
        $lines = @("### План: Платформа Коннектор $($request.Version) → $($request.Channel)", '',
            'Проверьте перед одобрением окружения `release-signing`: задание `sign` подпишет ровно это и откажет, если что-то изменится.', '',
            '| | |', '|---|---|')
        foreach ($key in $facts.Keys) { $lines += "| $key | ``$($facts[$key])`` |" }
        if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $lines -Encoding utf8 }
        if ($env:GITHUB_OUTPUT) { Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value @($facts.Keys | ForEach-Object { "$_=$($facts[$_])" }) -Encoding utf8 }
        $lines | ForEach-Object { Write-Host $_ }
    } else {
        Assert-ReleasePlanMatch -Facts $facts -Planned (Get-ConnectorPlannedFacts)
        [void][IO.Directory]::CreateDirectory($OutputDirectory)
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'connector-release.json'), $plan.Bytes)
        foreach ($key in $facts.Keys) { Write-Host "$key = $($facts[$key])" }
    }
} finally {
    $publicKey.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
