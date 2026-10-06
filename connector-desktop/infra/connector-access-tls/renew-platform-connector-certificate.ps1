[CmdletBinding()]
param(
    [string]$SourcePfxPath = 'C:\Platform\runtime\connector-access\renewed-server.pfx',
    [string]$ActivePfxPath = 'C:\Platform\runtime\connector-access\server.pfx',
    [string]$PasswordEnvironmentVariable = 'PLATFORM_CONNECTOR_SERVER_PFX_PASSWORD',
    [string]$SecretFilePath = '',
    [string]$DeployRoot = 'C:\Platform',
    [int]$HealthPort = 24443,
    [ValidateRange(10, 300)][int]$TimeoutSeconds = 90,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-NoReparsePath([string]$Path, [switch]$MayNotExist) {
    $full = [IO.Path]::GetFullPath($Path)
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point is forbidden in certificate path: $cursor" }
        } elseif (-not $MayNotExist -and $cursor -eq $full) { throw "Certificate path does not exist: $full" }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full
}

function Assert-PrivateFileAcl([string]$Path, [string[]]$AdditionalAllowedSid = @(), [string[]]$RequireFullControlSid = @()) {
    if ($env:OS -ne 'Windows_NT') { return }
    $acl = Get-Acl -LiteralPath $Path
    Assert-NonNullDacl $acl 'Certificate file'
    $allowed = @('S-1-5-18', 'S-1-5-32-544') + $AdditionalAllowedSid
    $ownerSid = $acl.Owner
    try { if ($ownerSid -notmatch '^S-1-' ) { $ownerSid = ([Security.Principal.NTAccount]::new($ownerSid).Translate([Security.Principal.SecurityIdentifier])).Value } }
    catch { throw 'Certificate file owner cannot be resolved safely.' }
    if ($ownerSid -notin $allowed) { throw 'Certificate file owner is not an approved runtime identity.' }
    $granted = @{}
    foreach ($ace in $acl.Access) {
        $sid = $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($ace.AccessControlType -eq 'Allow' -and $sid -notin $allowed) {
            throw "Certificate file ACL grants access to an unapproved identity: $($ace.IdentityReference.Value)"
        }
        if ($ace.AccessControlType -eq 'Allow' -and ($sid -in @('S-1-5-18','S-1-5-32-544') -or (($ace.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl))) { $granted[$sid] = $true }
    }
    foreach ($sid in $RequireFullControlSid) { if (-not $granted.ContainsKey($sid)) { throw 'A required file ACL identity lacks FullControl.' } }
}

function Assert-NonNullDacl([object]$Acl, [string]$Description) {
    try {
        $sddl = $Acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
        $raw = [Security.AccessControl.RawSecurityDescriptor]::new($sddl)
    } catch { throw "$Description ACL cannot be inspected safely." }
    if ($null -eq $raw.DiscretionaryAcl) { throw "$Description ACL must not be a null DACL." }
}

function Assert-NoUnapprovedWriteAcl([string]$Path, [string[]]$AllowedSid) {
    if ($env:OS -ne 'Windows_NT') { return }
    $acl = Get-Acl -LiteralPath $Path
    Assert-NonNullDacl $acl 'Protected path'
    $ownerSid = $acl.Owner
    try { if ($ownerSid -notmatch '^S-1-') { $ownerSid = ([Security.Principal.NTAccount]::new($ownerSid).Translate([Security.Principal.SecurityIdentifier])).Value } }
    catch { throw 'Protected path owner cannot be resolved safely.' }
    if ($ownerSid -notin $AllowedSid) { throw "Unapproved owner exists on protected path: $Path" }
    foreach ($ace in $acl.Access) {
        if ($ace.AccessControlType -ne 'Allow') { continue }
        $sid = $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        $rights = [int]$ace.FileSystemRights
        $writeMask = [int][Security.AccessControl.FileSystemRights]::WriteData -bor
            [int][Security.AccessControl.FileSystemRights]::AppendData -bor
            [int][Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
            [int][Security.AccessControl.FileSystemRights]::WriteAttributes -bor
            [int][Security.AccessControl.FileSystemRights]::Delete -bor
            [int][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
            [int][Security.AccessControl.FileSystemRights]::ChangePermissions -bor
            [int][Security.AccessControl.FileSystemRights]::TakeOwnership -bor
            0x40000000 -bor 0x10000000
        if ($sid -notin $AllowedSid -and (($rights -band $writeMask) -ne 0)) { throw "Unapproved write access exists on protected path: $Path" }
    }
}

function Assert-ProtectedDirectoryPath([string]$Path, [string[]]$AllowedSid, [string]$TrustedRoot) {
    $full = Assert-NoReparsePath $Path
    $root = [IO.Path]::GetFullPath($TrustedRoot).TrimEnd('\','/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Trusted runtime root does not exist.' }
    [void](Assert-NoReparsePath $root)
    if ($full -ine $root -and -not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Protected directory must be inside its trusted runtime root.' }
    $allowed = @($AllowedSid) + @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $cursor = $full
    while ($cursor) {
        if ($env:OS -eq 'Windows_NT' -and (Test-Path -LiteralPath $cursor -PathType Container)) {
            Assert-NoUnapprovedWriteAcl $cursor $allowed
        }
        if ($cursor -ieq $root) { break }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $full
}

function Get-ConnectorAccessCertificatePassword([string]$SecretFilePath, [string]$SecretName, [string]$Root, [string[]]$AllowedSid) {
    if ($SecretName -cne 'PLATFORM_CONNECTOR_ACCESS_SERVER_CERT_PASSWORD') { throw 'Unsupported certificate secret name.' }
    try {
        if (-not [IO.Path]::IsPathRooted($SecretFilePath)) { throw 'invalid path' }
        $fullPath = [IO.Path]::GetFullPath($SecretFilePath)
        $expectedPath = [IO.Path]::GetFullPath((Join-Path (Join-Path $Root 'runtime\connector-access') 'service-secrets.json'))
        if (-not [string]::Equals($SecretFilePath, $fullPath, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($fullPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'noncanonical path' }
        [void](Assert-NoReparsePath $fullPath)
        $directory = Split-Path -Parent $fullPath
        [void](Assert-ProtectedDirectoryPath $directory $AllowedSid $Root)
        Assert-PrivateFileAcl $fullPath -AdditionalAllowedSid $AllowedSid
    } catch { throw 'SERVICE_SECRET_PATH_OR_ACL_INVALID' }

    try {
        $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            if ($stream.Length -lt 2 -or $stream.Length -gt 65536) { throw 'size' }
            $bytes = New-Object byte[] ([int]$stream.Length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
                if ($read -le 0) { throw 'short read' }
                $offset += $read
            }
            if ($stream.ReadByte() -ne -1) { throw 'grew during read' }
        } finally { $stream.Dispose() }
        $encoding = New-Object System.Text.UTF8Encoding($false, $true)
        $jsonText = $encoding.GetString($bytes)
        $document = ConvertFrom-Json -InputObject $jsonText -ErrorAction Stop
    } catch { throw 'SERVICE_SECRET_JSON_INVALID' }
    if ($null -eq $document -or $document -is [Array] -or $document -is [string] -or $document -is [ValueType]) { throw 'SERVICE_SECRET_JSON_INVALID' }
    $properties = @($document.PSObject.Properties | Where-Object { $_.Name -ceq $SecretName })
    if ($properties.Count -ne 1) { throw 'SERVICE_SECRET_REQUIRED_VALUE_MISSING' }
    if ($properties[0].Value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$properties[0].Value)) { throw 'SERVICE_SECRET_REQUIRED_VALUE_INVALID' }
    return [string]$properties[0].Value
}

function Set-PrivateFileAcl([string]$Path, [string[]]$AdditionalAllowedSid = @(), [string[]]$FullControlSid = @()) {
    if ($env:OS -ne 'Windows_NT') { return }
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    & $icacls $Path '/reset' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not reset the protected certificate file ACL.' }
    $arguments = @('/inheritance:r','/grant:r')
    foreach ($sidText in (@('S-1-5-18', 'S-1-5-32-544') + $AdditionalAllowedSid | Select-Object -Unique)) {
        if ([string]$sidText -notmatch '^S-1-\d+(?:-\d+)+$') { throw 'A protected file ACL identity did not resolve to a SID.' }
        $rights = if ($sidText -in (@('S-1-5-18','S-1-5-32-544') + $FullControlSid)) { 'F' } else { 'RX' }
        $arguments += ('*' + $sidText + ':' + $rights)
    }
    & $icacls $Path @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not apply the protected certificate file ACL.' }
    Assert-PrivateFileAcl $Path -AdditionalAllowedSid $AdditionalAllowedSid
}

function Set-StageFileAcl([string]$Path, [string[]]$RuntimeSid, [string]$WriterSid) {
    if ($env:OS -ne 'Windows_NT') { return }
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sidText in (@('S-1-5-18', 'S-1-5-32-544', $WriterSid) + $RuntimeSid | Select-Object -Unique)) {
        if ([string]$sidText -notmatch '^S-1-\d+-\d+(?:-\d+)*$') { throw 'Runtime or hook process identity did not resolve to a SID.' }
        $identity = [Security.Principal.SecurityIdentifier]::new($sidText)
        $rights = if ($sidText -in @('S-1-5-18','S-1-5-32-544',$WriterSid)) { [Security.AccessControl.FileSystemRights]::FullControl } else { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, $rights, 'Allow')
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    Assert-PrivateFileAcl $Path -AdditionalAllowedSid (@($RuntimeSid,$WriterSid))
}

function Assert-ActivatedCertificateAcl([string]$Path, [string[]]$RuntimeSid, [string]$WriterSid) {
    Assert-PrivateFileAcl $Path -AdditionalAllowedSid (@($RuntimeSid,$WriterSid)) -RequireFullControlSid @($WriterSid)
}

function Get-ValidatedServerCertificate([string]$Path, [string]$Password, [string[]]$AdditionalAllowedSid = @()) {
    $full = Assert-NoReparsePath $Path
    $info = Get-Item -LiteralPath $full -Force
    if ($info.PSIsContainer -or $info.Length -gt 1048576 -or $info.Length -lt 512) { throw 'Server PFX must be a regular file between 512 bytes and 1 MiB.' }
    Assert-PrivateFileAcl $full -AdditionalAllowedSid $AdditionalAllowedSid
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($full, $Password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        $now = [DateTime]::UtcNow
        if (-not $cert.HasPrivateKey -or $cert.NotBefore.ToUniversalTime() -gt $now -or $cert.NotAfter.ToUniversalTime() -le $now) { throw 'Server PFX needs a private key and a currently valid certificate.' }
        $eku = @($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | ForEach-Object { $_.EnhancedKeyUsages | ForEach-Object Value })
        if ('1.3.6.1.5.5.7.3.1' -notin $eku) { throw 'Certificate does not allow TLS Web Server Authentication.' }
        $keyUsage = @($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' })
        if ($keyUsage.Count -and (([Security.Cryptography.X509Certificates.X509KeyUsageExtension]$keyUsage[0]).KeyUsages -band ([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment)) -eq 0) {
            throw 'Certificate key usage does not allow TLS server authentication.'
        }
        $sanExtension = @($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' })
        if (-not $sanExtension.Count) { throw 'Certificate has no Subject Alternative Name extension.' }
        $san = Get-DnsSubjectAlternativeNames $sanExtension[0].RawData
        foreach ($hostName in @('connector-access.structura-most.ru', 'connector-gateway.structura-most.ru')) {
            if (-not @($san | Where-Object { [string]::Equals($_, $hostName, [StringComparison]::OrdinalIgnoreCase) }).Count) { throw "Required DNS SAN missing: $hostName" }
        }
        $constraints = @($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
        if ($constraints.Count -and $constraints[0].CertificateAuthority) { throw 'A CA certificate cannot be used as the TLS server certificate.' }
        return $cert
    } catch { $cert.Dispose(); throw }
}

function Read-DerElement([byte[]]$Data, [ref]$Offset, [int]$Limit) {
    if ($Offset.Value -ge $Limit) { throw 'Malformed SAN DER element.' }
    $tag = [int]$Data[$Offset.Value]; $Offset.Value++
    if ($Offset.Value -ge $Limit) { throw 'Malformed SAN DER length.' }
    $first = [int]$Data[$Offset.Value]; $Offset.Value++
    if ($first -lt 128) { $length = $first }
    else {
        $count = $first -band 127
        if ($count -lt 1 -or $count -gt 4 -or $Offset.Value + $count -gt $Limit -or $Data[$Offset.Value] -eq 0) { throw 'Unsupported or non-canonical SAN DER length.' }
        $length = 0
        for ($i = 0; $i -lt $count; $i++) { $length = ($length * 256) + [int]$Data[$Offset.Value]; $Offset.Value++ }
        if ($length -lt 128) { throw 'Non-canonical SAN DER length.' }
    }
    if ($length -lt 0 -or $Offset.Value + $length -gt $Limit) { throw 'SAN DER element exceeds its container.' }
    return [pscustomobject]@{ Tag=$tag; Offset=$Offset.Value; Length=$length; End=($Offset.Value + $length) }
}

function Get-DnsSubjectAlternativeNames([byte[]]$RawData) {
    $offset = 0
    $sequence = Read-DerElement $RawData ([ref]$offset) $RawData.Length
    if ($sequence.Tag -ne 0x30 -or $sequence.End -ne $RawData.Length) { throw 'Invalid SAN sequence.' }
    $names = [System.Collections.Generic.List[string]]::new()
    while ($offset -lt $sequence.End) {
        $element = Read-DerElement $RawData ([ref]$offset) $sequence.End
        if ($element.Tag -eq 0x82) {
            for ($i = $element.Offset; $i -lt $element.End; $i++) { if ($RawData[$i] -gt 127) { throw 'DNS SAN is not IA5 ASCII.' } }
            $names.Add([Text.Encoding]::ASCII.GetString($RawData, $element.Offset, $element.Length))
        }
        $offset = $element.End
    }
    return ,$names.ToArray()
}

function Get-ExactPlatformServerProcesses([string]$RuntimeRoot) {
    $runtime = [IO.Path]::GetFullPath((Join-Path $RuntimeRoot 'runtime')).TrimEnd('\','/')
    $dll = Join-Path $runtime 'current\Platform.Server.dll'
    $exe = Join-Path $runtime 'current\Platform.Server.exe'
    $dotnet = Join-Path $RuntimeRoot 'dotnet\dotnet.exe'
    @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        ($_.Name -ieq 'dotnet.exe' -and $_.ExecutablePath -ieq $dotnet -and $_.CommandLine -match ('(?i)(?:^|\s|")' + [regex]::Escape($dll) + '(?:"|\s|$)')) -or
        ($_.Name -ieq 'Platform.Server.exe' -and $_.ExecutablePath -ieq $exe)
    })
}

function Assert-PlatformTaskAction([string]$RuntimeRoot) {
    $task = Get-ScheduledTask -TaskName 'PlatformServerApp' -ErrorAction Stop
    $launcher = [IO.Path]::GetFullPath((Join-Path (Join-Path $RuntimeRoot 'runtime') 'runtime_launch.ps1'))
    $actions = @($task.Actions)
    if ($actions.Count -ne 1) { throw 'PlatformServerApp must have exactly one action targeting the verified Platform runtime launcher.' }
    $action = $actions[0]
    $systemRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $knownPowerShell = @('powershell.exe', [IO.Path]::GetFullPath((Join-Path $systemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')))
    if ([string]$action.Execute -notin $knownPowerShell) { throw 'PlatformServerApp action executable is not the approved Windows PowerShell binary.' }
    $tokens = [System.Collections.Generic.List[string]]::new()
    foreach ($match in [regex]::Matches([string]$action.Arguments, '"([^"\\]*(?:\\.[^"\\]*)*)"|([^\s]+)')) {
        if ($match.Groups[1].Success) { $tokens.Add($match.Groups[1].Value) } else { $tokens.Add($match.Groups[2].Value) }
    }
    $index = 0
    while ($index -lt $tokens.Count) {
        $flag = $tokens[$index]
        if ($flag -ieq '-NoProfile' -or $flag -ieq '-NoLogo' -or $flag -ieq '-NonInteractive') { $index++; continue }
        if ($flag -ieq '-ExecutionPolicy') {
            if ($index + 1 -ge $tokens.Count -or $tokens[$index + 1] -notin @('Unrestricted','RemoteSigned','AllSigned','Restricted','Bypass')) { throw 'PlatformServerApp has an unsupported PowerShell execution-policy argument.' }
            $index += 2; continue
        }
        break
    }
    if (($index + 2) -ne $tokens.Count -or $index -ge $tokens.Count -or $tokens[$index] -ine '-File' -or [IO.Path]::GetFullPath($tokens[$index + 1]) -ine $launcher) { throw 'PlatformServerApp must invoke only the exact runtime launcher through -File with no trailing script arguments.' }
    $principal = [string]$task.Principal.UserId
    if ([string]::IsNullOrWhiteSpace($principal)) { throw 'PlatformServerApp has no resolvable runtime principal.' }
    try {
        $sid = if ($principal -match '^S-1-') { ([Security.Principal.SecurityIdentifier]::new($principal)).Value }
            else { ([Security.Principal.NTAccount]::new($principal).Translate([Security.Principal.SecurityIdentifier])).Value }
        if ($sid -in @('S-1-1-0','S-1-5-11','S-1-5-32-545')) { throw 'PlatformServerApp must not run as a broad built-in group.' }
        return $sid
    } catch { throw 'PlatformServerApp runtime principal could not be resolved to a SID.' }
}

function Assert-SupportedRuntime {
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion -lt [Version]'5.1') { throw 'TLS renewal hook requires Windows PowerShell 5.1.' }
    $framework = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop
    if ([int]$framework.Release -lt 528040) { throw 'TLS renewal hook requires .NET Framework 4.8 or newer.' }
}

function Invoke-PlatformRestart([string]$RuntimeRoot, [int]$Timeout) {
    [void](Assert-PlatformTaskAction $RuntimeRoot)
    Stop-ScheduledTask -TaskName 'PlatformServerApp' -ErrorAction Stop
    $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Min($Timeout, 30))
    do {
        $taskState = (Get-ScheduledTask -TaskName 'PlatformServerApp' -ErrorAction Stop).State
        if ($taskState -ne 'Running') { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $taskDeadline)
    if ($taskState -eq 'Running') { throw 'PlatformServerApp launcher task did not stop.' }
    $before = @(Get-ExactPlatformServerProcesses $RuntimeRoot)
    if ($before.Count -gt 1) { throw 'Multiple exact Platform.Server processes were found; refusing restart.' }
    foreach ($process in $before) {
        $fresh = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ProcessId)" -ErrorAction Stop
        if (-not $fresh -or $fresh.CreationDate -ne $process.CreationDate -or $fresh.ExecutablePath -cne $process.ExecutablePath -or $fresh.CommandLine -cne $process.CommandLine) { throw 'Platform process identity changed during restart preflight.' }
        Stop-Process -Id $fresh.ProcessId -Force -ErrorAction Stop
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Min($Timeout, 30))
    do {
        if (@(Get-ExactPlatformServerProcesses $RuntimeRoot).Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (@(Get-ExactPlatformServerProcesses $RuntimeRoot).Count) { throw 'The exact Platform.Server process is still running.' }
    Start-ScheduledTask -TaskName 'PlatformServerApp' -ErrorAction Stop
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Min($Timeout, 30))
    do {
        $started = @(Get-ExactPlatformServerProcesses $RuntimeRoot)
        if ($started.Count -gt 1) { throw 'More than one exact Platform.Server process appeared after task start.' }
        if ($started.Count -eq 1) { return $started }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'PlatformServerApp did not create the exact Platform.Server process.'
}

function Wait-ConnectorAsyncResult([IAsyncResult]$Operation, [DateTimeOffset]$Deadline, [scriptblock]$Complete) {
    $remaining = [int][Math]::Ceiling(($Deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
    if ($remaining -le 0 -or -not $Operation.AsyncWaitHandle.WaitOne($remaining)) { throw 'HTTPS health operation exceeded its overall deadline.' }
    return (& $Complete $Operation)
}

function Test-ConnectorTlsHealth([string]$HostName, [int]$Port, [int]$Timeout, [string]$ExpectedThumbprint = '', [scriptblock]$Probe = $null) {
    if ($Probe) { return (& $Probe $HostName $Port $ExpectedThumbprint) }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Timeout)
    $last = 'listener not ready'
    try {
        do {
            $tcp = $null
            $tls = $null
            try {
                $tcp = [Net.Sockets.TcpClient]::new()
                $connect = $tcp.BeginConnect([Net.IPAddress]::Loopback, $Port, $null, $null)
                [void](Wait-ConnectorAsyncResult $connect $deadline { param($op) $tcp.EndConnect($op) }.GetNewClosure())
                $tls = [Net.Security.SslStream]::new($tcp.GetStream(), $false)
                # This overload uses the OS chain and target-host validators; no callback or bypass is installed.
                $handshake = $tls.BeginAuthenticateAsClient($HostName, $null, $null)
                [void](Wait-ConnectorAsyncResult $handshake $deadline { param($op) $tls.EndAuthenticateAsClient($op) }.GetNewClosure())
                $observedThumbprint = $tls.RemoteCertificate.GetCertHashString()
                if ($ExpectedThumbprint -and $observedThumbprint -cne $ExpectedThumbprint) { throw 'HTTPS listener served a different certificate than the activated PFX.' }
                $request = [Text.Encoding]::ASCII.GetBytes("GET /api/platform/health HTTP/1.1`r`nHost: $HostName`r`nConnection: close`r`nAccept: application/json`r`n`r`n")
                $write = $tls.BeginWrite($request, 0, $request.Length, $null, $null)
                [void](Wait-ConnectorAsyncResult $write $deadline { param($op) $tls.EndWrite($op) }.GetNewClosure())
                $buffer = [IO.MemoryStream]::new()
                try {
                    $readBuffer = New-Object byte[] 4096
                    while ($true) {
                        $read = $tls.BeginRead($readBuffer, 0, $readBuffer.Length, $null, $null)
                        $count = Wait-ConnectorAsyncResult $read $deadline { param($op) $tls.EndRead($op) }.GetNewClosure()
                        if ($count -eq 0) { break }
                        if ($buffer.Length + $count -gt 65536) { throw 'HTTPS health response exceeded 64 KiB.' }
                        $buffer.Write($readBuffer, 0, $count)
                    }
                    $text = [Text.Encoding]::UTF8.GetString($buffer.ToArray())
                } finally { $buffer.Dispose() }
                $separator = $text.IndexOf("`r`n`r`n", [StringComparison]::Ordinal)
                if ($separator -lt 0) { throw 'HTTPS health response has no complete header.' }
                $headers = $text.Substring(0, $separator)
                $status = ($headers -split "`r`n", 2)[0]
                if ($status -notmatch '^HTTP/1\.[01] 200(?:\s|$)') { throw "health endpoint returned a non-200 status: $status" }
                $body = $text.Substring($separator + 4)
                if ($headers -match '(?im)^Transfer-Encoding:\s*chunked\s*$') {
                    $decoded = [Text.StringBuilder]::new()
                    $lines = $body -split "`r`n"
                    for ($i = 0; $i -lt $lines.Length; ) {
                        $sizeText = ($lines[$i++] -split ';', 2)[0]
                        $size = [Convert]::ToInt32($sizeText, 16)
                        if ($size -eq 0) { break }
                        if ($size -lt 0 -or $i -ge $lines.Length -or $lines[$i].Length -lt $size) { throw 'Malformed chunked health response.' }
                        [void]$decoded.Append($lines[$i].Substring(0, $size)); $i++
                    }
                    $body = $decoded.ToString()
                }
                $health = $body | ConvertFrom-Json
                if ($health.ok -eq $true) { return $true }
                throw 'health endpoint did not report ok=true'
            } catch { $last = $_.Exception.GetBaseException().Message }
            finally { if ($tls) { $tls.Dispose() }; if ($tcp) { $tcp.Dispose() } }
            $remaining = [int][Math]::Ceiling(($deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds)
            if ($remaining -gt 0) { Start-Sleep -Milliseconds ([Math]::Min(750, $remaining)) }
        } while ([DateTimeOffset]::UtcNow -lt $deadline)
        throw "HTTPS health check failed: $last"
    } catch { throw }
}

function Invoke-ConnectorAccessTlsRenewal {
    param([string]$Incoming,[string]$Active,[string]$SecretName,[string]$SecretFilePath='',[string]$Root,[int]$Port,[int]$Timeout)
    if ($SecretFilePath -ceq '') {
        $password = [Environment]::GetEnvironmentVariable($SecretName, 'Machine')
        if ([string]::IsNullOrEmpty($password)) { $password = [Environment]::GetEnvironmentVariable($SecretName, 'Process') }
        if ([string]::IsNullOrEmpty($password)) { throw "Required certificate password environment variable is missing: $SecretName" }
    } else {
        $runtimeSid = Assert-PlatformTaskAction $Root
        $writerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $password = Get-ConnectorAccessCertificatePassword -SecretFilePath $SecretFilePath -SecretName $SecretName -Root $Root -AllowedSid @($runtimeSid,$writerSid)
    }
    $incomingFull = Assert-NoReparsePath $Incoming
    $activeFull = Assert-NoReparsePath $Active -MayNotExist
    $directory = Split-Path -Parent $activeFull
    if ([IO.Path]::GetFullPath((Split-Path -Parent $incomingFull)) -cne [IO.Path]::GetFullPath($directory)) { throw 'Incoming and active PFX files must share the same directory for atomic replacement.' }
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw 'Active certificate directory must already exist and be protected.' }
    [void](Assert-NoReparsePath $directory)
    $deployLockPath = Join-Path (Join-Path $Root 'deploy') 'platform-server.deploy.lock'
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $deployLockPath) -PathType Container)) { throw 'Shared Platform release-lock directory does not exist.' }
    [void](Assert-NoReparsePath $deployLockPath -MayNotExist)
    $runtimeSid = Assert-PlatformTaskAction $Root
    $writerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($writerSid -in @('S-1-1-0','S-1-5-11','S-1-5-32-545')) { throw 'Certificate renewal must not run as a broad built-in group.' }
    if ($writerSid -eq $runtimeSid -and $writerSid -notin @('S-1-5-18','S-1-5-32-544')) { throw 'A non-administrative Platform runtime identity cannot also be the certificate writer; use a separate protected renewal identity.' }
    $allowedWriteSid = @($runtimeSid,$writerSid,'S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    [void](Assert-ProtectedDirectoryPath $directory $allowedWriteSid $Root)
    [void](Assert-ProtectedDirectoryPath (Split-Path -Parent $deployLockPath) $allowedWriteSid $Root)
    if (Test-Path -LiteralPath $deployLockPath -PathType Leaf) { Assert-NoUnapprovedWriteAcl $deployLockPath $allowedWriteSid }
    $lockPath = $deployLockPath
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-NoUnapprovedWriteAcl $lockPath $allowedWriteSid
        $currentCert = if (Test-Path -LiteralPath $activeFull -PathType Leaf) { Get-ValidatedServerCertificate $activeFull $password -AdditionalAllowedSid @($runtimeSid,$writerSid) } else { $null }
        $newCert = Get-ValidatedServerCertificate $incomingFull $password -AdditionalAllowedSid @($runtimeSid,$writerSid)
        try {
            if ($currentCert) { Assert-PrivateFileAcl $activeFull -AdditionalAllowedSid @($runtimeSid,$writerSid) -RequireFullControlSid @($writerSid) }
            if ($currentCert -and $currentCert.Thumbprint -eq $newCert.Thumbprint) {
                [void](Test-ConnectorTlsHealth 'connector-access.structura-most.ru' $Port $Timeout $currentCert.Thumbprint)
                return 'CERTIFICATE_ALREADY_ACTIVE'
            }
            if (-not $currentCert) { throw 'An existing active certificate is required so a failed activation can be rolled back safely.' }
            $originalProcesses = @(Get-ExactPlatformServerProcesses $Root)
            if ($originalProcesses.Count -ne 1) { throw 'Expected exactly one running Platform.Server process before certificate activation.' }
            $stage = Join-Path $directory ('server.pfx.new.' + [Guid]::NewGuid().ToString('N'))
            $backup = "$activeFull.previous"
            if (Test-Path -LiteralPath $backup) { throw 'A prior certificate backup already exists; manual review is required.' }
            $output = [IO.File]::Open($stage, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $output.Flush($true) } finally { $output.Dispose() }
            [void](Assert-NoReparsePath $stage)
            Set-StageFileAcl $stage @($runtimeSid) $writerSid
            Assert-PrivateFileAcl $stage -AdditionalAllowedSid @($runtimeSid,$writerSid)
            $input = [IO.File]::Open($incomingFull, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                if ($input.Length -gt 1048576) { throw 'Incoming PFX exceeds 1 MiB.' }
                $output = [IO.File]::Open($stage, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $input.CopyTo($output); $output.Flush($true) } finally { $output.Dispose() }
            } finally { $input.Dispose() }
            $staged = Get-ValidatedServerCertificate $stage $password -AdditionalAllowedSid @($runtimeSid,$writerSid)
            try { if ($staged.Thumbprint -cne $newCert.Thumbprint) { throw 'Staged PFX identity changed during copy.' } } finally { $staged.Dispose() }
            $activationStarted = $false
            $newProcess = @()
            try {
                $activationStarted = $true
                [IO.File]::Replace($stage, $activeFull, $backup, $true)
                Assert-PrivateFileAcl $backup -AdditionalAllowedSid @($runtimeSid,$writerSid) -RequireFullControlSid @($writerSid)
                Assert-ActivatedCertificateAcl $activeFull @($runtimeSid) $writerSid
                $newProcess = Invoke-PlatformRestart $Root ([Math]::Min($Timeout, 30))
                [void](Test-ConnectorTlsHealth 'connector-access.structura-most.ru' $Port $Timeout $newCert.Thumbprint)
                Remove-Item -LiteralPath $backup -Force
                return 'CERTIFICATE_RENEWAL_VERIFIED'
            } catch {
                $failure = $_
                if ($activationStarted) {
                    if (-not (Test-Path -LiteralPath $backup -PathType Leaf)) {
                        $onDisk = Get-ValidatedServerCertificate $activeFull $password -AdditionalAllowedSid @($runtimeSid,$writerSid)
                        try {
                            $remaining = @(Get-ExactPlatformServerProcesses $Root)
                            if ($onDisk.Thumbprint -eq $currentCert.Thumbprint -and $remaining.Count -eq 1 -and $remaining[0].ProcessId -eq $originalProcesses[0].ProcessId -and $remaining[0].CreationDate -eq $originalProcesses[0].CreationDate -and $remaining[0].ExecutablePath -ceq $originalProcesses[0].ExecutablePath -and $remaining[0].CommandLine -ceq $originalProcesses[0].CommandLine) {
                                throw "Renewal failed before certificate replacement; original active certificate and process are intact. Cause: $($failure.Exception.Message)"
                            }
                            throw 'Certificate replacement outcome is ambiguous; no process restart was attempted.'
                        } finally { $onDisk.Dispose() }
                    }
                    $mustRestartOld = $false
                    if (@($newProcess).Count -eq 1) {
                        $proc = $newProcess[0]
                        $fresh = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.ProcessId)" -ErrorAction Stop
                        if ($fresh -and $fresh.CreationDate -eq $proc.CreationDate -and $fresh.ExecutablePath -ceq $proc.ExecutablePath -and $fresh.CommandLine -ceq $proc.CommandLine) { Stop-Process -Id $fresh.ProcessId -Force -ErrorAction Stop }
                        $stopDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
                        do {
                            $sameProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.ProcessId)" -ErrorAction Stop
                            if (-not $sameProcess -or $sameProcess.CreationDate -ne $proc.CreationDate) { break }
                            Start-Sleep -Milliseconds 250
                        } while ([DateTimeOffset]::UtcNow -lt $stopDeadline)
                        if ($sameProcess -and $sameProcess.CreationDate -eq $proc.CreationDate) { throw "Could not stop the owned Platform.Server process; certificate backup retained at $backup." }
                        $mustRestartOld = $true
                    } elseif (@($newProcess).Count -eq 0) {
                        $remaining = @(Get-ExactPlatformServerProcesses $Root)
                        $taskState = (Get-ScheduledTask -TaskName 'PlatformServerApp' -ErrorAction Stop).State
                        if ($taskState -eq 'Running') { throw "PlatformServerApp launch is still active; certificate backup retained at $backup." }
                        if ($remaining.Count -eq 0) { $mustRestartOld = $true }
                        elseif ($remaining.Count -eq 1 -and $remaining[0].ProcessId -eq $originalProcesses[0].ProcessId -and $remaining[0].CreationDate -eq $originalProcesses[0].CreationDate -and $remaining[0].ExecutablePath -ceq $originalProcesses[0].ExecutablePath -and $remaining[0].CommandLine -ceq $originalProcesses[0].CommandLine) { $mustRestartOld = $false }
                        else { throw "Platform.Server process identity is ambiguous; certificate backup retained at $backup. Original failure: $($failure.Exception.Message)" }
                    } else { throw "Activation process identity is not proven; certificate backup retained at $backup. Original failure: $($failure.Exception.Message)" }
                    $rollbackStage = "$activeFull.rollback"
                    [IO.File]::Replace($backup, $activeFull, $rollbackStage, $true)
                    Assert-ActivatedCertificateAcl $activeFull @($runtimeSid) $writerSid
                    if ($mustRestartOld) { [void](Invoke-PlatformRestart $Root ([Math]::Min($Timeout, 30))) }
                    [void](Test-ConnectorTlsHealth 'connector-access.structura-most.ru' $Port $Timeout $currentCert.Thumbprint)
                    Remove-Item -LiteralPath $rollbackStage -Force
                }
                throw "Renewal failed; previous certificate and Platform runtime rollback verified. Cause: $($failure.Exception.Message)"
            } finally { if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force } }
        } finally { if ($currentCert) { $currentCert.Dispose() }; $newCert.Dispose() }
    } finally { $lock.Dispose() }
}

if ($SelfTest) {
    $required = 'Assert-NoReparsePath','Get-ValidatedServerCertificate','Get-ConnectorAccessCertificatePassword','Assert-NonNullDacl','Get-ExactPlatformServerProcesses','Assert-PlatformTaskAction','Invoke-PlatformRestart','Test-ConnectorTlsHealth'
    foreach ($name in $required) { if (-not (Get-Command $name -CommandType Function -ErrorAction SilentlyContinue)) { throw "Self-test missing function: $name" } }
    Write-Output 'TLS_HOOK_SELFTEST_SOURCE_OK'
} else {
    Assert-SupportedRuntime
    Invoke-ConnectorAccessTlsRenewal -Incoming $SourcePfxPath -Active $ActivePfxPath -SecretName $PasswordEnvironmentVariable -SecretFilePath $SecretFilePath -Root $DeployRoot -Port $HealthPort -Timeout $TimeoutSeconds
}
