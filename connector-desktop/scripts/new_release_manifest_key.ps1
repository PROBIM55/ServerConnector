#Requires -Version 7.4
param(
    [ValidateSet('machine', 'updates')]
    [string]$Purpose = 'machine'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Release key creation requires Windows.' }

$directory = Join-Path $env:LOCALAPPDATA 'StructuraConnectorRelease\keys'
$prefix = if ($Purpose -eq 'updates') { 'updates-p256' } else { 'release-p256' }
$privatePath = Join-Path $directory ($prefix + '-private.pem')
$publicPath = Join-Path $directory ($prefix + '-public.pem')
if (Test-Path -LiteralPath $privatePath -PathType Leaf) {
    throw "Release key already exists: $privatePath. Refusing to replace it."
}
if (Test-Path -LiteralPath $publicPath -PathType Leaf) {
    throw "Release public key already exists: $publicPath. Refusing to replace it."
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$userSid = $identity.User
if ($null -eq $userSid) { throw 'Current Windows user SID is unavailable.' }
$systemSid = [System.Security.Principal.SecurityIdentifier]::new(
    [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
$adminsSid = [System.Security.Principal.SecurityIdentifier]::new(
    [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)

$existingDirectory = Test-Path -LiteralPath $directory
if ($existingDirectory) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container))
        { throw 'Existing release key directory is not a directory.' }
}
else {
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
}
$acl = [System.Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($userSid)
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($userSid, $systemSid, $adminsSid)) {
    $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $sid,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
}
if ($existingDirectory) {
    $actual = Get-Acl -LiteralPath $directory
    $actualSids = @($actual.GetAccessRules($true, $true,
        [System.Security.Principal.SecurityIdentifier]) |
        Where-Object AccessControlType -EQ Allow |
        ForEach-Object { $_.IdentityReference.Value })
    $expectedSids = @($userSid.Value, $systemSid.Value, $adminsSid.Value)
    if (-not $actual.AreAccessRulesProtected -or
        $actual.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne $userSid.Value -or
        @($actualSids | Where-Object { $_ -notin $expectedSids }).Count -ne 0) {
        throw 'Existing release key directory has untrusted ownership or ACL.'
    }
}
else { Set-Acl -LiteralPath $directory -AclObject $acl }

$curve = [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256
$key = [System.Security.Cryptography.ECDsa]::Create($curve)
if ($null -eq $key -or $key.KeySize -ne 256) { throw 'P-256 key generation failed.' }
try {
    $privatePem = $key.ExportPkcs8PrivateKeyPem()
    $publicPem = $key.ExportSubjectPublicKeyInfoPem()
    $encoding = [System.Text.UTF8Encoding]::new($false)
    foreach ($entry in @(@($privatePath, $privatePem), @($publicPath, $publicPem))) {
        $stream = [System.IO.FileStream]::new(
            $entry[0], [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $bytes = $encoding.GetBytes($entry[1] + "`n")
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
    }
}
finally { $key.Dispose() }

foreach ($path in @($privatePath, $publicPath)) {
    $fileAcl = [System.Security.AccessControl.FileSecurity]::new()
    $fileAcl.SetOwner($userSid)
    $fileAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($userSid, $systemSid, $adminsSid)) {
        $fileAcl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid, [System.Security.AccessControl.FileSystemRights]::FullControl,
            [System.Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $path -AclObject $fileAcl
}

$publicFingerprint = (Get-FileHash -LiteralPath $publicPath -Algorithm SHA256).Hash
Write-Host "Release manifest key created outside the repository."
Write-Host "Private key: $privatePath"
Write-Host "Public key: $publicPath"
Write-Host "Public PEM SHA-256: $publicFingerprint"
