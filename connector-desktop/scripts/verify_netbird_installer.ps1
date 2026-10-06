param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $root 'infra/netbird/v0.79.0-windows-x64.lock.json'
$lock = Get-Content -Raw -LiteralPath $lockPath | ConvertFrom-Json
$resolvedMsi = (Resolve-Path -LiteralPath $MsiPath -ErrorAction Stop).Path
$file = Get-Item -LiteralPath $resolvedMsi

if ($lock.schemaVersion -ne 1 -or $file.Name -cne $lock.installerName -or
    $file.Length -ne $lock.installerBytes -or
    [version]$lock.version -lt [version]$lock.minimumSecureVersion) {
    throw 'NetBird installer identity, size, or secure version did not match the repository lock.'
}

$hash = (Get-FileHash -LiteralPath $resolvedMsi -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -cne $lock.installerSha256) {
    throw 'NetBird installer SHA-256 did not match the repository lock.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $resolvedMsi
if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
    $null -eq $signature.SignerCertificate -or
    !$signature.SignerCertificate.Subject.StartsWith($lock.signerSubjectPrefix, [StringComparison]::Ordinal) -or
    $signature.SignerCertificate.Thumbprint -cne $lock.signerThumbprint) {
    throw 'NetBird installer Authenticode signature or publisher did not match the repository lock.'
}

# MSI ProductVersion is read without running msiexec or changing system state.
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($resolvedMsi, 0)
$view = $database.OpenView("SELECT Value FROM Property WHERE Property = 'ProductVersion'")
[void]$view.Execute()
$record = $view.Fetch()
if ($null -eq $record -or $record.StringData(1) -cne $lock.version) {
    throw 'NetBird MSI ProductVersion did not match the repository lock.'
}

[pscustomobject]@{
    version = $lock.version
    releaseUrl = $lock.releaseUrl
    installerPath = $resolvedMsi
    bytes = $file.Length
    sha256 = $hash
    authenticodeStatus = [string]$signature.Status
    signerThumbprint = $signature.SignerCertificate.Thumbprint
    verified = $true
}
