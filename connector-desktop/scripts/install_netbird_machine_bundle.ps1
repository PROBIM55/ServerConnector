[CmdletBinding()]
param(
    [string]$MsiPath,
    [switch]$Elevated,
    [ValidateSet('Json', 'Object')]
    [string]$OutputFormat = 'Json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Security boundary: this repository script is intentionally read-only. A future signed,
# machine-trusted deployment launcher must own elevation, protected staging, MSI execution,
# owner-marker publication, and recovery. Do not add an auto-UAC fallback here.
$script:InstallerName = 'netbird_installer_0.79.0_windows_amd64.msi'
$script:InstallerSha256 = '50F822C0F5F6E54E7618096CDD36B63BF4B169E125C9DA06052FD4FD96115210'
$script:PreflightSha256 = '8BF7CFB9C667374F7F165A37C42C2B47F9FB4476E89A36995358375CA59490C2'
$script:VerifierSha256 = 'DFFFA2EC3BBDFB57583F66AB651F12259AFFFBCF39AA014B11411F9C152EA834'
$script:LockSha256 = 'EE48F195FAE2A08B83C0A44F238C237C79F05F27BCA8DBC995542B1FCD23FA96'

function Get-ProgramFiles64 {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try { $key = $base.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion', $false) }
    finally { $base.Dispose() }
    if ($null -eq $key) { throw 'The machine Windows registry path is unavailable.' }
    try { $path = [string]$key.GetValue('ProgramFilesDir', '') }
    finally { $key.Dispose() }
    if ([string]::IsNullOrWhiteSpace($path) -or ![IO.Path]::IsPathRooted($path)) {
        throw 'The 64-bit Program Files path is unavailable.'
    }
    return [IO.Path]::GetFullPath($path).TrimEnd('\')
}

function Get-MachineBundleLayout {
    $root = Get-ProgramFiles64
    [pscustomobject]@{
        root = Join-Path $root 'Structura Connector\NetBird'
        configuration = Join-Path $root 'Structura Connector\NetBird\connector-access.json'
        preflight = Join-Path $root 'Structura Connector\NetBird\scripts\netbird_windows_preflight.ps1'
        verifier = Join-Path $root 'Structura Connector\NetBird\scripts\verify_netbird_installer.ps1'
        lock = Join-Path $root 'Structura Connector\NetBird\infra\netbird\v0.79.0-windows-x64.lock.json'
        installer = Join-Path $root "Structura Connector\NetBird\installer\$script:InstallerName"
    }
}

function Get-SourcePayload {
    param(
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [string]$ScriptsRoot = $PSScriptRoot
    )
    if ([string]::IsNullOrWhiteSpace($ScriptsRoot)) { throw 'The source scripts root is required.' }
    $scripts = [IO.Path]::GetFullPath($ScriptsRoot)
    $project = Split-Path -Parent $scripts
    $packagedConfiguration = Join-Path $project 'connector-access.json'
    $repositoryConfiguration = Join-Path $project 'Connector.Desktop\connector-access.json'
    $configurationCandidates = @(@($packagedConfiguration, $repositoryConfiguration) |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($configurationCandidates.Count -ne 1) {
        throw 'Exactly one connector-access.json source is required at bundle root or Connector.Desktop.'
    }
    @(
        [pscustomobject]@{ relative = 'scripts\netbird_windows_preflight.ps1'; source = (Join-Path $scripts 'netbird_windows_preflight.ps1'); expectedHash = $script:PreflightSha256 },
        [pscustomobject]@{ relative = 'scripts\verify_netbird_installer.ps1'; source = (Join-Path $scripts 'verify_netbird_installer.ps1'); expectedHash = $script:VerifierSha256 },
        [pscustomobject]@{ relative = 'infra\netbird\v0.79.0-windows-x64.lock.json'; source = (Join-Path $project 'infra\netbird\v0.79.0-windows-x64.lock.json'); expectedHash = $script:LockSha256 },
        [pscustomobject]@{ relative = "installer\$script:InstallerName"; source = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path; expectedHash = $script:InstallerSha256 },
        [pscustomobject]@{ relative = 'connector-access.json'; source = $configurationCandidates[0]; expectedHash = $null }
    )
}

function Get-ValidatedHttpsUri {
    param([string]$Value, [string]$Field, [switch]$RejectQueryAndFragment)
    $uri = $null
    if (![Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne [Uri]::UriSchemeHttps -or [string]::IsNullOrWhiteSpace($uri.Host) -or
        ![string]::IsNullOrEmpty($uri.UserInfo) -or
        ($RejectQueryAndFragment -and (![string]::IsNullOrEmpty($uri.Query) -or ![string]::IsNullOrEmpty($uri.Fragment)))) {
        throw "$Field must be an absolute HTTPS URI without credentials$(if ($RejectQueryAndFragment) { ', query, or fragment' })."
    }
    return $uri
}

function Test-PrivateOverlayAddress {
    param([string]$Value, [switch]$AllowCidr)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $parts = @($Value.Split('/', 2))
    if (!$AllowCidr -and $parts.Count -ne 1) { return $false }
    $address = $null
    if (![Net.IPAddress]::TryParse($parts[0], [ref]$address) -or [Net.IPAddress]::IsLoopback($address)) { return $false }
    $bytes = $address.GetAddressBytes()
    $isV4 = $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork
    $private = if ($isV4) {
        $bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168) -or ($bytes[0] -eq 100 -and ($bytes[1] -band 0xc0) -eq 0x40)
    } else {
        $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6 -and ($bytes[0] -band 0xfe) -eq 0xfc
    }
    if (!$private) { return $false }
    if ($parts.Count -eq 1) { return $true }
    $prefix = 0
    $maximum = if ($isV4) { 32 } else { 128 }
    return [int]::TryParse($parts[1], [ref]$prefix) -and $prefix -ge 0 -and $prefix -le $maximum
}

function Assert-ProtectedServiceDefinition {
    param([Parameter(Mandatory = $true)]$Service)
    foreach ($name in @('serviceId', 'baseUri', 'allowedOverlayDestinations', 'healthPath')) {
        if ($Service.PSObject.Properties.Name -notcontains $name) { throw "Protected service is missing $name." }
    }
    if ([string]::IsNullOrWhiteSpace([string]$Service.serviceId) -or ([string]$Service.serviceId).Length -gt 128) {
        throw 'Protected service id is invalid.'
    }
    $baseUri = Get-ValidatedHttpsUri -Value ([string]$Service.baseUri) -Field 'Protected service baseUri' -RejectQueryAndFragment
    $destinations = @($Service.allowedOverlayDestinations)
    if ($destinations.Count -eq 0) { throw 'Protected service requires an overlay destination.' }
    foreach ($destination in $destinations) {
        if (!(Test-PrivateOverlayAddress -Value ([string]$destination) -AllowCidr)) {
            throw "Invalid protected service overlay destination: $destination"
        }
    }
    $healthPath = [string]$Service.healthPath
    $absoluteHealth = $null
    if ([string]::IsNullOrWhiteSpace($healthPath) -or $healthPath.StartsWith('/') -or $healthPath.Contains('\') -or
        [Uri]::TryCreate($healthPath, [UriKind]::Absolute, [ref]$absoluteHealth)) {
        throw 'Protected service healthPath must be relative.'
    }
    $normalizedBase = [Uri]::new($baseUri.AbsoluteUri.TrimEnd('/') + '/', [UriKind]::Absolute)
    $health = [Uri]::new($normalizedBase, $healthPath)
    if ($health.Scheme -cne $normalizedBase.Scheme -or $health.IdnHost -cne $normalizedBase.IdnHost -or
        $health.Port -ne $normalizedBase.Port -or !$health.AbsolutePath.StartsWith($normalizedBase.AbsolutePath, [StringComparison]::Ordinal)) {
        throw 'Protected service healthPath escapes baseUri.'
    }
}

function Assert-DeploymentConfiguration {
    param([Parameter(Mandatory = $true)][string]$Path)
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($file.Length -le 0 -or $file.Length -gt 65536) { throw 'Invalid connector-access.json size.' }
    $configuration = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    foreach ($name in @('enabled', 'serviceBaseUri', 'issuerCertificateSha256', 'netBirdExecutablePath', 'netBirdManagementUri', 'services', 'smbOverlayDestinations')) {
        if ($configuration.PSObject.Properties.Name -notcontains $name) { throw "connector-access.json is missing $name." }
    }
    if (![bool]$configuration.enabled) { return }
    [void](Get-ValidatedHttpsUri -Value ([string]$configuration.serviceBaseUri) -Field 'serviceBaseUri' -RejectQueryAndFragment)
    [void](Get-ValidatedHttpsUri -Value ([string]$configuration.netBirdManagementUri) -Field 'netBirdManagementUri' -RejectQueryAndFragment)
    if (![regex]::IsMatch([string]$configuration.issuerCertificateSha256, '^[0-9A-Fa-f]{64}$')) {
        throw 'issuerCertificateSha256 must be exactly one SHA-256 hexadecimal value.'
    }
    $expectedCli = [IO.Path]::GetFullPath((Join-Path (Get-ProgramFiles64) 'NetBird\netbird.exe'))
    if (![string]::IsNullOrWhiteSpace([string]$configuration.netBirdExecutablePath) -and
        ![IO.Path]::GetFullPath([string]$configuration.netBirdExecutablePath).Equals($expectedCli, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Only the pinned system NetBird executable is supported.'
    }
    $services = @($configuration.services)
    if ($services.Count -eq 0) { throw 'Enabled connector-access.json requires protected services.' }
    $serviceIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($service in $services) {
        Assert-ProtectedServiceDefinition -Service $service
        if (!$serviceIds.Add([string]$service.serviceId)) { throw 'Protected service ids must be unique.' }
    }
    $controlPlanes = @($services | Where-Object { $_.serviceId -ceq 'control-plane' })
    if ($controlPlanes.Count -ne 1 -or ([Uri]$controlPlanes[0].baseUri).AbsolutePath.TrimEnd('/') -cne '/api/platform/connector/access/v1' -or
        [string]$controlPlanes[0].healthPath -cne 'jobs/health') {
        throw 'Exactly one canonical control-plane service is required.'
    }
    $smbDestinations = @($configuration.smbOverlayDestinations)
    if ($smbDestinations.Count -gt 128) { throw 'Too many SMB overlay destinations.' }
    foreach ($destination in $smbDestinations) {
        if (!(Test-PrivateOverlayAddress -Value ([string]$destination))) {
            throw "Invalid fixed SMB overlay destination: $destination"
        }
    }
}

function Invoke-PinnedInstallerVerification {
    param([string]$VerifierPath, [string]$InstallerPath)
    $output = @(& $VerifierPath -MsiPath $InstallerPath)
    $results = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -contains 'verified' })
    $unexpected = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -notcontains 'verified' })
    if ($results.Count -ne 1 -or $unexpected.Count -ne 0 -or !$results[0].verified -or
        [string]$results[0].version -cne '0.79.0' -or ([string]$results[0].sha256).ToUpperInvariant() -cne $script:InstallerSha256) {
        throw 'Pinned NetBird installer verification failed.'
    }
    return $results[0]
}

function Assert-SourcePayload {
    param([Parameter(Mandatory = $true)][object[]]$Payload)
    if ($Payload.Count -ne 5) { throw 'The NetBird bundle payload is incomplete.' }
    foreach ($item in $Payload) {
        if (!(Test-Path -LiteralPath $item.source -PathType Leaf)) { throw "Missing bundle input: $($item.relative)" }
        $actual = (Get-FileHash -LiteralPath $item.source -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($null -ne $item.expectedHash -and $actual -cne $item.expectedHash) { throw "Bundle input hash mismatch: $($item.relative)" }
        $item | Add-Member -NotePropertyName sha256 -NotePropertyValue $actual -Force
    }
    $configuration = @($Payload | Where-Object relative -ceq 'connector-access.json')[0]
    Assert-DeploymentConfiguration -Path $configuration.source
    $verifier = @($Payload | Where-Object relative -ceq 'scripts\verify_netbird_installer.ps1')[0]
    $installer = @($Payload | Where-Object relative -ceq "installer\$script:InstallerName")[0]
    [void](Invoke-PinnedInstallerVerification -VerifierPath $verifier.source -InstallerPath $installer.source)
}

function Invoke-NetBirdPreflight {
    param([string]$PreflightPath, [string]$InstallerPath)
    $output = @(& $PreflightPath -MsiPath $InstallerPath -OutputFormat Object)
    $results = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -contains 'decision' })
    $unexpected = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -notcontains 'decision' })
    if ($results.Count -ne 1 -or $unexpected.Count -ne 0 -or $results[0].schemaVersion -ne 1 -or !$results[0].readOnly) {
        throw 'NetBird preflight returned an invalid result.'
    }
    return $results[0]
}

