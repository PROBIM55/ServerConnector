# Library only: dot-source before starting/stopping Platform.Server.
# Does not change services, interfaces, routes, DNS, or configuration.
function Wait-PlatformConnectorOverlay {
    [CmdletBinding()]
    param(
        [AllowNull()][object]$AccessOptions,
        [ValidateRange(1, 300)][int]$TimeoutSeconds = 180
    )
    if ($null -eq $AccessOptions) { return }
    $enabled = $AccessOptions.PSObject.Properties['Enabled']
    if ($null -eq $enabled -or $enabled.Value -ne $true) { return }
    $netBird = $AccessOptions.PSObject.Properties['NetBird']
    if ($null -eq $netBird -or $null -eq $netBird.Value) { throw 'CONNECTOR_OVERLAY_OPTIONS_INVALID' }
    $peer = $netBird.Value.PSObject.Properties['PeerAttestation']
    if ($null -eq $peer -or $null -eq $peer.Value) { throw 'CONNECTOR_OVERLAY_OPTIONS_INVALID' }
    $peerEnabled = $peer.Value.PSObject.Properties['Enabled']
    if ($null -eq $peerEnabled -or $peerEnabled.Value -ne $true) { return }
    $address = $peer.Value.PSObject.Properties['ListenerIpAddress']
    if ($null -eq $address -or $address.Value -isnot [string] -or
        $address.Value -cne '100.108.74.8') { throw 'CONNECTOR_OVERLAY_ADDRESS_UNEXPECTED' }

    $deadline = [Diagnostics.Stopwatch]::StartNew()
    try {
        do {
            $ready = @(Get-NetIPAddress -IPAddress $address.Value -ErrorAction SilentlyContinue |
                Where-Object { [string]$_.AddressState -eq 'Preferred' -and $_.InterfaceAlias -ceq 'wt0' })
            if ($ready.Count -eq 1) { return }
            if ($deadline.Elapsed.TotalSeconds -ge $TimeoutSeconds) { break }
            Start-Sleep -Milliseconds 500
        } while ($true)
    } finally { $deadline.Stop() }
    # The caller must run this gate before stopping a healthy existing Platform process.
    throw 'CONNECTOR_OVERLAY_START_TIMEOUT'
}
