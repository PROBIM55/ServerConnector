#Requires -Version 7.4
# Signs release descriptions (schema 2) for the cloud update check only, in two steps of sign-connector-ci-release.yml
# (environment "release-signing", never a job that builds anything):
#   -Stage Verify  token, no key: the request names an in-progress run of build-unified-connector-candidate.yml on this
#                  very commit; both documents are checked and written to -WorkDirectory;
#   -Stage Sign    key, no token, no network: the same documents are checked again and signed into -OutputDirectory
#                  (document1.sig, document2.sig).
# Accepts nothing but descriptions of the exact disposable channel "ci-<run id>" with paused=false. Such a description
# is useless to any real computer: the channel is inside the signature and only an administrator sets a machine's channel.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Verify', 'Sign')][string]$Stage,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-CiDescription([byte[]]$Bytes, [string]$Channel, [int]$Index) {
    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt 65536) { throw "Document $Index length or digest differs." }
    $document = [System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($Bytes))
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
            $d.GetProperty('channel').GetString() -cne $Channel -or
            $d.GetProperty('paused').ValueKind -ne [System.Text.Json.JsonValueKind]::False -or
            $version -cnotmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
            $d.GetProperty('minimumVersion').GetString() -cnotmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$' -or
            $installer.GetProperty('fileName').GetString() -cne "PlatformConnector-$version.exe" -or
            $installer.GetProperty('size').GetRawText() -cnotmatch '^[1-9][0-9]{0,9}$' -or
            $installer.GetProperty('sha256').GetString() -cnotmatch '^[a-f0-9]{64}$') { throw "Document $Index is not a disposable CI description of channel $Channel." }
    } finally { $document.Dispose() }
}

try {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Hosted signing only.' }
    if ($env:GITHUB_REF -cne 'refs/heads/master' -or $env:GITHUB_REPOSITORY -cne 'PROBIM55/ServerConnector') { throw 'Signing runs on branch master of PROBIM55/ServerConnector only.' }
    if ($env:SIGN_REQUEST_ID -cnotmatch '^[a-f0-9]{32}$' -or $env:SIGN_CI_RUN_ID -cnotmatch '^[1-9][0-9]{0,19}$' -or
        $env:SIGN_SOURCE_SHA -cnotmatch '^[a-f0-9]{40}$' -or $env:GITHUB_SHA -cne $env:SIGN_SOURCE_SHA) { throw 'Invalid signing request metadata.' }
    $channel = 'ci-' + $env:SIGN_CI_RUN_ID

    if ($Stage -ceq 'Verify') {
        if (-not [string]::IsNullOrEmpty($env:SIGN_KEY_PEM)) { throw 'The verification step must not have the signing key.' }
        # The run named by the channel must be an in-progress update check of build-unified-connector-candidate.yml
        # on exactly this commit; a finished, foreign or unrelated run cannot obtain a signature.
        $run = & gh api "repos/$($env:GITHUB_REPOSITORY)/actions/runs/$($env:SIGN_CI_RUN_ID)" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $run.path -cne '.github/workflows/build-unified-connector-candidate.yml' -or
            $run.head_sha -cne $env:SIGN_SOURCE_SHA -or $run.status -cnotin @('in_progress','queued','waiting')) { throw 'The requesting run is not an in-progress update check of this commit.' }
        if (Test-Path -LiteralPath $WorkDirectory) { throw 'Verification directory already exists.' }
        $documents = @()
        foreach ($index in 1, 2) {
            $base64 = [Environment]::GetEnvironmentVariable("SIGN_DOCUMENT${index}_BASE64")
            $sha = [Environment]::GetEnvironmentVariable("SIGN_DOCUMENT${index}_SHA256")
            $bytes = [Convert]::FromBase64String($base64)
            if ($bytes.Length -eq 0 -or $bytes.Length -gt 65536 -or $sha -cnotmatch '^[a-f0-9]{64}$' -or
                [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $sha) { throw "Document $index length or digest differs." }
            Assert-CiDescription $bytes $channel $index
            $documents += , $bytes
        }
        [void][IO.Directory]::CreateDirectory($WorkDirectory)
        for ($index = 1; $index -le 2; $index++) { [IO.File]::WriteAllBytes((Join-Path $WorkDirectory "document$index.json"), $documents[$index - 1]) }
        Write-Host "Verified 2 disposable CI descriptions of channel $channel for the signing step."
    } else {
        if (-not [string]::IsNullOrEmpty($env:GH_TOKEN) -or -not [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) { throw 'The signing step must not have a token.' }
        if (-not $OutputDirectory) { throw 'OutputDirectory is required for signing.' }
        $names = @(Get-ChildItem -LiteralPath $WorkDirectory -Force | ForEach-Object Name | Sort-Object)
        if ($names.Count -ne 2 -or $names[0] -cne 'document1.json' -or $names[1] -cne 'document2.json') { throw 'The verified documents differ.' }
        $documents = @()
        foreach ($index in 1, 2) {
            $bytes = [IO.File]::ReadAllBytes((Join-Path $WorkDirectory "document$index.json"))
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne
                [Environment]::GetEnvironmentVariable("SIGN_DOCUMENT${index}_SHA256")) { throw "Document $index length or digest differs." }
            Assert-CiDescription $bytes $channel $index
            $documents += , $bytes
        }
        if (Test-Path -LiteralPath $OutputDirectory) { throw 'Signing output directory already exists.' }
        $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        # The one signing implementation (key from SIGN_KEY_PEM of this step, pin check, verification of every signature).
        . (Join-Path $PSScriptRoot 'connector_release_description.ps1')
        $signatures = Invoke-PinnedReleaseSignature -Documents $documents -PublicKeyPemPath (Join-Path $root 'Connector.Desktop/Assets/update-public-key.pem')
        [void][IO.Directory]::CreateDirectory($OutputDirectory)
        $index = 0
        foreach ($signature in $signatures) {
            $index++
            [IO.File]::WriteAllBytes((Join-Path $OutputDirectory "document$index.sig"), $signature)
        }
        Write-Host "Signed $index disposable CI descriptions of channel $channel; the key stays in this step."
    }
} finally {
    $env:SIGN_KEY_PEM = $null
}
