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
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class StructuraFixtureProfile {
    [DllImport("userenv.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern int CreateProfile(string sid, string userName, StringBuilder profilePath, uint length);
}
'@
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
        $resultRoot = Join-Path $publicRoot $mode
        New-Item -ItemType Directory -Path $resultRoot | Out-Null
        $resultAcl = Get-Acl -LiteralPath $resultRoot
        $resultAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($user.SID,'Modify','ContainerInherit,ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $resultRoot -AclObject $resultAcl
        $resultPath = Join-Path $resultRoot 'result.json'
        $process = $null
        try {
            # A demand-started Password task on hosted Windows remained queued
            # forever (SCHED_S_TASK_HAS_NOT_RUN, LastRunTime 1999-11-30). Use the
            # normal Windows logon API directly, with a real ordinary-user profile.
            # Userenv's profile RPC accepts MAX_PATH, not the extended path limit.
            $profile = [Text.StringBuilder]::new(260)
            $created = [StructuraFixtureProfile]::CreateProfile($user.SID.Value,$name,$profile,260)
            if ($created -ne 0) { throw ('Cannot create disposable user profile: 0x' + $created.ToString('X8')) }
            $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $probeRoot 'Connector.Upgrade.StandaloneInstallProbe.exe'))
            $start.UseShellExecute = $false
            $start.UserName = $name
            $start.Domain = $env:COMPUTERNAME
            $start.Password = $password
            $start.LoadUserProfile = $true
            $start.WorkingDirectory = $probeRoot
            $start.ArgumentList.Add($resultPath)
            if ($mode -ceq 'upgrade') { $start.ArgumentList.Add($previous) }
            # Do not inherit runner credentials or its APPDATA/USERPROFILE paths.
            $start.Environment.Clear()
            foreach ($key in @('SystemRoot','WINDIR','SystemDrive','ComSpec','ProgramFiles','ProgramFiles(x86)','ProgramW6432','CommonProgramFiles','CommonProgramFiles(x86)','CommonProgramW6432','ProgramData','PUBLIC','COMPUTERNAME','PROCESSOR_ARCHITECTURE')) {
                $value = [Environment]::GetEnvironmentVariable($key)
                if ($value) { $start.Environment[$key] = $value }
            }
            $start.Environment['PATH'] = [Environment]::GetEnvironmentVariable('PATH','Machine')
            $start.Environment['USERNAME'] = $name
            $start.Environment['USERDOMAIN'] = $env:COMPUTERNAME
            $start.Environment['USERPROFILE'] = $profile.ToString()
            $start.Environment['LOCALAPPDATA'] = Join-Path $profile.ToString() 'AppData/Local'
            $start.Environment['APPDATA'] = Join-Path $profile.ToString() 'AppData/Roaming'
            $start.Environment['TEMP'] = Join-Path $resultRoot 'temp'
            $start.Environment['TMP'] = $start.Environment['TEMP']
            New-Item -ItemType Directory -Path $start.Environment['TEMP'] | Out-Null
            $process = [Diagnostics.Process]::Start($start)
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $lastProgress = ''
            $stageTimer = [Diagnostics.Stopwatch]::StartNew()
            do {
                Start-Sleep -Seconds 2
                if (Test-Path -LiteralPath ($resultPath + '.progress.json')) {
                    $progress = Get-Content -LiteralPath ($resultPath + '.progress.json') -Raw
                    if ($progress -cne $lastProgress) { Write-Host ($mode + ': ' + $progress); $lastProgress = $progress; $stageTimer.Restart() }
                }
                if ($process.HasExited -and !(Test-Path -LiteralPath $resultPath)) {
                    throw ('Native probe exited before producing evidence: ' + $process.ExitCode)
                }
                if (!$lastProgress -and $timer.Elapsed.TotalSeconds -gt 30) { throw 'Native probe did not start within 30 seconds.' }
                if ($stageTimer.Elapsed.TotalSeconds -gt 200 -or $timer.Elapsed.TotalMinutes -gt 7) { throw ('Native fixture stalled at: ' + $lastProgress) }
            } while (!(Test-Path -LiteralPath $resultPath))
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            if (!$result.success -or $result.tokenRequired -or $result.enrollmentCreated -or $result.blenderIncluded -or
                !$result.defaultCallerVerified -or !$result.repeatedInstallVerified -or !$result.desktopWebViewVerified -or !$result.bundledGitVerified) {
                throw ('Native token-free ' + $mode + ' fixture failed: ' + ($result | ConvertTo-Json -Compress))
            }
            $results += $result
            Write-Host "PASS: $mode, repeated install, actual desktop UI, no network, no token, no Blender."
        } finally {
            # Retain only this disposable user's probe results and bounded process metadata.
            # No command lines, passwords, environment variables or user data are captured.
            Get-Process -Name 'Connector.Upgrade.StandaloneInstallProbe','Connector.Upgrade.Bootstrapper','Connector.Desktop','Update','Setup' -ErrorAction SilentlyContinue |
                Select-Object ProcessName,Id,SessionId,Responding,MainWindowHandle,CPU |
                ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path $Output) ($mode + '-processes.json'))
            foreach ($evidence in Get-ChildItem -LiteralPath $resultRoot -File -Filter '*.json') {
                Copy-Item -LiteralPath $evidence.FullName -Destination (Join-Path (Split-Path $Output) ($mode + '-' + $evidence.Name))
            }
            if ($null -ne $process) {
                if (!$process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
                $process.Dispose()
            }
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
