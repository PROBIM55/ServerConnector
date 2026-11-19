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
$user=$null;$mapping=$null;$child=$null;$steps=[Collections.Generic.List[string]]::new();$orphanAcl=$false
$primaryFailure=$null;$cleanupFailures=[Collections.Generic.List[string]]::new();$proofPassed=$false
$ownerMarkerVerified=$false
$identityRightsVerified=$false
try{
    if((Get-SmbClientConfiguration).RequireEncryption){throw 'Fixture requires an initially optional encryption client to prove plain rejection.'}
    $runner=Join-Path $PWD.Path 'connector-access/smb-helper/SmbAclReconcile.ps1'
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($runner,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'Actual helper runner has parser errors.'}
    $assignment=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and
      $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and $node.Left.VariablePath.UserPath -ceq 'ownerMarker'},$true))
    if($assignment.Count-ne1){throw 'Actual owner-marker assignment is not unique.'}
    $ownerBytes=[Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('fixture-device-'+$suffix))
    . ([scriptblock]::Create($assignment[0].Extent.Text))
    if($ownerMarker.Length-ne47 -or $ownerMarker-cnotmatch '^cnb:[A-Za-z0-9_-]{43}$'){throw 'Full-digest owner marker exceeds the Windows description contract.'}
    $identityProof=& (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe') -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'test_hosted_smb_identity_rights.ps1') -RunnerPath $runner -AccountName ('clsa'+$suffix) -OwnerMarker $ownerMarker
    if($LASTEXITCODE-ne0){throw 'Actual Windows PowerShell 5.1 identity restrictions failed.'}
    $identityProof=($identityProof-join"`n")|ConvertFrom-Json
    if(!$identityProof.exactDenyRightsReadback -or !$identityProof.idempotent -or !$identityProof.ownedSidRightsAndSamCleaned){throw 'Native identity restrictions/readback/cleanup are incomplete.'}
    $identityRightsVerified=$true
    $oldRejected=$false
    try{$null=New-LocalUser -Name $name -NoPassword -Disabled -Description ('Structura Connector SMB '+('0'*64)) -WhatIf -ErrorAction Stop}
    catch{if($_.FullyQualifiedErrorId -notlike 'ParameterArgumentValidationError,*'){throw};$oldRejected=$true}
    if(!$oldRejected){throw 'Original oversized-description regression was not reproduced.'}
    $user=New-LocalUser -Name $name -Description $ownerMarker -Password $secure -PasswordNeverExpires
    if((Get-LocalUser -Name $name).Description-cne$ownerMarker){throw 'Actual Windows owner-marker readback differs.'}
    $ownerMarkerVerified=$true
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
    # Exercise incremental removal of an owned deleted-account ACE. A valid
    # grant introduced after the original image must survive this operation.
    Grant-SmbShareAccess -Name $share -AccountName $account -AccessRight Change -Force|Out-Null
    Remove-LocalUser -Name $name
    $admins=([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')).Translate([Security.Principal.NTAccount]).Value
    Grant-SmbShareAccess -Name $share -AccountName $admins -AccessRight Full -Force|Out-Null
    $orphan=@(Get-SmbShareAccess -Name $share|Where-Object{$_.AccountName -ceq ('*'+$user.SID.Value)})
    if($orphan.Count-ne1){throw 'Owned orphan share ACE was not observed.'}
    Revoke-SmbShareAccess -Name $share -AccountName $orphan[0].AccountName -Force -ErrorAction Stop|Out-Null
    $remaining=@(Get-SmbShareAccess -Name $share)
    if($remaining.Count-ne1 -or $remaining[0].AccountName -ine $admins -or $remaining[0].AccessRight -cne 'Full'){throw 'Incremental orphan cleanup changed the valid grant.'}
    Grant-SmbShareAccess -Name $share -AccountName $orphan[0].AccountName -AccessRight Change -Force -ErrorAction Stop|Out-Null
    $restored=@(Get-SmbShareAccess -Name $share)
    if($restored.Count-ne2 -or @($restored|Where-Object{$_.AccountName -ceq $orphan[0].AccountName -and $_.AccessRight -ceq 'Change'}).Count-ne1 -or
       @($restored|Where-Object{$_.AccountName -ieq $admins -and $_.AccessRight -ceq 'Full'}).Count-ne1){throw 'Incremental orphan rollback did not preserve both grants.'}
    Revoke-SmbShareAccess -Name $share -AccountName $orphan[0].AccountName -Force -ErrorAction Stop|Out-Null
    $remaining=@(Get-SmbShareAccess -Name $share)
    if($remaining.Count-ne1 -or $remaining[0].AccountName -ine $admins -or $remaining[0].AccessRight -cne 'Full'){throw 'Final incremental orphan cleanup differs.'}
    $orphanAcl=$true
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
[IO.File]::WriteAllText([IO.Path]::GetFullPath($EvidencePath),(@{schemaVersion=1;ordinaryUser=$true;mandatoryCreationFlags='0x18';actualPrivacyVerified=$true;incrementalOrphanAceCleanup=$orphanAcl;managedOwnerMarkerNativeReadback=$ownerMarkerVerified;managedIdentityDenyRightsReadback=$identityRightsVerified;lsaRuntime='WindowsPowerShell5.1';steps=$steps.ToArray();cleanup='confirmed';productionTouched=$false;utc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 6))
