$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'wait-platform-connector-overlay.ps1')
$script:queries = 0
$script:readyAfter = 1
$script:fixtureAlias = 'wt0'
function Get-NetIPAddress {
    param([string]$IPAddress, [string]$ErrorAction)
    if ($IPAddress -cne '100.108.74.8') { throw 'Unexpected address query' }
    $script:queries++
    if ($script:queries -ge $script:readyAfter) {
        [pscustomobject]@{IPAddress=$IPAddress;AddressState='Preferred';InterfaceAlias=$script:fixtureAlias}
    }
}
function New-Options([bool]$Enabled, [string]$Address = '100.108.74.8') {
    [pscustomobject]@{Enabled=$Enabled;NetBird=[pscustomobject]@{PeerAttestation=[pscustomobject]@{
        Enabled=$true;ListenerIpAddress=$Address
    }}}
}
Wait-PlatformConnectorOverlay $null
Wait-PlatformConnectorOverlay (New-Options $false)
if ($script:queries -ne 0) { throw 'Disabled deployment queried the interface' }
Wait-PlatformConnectorOverlay (New-Options $true)
if ($script:queries -ne 1) { throw 'Ready interface did not start immediately' }
$script:queries = 0; $script:readyAfter = 2
Wait-PlatformConnectorOverlay (New-Options $true)
if ($script:queries -ne 2) { throw 'Delayed interface was not awaited' }
foreach ($bad in @('127.0.0.1','0.0.0.0','100.108.74.8 ')) {
    $before = $script:queries
    try { Wait-PlatformConnectorOverlay (New-Options $true $bad); throw 'Wrong IP accepted' }
    catch { if ($_.Exception.Message -cne 'CONNECTOR_OVERLAY_ADDRESS_UNEXPECTED') { throw } }
    if ($script:queries -ne $before) { throw 'Invalid address reached the interface probe' }
}
$script:queries = 0; $script:readyAfter = 1; $script:fixtureAlias = 'unrelated-interface'
try { Wait-PlatformConnectorOverlay (New-Options $true) -TimeoutSeconds 1; throw 'Foreign interface accepted' }
catch { if ($_.Exception.Message -cne 'CONNECTOR_OVERLAY_START_TIMEOUT') { throw } }
$script:fixtureAlias = 'wt0'; $script:readyAfter = [int]::MaxValue
try { Wait-PlatformConnectorOverlay (New-Options $true) -TimeoutSeconds 1; throw 'Missing IP accepted' }
catch { if ($_.Exception.Message -cne 'CONNECTOR_OVERLAY_START_TIMEOUT') { throw } }
Write-Output 'CONNECTOR_OVERLAY_WAIT_FIXTURES_PASSED; no production interface or service was changed'
