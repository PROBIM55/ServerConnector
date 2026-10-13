[CmdletBinding()]
param(
    [ValidateSet('Client', 'Server')][string]$Mode = 'Client',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator rights are required.' }
$taskName = 'StructuraConnectorVpnRecovery' + $Mode
if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    return # Keep the small protected audit log for diagnosis; never touch the tunnel.
}
$serviceName = if ($Mode -eq 'Server') { 'AmneziaWGTunnel$awgserver' } else { 'AmneziaWGTunnel$structura-vpn' }
if (!(Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { throw 'The existing Connector VPN service was not found.' }
$source = Join-Path $PSScriptRoot 'connector_vpn_recovery.ps1'
$content = [IO.File]::ReadAllText($source)
$tokens = $null; $errors = $null
[void][Management.Automation.Language.Parser]::ParseInput($content, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Recovery script failed syntax validation.' }

$root = Join-Path $env:ProgramData 'StructuraConnectorVpnRecovery'
# Do not follow a standard user's pre-created junction into another directory.
if (Test-Path -LiteralPath $root) {
    if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery directory is a reparse point.' }
    $owner = (Get-Acl -LiteralPath $root).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) { throw 'Recovery directory has an untrusted owner.' }
}
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
if (Test-Path -LiteralPath $root) { Set-Acl -LiteralPath $root -AclObject $acl } else {
    # Windows PowerShell/.NET Framework creates it protected from the first instant.
    [void][IO.Directory]::CreateDirectory($root, $acl)
}
$script = Join-Path $root 'connector_vpn_recovery.ps1'
foreach ($path in @($script, (Join-Path $root 'installed.json'), (Join-Path $root 'client-state.json'), (Join-Path $root 'recovery.log'))) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Recovery file is a reparse point.' }
    if (Test-Path -LiteralPath $path) {
        $owner = (Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) { throw 'Recovery file has an untrusted owner.' }
        $fileAcl = New-Object Security.AccessControl.FileSecurity
        $fileAcl.SetAccessRuleProtection($false, $false)
        $fileAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
        Set-Acl -LiteralPath $path -AclObject $fileAcl
    }
}
[IO.File]::WriteAllText($script, $content, (New-Object Text.UTF8Encoding($true)))
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $script + '" -Mode ' + $Mode)
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$boot = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 50) `
    -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($trigger, $boot) -Settings $settings `
    -User SYSTEM -RunLevel Highest -Force | Out-Null
@{ InstalledUtc = [DateTimeOffset]::UtcNow.ToString('o'); Mode = $Mode; Version = 1 } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $root 'installed.json') -Encoding UTF8
# Installation registers the monitor; it does NOT restart any service.
@{ Task = $taskName; Script = $script; ServiceRestarted = $false } | ConvertTo-Json
