[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupExecutable,[Parameter(Mandatory)][string]$ProbeExecutable,[Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Disposable hosted Windows required.' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$setup=[IO.Path]::GetFullPath($SetupExecutable)
$msiInstaller=New-Object -ComObject WindowsInstaller.Installer
$cleanUpgradeCode='{67B43FCD-4FE8-4D29-9F64-6F190FCD6E51}'
if (@($msiInstaller.RelatedProducts($cleanUpgradeCode)).Count -ne 0) { throw 'Clean fixture requires no existing product of its own identity.' }
if ((Get-Service NetBird -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'NetBird/netbird.exe')) -or
    (Test-Path -LiteralPath (Join-Path $env:ProgramData 'NetBird')) -or
    (Test-Path -LiteralPath (Join-Path $env:ProgramData 'Structura Connector/Network/netbird-bundle-owner.json')) -or
    (Test-Path -LiteralPath (Join-Path $env:ProgramData 'Structura Connector/Network/netbird-install-owner.json'))) { throw 'Fresh fixture requires no pre-existing NetBird state.' }
function Run-Setup([string]$Mode) {
    $log=Join-Path $EvidenceDirectory ($Mode+'.log')
    $process=Start-Process -FilePath $setup -ArgumentList @('/quiet','/norestart',('/'+$Mode),'/log',('"'+$log+'"')) -WindowStyle Hidden -PassThru
    try {
        if (!$process.WaitForExit(240000)) { $process.Kill($true); throw 'The final installer EXE timed out.' }
        if ($process.ExitCode -notin @(0,3010)) { throw "Final EXE $Mode failed: $($process.ExitCode)" }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CleanFixtureProfile {
 [DllImport("userenv.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern int CreateProfile(string sid,string name,StringBuilder path,uint length);
 [DllImport("userenv.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteProfile(string sid,string path,string computer);
}
'@
$name='platform'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$publicRoot=Join-Path $env:PUBLIC ('PlatformCleanFixture-'+[Guid]::NewGuid().ToString('N'))
$profilePath=$null; $user=$null; $probe=$null; $password=$null; $setupAttempted=$false; $complete=$false; $primaryFailure=$null
try {
    $setupAttempted=$true; $installExit=Run-Setup 'install'
    if (@($msiInstaller.RelatedProducts($cleanUpgradeCode)).Count -ne 1) { throw 'The final EXE did not register exactly one new application MSI.' }
    if (!(Get-Service NetBird -ErrorAction SilentlyContinue)) { throw 'The EXE did not install NetBird.' }
    $repairExit=Run-Setup 'repair'
    New-Item -ItemType Directory -Path $publicRoot | Out-Null
    $probeRoot=Join-Path $publicRoot 'probe'
    Copy-Item -LiteralPath (Split-Path $ProbeExecutable) -Destination $probeRoot -Recurse
    & icacls.exe $publicRoot /grant '*S-1-5-32-545:(OI)(CI)RX' /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Probe read ACL failed.' }
    $plain=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))+'!aA1'
    $password=ConvertTo-SecureString $plain -AsPlainText -Force; $plain=$null
    $user=New-LocalUser -Name $name -Password $password -PasswordNeverExpires
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
    $profile=[Text.StringBuilder]::new(260)
    if ([CleanFixtureProfile]::CreateProfile($user.SID.Value,$name,$profile,260) -ne 0) { throw 'Cannot create ordinary user profile.' }
    $profilePath=$profile.ToString()
    $resultRoot=Join-Path $publicRoot 'result'; New-Item -ItemType Directory -Path $resultRoot | Out-Null
    & icacls.exe $resultRoot /grant ("*"+$user.SID.Value+':(OI)(CI)M') /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Probe result ACL failed.' }
    $result=Join-Path $resultRoot 'native.json'
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $probeRoot 'Connector.Clean.InstallProbe.exe'))
    $start.UseShellExecute=$false; $start.UserName=$name; $start.Domain=$env:COMPUTERNAME; $start.Password=$password; $start.LoadUserProfile=$true; $start.WorkingDirectory=$probeRoot
    $start.ArgumentList.Add($result); $start.Environment.Clear()
    foreach ($key in @('SystemRoot','WINDIR','SystemDrive','ComSpec','ProgramFiles','ProgramFiles(x86)','ProgramW6432','CommonProgramFiles','CommonProgramFiles(x86)','CommonProgramW6432','ProgramData','PUBLIC','COMPUTERNAME','PROCESSOR_ARCHITECTURE')) {
        $value=[Environment]::GetEnvironmentVariable($key); if ($value) { $start.Environment[$key]=$value }
    }
    $start.Environment['PATH']=[Environment]::GetEnvironmentVariable('PATH','Machine')
    $start.Environment['USERNAME']=$name; $start.Environment['USERDOMAIN']=$env:COMPUTERNAME; $start.Environment['USERPROFILE']=$profilePath
    $start.Environment['LOCALAPPDATA']=Join-Path $profilePath 'AppData/Local'; $start.Environment['APPDATA']=Join-Path $profilePath 'AppData/Roaming'
    $start.Environment['TEMP']=Join-Path $resultRoot 'temp'; $start.Environment['TMP']=$start.Environment['TEMP']; New-Item -ItemType Directory -Path $start.Environment['TEMP'] | Out-Null
    $probe=[Diagnostics.Process]::Start($start)
    if (!$probe.WaitForExit(240000)) { $probe.Kill($true); throw 'Ordinary-user desktop fixture timed out.' }
    if (!(Test-Path -LiteralPath $result)) { throw "Native fixture exited without evidence: $($probe.ExitCode)" }
    Copy-Item -LiteralPath $result -Destination (Join-Path $EvidenceDirectory 'native.json')
    $proof=Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
    if ($probe.ExitCode -ne 0 -or !$proof.success -or !$proof.actualDesktopWebView -or !$proof.ordinaryUser -or !$proof.protectedNetworkReady -or
        $proof.credentialCreated -or $proof.legacyImport -or $proof.blenderIncluded) { throw 'The actual installed application failed ordinary-user verification.' }
    $complete=$true
} catch {
    $primaryFailure=$_
    @{failure=$_.Exception.Message;stage='native-fixture'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'fixture-failure.json') -Encoding utf8
    throw
} finally {
    $cleanupFailures=[Collections.Generic.List[string]]::new()
    $cleanupState=@{probeStopped=($null -eq $probe);uninstallExit=$null}
    function Invoke-FixtureCleanup([string]$Stage,[scriptblock]$Action) {
        try { & $Action }
        catch { $cleanupFailures.Add($Stage+': '+$_.Exception.Message) }
    }
    Invoke-FixtureCleanup 'owned-probe-stop' {
        if ($probe) {
            try {
                if (!$probe.HasExited) { $probe.Kill($true); if (!$probe.WaitForExit(10000)) { throw 'Own probe did not stop.' } }
                $cleanupState.probeStopped=$true
            } finally { $probe.Dispose() }
        }
    }
    Invoke-FixtureCleanup 'owned-application-uninstall' {
        if (!$cleanupState.probeStopped) { throw 'Cannot uninstall while own probe may still run.' }
        if ($setupAttempted) { $cleanupState.uninstallExit=Run-Setup 'uninstall' }
    }
    Invoke-FixtureCleanup 'owned-profile-delete' {
        if (!$cleanupState.probeStopped) { throw 'Cannot delete a profile while own probe may still run.' }
        if ($user -and $profilePath) {
            $profileFull=[IO.Path]::GetFullPath($profilePath)
            if ([IO.Path]::GetFileName($profileFull) -cne $name -or
                [IO.Path]::GetDirectoryName($profileFull) -ine (Join-Path $env:SystemDrive 'Users')) { throw 'Disposable profile path differs from its exact owned user.' }
            $until=[DateTime]::UtcNow.AddSeconds(15);$deleted=$false;$lastError=0
            do {
                $deleted=[CleanFixtureProfile]::DeleteProfile($user.SID.Value,$profileFull,$null)
                if ($deleted) { break }
                $lastError=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
                Start-Sleep -Milliseconds 500
            } while ([DateTime]::UtcNow -lt $until)
            if (!$deleted) { throw "Disposable user profile cleanup failed: Win32=$lastError" }
        }
    }
    Invoke-FixtureCleanup 'owned-local-user-delete' {
        if (!$cleanupState.probeStopped) { throw 'Cannot delete a user while own probe may still run.' }
        if ($user) { Remove-LocalUser -Name $name }
    }
    $password=$null
    Invoke-FixtureCleanup 'owned-fixture-directory-delete' {
        if (!$cleanupState.probeStopped) { throw 'Cannot delete the fixture while own probe may still run.' }
        $resolved=[IO.Path]::GetFullPath($publicRoot)
        if (!$resolved.StartsWith(([IO.Path]::GetFullPath($env:PUBLIC).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notlike 'PlatformCleanFixture-*') { throw 'Fixture cleanup path escaped its owned root.' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    if ($cleanupFailures.Count) {
        @{failures=@($cleanupFailures);primaryFailurePreserved=($null -ne $primaryFailure)} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'cleanup-failure.json') -Encoding utf8
        if (!$primaryFailure) { throw ('Owned fixture cleanup failed: '+($cleanupFailures -join '; ')) }
        Write-Warning 'Owned cleanup also failed; original native failure is preserved in fixture-failure.json.'
    }
    $uninstallExit=$cleanupState.uninstallExit
}
if ($complete) {
    $appRoot=Join-Path $env:ProgramFiles 'Platform Connector'
    if (@($msiInstaller.RelatedProducts($cleanUpgradeCode)).Count -ne 0) { throw 'Uninstall left the application MSI registered.' }
    if ((Test-Path -LiteralPath $appRoot) -and @(Get-ChildItem -LiteralPath $appRoot -Recurse -File -Force).Count -ne 0) { throw 'Uninstall left installed application or configuration files.' }
    foreach ($folder in @([Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('CommonPrograms'))) {
        if (Test-Path -LiteralPath (Join-Path $folder 'Платформа Коннектор.lnk')) { throw 'Uninstall left a machine application shortcut.' }
    }
    # The only remaining NetBird instance and marker were created by this fixture.
    # Hide that exact marker to prove a foreign exact-version installation is rejected.
    $owner=Join-Path $env:ProgramData 'Structura Connector/Network/netbird-bundle-owner.json'
    $hidden=$owner+'.clean-fixture-held'
    $ownerHash=(Get-FileHash -LiteralPath $owner).Hash
    if ((Test-Path -LiteralPath $hidden) -or (Get-Content -LiteralPath $owner -Raw|ConvertFrom-Json).installerSha256 -cne '50f822c0f5f6e54e7618096cdd36b63bf4b169e125c9da06052fd4fd96115210') { throw 'Unexpected fixture ownership marker.' }
    Move-Item -LiteralPath $owner -Destination $hidden
    try {
        $rejectLog=Join-Path $EvidenceDirectory 'foreign-exact-rejected.log'
        $foreign=Start-Process -FilePath $setup -ArgumentList @('/quiet','/norestart','/install','/log',('"'+$rejectLog+'"')) -WindowStyle Hidden -PassThru
        try {
            if (!$foreign.WaitForExit(60000)) { $foreign.Kill($true); throw 'Foreign-install rejection timed out.' }
            if ($foreign.ExitCode -in @(0,3010) -or @($msiInstaller.RelatedProducts($cleanUpgradeCode)).Count -ne 0) { throw 'Foreign exact-version NetBird state was accepted.' }
        } finally { $foreign.Dispose() }
    } finally {
        if ((Get-FileHash -LiteralPath $hidden).Hash -cne $ownerHash -or (Test-Path -LiteralPath $owner)) { throw 'Fixture marker changed while held.' }
        Move-Item -LiteralPath $hidden -Destination $owner
    }
    @{success=$true;installExit=$installExit;repairExit=$repairExit;uninstallExit=$uninstallExit;ordinaryUser=$true;profileRemoved=$true;fixtureRemoved=$true;foreignExactVersionRejected=$true;freshInstall=$true;legacyImport=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'installation.json') -Encoding utf8
}
