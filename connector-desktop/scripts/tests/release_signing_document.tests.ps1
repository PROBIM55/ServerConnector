$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\validate_release_signing_document.ps1')
$assertions = 0
function Check-Document([string]$Json, [string]$Purpose, [bool]$Accepted) {
    $actual = $true
    try { Assert-ReleaseSigningDocument ([Text.Encoding]::UTF8.GetBytes($Json)) $Purpose } catch { $actual = $false }
    if ($actual -ne $Accepted) { throw "Signing contract test failed: $Purpose / expected $Accepted" }
    $script:assertions++
}
# The schema-1 update feed is no longer signed through this path: an old feed document is refused under any purpose.
$asset = [ordered]@{fileName='Structura.Connector.Desktop-1.1.0-stable-full.nupkg';version='1.1.0';type='Full';size=100;sha256=('a'*64)}
$legacyUpdate = [ordered]@{schemaVersion=1;applicationId='Structura.Connector.Desktop';channel='stable';assets=@($asset)}|ConvertTo-Json -Depth 6 -Compress
Check-Document $legacyUpdate updates $false
Check-Document $legacyUpdate helper $false
$helper = [ordered]@{schemaVersion=4;version='1.1.0';helperRelativePath='Connector.Upgrade.MachineHelper.exe';size=100;sha256=('b'*64);
    velopackSetup=[ordered]@{relativePath='Setup.exe';size=200;sha256=('c'*64);packageId='Structura.Connector.Desktop';version='1.1.0'};
    callerImage=[ordered]@{relativePath='StructuraConnectorInstaller\Bootstrapper\Connector.Upgrade.Bootstrapper.exe';size=100;sha256=('d'*64)}}
$validHelper = $helper|ConvertTo-Json -Depth 6 -Compress
Check-Document $validHelper helper $true
Check-Document $validHelper updates $false
$helper.callerImage.relativePath='..\untrusted.exe'; Check-Document ($helper|ConvertTo-Json -Depth 6 -Compress) helper $false
$helper.callerImage.relativePath='StructuraConnectorInstaller\Bootstrapper\Connector.Upgrade.Bootstrapper.exe'
$helper.velopackSetup.version='1.0.31'; Check-Document ($helper|ConvertTo-Json -Depth 6 -Compress) helper $false
$signer = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
try {
    $bytes=[Text.Encoding]::UTF8.GetBytes($validHelper)
    $signature=$signer.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if ($signature.Length -ne 64 -or -not $signer.VerifyData($bytes,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Valid signature failed' }
    $assertions++
    $bytes[0]=$bytes[0] -bxor 1
    if ($signer.VerifyData($bytes,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Tampered document accepted' }
    $assertions++
} finally { $signer.Dispose() }
"RELEASE_SIGNING_CONTRACT_PASS: $assertions assertions; no build or real signing key used"
