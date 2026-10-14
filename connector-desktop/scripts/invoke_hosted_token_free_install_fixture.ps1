[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProbeExecutable,
    [Parameter(Mandatory)][string]$PreviousSetup,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Token-free install verification requires disposable hosted Windows.'
}
if (!(Get-Service -Name NetBird -ErrorAction SilentlyContinue)) { throw 'The EXE did not install NetBird by default.' }
$publicRoot = Join-Path $env:PUBLIC ('StructuraInstallFixture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publicRoot | Out-Null
$acl = Get-Acl -LiteralPath $publicRoot
$acl.SetAccessRuleProtection($true,$false)
foreach ($pair in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($pair[0]),
        [Enum]::Parse([Security.AccessControl.FileSystemRights],$pair[1]),'ContainerInherit,ObjectInherit','None','Allow'))
}
Set-Acl -LiteralPath $publicRoot -AclObject $acl
$probeRoot = Join-Path $publicRoot 'probe'
Copy-Item -LiteralPath (Split-Path $ProbeExecutable) -Destination $probeRoot -Recurse
# CopyFile may retain the protected build output's ACL. The test user must be
# able to read every copied executable/DLL, not only the containing directory.
& icacls.exe $probeRoot /grant '*S-1-5-32-545:(OI)(CI)RX' /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot grant test users read access to the native probe.' }
$previous = Join-Path $publicRoot 'previous-Setup.exe'
Copy-Item -LiteralPath $PreviousSetup -Destination $previous
& icacls.exe $previous /grant '*S-1-5-32-545:RX' /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot grant test users read access to the baseline desktop setup.' }
$caller = Join-Path $env:ProgramFiles 'StructuraConnectorInstaller/Bootstrapper/Connector.Upgrade.Bootstrapper.exe'
$rules = @()
try {
    # A live backend cannot accidentally make installation appear token-free.
    # These rules affect only the test probe and the actual installer caller.
    foreach ($program in @((Join-Path $probeRoot 'Connector.Upgrade.StandaloneInstallProbe.exe'),$caller)) {
        $name = 'StructuraFixture-' + [Guid]::NewGuid().ToString('N')
        New-NetFirewallRule -DisplayName $name -Direction Outbound -Program $program -Action Block | Out-Null
        $rules += $name
    }
    $results = @()
    foreach ($mode in @('fresh','upgrade')) {
        $name = 'structura' + [Guid]::NewGuid().ToString('N').Substring(0,8)
        $plain = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + '!aA1'
        $password = ConvertTo-SecureString $plain -AsPlainText -Force
        $user = New-LocalUser -Name $name -Password $password -PasswordNeverExpires
        Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
        $taskName = 'StructuraTokenFreeFixture-' + $mode
        $resultRoot = Join-Path $publicRoot $mode
        New-Item -ItemType Directory -Path $resultRoot | Out-Null
        $resultAcl = Get-Acl -LiteralPath $resultRoot
        $resultAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user.SID,'Modify','ContainerInherit,ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $resultRoot -AclObject $resultAcl
        $resultPath = Join-Path $resultRoot 'result.json'
        try {
            $arguments = '"' + $resultPath + '"'
            if ($mode -ceq 'upgrade') { $arguments += ' "' + $previous + '"' }
            $action = New-ScheduledTaskAction -Execute (Join-Path $probeRoot 'Connector.Upgrade.StandaloneInstallProbe.exe') -Argument $arguments -WorkingDirectory $probeRoot
            $task = New-ScheduledTask -Action $action -Principal (New-ScheduledTaskPrincipal -UserId $user.SID.Value -LogonType Password -RunLevel Limited) `
                -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 10))
            Register-ScheduledTask -TaskName $taskName -InputObject $task -User "$env:COMPUTERNAME\$name" -Password $plain | Out-Null
            Start-ScheduledTask -TaskName $taskName
            $timer = [Diagnostics.Stopwatch]::StartNew()
            do {
                Start-Sleep -Seconds 2
                $state = (Get-ScheduledTask -TaskName $taskName).State
                $info = Get-ScheduledTaskInfo -TaskName $taskName
                if ($state -ne 'Running' -and $info.LastRunTime -gt (Get-Date).AddMinutes(-10) -and !(Test-Path -LiteralPath $resultPath)) {
                    throw ('Native task ended before producing evidence: 0x' + $info.LastTaskResult.ToString('X8'))
                }
                if ($timer.Elapsed.TotalMinutes -gt 9) { throw 'Native user-stage fixture timed out.' }
            } while ($state -eq 'Running' -or !(Test-Path -LiteralPath $resultPath))
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            if (!$result.success -or $result.tokenRequired -or $result.enrollmentCreated -or $result.blenderIncluded -or
                !$result.defaultCallerVerified -or !$result.repeatedInstallVerified -or !$result.desktopWebViewVerified -or !$result.bundledGitVerified) {
                throw ('Native token-free ' + $mode + ' fixture failed: ' + ($result | ConvertTo-Json -Compress))
            }
            $results += $result
            Write-Host "PASS: $mode, repeated install, actual desktop UI, no network, no token, no Blender."
        } finally {
            Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            Remove-LocalUser -Name $name
            $plain = $null
            $password = $null
        }
    }
    [ordered]@{defaultInstallationVerified=$true;netBirdInstalledByBundle=$true;webView2Verified=$true;
        noTokenRequired=$true;offlineInstallationVerified=$true;blenderIncluded=$false;cases=$results} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Output -Encoding utf8
} finally {
    foreach ($rule in $rules) { Remove-NetFirewallRule -DisplayName $rule }
}
