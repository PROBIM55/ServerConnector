#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($env:SIGN_REQUEST_ID -cnotmatch '^[a-f0-9]{32}$' -or
    $env:SIGN_PURPOSE -cnotmatch '^(updates|helper)$' -or
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
Assert-ReleaseSigningDocument $bytes $env:SIGN_PURPOSE
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publicPath = if ($env:SIGN_PURPOSE -ceq 'updates') {
    Join-Path $root 'Connector.Desktop/Assets/update-public-key.pem'
} else { Join-Path $root 'installer/release-public-key.pem' }
$pem = $env:SIGN_KEY_PEM
$private = [Security.Cryptography.ECDsa]::Create()
$public = [Security.Cryptography.ECDsa]::Create()
try {
    $private.ImportFromPem($pem)
    $public.ImportFromPem([IO.File]::ReadAllText($publicPath))
    if ($private.KeySize -ne 256 -or
        [Convert]::ToBase64String($private.ExportSubjectPublicKeyInfo()) -cne
        [Convert]::ToBase64String($public.ExportSubjectPublicKeyInfo())) { throw 'Signing key does not match the repository pin' }
    $signature = $private.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if ($signature.Length -ne 64 -or -not $public.VerifyData($bytes, $signature,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Signature verification failed' }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Signing output directory already exists' }
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'document.sig'), $signature)
    Write-Host 'Release document signature verified; signing keys remain in CI.'
} finally {
    $private.Dispose(); $public.Dispose(); $pem = $null
    $env:SIGN_KEY_PEM = $null
}
