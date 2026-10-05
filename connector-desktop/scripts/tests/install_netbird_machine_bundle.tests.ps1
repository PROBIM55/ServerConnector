$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $scriptsRoot 'install_netbird_machine_bundle.ps1'
. $scriptPath

function Assert-True($Value, [string]$Message) { if (!$Value) { throw $Message } }
function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message Expected '$Expected', got '$Actual'." }
}
function Assert-Throws([scriptblock]$Action, [string]$Message) {
    try { & $Action; throw "Expected failure: $Message" }
    catch { if ($_.Exception.Message -ceq "Expected failure: $Message") { throw } }
}

function Write-Configuration([string]$Path, [hashtable]$Overrides = @{}) {
    $configuration = [ordered]@{
        enabled = $true
        serviceBaseUri = 'https://access.example.test/'
        issuerCertificateSha256 = ('A1' * 32)
        netBirdExecutablePath = ''
        netBirdManagementUri = 'https://vpn.example.test/'
        services = @([ordered]@{
            serviceId = 'control-plane'
            baseUri = 'https://control.example.test/api/platform/connector/access/v1'
            allowedOverlayDestinations = @('100.64.1.10/32')
            healthPath = 'jobs/health'
        })
        smbOverlayDestinations = @('100.64.1.20')
    }
    foreach ($entry in $Overrides.GetEnumerator()) { $configuration[$entry.Key] = $entry.Value }
    $configuration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
}

$projectRoot = Split-Path -Parent $scriptsRoot
$msiPath = Join-Path $projectRoot 'artifacts\netbird-staging\v0.79.0\netbird_installer_0.79.0_windows_amd64.msi'
if (!(Test-Path -LiteralPath $msiPath -PathType Leaf)) { throw 'Pinned NetBird MSI fixture is missing.' }
$payload = @(Get-SourcePayload -InstallerPath $msiPath -ScriptsRoot $scriptsRoot)
Assert-SourcePayload -Payload $payload
Assert-Equal $payload.Count 5 'Bundle payload count.'
Assert-Equal (($payload.relative | Sort-Object) -join '|') (
    @('connector-access.json', 'infra\netbird\v0.79.0-windows-x64.lock.json',
      'installer\netbird_installer_0.79.0_windows_amd64.msi',
      'scripts\netbird_windows_preflight.ps1', 'scripts\verify_netbird_installer.ps1') -join '|'
) 'Bundle payload topology.'

$savedProgramW6432 = $env:ProgramW6432
$savedProgramFiles = $env:ProgramFiles
try {
    $env:ProgramW6432 = 'C:\Untrusted\ProgramFiles'
    $env:ProgramFiles = 'C:\Untrusted\ProgramFiles'
    Assert-True (-not (Get-ProgramFiles64).StartsWith('C:\Untrusted', [StringComparison]::OrdinalIgnoreCase)) 'Program Files must ignore inherited env.'
}
finally { $env:ProgramW6432 = $savedProgramW6432; $env:ProgramFiles = $savedProgramFiles }

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('netbird-bundle-validator-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temporaryRoot)
try {
    $configurationPath = Join-Path $temporaryRoot 'connector-access.json'
    Write-Configuration -Path $configurationPath
    Assert-DeploymentConfiguration -Path $configurationPath
    Write-Configuration -Path $configurationPath -Overrides @{ issuerCertificateSha256 = '1234' }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'Short issuer pin must fail.'
    Write-Configuration -Path $configurationPath -Overrides @{ serviceBaseUri = 'http://access.example.test/' }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'HTTP enrollment URI must fail.'
    Write-Configuration -Path $configurationPath -Overrides @{ netBirdManagementUri = 'https://user:pass@vpn.example.test/' }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'Management credentials must fail.'
    Write-Configuration -Path $configurationPath -Overrides @{ netBirdManagementUri = 'https://vpn.example.test/?tenant=x' }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'Management query must fail.'
    Write-Configuration -Path $configurationPath -Overrides @{ services = @() }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'Missing control plane must fail.'
    Write-Configuration -Path $configurationPath -Overrides @{ services = @([ordered]@{
        serviceId = 'control-plane'; baseUri = 'https://control.example.test/wrong'
        allowedOverlayDestinations = @('100.64.1.10/32'); healthPath = 'jobs/health'
    }) }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'Wrong control-plane path must fail.'
    Write-Configuration -Path $configurationPath -Overrides @{ services = @([ordered]@{
        serviceId = 'control-plane'; baseUri = 'https://control.example.test/api/platform/connector/access/v1'
        allowedOverlayDestinations = @('8.8.8.8/32'); healthPath = 'jobs/health'
    }) }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'Public service destination must fail.'
    Write-Configuration -Path $configurationPath -Overrides @{ smbOverlayDestinations = @('100.64.1.20/32') }
    Assert-Throws { Assert-DeploymentConfiguration -Path $configurationPath } 'SMB CIDR must fail.'
}
finally { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }

$fixture = [pscustomobject]@{
    state = 'absent'; ownership = 'none'; safeToInstall = $true; safeToUse = $false
    reasons = @(); schemaVersion = 1; readOnly = $true; decision = 'safe-to-install-candidate'
}
$assessment = Get-DeploymentAssessment -Preflight $fixture
Assert-Equal $assessment.deploymentDecision 'blocked-signed-launcher-required' 'Deployment gate.'
Assert-Equal $assessment.mutationAllowed $false 'Mutation must be disabled.'
$elevatedAssessment = Get-DeploymentAssessment -Preflight $fixture -ElevatedRequested
Assert-Equal $elevatedAssessment.mutationAllowed $false 'Elevated switch must not enable mutation.'
Assert-True ($elevatedAssessment.reasons -contains 'repository-script-cannot-authorize-elevated-mutation') 'Elevated rejection reason.'

$scriptText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $scriptPath).Path)
foreach ($forbidden in @('Start-Process', '-Verb RunAs', 'msiexec.exe', 'Set-Acl', 'Remove-Item')) {
    Assert-True ($scriptText.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -lt 0) "Installer must not contain mutator: $forbidden"
}

$livePreflight = Invoke-NetBirdPreflight `
    -PreflightPath (@($payload | Where-Object relative -ceq 'scripts\netbird_windows_preflight.ps1')[0].source) `
    -InstallerPath (@($payload | Where-Object relative -ceq "installer\$script:InstallerName")[0].source)
Assert-Equal $livePreflight.readOnly $true 'Live preflight must be read-only.'

[pscustomobject]@{
    passed = 22
    pinnedMsiVerified = $true
    livePreflightState = $livePreflight.state
    elevationRun = $false
    machineInstallRun = $false
    mutationAllowed = $false
} | ConvertTo-Json -Compress
