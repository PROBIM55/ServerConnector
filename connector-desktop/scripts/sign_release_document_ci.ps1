#Requires -Version 7.4
# Signs the machine-helper manifest of the migration helper (schema 4) in sign-unified-release-document.yml,
# environment production, key UNIFIED_HELPER_MANIFEST_SIGNING_KEY_PEM. Nothing else is accepted: release
# descriptions of the update channels are signed only by publish-connector-release.yml and sign-connector-ci-release.yml.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
try {
    if ($env:SIGN_REQUEST_ID -cnotmatch '^[a-f0-9]{32}$' -or
        $env:SIGN_DOCUMENT_SHA256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid signing request metadata' }
    if ($env:SIGN_SOURCE_SHA -cnotmatch '^[a-f0-9]{40}$' -or $env:GITHUB_SHA -cne $env:SIGN_SOURCE_SHA) {
        throw 'Signing workflow source differs from the expected commit'
    }
    $bytes = [Convert]::FromBase64String($env:SIGN_DOCUMENT_BASE64)
    if ($bytes.Length -eq 0 -or $bytes.Length -gt 16384 -or
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $env:SIGN_DOCUMENT_SHA256) {
        throw 'Signing document length or digest differs'
    }
    . (Join-Path $PSScriptRoot 'validate_release_signing_document.ps1')
    Assert-ReleaseSigningDocument $bytes 'helper'
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Signing output directory already exists' }
    # The one signing implementation: key from SIGN_KEY_PEM of this step, pin check, verification of the signature.
    . (Join-Path $PSScriptRoot 'connector_release_description.ps1')
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $signatures = Invoke-PinnedReleaseSignature -Documents @(, $bytes) -PublicKeyPemPath (Join-Path $root 'installer/release-public-key.pem')
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'document.sig'), $signatures[0])
    Write-Host 'Helper manifest signature verified; the signing key remains in CI.'
} finally {
    $env:SIGN_KEY_PEM = $null
}
