[CmdletBinding()]
param(
    [ValidateSet('Client', 'Server')][string]$Mode = 'Client',
    [switch]$ReportOnly,
    [switch]$FunctionsOnly
)

$ErrorActionPreference = 'Stop'

# Deliberately fixed targets: the SYSTEM task must never execute caller-supplied
# commands or restart an arbitrary service. No model files or VPN keys are read.
function Get-ConnectorVpnRecoveryDecision {
    param(
        [bool]$Installed, [string]$StartMode, [string]$Status,
        [bool]$NetworkReady, [bool]$ServerReachable, [bool]$SmbConnected,
        [bool]$Maintenance, [int]$Failures, [double]$ProcessAgeSeconds,
        [double]$SinceRestartSeconds
    )
    if (!$Installed) { return 'Absent' }
    if ($StartMode -ne 'Auto') { return 'NotAutomatic' }
    if ($Maintenance) { return 'Maintenance' }
    if ($Status -notin @('Running', 'Stopped')) { return 'Transition' }
    if ($ServerReachable) { return 'Healthy' }
    if ($SmbConnected) { return 'SmbInUse' }
    if (!$NetworkReady) { return 'NetworkUnavailable' }
    if ($ProcessAgeSeconds -lt 180) { return 'StartupGrace' }
    if ($Failures -lt 3) { return 'Waiting' }
    if ($SinceRestartSeconds -lt 900) { return 'Cooldown' }
    if ($Status -eq 'Stopped') { return 'Start' }
    return 'Restart'
}

function Test-ConnectorVpnTcp {
    param([int]$Port)
    $client = New-Object Net.Sockets.TcpClient
    try {
        # This address is reachable only through the firm's existing VPN.
        $attempt = $client.BeginConnect('10.77.123.1', $Port, $null, $null)
        if (!$attempt.AsyncWaitHandle.WaitOne(2000)) { return $false }
        $client.EndConnect($attempt)
        return $client.Connected
    } catch { return $false } finally { $client.Dispose() }
}

