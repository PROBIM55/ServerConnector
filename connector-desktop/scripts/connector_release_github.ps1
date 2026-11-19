#Requires -Version 7.4
# GitHub side of publish-connector-release.yml (gh CLI with the job token). Dot-sourced after
# connector_release_description.ps1 by new_release_description.ps1 and publish_connector_release.ps1.
# Never loaded by the signing step: that step has the key and no token.
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

# Hosted runner, manual dispatch, branch master of this repository only.
function Assert-ConnectorReleaseRunner {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') { throw 'Publication runs on a GitHub-hosted runner only.' }
    if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_REF -cne 'refs/heads/master' -or
        $env:GITHUB_REPOSITORY -cne $script:ConnectorReleaseRepository -or $env:GITHUB_SHA -cnotmatch '^[a-f0-9]{40}\z') {
        throw 'Publication is a manual run of branch master of PROBIM55/ServerConnector only.'
    }
}

function Get-ConnectorReleaseRequestFromEnvironment {
    ConvertTo-ConnectorReleaseRequest -Channel ([string]$env:RELEASE_CHANNEL) -Version ([string]$env:RELEASE_VERSION) `
        -SourceRunId ([string]$env:RELEASE_SOURCE_RUN_ID) -Paused ([string]$env:RELEASE_PAUSED) `
        -MinimumVersion ([string]$env:RELEASE_MINIMUM_VERSION) -Notes ([string]$env:RELEASE_NOTES)
}

function Invoke-ConnectorGhJson([Parameter(Mandatory)][string]$Path) {
    $text = (& gh api $Path 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "GitHub API request failed: $Path" }
    return $text | ConvertFrom-Json
}

# A published (non-draft) release by tag, or $null when there is none.
function Get-ConnectorGhRelease([Parameter(Mandatory)][string]$Tag) {
    $text = (& gh api "repos/$($script:ConnectorReleaseRepository)/releases/tags/$Tag" 2>$null) -join "`n"
    if ($LASTEXITCODE -eq 0) { return $text | ConvertFrom-Json }
    $body = $null
    try { $body = $text | ConvertFrom-Json } catch { $body = $null }
    if ($null -ne $body -and $body.PSObject.Properties['status'] -and [string]$body.status -ceq '404') { return $null }
    throw "Cannot read release $Tag."
}

function Get-ConnectorGhAsset($Release, [string]$Name) {
    if ($null -eq $Release) { return $null }
    $matching = @($Release.assets | Where-Object { $_.name -ceq $Name })
    if ($matching.Count -gt 1) { throw "Release $($Release.tag_name) has several assets named $Name." }
    if ($matching.Count -eq 0) { return $null }
    $digest = if ($matching[0].PSObject.Properties['digest'] -and $matching[0].digest) { [string]$matching[0].digest } else { $null }
    [pscustomobject]@{ Name = $Name; Size = [long]$matching[0].size; Digest = $digest }
}

