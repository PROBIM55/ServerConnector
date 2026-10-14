[CmdletBinding()]
param([Parameter(Mandatory)][string]$InputMsi, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'MSI packaging and upgrade verification require a disposable hosted runner.'
}
if ((Get-FileHash -LiteralPath $InputMsi).Hash -ne 'D8302FFE7C7176417C5A54D32830751804CCD9CF1952C6F41FF6E3B4E5898C2C') {
    throw 'Original pilot MSI changed.'
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null
function Invoke-Msi([string[]]$Arguments) {
    $p = Start-Process -FilePath "$env:WINDIR/System32/msiexec.exe" -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -notin @(0,3010)) { throw "Disposable runner MSI operation failed: $($p.ExitCode)" }
}
function Read-Identity([string]$Path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($Path,0)
    $view = $db.OpenView('SELECT `Property`, `Value` FROM `Property`')
    $view.Execute()
    $values = @{}
    while ($null -ne ($row = $view.Fetch())) { $values[$row.StringData(1)] = $row.StringData(2) }
    $view.Close()
    return $values
}
$original = Join-Path $Output 'original-image'
Invoke-Msi @('/a', ('"' + $InputMsi + '"'), '/qn', 'REBOOT=ReallySuppress', ('TARGETDIR="' + $original + '"'),
    '/L*v', ('"' + (Join-Path $Output 'administrative-image.log') + '"'))
function Find-Payload([string]$Name) {
    $matches = @(Get-ChildItem -LiteralPath $original -Recurse -File -Filter $Name)
    if ($matches.Count -eq 0) { throw "Original signed runtime payload missing: $Name" }
    $hashes = @($matches | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash } | Select-Object -Unique)
    if ($hashes.Count -ne 1) { throw "Ambiguous runtime payload: $Name" }
    return $matches[0].FullName
}
$bootstrapper = Split-Path (Find-Payload 'Connector.Upgrade.Bootstrapper.exe') -Parent
$originalHelper = Split-Path (Find-Payload 'Connector.Upgrade.MachineHelper.exe') -Parent
$helper = Join-Path $Output 'helper-runtime'
New-Item -ItemType Directory -Path $helper -Force | Out-Null
$explicitHelperPayloads = @('Setup.exe','netbird_installer_0.79.0_windows_amd64.msi',
    'Connector.Desktop.Setup.msi','Platform.Connector.Desktop.Setup.msi')
foreach ($item in Get-ChildItem -LiteralPath $originalHelper -Force) {
    if ($item.Name -notin $explicitHelperPayloads) { Copy-Item -LiteralPath $item.FullName -Destination $helper -Recurse }
}
$properties = [ordered]@{
    ProductVersion = '1.1.0'
    BootstrapperPublishDir = $bootstrapper
    MachineHelperPublishDir = $helper
    SetupExePath = Find-Payload 'Setup.exe'
    HelperReleaseManifestPath = Find-Payload 'helper-release.json'
    HelperReleaseSignaturePath = Find-Payload 'helper-release.sig'
    NetBirdMsiPath = Find-Payload 'netbird_installer_0.79.0_windows_amd64.msi'
    LegacyConnectorMsiPath = Find-Payload 'Connector.Desktop.Setup.msi'
    LegacyPlatformMsiPath = Find-Payload 'Platform.Connector.Desktop.Setup.msi'
    ConnectorAccessConfigurationPath = Find-Payload 'connector-access.json'
    NetBirdPreflightScriptPath = Find-Payload 'netbird_windows_preflight.ps1'
    NetBirdVerifierScriptPath = Find-Payload 'verify_netbird_installer.ps1'
    NetBirdIdentityLockPath = Find-Payload 'v0.79.0-windows-x64.lock.json'
}
$args = @('build', 'connector-desktop/Connector.Unified.Setup/Connector.Unified.Setup.wixproj', '-c', 'Release',
    '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '-p:BuildInParallel=false', '-o', $Output, '--nologo')
foreach ($key in $properties.Keys) { $args += "-p:$key=$($properties[$key])" }
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw 'MSI repack failed; desktop runtime was not compiled.' }
$final = Join-Path $Output 'Connector.Unified.Setup.msi'
$rebuilt = Join-Path $Output 'rebuilt-image'
Invoke-Msi @('/a', ('"' + $final + '"'), '/qn', 'REBOOT=ReallySuppress', ('TARGETDIR="' + $rebuilt + '"'),
    '/L*v', ('"' + (Join-Path $Output 'rebuilt-image.log') + '"'))
