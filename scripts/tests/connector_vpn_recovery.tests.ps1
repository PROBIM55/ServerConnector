$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\connector_vpn_recovery.ps1') -FunctionsOnly
$base = @{ Installed=$true; StartMode='Auto'; Status='Running'; NetworkReady=$true; ServerReachable=$false; SmbConnected=$false; Maintenance=$false; Failures=3; ProcessAgeSeconds=600; SinceRestartSeconds=1800 }
$cases = @(
    @{ Expected='Restart'; Changes=@{} },
    @{ Expected='Healthy'; Changes=@{ServerReachable=$true} },
    @{ Expected='SmbInUse'; Changes=@{SmbConnected=$true} },
    @{ Expected='NetworkUnavailable'; Changes=@{NetworkReady=$false} },
    @{ Expected='Waiting'; Changes=@{Failures=2} },
    @{ Expected='Cooldown'; Changes=@{SinceRestartSeconds=899} },
    @{ Expected='StartupGrace'; Changes=@{ProcessAgeSeconds=179} },
    @{ Expected='NotAutomatic'; Changes=@{StartMode='Disabled'} },
    @{ Expected='Maintenance'; Changes=@{Maintenance=$true} },
    @{ Expected='Absent'; Changes=@{Installed=$false} },
    @{ Expected='Transition'; Changes=@{Status='Stop Pending'} },
    @{ Expected='Start'; Changes=@{Status='Stopped'} }
)
foreach ($case in $cases) {
    $inputValues = $base.Clone()
    foreach ($key in $case.Changes.Keys) { $inputValues[$key] = $case.Changes[$key] }
    $actual = Get-ConnectorVpnRecoveryDecision @inputValues
    if ($actual -ne $case.Expected) { throw "Expected $($case.Expected), got $actual" }
}
"Recovery policy: $($cases.Count) checks passed."

# Exercise the last-moment SMB protection, not just the pure decision function.
$temporaryState = Join-Path ([IO.Path]::GetTempPath()) ('connector-vpn-test-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    @{ Failures=2; FirstFailureUtc=[DateTimeOffset]::UtcNow.AddMinutes(-5).ToString('o'); LastRestartUtc=$null } |
        ConvertTo-Json | Set-Content -LiteralPath $temporaryState -Encoding UTF8
    $script:tcpReads = 0; $script:restarts = 0
    function Get-CimInstance { @{ State='Running'; StartMode='Auto'; ProcessId=123 } }
    function Get-NetTCPConnection {
        $script:tcpReads++
        if ($script:tcpReads -gt 1) { [pscustomobject]@{ State='Established'; RemotePort=445; RemoteAddress='62.113.36.107' } }
    }
    function Get-NetRoute { [pscustomobject]@{InterfaceAlias='Ethernet';NextHop='192.0.2.1'} }
    function Get-Process { param($Name, $Id) if ($Id) { @{StartTime=(Get-Date).AddMinutes(-20)} } }
    function Test-ConnectorVpnTcp { return $false }
    function Restart-Service { $script:restarts++ }
    $result = Invoke-ConnectorClientVpnRecovery -StatePath $temporaryState
    if ($result.Decision -ne 'RecoveredBeforeRestart' -or $script:restarts -ne 0) { throw 'New SMB session was not protected.' }
    'SMB race protection: passed.'
} finally { if (Test-Path -LiteralPath $temporaryState) { Remove-Item -LiteralPath $temporaryState } }

$script:networkChanges = 0
function Get-Service { @{Status='Running'} }
function Get-NetIPAddress { @{IPAddress='present'} }
function Get-NetIPInterface { @{WeakHostSend='Disabled';WeakHostReceive='Disabled'} }
function Set-NetIPInterface { $script:networkChanges++ }
$serverPlan = Invoke-ConnectorServerNetworkRecovery -ReadOnly
if ($serverPlan.Changed.Count -ne 4 -or $script:networkChanges -ne 0) { throw 'Read-only server check changed network settings.' }
$serverApplied = Invoke-ConnectorServerNetworkRecovery
if ($script:networkChanges -ne 4) { throw 'Server guard did not restore both stores on both owned interfaces.' }
function Get-NetIPInterface { @{WeakHostSend='Enabled';WeakHostReceive='Enabled'} }
$serverNoop = Invoke-ConnectorServerNetworkRecovery
if ($serverNoop.Changed.Count -ne 0 -or $script:networkChanges -ne 4) { throw 'Healthy server network was changed.' }
'Server guard: read-only, restoration and idempotency passed.'