function Get-DeploymentAssessment {
    param([Parameter(Mandatory = $true)]$Preflight, [switch]$ElevatedRequested)
    $reasons = @('signed-deployment-launcher-required')
    if ($ElevatedRequested) { $reasons += 'repository-script-cannot-authorize-elevated-mutation' }
    if (!$Preflight.safeToInstall -and !$Preflight.safeToUse) { $reasons += @($Preflight.reasons) }
    [pscustomobject]@{
        schemaVersion = 1
        readOnly = $true
        deploymentDecision = 'blocked-signed-launcher-required'
        mutationAllowed = $false
        elevatedRequested = [bool]$ElevatedRequested
        machineState = $Preflight.state
        ownership = $Preflight.ownership
        target = Get-MachineBundleLayout
        reasons = @($reasons | Select-Object -Unique)
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($MsiPath)) { throw 'MsiPath is required.' }
    $payload = @(Get-SourcePayload -InstallerPath $MsiPath)
    Assert-SourcePayload -Payload $payload
    $preflight = @($payload | Where-Object relative -ceq 'scripts\netbird_windows_preflight.ps1')[0]
    $installer = @($payload | Where-Object relative -ceq "installer\$script:InstallerName")[0]
    $assessment = Get-DeploymentAssessment -Preflight (Invoke-NetBirdPreflight -PreflightPath $preflight.source -InstallerPath $installer.source) -ElevatedRequested:$Elevated
    if ($OutputFormat -eq 'Object') { $assessment } else { $assessment | ConvertTo-Json -Depth 6 }
    exit 3
}
