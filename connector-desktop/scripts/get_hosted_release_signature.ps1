#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][ValidateSet('updates','helper')][string]$Purpose,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$SigningRef,
    [Parameter(Mandatory)][string]$PublicKeyPath,
    [Parameter(Mandatory)][string]$SignaturePath
)
$ErrorActionPreference = 'Stop'
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
$repository = 'PROBIM55/ServerConnector'
$bytes = [IO.File]::ReadAllBytes($ManifestPath)
if ($bytes.Length -eq 0 -or $bytes.Length -gt 16384 -or (Test-Path -LiteralPath $SignaturePath)) { throw 'Invalid hosted signature input/output' }
. (Join-Path $PSScriptRoot 'validate_release_signing_document.ps1')
Assert-ReleaseSigningDocument $bytes $Purpose
$requestId = [Guid]::NewGuid().ToString('N')
$request = [ordered]@{ref='master';inputs=@{request_id=$requestId;purpose=$Purpose;source_sha=$SigningRef;
    document_base64=[Convert]::ToBase64String($bytes);
    document_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()}}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('connector-signature-' + $requestId)
[void][IO.Directory]::CreateDirectory($temporary)
$requestPath = Join-Path $temporary 'request.json'
[IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
try {
    & $gh api --method POST "repos/$repository/actions/workflows/sign-unified-release-document.yml/dispatches" --input $requestPath
    if ($LASTEXITCODE -ne 0) { throw 'Hosted signing dispatch failed' }
    $deadline = [DateTime]::UtcNow.AddMinutes(15); $run = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $runs = (& $gh run list --repo $repository --workflow sign-unified-release-document.yml --limit 30 --json databaseId,displayTitle,headSha,status,conclusion | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0) { throw 'Hosted signing status could not be read' }
        $matches = @($runs | Where-Object displayTitle -CEQ "Connector signature $requestId")
        if ($matches.Count -gt 1) { throw 'Ambiguous hosted signing request' }
        if ($matches.Count -eq 1) {
            $run = $matches[0]
            if ($run.headSha -cne $SigningRef) { throw 'Hosted signer source differs' }
            if ($run.status -ceq 'completed') { break }
        }
        Write-Host "Waiting for signature $Purpose; no local compilation in this step."
        Start-Sleep -Seconds 10
    }
    if (-not $run -or $run.status -cne 'completed' -or $run.conclusion -cne 'success') { throw 'Hosted signing did not succeed' }
    & $gh run download $run.databaseId --repo $repository --name "connector-signature-$requestId" --dir (Join-Path $temporary 'result')
    if ($LASTEXITCODE -ne 0) { throw 'Hosted signature download failed' }
    $signature = [IO.File]::ReadAllBytes((Join-Path $temporary 'result/document.sig'))
    $public = [Security.Cryptography.ECDsa]::Create()
    try {
        $public.ImportFromPem([IO.File]::ReadAllText($PublicKeyPath))
        if ($public.KeySize -ne 256 -or $signature.Length -ne 64 -or -not $public.VerifyData($bytes,$signature,
            [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) {
            throw 'Hosted signature does not match this document and the pinned public key'
        }
    } finally { $public.Dispose() }
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -cne
        [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($ManifestPath)))) {
        throw 'Release document changed while awaiting its signature'
    }
    $file = [IO.File]::Open($SignaturePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $file.Write($signature,0,$signature.Length); $file.Flush($true) } finally { $file.Dispose() }
    Write-Host "Verified hosted $Purpose signature."
} finally {
    # Only public release hashes/documents and their signatures were transferred.
    if (Test-Path -LiteralPath $requestPath) { Remove-Item -LiteralPath $requestPath }
}
