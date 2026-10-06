#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackVersion,
    [Parameter(Mandatory = $true)][string]$InstallerVersion,
    [Parameter(Mandatory = $true)][string]$Channel,
    [Parameter(Mandatory = $true)][string]$FeedUrl,
    [Parameter(Mandatory = $true)][string]$IfcWorkerPath,
    [Parameter(Mandatory = $true)][string]$UpdateSigningKeyPemPath,
    [Parameter(Mandatory = $true)][string]$HelperManifestSigningKeyPemPath,
    [Parameter(Mandatory = $true)][string]$HelperReleasePublicKeyFile,
    [Parameter(Mandatory = $true)][string]$ConnectorAccessConfigurationPath,
    [Parameter(Mandatory = $true)][string]$ProtectedOutputRoot,
    [string]$PinnedAssetBundleDirectory = (Join-Path $PSScriptRoot '..\artifacts\migration-helper-bundle-20261003'),
    [string]$SetupPackageId = 'Structura.Connector.Desktop'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:BuildPackageUpdate = Join-Path $PSScriptRoot 'build_package_update.ps1'
$script:BootstrapperProject = Join-Path $script:RepositoryRoot 'installer\Connector.Upgrade.Bootstrapper\Connector.Upgrade.Bootstrapper.csproj'
$script:MachineHelperProject = Join-Path $script:RepositoryRoot 'installer\Connector.Upgrade.MachineHelper\Connector.Upgrade.MachineHelper.csproj'
$script:ManifestBuilderProject = Join-Path $script:RepositoryRoot 'installer\Connector.Upgrade.ReleaseManifestBuilder\Connector.Upgrade.ReleaseManifestBuilder.csproj'
$script:LaunchBridgeProject = Join-Path $script:RepositoryRoot 'installer\Connector.Upgrade.LaunchBridge\Connector.Upgrade.LaunchBridge.csproj'
$script:UnifiedSetupProject = Join-Path $script:RepositoryRoot 'Connector.Unified.Setup\Connector.Unified.Setup.wixproj'
$script:UnifiedBundleProject = Join-Path $script:RepositoryRoot 'Connector.Unified.Bundle\Connector.Unified.Bundle.wixproj'
$script:NetBirdPreflight = Join-Path $PSScriptRoot 'netbird_windows_preflight.ps1'
$script:NetBirdVerifier = Join-Path $PSScriptRoot 'verify_netbird_installer.ps1'
$script:NetBirdLock = Join-Path $script:RepositoryRoot 'infra\netbird\v0.79.0-windows-x64.lock.json'

function Assert-NoReparsePath([string]$Path, [string]$Description) {
    $currentPath = [IO.Path]::GetFullPath($Path)
    while (![string]::IsNullOrWhiteSpace($currentPath)) {
        $current = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description path contains a reparse point."
        }
        $parent = [IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent -or [string]::Equals($parent.FullName, $currentPath, [StringComparison]::OrdinalIgnoreCase)) { break }
        $currentPath = $parent.FullName
    }
}

function Assert-PrivateP256Key([string]$Path, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing. Supply an external P-256 private-key PEM file."
    }

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must be a regular, non-reparse file."
    }
    $fullPath = [IO.Path]::GetFullPath($item.FullName)
    Assert-NoReparsePath $fullPath $Description
    $repositoryPrefix = $script:RepositoryRoot.TrimEnd('\') + '\'
    if ($fullPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be outside the repository checkout."
    }

    try {
        $pem = [IO.File]::ReadAllText($fullPath, [Text.Encoding]::UTF8).Trim()
        if (!$pem.StartsWith('-----BEGIN PRIVATE KEY-----', [StringComparison]::Ordinal) -or
            !$pem.EndsWith('-----END PRIVATE KEY-----', [StringComparison]::Ordinal) -or
            [regex]::Matches($pem, '-----BEGIN ').Count -ne 1 -or
            [regex]::Matches($pem, '-----END ').Count -ne 1) {
            throw 'invalid PEM envelope'
        }
        $key = [Security.Cryptography.ECDsa]::Create()
        try {
            $key.ImportFromPem($pem)
            $parameters = $key.ExportParameters($true)
            if ($key.KeySize -ne 256 -or $null -eq $parameters.D -or
                $parameters.Curve.Oid.Value -cne '1.2.840.10045.3.1.7') {
                throw 'key is not P-256 private material'
            }
        }
        finally { $key.Dispose() }
    }
    catch {
        throw "$Description must contain exactly one unencrypted P-256 PKCS#8 private-key PEM. Key contents are not displayed."
    }
    return $fullPath
}