function Invoke-ConnectorServerNetworkRecovery {
    param([switch]$ReadOnly)
    $service = Get-Service -Name 'AmneziaWGTunnel$awgserver' -ErrorAction SilentlyContinue
    if (!$service -or $service.Status -ne 'Running') { return @{ Decision = 'ServerVpnNotRunning' } }
    $tunnel = Get-NetIPInterface -InterfaceAlias 'awgserver' -AddressFamily IPv4 -ErrorAction SilentlyContinue
    $public = Get-NetIPAddress -InterfaceAlias 'Ethernet' -AddressFamily IPv4 -IPAddress '62.113.36.107' -ErrorAction SilentlyContinue
    $private = Get-NetIPAddress -InterfaceAlias 'awgserver' -AddressFamily IPv4 -IPAddress '10.77.123.1' -ErrorAction SilentlyContinue
    if (!$tunnel -or !$public -or !$private) { return @{ Decision = 'UnexpectedServerNetwork' } }
    $changes = @()
    foreach ($alias in @('Ethernet', 'awgserver')) {
        foreach ($store in @('ActiveStore', 'PersistentStore')) {
            $net = Get-NetIPInterface -InterfaceAlias $alias -AddressFamily IPv4 -PolicyStore $store
            if ($net.WeakHostSend -ne 'Enabled' -or $net.WeakHostReceive -ne 'Enabled') {
                $changes += "$alias/$store"
                if (!$ReadOnly) {
                    Set-NetIPInterface -InterfaceAlias $alias -AddressFamily IPv4 -PolicyStore $store `
                        -WeakHostSend Enabled -WeakHostReceive Enabled
                }
            }
        }
    }
    return @{ Decision = 'ServerNetworkChecked'; Changed = $changes; ReadOnly = [bool]$ReadOnly }
}

function Invoke-ConnectorClientVpnRecovery {
    param([string]$StatePath, [switch]$ReadOnly)
    $serviceName = 'AmneziaWGTunnel$structura-vpn'
    $service = Get-CimInstance Win32_Service -Filter ('Name="' + $serviceName + '"')
    if (!$service) { return @{ Decision = 'Absent' } }
    $now = [DateTimeOffset]::UtcNow
    $state = @{ Failures = 0; FirstFailureUtc = $null; LastRestartUtc = $null }
    if (Test-Path -LiteralPath $StatePath) {
        try {
            $saved = Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            $state.Failures = [int]$saved.Failures
            $state.FirstFailureUtc = $saved.FirstFailureUtc
            $state.LastRestartUtc = $saved.LastRestartUtc
        } catch { # A damaged state file must delay recovery, never accelerate it.
            $state.LastRestartUtc = $now.ToString('o')
        }
    }
    $reachable = (Test-ConnectorVpnTcp -Port 445) -or (Test-ConnectorVpnTcp -Port 443)
    # Even a stale Established SMB connection can contain pending writes.
    # Leave it alone; Windows must finish or time out that connection first.
    $smb = @(Get-NetTCPConnection -ErrorAction Stop | Where-Object {
        $_.State -eq 'Established' -and $_.RemotePort -eq 445 -and $_.RemoteAddress -in @('62.113.36.107', '10.77.123.1')
    }).Count -gt 0
    $network = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
        Where-Object { $_.InterfaceAlias -ne 'structura-vpn' -and $_.NextHop -ne '0.0.0.0' }).Count -gt 0
    $maintenance = @(Get-Process -Name msiexec -ErrorAction SilentlyContinue).Count -gt 0
    $age = 0.0
    if ($service.State -eq 'Running' -and $service.ProcessId -gt 0) {
        $age = ($now.UtcDateTime - (Get-Process -Id $service.ProcessId).StartTime.ToUniversalTime()).TotalSeconds
    } elseif ($state.FirstFailureUtc) {
        $age = ($now - [DateTimeOffset]::Parse($state.FirstFailureUtc)).TotalSeconds
    }
    if ($reachable -or $smb -or !$network -or $maintenance -or $service.StartMode -ne 'Auto') {
        $state.Failures = 0
        $state.FirstFailureUtc = $null
    } else {
        $state.Failures++
        if (!$state.FirstFailureUtc) { $state.FirstFailureUtc = $now.ToString('o') }
    }
    $elapsed = if ($state.LastRestartUtc) { ($now - [DateTimeOffset]::Parse($state.LastRestartUtc)).TotalSeconds } else { [double]::MaxValue }
    $decision = Get-ConnectorVpnRecoveryDecision -Installed $true -StartMode $service.StartMode -Status $service.State `
        -NetworkReady $network -ServerReachable $reachable -SmbConnected $smb -Maintenance $maintenance `
        -Failures $state.Failures -ProcessAgeSeconds $age -SinceRestartSeconds $elapsed
    if (!$ReadOnly) {
        if ($decision -in @('Start', 'Restart')) {
            # Persist the throttle BEFORE attempting recovery, including failed attempts.
            $state.LastRestartUtc = $now.ToString('o')
            $state.Failures = 0
            $state.FirstFailureUtc = $null
        }
        $state | ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding UTF8
        if ($decision -eq 'Restart') {
            # Recheck immediately before changing a live service. Never force-stop it.
            $stillInUse = @(Get-NetTCPConnection -ErrorAction Stop | Where-Object {
                $_.State -eq 'Established' -and $_.RemotePort -eq 445 -and $_.RemoteAddress -in @('62.113.36.107', '10.77.123.1')
            }).Count -gt 0
            if ($stillInUse -or (Test-ConnectorVpnTcp -Port 445) -or (Test-ConnectorVpnTcp -Port 443)) {
                $decision = 'RecoveredBeforeRestart'
            } else { Restart-Service -Name $serviceName -ErrorAction Stop }
        } elseif ($decision -eq 'Start') {
            Start-Service -Name $serviceName -ErrorAction Stop
        }
    }
    return @{ Decision = $decision; TimeUtc = $now.ToString('o'); ServerReachable = $reachable; SmbConnected = $smb; ReadOnly = [bool]$ReadOnly }
}

if (!$FunctionsOnly) {
    $root = Join-Path $env:ProgramData 'StructuraConnectorVpnRecovery'
    if (!$ReportOnly -and !(Test-Path -LiteralPath (Join-Path $root 'installed.json'))) {
        throw 'Install the protected recovery task before running in write mode.'
    }
    $mutex = New-Object Threading.Mutex($false, ('Global\StructuraConnectorVpnRecovery' + $Mode))
    $locked = $false
    try {
        $locked = $mutex.WaitOne(0)
        if (!$locked) { return }
        $result = if ($Mode -eq 'Server') { Invoke-ConnectorServerNetworkRecovery -ReadOnly:$ReportOnly } else {
            Invoke-ConnectorClientVpnRecovery -StatePath (Join-Path $root 'client-state.json') -ReadOnly:$ReportOnly
        }
        $result | ConvertTo-Json -Compress
        if (!$ReportOnly -and ($result.Decision -in @('Start', 'Restart') -or @($result.Changed).Count -gt 0)) {
            $log = Join-Path $root 'recovery.log'
            if ((Test-Path -LiteralPath $log) -and (Get-Item -LiteralPath $log).Length -gt 1MB) {
                [IO.File]::WriteAllText($log, '')
            }
            $result | ConvertTo-Json -Compress | Add-Content -LiteralPath $log -Encoding UTF8
        }
    } finally {
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
