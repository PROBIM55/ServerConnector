#Requires -Version 7.4
# Signs release descriptions (schema 2) for the cloud update check only. Runs in the "release-signing" environment of
# sign-connector-ci-release.yml, never in a job that builds anything. Accepts nothing but descriptions of the exact
# disposable channel "ci-<run id>" of a build run of this very commit that is in progress, with paused=false.
# Such a description is useless to any real computer: the channel is inside the signature and only an
# administrator can set a machine's channel.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Hosted signing only.' }
if ($env:SIGN_REQUEST_ID -cnotmatch '^[a-f0-9]{32}$' -or $env:SIGN_CI_RUN_ID -cnotmatch '^[1-9][0-9]{0,19}$' -or
    $env:SIGN_SOURCE_SHA -cnotmatch '^[a-f0-9]{40}$' -or $env:GITHUB_SHA -cne $env:SIGN_SOURCE_SHA) { throw 'Invalid signing request metadata.' }

# The run named by the channel must be an in-progress update check of build-unified-connector-candidate.yml
# on exactly this commit; a finished, foreign or unrelated run cannot obtain a signature.
$run = & gh api "repos/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:SIGN_CI_RUN_ID)" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $run.path -cne '.github/workflows/build-unified-connector-candidate.yml' -or
    $run.head_sha -cne $env:SIGN_SOURCE_SHA -or $run.status -cnotin @('in_progress','queued','waiting')) { throw 'The requesting run is not an in-progress update check of this commit.' }

$channel = 'ci-' + $env:SIGN_CI_RUN_ID
$documents = @()
foreach ($index in 1, 2) {
    $base64 = [Environment]::GetEnvironmentVariable("SIGN_DOCUMENT${index}_BASE64")
    $sha = [Environment]::GetEnvironmentVariable("SIGN_DOCUMENT${index}_SHA256")
    $bytes = [Convert]::FromBase64String($base64)
    if ($bytes.Length -eq 0 -or $bytes.Length -gt 65536 -or $sha -cnotmatch '^[a-f0-9]{64}$' -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $sha) { throw "Document $index length or digest differs." }
    $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($bytes))
    try {
        $d = $document.RootElement
        $expected = @('schemaVersion','applicationId','channel','version','publishedAtUtc','paused','minimumVersion','installer','notes')
        $actual = @($d.EnumerateObject() | ForEach-Object Name)
        if ($actual.Count -ne $expected.Count -or @($actual | Select-Object -Unique).Count -ne $actual.Count -or @($actual | Where-Object { $_ -cnotin $expected }).Count) { throw 'Unexpected description fields.' }
        $installer = $d.GetProperty('installer')
        $installerFields = @($installer.EnumerateObject() | ForEach-Object Name)
        if ($installerFields.Count -ne 3 -or @($installerFields | Where-Object { $_ -cnotin @('fileName','size','sha256') }).Count) { throw 'Unexpected installer fields.' }
        $version = $d.GetProperty('version').GetString()
        if ($d.GetProperty('schemaVersion').GetRawText() -cne '2' -or
            $d.GetProperty('applicationId').GetString() -cne 'Structura.PlatformConnector' -or
            $d.GetProperty('channel').GetString() -cne $channel -or
            $d.GetProperty('paused').ValueKind -ne [System.Text.Json.JsonValueKind]::False -or
            $version -cnotmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
            $d.GetProperty('minimumVersion').GetString() -cnotmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
            $installer.GetProperty('fileName').GetString() -cne "PlatformConnector-$version.exe" -or
            $installer.GetProperty('size').GetRawText() -cnotmatch '^[1-9][0-9]{0,9}$' -or
            $installer.GetProperty('sha256').GetString() -cnotmatch '^[a-f0-9]{64}$') { throw "Document $index is not a disposable CI description of channel $channel." }
    } finally { $document.Dispose() }
    $documents += ,$bytes
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$private = [Security.Cryptography.ECDsa]::Create()
$public = [Security.Cryptography.ECDsa]::Create()
try {
    $private.ImportFromPem($env:SIGN_KEY_PEM)
    $public.ImportFromPem([IO.File]::ReadAllText((Join-Path $root 'Connector.Desktop/Assets/update-public-key.pem')))
    if ($private.KeySize -ne 256 -or [Convert]::ToBase64String($private.ExportSubjectPublicKeyInfo()) -cne
        [Convert]::ToBase64String($public.ExportSubjectPublicKeyInfo())) { throw 'Signing key does not match the repository pin.' }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Signing output directory already exists.' }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    $index = 0
    foreach ($bytes in $documents) {
        $index++
        $signature = $private.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        if ($signature.Length -ne 64 -or -not $public.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Signature verification failed.' }
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory "document$index.sig"), $signature)
    }
    Write-Host "Signed $index disposable CI descriptions of channel $channel; the key stays in this job."
} finally {
    $private.Dispose(); $public.Dispose()
    $env:SIGN_KEY_PEM = $null
}