function Get-PrivateKeyPublicFingerprint([string]$Path) {
    $key = [Security.Cryptography.ECDsa]::Create()
    try {
        $key.ImportFromPem([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8))
        return [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo())
    }
    finally { $key.Dispose() }
}

function Assert-MatchingPublicKey([string]$PrivatePath, [string]$PublicPath, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($PublicPath) -or !(Test-Path -LiteralPath $PublicPath -PathType Leaf)) {
        throw "$Description is missing."
    }
    $publicItem = Get-Item -LiteralPath $PublicPath -Force -ErrorAction Stop
    if (($publicItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must be a regular, non-reparse file."
    }
    Assert-NoReparsePath $publicItem.FullName $Description

    try {
        $private = [Security.Cryptography.ECDsa]::Create()
        $public = [Security.Cryptography.ECDsa]::Create()
        try {
            $private.ImportFromPem([IO.File]::ReadAllText($PrivatePath, [Text.Encoding]::UTF8))
            $publicPem = [IO.File]::ReadAllText($publicItem.FullName, [Text.Encoding]::UTF8).Trim()
            if (!$publicPem.StartsWith('-----BEGIN PUBLIC KEY-----', [StringComparison]::Ordinal) -or
                !$publicPem.EndsWith('-----END PUBLIC KEY-----', [StringComparison]::Ordinal) -or
                [regex]::Matches($publicPem, '-----BEGIN ').Count -ne 1 -or
                [regex]::Matches($publicPem, '-----END ').Count -ne 1) {
                throw 'invalid public-key PEM envelope'
            }
            $public.ImportFromPem($publicPem)
            if ($private.KeySize -ne 256 -or $public.KeySize -ne 256 -or
                $private.ExportParameters($false).Curve.Oid.Value -cne '1.2.840.10045.3.1.7' -or
                $public.ExportParameters($false).Curve.Oid.Value -cne '1.2.840.10045.3.1.7' -or
                [Convert]::ToBase64String($private.ExportSubjectPublicKeyInfo()) -cne
                [Convert]::ToBase64String($public.ExportSubjectPublicKeyInfo())) {
                throw 'public key does not match the signing key'
            }
        }
        finally { $private.Dispose(); $public.Dispose() }
    }
    catch {
        throw "$Description must be a P-256 public PEM matching its signing key."
    }
    return [IO.Path]::GetFullPath($publicItem.FullName)
}

function Assert-ExistingRegularFile([string]$Path, [string]$Description) {
    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing."
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -le 0) {
        throw "$Description must be a non-empty regular file."
    }
    return [IO.Path]::GetFullPath($item.FullName)
}

function Assert-PinnedAsset([string]$Path, [string]$Name, [int64]$Bytes, [string]$Sha256) {
    $fullPath = Assert-ExistingRegularFile $Path $Name
    $item = Get-Item -LiteralPath $fullPath -Force
    $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    if ($item.Name -cne $Name -or $item.Length -ne $Bytes -or $hash -cne $Sha256) {
        throw "$Name did not match the pinned release bundle."
    }
    return $fullPath
}