function Save-ConnectorGhAsset([Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$WorkDirectory) {
    $directory = Join-Path $WorkDirectory ([guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($directory)
    & gh release download $Tag --repo $script:ConnectorReleaseRepository --pattern $Name --dir $directory
    if ($LASTEXITCODE -ne 0) { throw "Cannot download $Name of $Tag." }
    $path = Join-Path $directory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or @(Get-ChildItem -LiteralPath $directory -Force).Count -ne 1) { throw "Download of $Name of $Tag differs." }
    return $path
}

# A description of a version from platform-connector-v<version>. Only a valid signature of the pinned key counts: a pair
# that is missing, oversized or wrongly signed is ignored with a warning. A validly signed pair that breaks the rules
# (another channel, another version) stops the run: it is either our mistake or a genuine signed description replayed
# into the wrong place by someone with write access, and it has to be removed by hand.
function Read-ConnectorGhDescription($Release, [string]$Name, [string]$Channel, [Security.Cryptography.ECDsa]$PublicKey, [string]$WorkDirectory) {
    $json = Get-ConnectorGhAsset $Release $Name
    $signature = Get-ConnectorGhAsset $Release "$Name.sig"
    if ($null -eq $json -or $null -eq $signature) { return $null }
    if ($json.Size -le 0 -or $json.Size -gt $script:ConnectorReleaseMaximumDescriptionBytes -or $signature.Size -ne 64) {
        Write-Warning "$($Release.tag_name)/$Name is ignored: sizes differ from a signed description."
        return $null
    }
    $bytes = [IO.File]::ReadAllBytes((Save-ConnectorGhAsset $Release.tag_name $Name $WorkDirectory))
    $signatureBytes = [IO.File]::ReadAllBytes((Save-ConnectorGhAsset $Release.tag_name "$Name.sig" $WorkDirectory))
    if (-not (Test-ReleaseSignature -Bytes $bytes -Signature $signatureBytes -PublicKey $PublicKey)) {
        Write-Warning "$($Release.tag_name)/$Name is ignored: the signature is not valid for the pinned key."
        return $null
    }
    $description = ConvertFrom-ReleaseDescription -Bytes $bytes -Channel $Channel
    $description | Add-Member -NotePropertyName Bytes -NotePropertyValue $bytes
    return $description
}

# Current description of a channel: @{ Release; Description; Signature }. Description is $null only when the channel
# release does not exist (404); an existing release with an unreadable description stops the run
# (Resolve-ChannelDescription). Network and server errors stop the run too (Get-ConnectorGhRelease, Save-ConnectorGhAsset).
function Read-ConnectorChannelState([Parameter(Mandatory)][string]$Channel, [Security.Cryptography.ECDsa]$PublicKey, [string]$WorkDirectory) {
    $tag = "platform-connector-channel-$Channel"
    $release = Get-ConnectorGhRelease $tag
    $bytes = $null
    $signature = $null
    if ($null -ne $release) {
        $json = Get-ConnectorGhAsset $release 'connector-release.json'
        $sig = Get-ConnectorGhAsset $release 'connector-release.json.sig'
        if ($null -ne $json -and $json.Size -gt 0 -and $json.Size -le $script:ConnectorReleaseMaximumDescriptionBytes) {
            $bytes = [IO.File]::ReadAllBytes((Save-ConnectorGhAsset $tag 'connector-release.json' $WorkDirectory))
        }
        if ($null -ne $sig -and $sig.Size -eq 64) {
            $signature = [IO.File]::ReadAllBytes((Save-ConnectorGhAsset $tag 'connector-release.json.sig' $WorkDirectory))
        }
    }
    $description = Resolve-ChannelDescription -ReleaseExists ($null -ne $release) -Bytes $bytes -Signature $signature -Channel $Channel -PublicKey $PublicKey
    [pscustomobject]@{ Release = $release; Description = $description; Signature = $signature }
}

function Get-ConnectorVersionDescriptions($VersionRelease, [string]$Version, [Security.Cryptography.ECDsa]$PublicKey, [string]$WorkDirectory) {
    $found = @()
    foreach ($channel in $script:ConnectorReleaseChannels) {
        $description = Read-ConnectorGhDescription $VersionRelease "connector-release.$channel.json" $channel $PublicKey $WorkDirectory
        if ($null -eq $description) { continue }
        if ($description.Version -cne $Version) { Stop-ConnectorRelease 'VERSION_RELEASE_INCONSISTENT' "connector-release.$channel.json of $Version describes $($description.Version)." }
        $found += $description
    }
    return , $found
}

function Get-ConnectorBlobSha([string]$Path, [string]$Ref) {
    [string](Invoke-ConnectorGhJson "repos/$($script:ConnectorReleaseRepository)/contents/$($Path)?ref=$Ref").sha
}

# The successful candidate run, checked against this commit, and its verified installer. The artifact is taken by id
# (digest compared when GitHub reports one) and the run is read again afterwards: it must be the same attempt.
function Get-ConnectorCandidate([string]$SourceRunId, [string]$Version, [string]$WorkDirectory) {
    $repository = $script:ConnectorReleaseRepository
    $run = Invoke-ConnectorGhJson "repos/$repository/actions/runs/$SourceRunId"
    Assert-CandidateRun -Run $run -RunId $SourceRunId
    Assert-AncestorComparison (Invoke-ConnectorGhJson "repos/$repository/compare/$($run.head_sha)...$($env:GITHUB_SHA)")
    $keyPath = 'connector-desktop/Connector.Desktop/Assets/update-public-key.pem'
    Assert-SamePinnedKeyBlob (Get-ConnectorBlobSha $keyPath $run.head_sha) (Get-ConnectorBlobSha $keyPath $env:GITHUB_SHA)
    $name = "platform-connector-$Version-cloud-tested-unsigned"
    $artifact = Select-CandidateArtifact (Invoke-ConnectorGhJson "repos/$repository/actions/runs/$SourceRunId/artifacts?name=$name&per_page=100") $name
    $zip = Join-Path $WorkDirectory ('candidate-' + [guid]::NewGuid().ToString('N') + '.zip')
    $headers = @{ Authorization = "Bearer $($env:GH_TOKEN)"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    # PowerShell drops the Authorization header on the redirect to the storage host.
    Invoke-WebRequest -Uri "https://api.github.com/repos/$repository/actions/artifacts/$($artifact.Id)/zip" -Headers $headers -OutFile $zip -MaximumRedirection 5 | Out-Null
    $zipDigest = 'sha256:' + (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($null -ne $artifact.Digest -and $artifact.Digest -cne $zipDigest) { Stop-ConnectorRelease 'CANDIDATE_INVALID' "Artifact $name differs from its digest." }
    $directory = Join-Path $WorkDirectory ('candidate-' + [guid]::NewGuid().ToString('N'))
    Add-Type -AssemblyName System.IO.Compression.ZipFile
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $directory)
    Remove-Item -LiteralPath $zip -Force
    Assert-SameRunState -Before $run -After (Invoke-ConnectorGhJson "repos/$repository/actions/runs/$SourceRunId")
    $candidate = Assert-CandidateArtifact -Directory $directory -Version $Version -RunId $SourceRunId -HeadSha $run.head_sha
    $candidate | Add-Member -NotePropertyName RunAttempt -NotePropertyValue ([string]$run.run_attempt)
    return $candidate
}

# Before a release is created on tag $Tag: an existing tag (lightweight or annotated) must point to this commit or
# one of its ancestors, otherwise gh release create would silently bind the release to a foreign commit.
function Assert-ConnectorReleaseTag([Parameter(Mandatory)][string]$Tag) {
    $repository = $script:ConnectorReleaseRepository
    $text = (& gh api "repos/$repository/git/ref/tags/$Tag" 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        $body = $null
        try { $body = $text | ConvertFrom-Json } catch { $body = $null }
        if ($null -ne $body -and $body.PSObject.Properties['status'] -and [string]$body.status -ceq '404') { return }
        throw "Cannot read tag $Tag."
    }
    $object = Get-ReleaseTagObject ($text | ConvertFrom-Json) $Tag
    for ($depth = 0; $object.type -ceq 'tag' -and $depth -lt 4; $depth++) {
        $object = (Invoke-ConnectorGhJson "repos/$repository/git/tags/$($object.sha)").object
    }
    Assert-ReleaseTagCommit $object $Tag
    try { Assert-AncestorComparison (Invoke-ConnectorGhJson "repos/$repository/compare/$($object.sha)...$($env:GITHUB_SHA)") }
    catch { Stop-ConnectorRelease 'TAG_INVALID' "Existing tag $Tag points to $($object.sha), which is not an ancestor of $($env:GITHUB_SHA)." }
}

# Outputs of job "plan" as the PLAN_* environment of job "sign"; only compared with what "sign" computes itself.
function Get-ConnectorPlannedFacts {
    $planned = @{}
    foreach ($key in 'channel', 'version', 'paused', 'minimum_version', 'installer_file', 'installer_size', 'installer_sha256', 'source', 'channel_current') {
        $planned[$key] = [string][Environment]::GetEnvironmentVariable('PLAN_' + $key.ToUpperInvariant())
    }
    return $planned
}
