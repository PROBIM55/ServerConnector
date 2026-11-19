#Requires -Version 7.4
# Step "sign" of publish-connector-release.yml: the only step that has UPDATE_SIGNING_KEY_PEM (as SIGN_KEY_PEM),
# no token and no network. Re-checks the description written by new_release_description.ps1 with the same rules,
# signs its exact bytes with the shared implementation and hands both to job publish as job outputs.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$DescriptionDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
try {
    . (Join-Path $PSScriptRoot 'connector_release_description.ps1')
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:GITHUB_REF -cne 'refs/heads/master' -or
        $env:GITHUB_REPOSITORY -cne $ConnectorReleaseRepository) { throw 'Hosted signing of branch master only.' }
    if ($env:RELEASE_CHANNEL -cnotin $ConnectorReleaseChannels) { throw 'Channel must be pilot or stable.' }
    if (-not [string]::IsNullOrEmpty($env:GH_TOKEN) -or -not [string]::IsNullOrEmpty($env:GITHUB_TOKEN)) { throw 'The signing step must not have a token.' }
    $names = @(Get-ChildItem -LiteralPath $DescriptionDirectory -Force | ForEach-Object Name)
    if ($names.Count -ne 1 -or $names[0] -cne 'connector-release.json') { throw 'The description directory must hold connector-release.json only.' }
    $path = Join-Path $DescriptionDirectory 'connector-release.json'
    $bytes = [IO.File]::ReadAllBytes($path)
    $description = Assert-OwnReleaseDescription -Bytes $bytes -Channel $env:RELEASE_CHANNEL -Version $env:RELEASE_VERSION
    $signatures = Invoke-PinnedReleaseSignature -Documents @(, $bytes) -PublicKeyPemPath (Get-PinnedReleaseKeyPath)
    # Description and signature leave this job only as job outputs (base64, a few KiB): no artifact action runs here.
    if (-not $env:GITHUB_OUTPUT) { throw 'Job outputs are not available.' }
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Value @(
        "description_base64=$([Convert]::ToBase64String($bytes))", "signature_base64=$([Convert]::ToBase64String($signatures[0]))")
    Write-Host "Signed $($description.Channel) $($description.Version) (description sha256 $([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant())); the key stays in this step."
} finally {
    $env:SIGN_KEY_PEM = $null
}