function Assert-PinnedSourceHash([string]$Path, [string]$Description, [string]$Sha256) {
    $fullPath = Assert-ExistingRegularFile $Path $Description
    if ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -cne $Sha256) {
        throw "$Description did not match the release trust pin."
    }
    return $fullPath
}

function Assert-EnabledProductionAccessConfig([string]$Path) {
    $fullPath = Assert-ExistingRegularFile $Path 'Production connector-access configuration'
    try { $config = Get-Content -LiteralPath $fullPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
    catch { throw 'Production connector-access configuration is not valid JSON.' }

    $serviceUri = $null
    $issuer = [string]$config.issuerCertificateSha256
    if ($config.enabled -cne $true -or
        $issuer -notmatch '^[A-Fa-f0-9]{64}$' -or
        ![Uri]::TryCreate([string]$config.serviceBaseUri, [UriKind]::Absolute, [ref]$serviceUri) -or
        $serviceUri.Scheme -cne 'https' -or $serviceUri.IsLoopback -or
        $serviceUri.UserInfo.Length -ne 0) {
        throw 'Connector-access configuration must be explicitly enabled and contain HTTPS production endpoints and an issuer certificate SHA-256.'
    }

    $controlPlane = @($config.services | Where-Object {
        $_.serviceId -ceq 'control-plane' -and
        $_.healthPath -ceq 'jobs/health' -and
        $null -ne $_.baseUri
    })
    if ($controlPlane.Count -ne 1) { throw 'Connector-access configuration must define exactly one production control-plane service.' }
    $controlUri = $null
    if (![Uri]::TryCreate([string]$controlPlane[0].baseUri, [UriKind]::Absolute, [ref]$controlUri) -or
        $controlUri.Scheme -cne 'https' -or $controlUri.IsLoopback -or $controlUri.UserInfo.Length -ne 0 -or
        $controlUri.AbsolutePath.TrimEnd('/') -cne '/api/platform/connector/access/v1') {
        throw 'Connector-access control-plane endpoint must use the production HTTPS API path.'
    }
    return $fullPath
}

function Assert-ProtectedOutputRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or !(Test-Path -LiteralPath $Path -PathType Container)) {
        throw 'ProtectedOutputRoot must be an existing, pre-provisioned directory.'
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'ProtectedOutputRoot must not be a reparse point.'
    }
    $fullPath = [IO.Path]::GetFullPath($item.FullName)
    $current = $item
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'ProtectedOutputRoot has a reparse-point ancestor.'
        }
        $current = [IO.Directory]::GetParent($current.FullName)
    }

    try {
        $acl = Get-Acl -LiteralPath $fullPath -ErrorAction Stop
        $sidType = [Security.Principal.SecurityIdentifier]
        $trusted = @(
            [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null).Value,
            [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null).Value,
            [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        )
        $owner = $acl.GetOwner($sidType).Value
        if ($owner -notin $trusted) { throw 'untrusted owner' }
        $mutationRights = [Security.AccessControl.FileSystemRights]::FullControl -bor [Security.AccessControl.FileSystemRights]::Modify -bor
            [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [Security.AccessControl.FileSystemRights]::TakeOwnership -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
            [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::AppendData -bor
            [Security.AccessControl.FileSystemRights]::WriteData
        foreach ($rule in $acl.GetAccessRules($true, $true, $sidType)) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            if ($rule.IdentityReference.Value -notin $trusted -and ($rule.FileSystemRights -band $mutationRights) -ne 0) {
                throw 'untrusted write access'
            }
        }
    }
    catch {
        throw 'ProtectedOutputRoot must have a trusted owner and no inherited or explicit write access for untrusted principals; this script does not change ACLs.'
    }
    return $fullPath
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed with exit code $LASTEXITCODE." }
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-MsiProductVersion([string]$Value) {
    $parsed = $null
    if ($Value -cnotmatch '^\d+\.\d+\.\d+$' -or
        ![Version]::TryParse($Value, [ref]$parsed) -or
        $parsed.ToString(3) -cne $Value -or
        $parsed.Major -gt 255 -or $parsed.Minor -gt 255 -or $parsed.Build -gt 65535) {
        throw 'PackVersion and InstallerVersion must be the same MSI ProductVersion: major 0–255, minor 0–255, build 0–65535.'
    }
}

# Validate keys first so an invalid/missing key fails before any output directory, package,
# publish, tool install, or other build side effect can be created.
$updateKey = Assert-PrivateP256Key $UpdateSigningKeyPemPath 'Update-feed signing key'
$manifestKey = Assert-PrivateP256Key $HelperManifestSigningKeyPemPath 'Helper-manifest signing key'
if ([string]::Equals($updateKey, $manifestKey, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Update-feed and helper-manifest signing keys must be separate key files.'
}
if ((Get-PrivateKeyPublicFingerprint $updateKey) -ceq (Get-PrivateKeyPublicFingerprint $manifestKey)) {
    throw 'Update-feed and helper-manifest signing keys must contain different P-256 keys.'
}
$updatePublicKey = Assert-MatchingPublicKey $updateKey `
    (Join-Path $script:RepositoryRoot 'Connector.Desktop\Assets\update-public-key.pem') 'Embedded update public key'
$helperPublicKey = Assert-MatchingPublicKey $manifestKey $HelperReleasePublicKeyFile 'HelperReleasePublicKeyFile'

Assert-MsiProductVersion $PackVersion
Assert-MsiProductVersion $InstallerVersion
if ($PackVersion -cne $InstallerVersion) { throw 'PackVersion and InstallerVersion must match.' }
if ($Channel -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]*$') { throw 'Channel contains unsupported characters.' }
$feedUri = $null
if (![Uri]::TryCreate($FeedUrl, [UriKind]::Absolute, [ref]$feedUri) -or
    $feedUri.Scheme -cne 'https' -or $feedUri.UserInfo.Length -ne 0) {
    throw 'FeedUrl must be an HTTPS URL without embedded credentials.'
}
if ($SetupPackageId -cne 'Structura.Connector.Desktop') {
    throw 'Unified releases require the production Structura.Connector.Desktop package ID.'
}

$accessConfig = Assert-EnabledProductionAccessConfig $ConnectorAccessConfigurationPath
$workerPath = Assert-ExistingRegularFile $IfcWorkerPath 'IFC worker'
$bundleRoot = [IO.Path]::GetFullPath($PinnedAssetBundleDirectory)
$netBirdPath = Assert-PinnedAsset (Join-Path $bundleRoot 'netbird_installer_0.79.0_windows_amd64.msi') `
    'netbird_installer_0.79.0_windows_amd64.msi' 23543808 '50F822C0F5F6E54E7618096CDD36B63BF4B169E125C9DA06052FD4FD96115210'
$legacyConnectorPath = Assert-PinnedAsset (Join-Path $bundleRoot 'Connector.Desktop.Setup.msi') `
    'Connector.Desktop.Setup.msi' 114074894 '47B5B3434CED41C3B9A71615906CBAF835B6CB45433554BE612E7BF2D88EF515'
$legacyPlatformPath = Assert-PinnedAsset (Join-Path $bundleRoot 'Platform.Connector.Desktop.Setup.msi') `
    'Platform.Connector.Desktop.Setup.msi' 59929448 'D6666FD1613B16879AB6FDA9912D3476D0BA0BE2A782A7F934772D9F89DEDC3D'
$uniqueInstallerInputs = @(@($netBirdPath, $legacyConnectorPath, $legacyPlatformPath) | Select-Object -Unique)
if ($uniqueInstallerInputs.Count -ne 3) { throw 'The three pinned installer inputs must be separate files.' }
foreach ($requiredPath in @($script:BuildPackageUpdate, $script:BootstrapperProject, $script:MachineHelperProject,
                            $script:ManifestBuilderProject, $script:LaunchBridgeProject, $script:UnifiedSetupProject,
                            $script:UnifiedBundleProject, $script:NetBirdPreflight,
                            $script:NetBirdVerifier, $script:NetBirdLock)) {
    [void](Assert-ExistingRegularFile $requiredPath 'Required release source')
}
$script:NetBirdPreflight = Assert-PinnedSourceHash $script:NetBirdPreflight 'NetBird preflight script' '8BF7CFB9C667374F7F165A37C42C2B47F9FB4476E89A36995358375CA59490C2'
$script:NetBirdVerifier = Assert-PinnedSourceHash $script:NetBirdVerifier 'NetBird verifier script' 'DFFFA2EC3BBDFB57583F66AB651F12259AFFFBCF39AA014B11411F9C152EA834'
$script:NetBirdLock = Assert-PinnedSourceHash $script:NetBirdLock 'NetBird identity lock' 'EE48F195FAE2A08B83C0A44F238C237C79F05F27BCA8DBC995542B1FCD23FA96'
$outputRoot = Assert-ProtectedOutputRoot $ProtectedOutputRoot

# All paths and inputs are now preflighted. Every generated artifact is isolated under a
# fresh child of the caller-supplied protected directory; existing data is never cleared.
$runDirectory = Join-Path $outputRoot ("unified-$PackVersion-" + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $runDirectory) { throw 'Fresh unified release output path already exists.' }
$packageDirectory = Join-Path $runDirectory 'update-package'
$helperPublishDirectory = Join-Path $runDirectory 'machine-helper'
$bootstrapperPublishDirectory = Join-Path $runDirectory 'bootstrapper'
$launchBridgePublishDirectory = Join-Path $runDirectory 'launch-bridge'
$manifestStageDirectory = Join-Path $runDirectory 'manifest-stage'
$setupOutputDirectory = Join-Path $runDirectory 'unified-setup'
$bundleOutputDirectory = Join-Path $runDirectory 'unified-installer'
$bootstrapperArtifactsDirectory = Join-Path $runDirectory 'bootstrapper-artifacts'
$helperArtifactsDirectory = Join-Path $runDirectory 'helper-artifacts'
$launchBridgeArtifactsDirectory = Join-Path $runDirectory 'launch-bridge-artifacts'
$manifestArtifactsDirectory = Join-Path $runDirectory 'manifest-artifacts'
$wixArtifactsDirectory = Join-Path $runDirectory 'wix-artifacts'
$bundleArtifactsDirectory = Join-Path $runDirectory 'bundle-artifacts'
New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
foreach ($protectedDirectory in @($packageDirectory, $helperPublishDirectory, $bootstrapperPublishDirectory,
                                  $launchBridgePublishDirectory, $manifestStageDirectory, $setupOutputDirectory,
                                  $bundleOutputDirectory)) {
    New-Item -ItemType Directory -Path $protectedDirectory -ErrorAction Stop | Out-Null
    [void](Assert-ProtectedOutputRoot $protectedDirectory)
}

$packageParameters = @{
    PackVersion = $PackVersion
    Channel = $Channel
    FeedUrl = $feedUri.AbsoluteUri
    IfcWorkerPath = $workerPath
    UpdateSigningKeyPemPath = $updateKey
    PackageOutputDir = $packageDirectory
}
& $script:BuildPackageUpdate @packageParameters
if (!$?) { throw 'build_package_update.ps1 failed.' }

$assetsPath = Join-Path $packageDirectory "assets.$Channel.json"
if (!(Test-Path -LiteralPath $assetsPath -PathType Leaf)) { throw 'The package builder did not emit its pinned channel asset list.' }
try { $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -ErrorAction Stop }
catch { throw 'The package builder emitted an invalid channel asset list.' }
$installerAssets = @($assets | Where-Object { $_.Type -ceq 'Installer' })
if ($installerAssets.Count -ne 1 -or [IO.Path]::GetFileName([string]$installerAssets[0].RelativeFileName) -cne [string]$installerAssets[0].RelativeFileName) {
    throw 'The package asset list must identify exactly one root-level Setup executable.'
}
$setupSource = Assert-ExistingRegularFile (Join-Path $packageDirectory ([string]$installerAssets[0].RelativeFileName)) 'Velopack Setup.exe'

$releaseProperties = @(
    "-p:UnifiedReleaseBuild=true", "-p:HelperReleasePublicKeyFile=$helperPublicKey",
    "-p:Version=$PackVersion", '-p:RestoreLockedMode=true'
)
$helperArguments = @('publish', $script:MachineHelperProject, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '--artifacts-path', $helperArtifactsDirectory, '-o', $helperPublishDirectory) + $releaseProperties
Invoke-Dotnet $helperArguments
$bootstrapperArguments = @('publish', $script:BootstrapperProject, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '--artifacts-path', $bootstrapperArtifactsDirectory, '-o', $bootstrapperPublishDirectory) + $releaseProperties
Invoke-Dotnet $bootstrapperArguments
$launchBridgeArguments = @('publish', $script:LaunchBridgeProject, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '--artifacts-path', $launchBridgeArtifactsDirectory, '-o', $launchBridgePublishDirectory) + $releaseProperties
Invoke-Dotnet $launchBridgeArguments

foreach ($directory in @($helperPublishDirectory, $bootstrapperPublishDirectory, $launchBridgePublishDirectory)) {
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { throw 'A unified release publish directory was not created.' }
}
$helperExe = Assert-ExistingRegularFile (Join-Path $helperPublishDirectory 'Connector.Upgrade.MachineHelper.exe') 'Published MachineHelper.exe'
$bootstrapperExe = Assert-ExistingRegularFile (Join-Path $bootstrapperPublishDirectory 'Connector.Upgrade.Bootstrapper.exe') 'Published Bootstrapper.exe'
$launchBridgeExe = Assert-ExistingRegularFile (Join-Path $launchBridgePublishDirectory 'Connector.Upgrade.LaunchBridge.exe') 'Published LaunchBridge.exe'
$signedCallerHash = Get-Sha256 $bootstrapperExe
$signedCallerSize = (Get-Item -LiteralPath $bootstrapperExe -ErrorAction Stop).Length
$stagedHelper = Join-Path $manifestStageDirectory 'Connector.Upgrade.MachineHelper.exe'
$stagedSetup = Join-Path $manifestStageDirectory 'Setup.exe'
Copy-Item -LiteralPath $helperExe -Destination $stagedHelper -ErrorAction Stop
Copy-Item -LiteralPath $setupSource -Destination $stagedSetup -ErrorAction Stop

$manifestArguments = @('run', '--project', $script:ManifestBuilderProject, '--configuration', 'Release',
    '--artifacts-path', $manifestArtifactsDirectory, '--', '--helper', $stagedHelper, '--setup', $stagedSetup,
    '--stage', $manifestStageDirectory, '--key', $manifestKey, '--version', $PackVersion,
    '--package-id', $SetupPackageId, '--setup-version', $InstallerVersion, '--schema-version', '4', '--caller', $bootstrapperExe)
Push-Location -LiteralPath $script:RepositoryRoot
try { Invoke-Dotnet $manifestArguments }
finally { Pop-Location }
$manifestPath = Join-Path $manifestStageDirectory 'helper-release.json'
$signaturePath = Join-Path $manifestStageDirectory 'helper-release.sig'
[void](Assert-ExistingRegularFile $manifestPath 'Signed helper release manifest')
[void](Assert-ExistingRegularFile $signaturePath 'Detached helper release signature')

# The manifest pins the protected stage copies; WiX consumes staged Setup.exe and
# the original helper/caller publish paths. Confirm all inputs have the pinned bytes.
if ((Get-Sha256 $stagedHelper) -cne (Get-Sha256 $helperExe) -or
    (Get-Sha256 $stagedSetup) -cne (Get-Sha256 $setupSource) -or
    (Get-Sha256 $bootstrapperExe) -cne $signedCallerHash -or
    (Get-Item -LiteralPath $bootstrapperExe -ErrorAction Stop).Length -ne $signedCallerSize) {
    throw 'A WiX helper, Setup or caller source differs from the bytes signed into the release manifest.'
}

$wixProperties = @(
    "-p:ProductVersion=$InstallerVersion",
    "-p:BootstrapperPublishDir=$bootstrapperPublishDirectory",
    "-p:MachineHelperPublishDir=$helperPublishDirectory",
    "-p:SetupExePath=$stagedSetup",
    "-p:HelperReleaseManifestPath=$manifestPath",
    "-p:HelperReleaseSignaturePath=$signaturePath",
    "-p:NetBirdMsiPath=$netBirdPath",
    "-p:LegacyConnectorMsiPath=$legacyConnectorPath",
    "-p:LegacyPlatformMsiPath=$legacyPlatformPath",
    "-p:ConnectorAccessConfigurationPath=$accessConfig",
    "-p:NetBirdPreflightScriptPath=$script:NetBirdPreflight",
    "-p:NetBirdVerifierScriptPath=$script:NetBirdVerifier",
    "-p:NetBirdIdentityLockPath=$script:NetBirdLock"
)
$wixArguments = @('build', $script:UnifiedSetupProject, '--configuration', 'Release',
    '--artifacts-path', $wixArtifactsDirectory, '-o', $setupOutputDirectory) + $wixProperties
Invoke-Dotnet $wixArguments
$msiOutputs = @(Get-ChildItem -LiteralPath $setupOutputDirectory -Filter '*.msi' -File -Recurse -ErrorAction Stop)
if ($msiOutputs.Count -ne 1 -or $msiOutputs[0].Name -cne 'Connector.Unified.Setup.msi') {
    throw 'WiX did not emit exactly one Connector.Unified.Setup.msi.'
}
if ((Get-Sha256 $bootstrapperExe) -cne $signedCallerHash -or
    (Get-Item -LiteralPath $bootstrapperExe -ErrorAction Stop).Length -ne $signedCallerSize) {
    throw 'The signed caller image changed while WiX built the machine MSI.'
}

# The end-user receives only the Burn EXE. Its machine MSI stages protected files;
# its unelevated bridge then starts the pinned bootstrapper under the original SID.
if ((Get-Sha256 $stagedHelper) -cne (Get-Sha256 $helperExe) -or
    (Get-Sha256 $stagedSetup) -cne (Get-Sha256 $setupSource) -or
    (Get-Sha256 $bootstrapperExe) -cne $signedCallerHash -or
    (Get-Item -LiteralPath $bootstrapperExe -ErrorAction Stop).Length -ne $signedCallerSize) {
    throw 'Installer inputs changed after the signed release manifest was created.'
}
$bundleArguments = @('build', $script:UnifiedBundleProject, '--configuration', 'Release',
    '--artifacts-path', $bundleArtifactsDirectory, '-o', $bundleOutputDirectory,
    "-p:ProductVersion=$InstallerVersion", "-p:UnifiedSetupMsiPath=$($msiOutputs[0].FullName)",
    "-p:LaunchBridgePath=$launchBridgeExe")
Invoke-Dotnet $bundleArguments
$bundleOutputs = @(Get-ChildItem -LiteralPath $bundleOutputDirectory -Filter '*.exe' -File -Recurse -ErrorAction Stop)
if ($bundleOutputs.Count -ne 1 -or $bundleOutputs[0].Name -cne 'Structura.Connector.Installer.exe') {
    throw 'WiX did not emit exactly one Structura.Connector.Installer.exe.'
}
Write-Host 'Unified installer build completed.'
Write-Host "Installer: $($bundleOutputs[0].FullName)"
Write-Host "Internal machine MSI: $($msiOutputs[0].FullName)"
