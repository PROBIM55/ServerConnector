#Requires -Version 7.4
# Job "publish" of publish-connector-release.yml: job token with contents: write, no environment, no key.
# Re-verifies the signed description with the pinned key, makes sure the installer it names is the one in
# platform-connector-v<version> (uploading it from the candidate run on a first publication, never replacing it),
# then replaces the channel description: signature first, description last.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'connector_release_description.ps1')
. (Join-Path $PSScriptRoot 'connector_release_github.ps1')

function Invoke-Gh([string[]]$Arguments, [string]$What) {
    & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$What failed." }
}

function Assert-InstallerAsset([string]$Tag, $Installer) {
    $asset = Get-ConnectorGhAsset (Get-ConnectorGhRelease $Tag) $Installer.FileName
    if ($null -eq $asset -or $asset.Size -ne [long]$Installer.Size -or ($asset.Digest -and $asset.Digest -cne "sha256:$($Installer.Sha256)")) {
        Stop-ConnectorRelease 'INSTALLER_CHANGED' "$Tag/$($Installer.FileName) differs from the signed description."
    }
}

Assert-ConnectorReleaseRunner
$request = Get-ConnectorReleaseRequestFromEnvironment
$repository = $ConnectorReleaseRepository
# Outputs of job "sign" (base64); nothing else crosses from the job with the key.
try {
    $bytes = [Convert]::FromBase64String([string]$env:RELEASE_DESCRIPTION_BASE64)
    $signature = [Convert]::FromBase64String([string]$env:RELEASE_SIGNATURE_BASE64)
} catch { throw 'The outputs of job sign are not base64.' }
if ($bytes.Length -eq 0 -or $bytes.Length -gt $ConnectorReleaseMaximumDescriptionBytes -or $signature.Length -ne 64) { throw 'The outputs of job sign are not a description and its signature.' }
$work = Join-Path $env:RUNNER_TEMP ('release-publish-work-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$publicKey = Import-PinnedReleaseKey (Get-PinnedReleaseKeyPath)
try {
    if (-not (Test-ReleaseSignature -Bytes $bytes -Signature $signature -PublicKey $publicKey)) { throw 'The output of job sign is not signed by the pinned key.' }
    $description = Assert-OwnReleaseDescription -Bytes $bytes -Channel $request.Channel -Version $request.Version
    if ($description.Paused -ne $request.Paused -or $description.MinimumVersion -cne $request.MinimumVersion -or
        ($description.Notes -join "`n") -cne ($request.Notes -join "`n")) { throw 'The signed description differs from the inputs of this run.' }
    $installer = $description.Installer
    $channel = $description.Channel
    $version = $description.Version
    $versionTag = "platform-connector-v$version"
    $channelTag = "platform-connector-channel-$channel"

    # The channel may not have moved since the description was built (one concurrency group; a re-run is idempotent).
    try { $channelState = Read-ConnectorChannelState $channel $publicKey $work }
    catch {
        if (-not $_.Exception.Message.StartsWith('CHANNEL_DESCRIPTION_UNREADABLE:', [StringComparison]::Ordinal)) { throw }
        # Only exception: this very publication stopped between its two channel uploads and is resumed with
        # "Re-run failed jobs" — the channel already carries exactly our signature. Anything else stays a refusal.
        $partial = Get-ConnectorGhRelease $channelTag
        $partialSignature = Get-ConnectorGhAsset $partial 'connector-release.json.sig'
        if ($null -eq $partialSignature -or $partialSignature.Size -ne 64 -or
            [Convert]::ToHexString([IO.File]::ReadAllBytes((Save-ConnectorGhAsset $channelTag 'connector-release.json.sig' $work))) -cne [Convert]::ToHexString($signature)) { throw }
        Write-Warning "$channelTag carries the signature of this publication without its description; completing it."
        $channelState = [pscustomobject]@{ Release = $partial; Description = $null; Signature = $signature }
    }
    $channelRelease = $channelState.Release
    $current = $channelState.Description
    if ($null -ne $current -and [Convert]::ToHexString($current.Bytes) -cne [Convert]::ToHexString($bytes)) {
        if ($current.VersionValue -gt $description.VersionValue) { Stop-ConnectorRelease 'VERSION_DOWNGRADE' "Channel $channel already has $($current.Version)." }
        if ($current.PublishedAtUtc -ge $description.PublishedAtUtc) { Stop-ConnectorRelease 'PUBLISHED_NOT_NEWER' 'The channel changed after this description was built.' }
        if ($current.Version -ceq $version) { Assert-SameReleaseInstaller $current.Installer $installer "the current $channel description" }
    }

    # Installer of the version: uploaded once from the verified candidate, afterwards only compared.
    $versionRelease = Get-ConnectorGhRelease $versionTag
    foreach ($earlier in (Get-ConnectorVersionDescriptions $versionRelease $version $publicKey $work)) {
        Assert-SameReleaseInstaller $earlier.Installer $installer "the signed $($earlier.Channel) description"
    }
    $installerAsset = Get-ConnectorGhAsset $versionRelease $installer.FileName
    if ($null -eq $installerAsset) {
        if ($request.SourceRunId -ceq '0') { Stop-ConnectorRelease 'INSTALLER_MISSING' "$versionTag has no installer; its first publication needs source_run_id." }
        $candidate = Get-ConnectorCandidate $request.SourceRunId $version $work
        Assert-SameReleaseInstaller $installer $candidate "the candidate of run $($request.SourceRunId)"
        if ($null -eq $versionRelease) {
            Assert-ConnectorReleaseTag $versionTag
            $notesPath = Join-Path $work 'version-notes.md'
            [IO.File]::WriteAllText($notesPath, (($description.Notes -join "`n") + "`n`nSHA-256 установщика: $($installer.Sha256)`n"), [Text.UTF8Encoding]::new($false))
            Invoke-Gh @('release', 'create', $versionTag, '--repo', $repository, '--target', $env:GITHUB_SHA, '--prerelease', '--latest=false',
                '--title', "Платформа Коннектор $version", '--notes-file', $notesPath) "Creating $versionTag"
        }
        $uploads = @($candidate.Path)
        if ($null -eq (Get-ConnectorGhAsset (Get-ConnectorGhRelease $versionTag) 'release.json')) { $uploads += $candidate.ManifestPath }
        Invoke-Gh (@('release', 'upload', $versionTag) + $uploads + @('--repo', $repository)) "Uploading the installer to $versionTag"
        Assert-InstallerAsset $versionTag $installer
    } else {
        if ($installerAsset.Size -ne [long]$installer.Size -or ($installerAsset.Digest -and $installerAsset.Digest -cne "sha256:$($installer.Sha256)")) {
            Stop-ConnectorRelease 'INSTALLER_CHANGED' "$versionTag/$($installer.FileName) differs from the signed description; nothing is replaced."
        }
        $downloaded = Save-ConnectorGhAsset $versionTag $installer.FileName $work
        if ((Get-Item -LiteralPath $downloaded).Length -ne [long]$installer.Size -or
            (Get-FileHash -LiteralPath $downloaded -Algorithm SHA256).Hash.ToLowerInvariant() -cne $installer.Sha256) {
            Stop-ConnectorRelease 'INSTALLER_CHANGED' "$versionTag/$($installer.FileName) differs from the signed description; nothing is replaced."
        }
        Remove-Item -LiteralPath $downloaded -Force
    }

    # Signed description of this channel kept with the version (source of later transfers, pauses and resumes).
    $staging = Join-Path $work 'staging'
    [void][IO.Directory]::CreateDirectory($staging)
    $versionJson = Join-Path $staging "connector-release.$channel.json"
    [IO.File]::WriteAllBytes($versionJson, $bytes)
    [IO.File]::WriteAllBytes("$versionJson.sig", $signature)
    # --clobber deletes the old asset before uploading the new one: for a moment the file is missing (404) — harmless
    # here, the server never reads the version release. A failed run is completed with "Re-run failed jobs".
    Invoke-Gh @('release', 'upload', $versionTag, "$versionJson.sig", '--repo', $repository, '--clobber') "Uploading the $channel signature to $versionTag"
    Invoke-Gh @('release', 'upload', $versionTag, $versionJson, '--repo', $repository, '--clobber') "Uploading the $channel description to $versionTag"

    # Channel: created once; the installer is already public, so the signature goes first and the description last.
    if ($null -eq $channelRelease) {
        Assert-ConnectorReleaseTag $channelTag
        $channelNotesPath = Join-Path $work 'channel-notes.md'
        [IO.File]::WriteAllText($channelNotesPath, "Подписанное описание текущего выпуска канала $channel (схема 2). Файлы заменяет только publish-connector-release.yml.`n", [Text.UTF8Encoding]::new($false))
        Invoke-Gh @('release', 'create', $channelTag, '--repo', $repository, '--target', $env:GITHUB_SHA, '--prerelease', '--latest=false',
            '--title', "Платформа Коннектор: канал $channel", '--notes-file', $channelNotesPath) "Creating $channelTag"
    }
    $channelDirectory = Join-Path $work 'channel'
    [void][IO.Directory]::CreateDirectory($channelDirectory)
    [IO.File]::WriteAllBytes((Join-Path $channelDirectory 'connector-release.json'), $bytes)
    [IO.File]::WriteAllBytes((Join-Path $channelDirectory 'connector-release.json.sig'), $signature)
    # --clobber deletes before it uploads: for seconds the channel serves 404, then the new signature with the old
    # description, then 404 for the description. The server must treat such a pair as invalid and keep what it has;
    # a run that fails here is completed with "Re-run failed jobs" (see the exception above).
    Invoke-Gh @('release', 'upload', $channelTag, (Join-Path $channelDirectory 'connector-release.json.sig'), '--repo', $repository, '--clobber') "Uploading the signature to $channelTag"
    Invoke-Gh @('release', 'upload', $channelTag, (Join-Path $channelDirectory 'connector-release.json'), '--repo', $repository, '--clobber') "Uploading the description to $channelTag"

    # What the server will fetch is exactly what was signed.
    $published = (Read-ConnectorChannelState $channel $publicKey $work).Description
    if ($null -eq $published -or [Convert]::ToHexString($published.Bytes) -cne [Convert]::ToHexString($bytes)) { throw "$channelTag does not serve the signed description." }
    Assert-InstallerAsset $versionTag $installer

    $base = "https://github.com/$repository/releases/download"
    $summary = @(
        "### Платформа Коннектор $version → $channel",
        '',
        "| | |", "|---|---|",
        "| Канал | ``$channel`` |",
        "| Версия | ``$version`` (минимальная ``$($description.MinimumVersion)``) |",
        "| Пауза | ``$($description.Paused.ToString().ToLowerInvariant())`` |",
        "| publishedAtUtc | ``$($description.PublishedAtUtc.UtcDateTime.ToString($ConnectorReleaseUtcFormat, [Globalization.CultureInfo]::InvariantCulture))`` |",
        "| SHA-256 установщика | ``$($installer.Sha256)`` ($($installer.Size) байт) |",
        "| Описание | $base/$channelTag/connector-release.json |",
        "| Подпись | $base/$channelTag/connector-release.json.sig |",
        "| Установщик | $base/$versionTag/$($installer.FileName) |")
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8 }
    $summary | ForEach-Object { Write-Host $_ }
} finally {
    $publicKey.Dispose()
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