function Get-PayloadMap([string]$Directory) {
    $map = @{}
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName)
        if ($relative -eq 'Connector.Unified.Setup.msi') { continue }
        $map[$relative] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    return $map
}
$oldFiles = Get-PayloadMap $original
$newFiles = Get-PayloadMap $rebuilt
if ($oldFiles.Count -ne $newFiles.Count) { throw 'MSI repack changed the runtime file inventory.' }
foreach ($name in $oldFiles.Keys) {
    if (-not $newFiles.ContainsKey($name) -or $oldFiles[$name] -ne $newFiles[$name]) {
        throw "MSI repack changed a runtime payload: $name"
    }
}
$oldIdentity = Read-Identity $InputMsi
$newIdentity = Read-Identity $final
if ($newIdentity.UpgradeCode -ne '{A487F2B6-0CA7-4D5D-9E38-954D956D8312}' -or
    $newIdentity.ProductVersion -ne '1.1.0' -or $newIdentity.ProductCode -eq $oldIdentity.ProductCode) {
    throw 'Repacked pilot MSI identity is invalid.'
}
function Registration([string]$Code) { return 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $Code }
if ((Test-Path -LiteralPath (Registration $oldIdentity.ProductCode)) -or
    (Test-Path -LiteralPath (Registration $newIdentity.ProductCode))) { throw 'Hosted runner is not clean for the upgrade fixture.' }
try {
    Invoke-Msi @('/i', ('"' + $InputMsi + '"'), '/qn', '/norestart', 'REBOOT=ReallySuppress', '/L*v',
        ('"' + (Join-Path $Output 'install-original-pilot.log') + '"'))
    Invoke-Msi @('/i', ('"' + $final + '"'), '/qn', '/norestart', 'REBOOT=ReallySuppress', '/L*v',
        ('"' + (Join-Path $Output 'upgrade-same-version.log') + '"'))
    if ((Test-Path -LiteralPath (Registration $oldIdentity.ProductCode)) -or
        -not (Test-Path -LiteralPath (Registration $newIdentity.ProductCode))) { throw 'Same-version pilot upgrade did not replace the old registration.' }
    $installedCaller = "$env:ProgramFiles/StructuraConnectorInstaller/Bootstrapper/Connector.Upgrade.Bootstrapper.exe"
    if ((Get-FileHash -LiteralPath $installedCaller).Hash -ne (Get-FileHash -LiteralPath (Find-Payload 'Connector.Upgrade.Bootstrapper.exe')).Hash) {
        throw 'Same-version upgrade did not install the verified caller.'
    }
    [ordered]@{
        sha256 = (Get-FileHash -LiteralPath $final).Hash.ToLowerInvariant()
        productCode = $newIdentity.ProductCode
        upgradeCode = $newIdentity.UpgradeCode
        runtimePayloadsUnchanged = $true
        runtimePayloadCount = $oldFiles.Count
        sameVersionUpgradeVerified = $true
        replacedFixtureProductCode = $oldIdentity.ProductCode
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'staging-proof.json') -Encoding utf8
} finally {
    foreach ($code in @($newIdentity.ProductCode,$oldIdentity.ProductCode)) {
        if (Test-Path -LiteralPath (Registration $code)) {
            Invoke-Msi @('/x', $code, '/qn', '/norestart', 'REBOOT=ReallySuppress')
        }
    }
}
