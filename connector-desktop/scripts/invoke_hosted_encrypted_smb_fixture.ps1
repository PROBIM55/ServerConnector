[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProbeExecutable,[Parameter(Mandatory)][string]$EvidencePath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Disposable hosted Windows required.'}
$suffix=[guid]::NewGuid().ToString('N').Substring(0,10)
$name='csmb'+$suffix
$share='csmb_'+$suffix
$root=Join-Path $env:PUBLIC $share
$plain=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))+'!aA1'
$secure=ConvertTo-SecureString $plain -AsPlainText -Force
$user=$null;$mapping=$null;$child=$null;$steps=[Collections.Generic.List[string]]::new()
$primaryFailure=$null;$cleanupFailures=[Collections.Generic.List[string]]::new();$proofPassed=$false
try{
    if((Get-SmbClientConfiguration).RequireEncryption){throw 'Fixture requires an initially optional encryption client to prove plain rejection.'}
    $user=New-LocalUser -Name $name -Password $secure -PasswordNeverExpires
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
    New-Item -ItemType Directory -Path $root | Out-Null
    $dataRoot=Join-Path $root 'data';New-Item -ItemType Directory -Path $dataRoot | Out-Null
    $probeRoot=Join-Path $root 'probe'
    Copy-Item -LiteralPath (Split-Path ([IO.Path]::GetFullPath($ProbeExecutable))) -Destination $probeRoot -Recurse
    $proof=[guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $dataRoot 'proof.txt'),$proof)
    & icacls.exe $root /grant ('*'+$user.SID.Value+':(OI)(CI)RX') /Q | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Fixture NTFS ACL failed.'}
    $account=$env:COMPUTERNAME+'\'+$name
    $mapping=New-SmbShare -Name $share -Path $dataRoot -ReadAccess $account -EncryptData $false -CachingMode None
    $ip=@(Get-NetIPAddress -AddressFamily IPv4|Where-Object{$_.IPAddress -ne '127.0.0.1' -and $_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*'}|Sort-Object InterfaceIndex)[0].IPAddress
    $unc='\\'+$ip+'\'+$share
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $probeRoot ([IO.Path]::GetFileName($ProbeExecutable))))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.UserName=$name;$start.Domain=$env:COMPUTERNAME
    # This native SMB-only process needs an ordinary logon token, not HKCU or a
    # desktop profile. Avoid an unrelated User Profile Service unload race.
    $start.Password=$secure;$start.LoadUserProfile=$false;$start.WorkingDirectory=$probeRoot
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    # No deployment secrets or inherited GitHub tokens reach the ordinary user.
    $start.Environment.Clear()
    foreach($key in @('SystemRoot','WINDIR','SystemDrive','ComSpec','ProgramFiles','ProgramFiles(x86)','ProgramW6432','ProgramData','PUBLIC','COMPUTERNAME','PROCESSOR_ARCHITECTURE')){
        $value=[Environment]::GetEnvironmentVariable($key);if($value){$start.Environment[$key]=$value}
    }
    $start.Environment['GITHUB_ACTIONS']='true';$start.Environment['RUNNER_ENVIRONMENT']='github-hosted'
    $start.Environment['PATH']=[Environment]::GetEnvironmentVariable('PATH','Machine')
    $child=[Diagnostics.Process]::Start($start)
    $stderr=$child.StandardError.ReadToEndAsync()
    $child.StandardInput.WriteLine((@{Share=$unc;User=$account;Password=$plain;Proof=$proof}|ConvertTo-Json -Compress))
    $child.StandardInput.Flush();$plain=$null
    foreach($expected in @('plain-mapping-rejected-by-crypto-readback','encrypted-native-read-and-reuse')){
        $line=$child.StandardOutput.ReadLineAsync().WaitAsync([timespan]::FromSeconds(45)).GetAwaiter().GetResult()
        if(!$line){throw ('Native SMB fixture exited early: '+$stderr.GetAwaiter().GetResult())}
        $result=$line|ConvertFrom-Json;if(!$result.ok -or $result.step -cne $expected){throw 'Unexpected native SMB fixture stage.'}
        $steps.Add($result.step)
    }
    Disable-LocalUser -Name $name
    foreach($session in @(Get-SmbSession|Where-Object{$_.ClientUserName -ieq $account})){
        Close-SmbSession -SessionId $session.SessionId -Force
    }
    if(@(Get-SmbSession|Where-Object{$_.ClientUserName -ieq $account}).Count){throw 'Owned revoked sessions remain.'}
    $child.StandardInput.WriteLine('revoked');$child.StandardInput.Flush();$child.StandardInput.Close()
    $line=$child.StandardOutput.ReadLineAsync().WaitAsync([timespan]::FromSeconds(45)).GetAwaiter().GetResult()
    if(!$line){throw ('Native revoke proof exited early: '+$stderr.GetAwaiter().GetResult())}
    $result=$line|ConvertFrom-Json;if(!$result.ok -or $result.step -cne 'revoked-account-denied'){throw 'Revocation proof missing.'}
    $steps.Add($result.step)
    if(!$child.WaitForExit(15000) -or $child.ExitCode -ne 0){throw 'Native SMB fixture did not exit cleanly.'}
    $proofPassed=$true
}catch{$primaryFailure=$_}finally{
    $plain=$null
    try{if($child){if(!$child.HasExited){$child.Kill($true);if(!$child.WaitForExit(10000)){throw 'Owned child still running.'}};$child.Dispose()}}catch{$cleanupFailures.Add('child-stop')}
    try{
        if($user){foreach($session in @(Get-SmbSession|Where-Object{$_.ClientUserName -ieq ($env:COMPUTERNAME+'\'+$name)})){Close-SmbSession -SessionId $session.SessionId -Force}}
        if(@(Get-SmbSession|Where-Object{$_.ClientUserName -ieq ($env:COMPUTERNAME+'\'+$name)}).Count){throw 'Owned sessions remain.'}
    }catch{$cleanupFailures.Add('owned-sessions')}
    try{
        $actual=Get-SmbShare -Name $share -ErrorAction SilentlyContinue
        if($actual){if($actual.Path -ine (Join-Path $root 'data')){throw 'Share ownership changed.'};Remove-SmbShare -Name $share -Force}
        if(Get-SmbShare -Name $share -ErrorAction SilentlyContinue){throw 'Owned share remains.'}
    }catch{$cleanupFailures.Add('owned-share')}
    try{
        if($user){
            foreach($profile in @(Get-CimInstance Win32_UserProfile|Where-Object{$_.SID -ceq $user.SID.Value})){
                if($profile.Special -or $profile.Loaded){throw 'Owned profile is still in use.'};$profile|Remove-CimInstance
            }
            if(@(Get-CimInstance Win32_UserProfile|Where-Object{$_.SID -ceq $user.SID.Value}).Count){throw 'Owned profile remains.'}
        }
    }catch{$cleanupFailures.Add('owned-profile')}
    try{
        $actual=Get-LocalUser -Name $name -ErrorAction SilentlyContinue
        if($actual){if(!$user -or $actual.SID.Value -cne $user.SID.Value){throw 'Account ownership changed.'};Remove-LocalUser -Name $name}
        if(Get-LocalUser -Name $name -ErrorAction SilentlyContinue){throw 'Owned user remains.'}
    }catch{$cleanupFailures.Add('owned-user')}
    try{
        # Only the exact new disposable fixture directory; never a computed ancestor.
        $resolved=[IO.Path]::GetFullPath($root);$expected=[IO.Path]::GetFullPath((Join-Path $env:PUBLIC ('csmb_'+$suffix)))
        if($resolved -cne $expected -or !$resolved.StartsWith([IO.Path]::GetFullPath($env:PUBLIC)+[IO.Path]::DirectorySeparatorChar)){throw 'Fixture cleanup boundary differs.'}
        if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
        if(Test-Path -LiteralPath $resolved){throw 'Owned directory remains.'}
    }catch{$cleanupFailures.Add('owned-directory')}
    try{if($secure){$secure.Dispose()}}catch{$cleanupFailures.Add('secret-dispose')}
}
if($cleanupFailures.Count -or $primaryFailure -or !$proofPassed){
    $failureType=if($primaryFailure){$primaryFailure.Exception.GetType().FullName}else{$null}
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($EvidencePath),(@{schemaVersion=1;state='failed';steps=$steps.ToArray();proofPassed=$proofPassed;cleanupFailures=$cleanupFailures.ToArray();failureType=$failureType;productionTouched=$false;utc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 6))
}
if($cleanupFailures.Count){throw ('Native SMB fixture cleanup incomplete: '+($cleanupFailures.ToArray() -join ','))}
if($primaryFailure){throw $primaryFailure}
if(!$proofPassed){throw 'Native SMB proof incomplete.'}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($EvidencePath),(@{schemaVersion=1;ordinaryUser=$true;mandatoryCreationFlags='0x18';actualPrivacyVerified=$true;steps=$steps.ToArray();cleanup='confirmed';productionTouched=$false;utc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 6))
