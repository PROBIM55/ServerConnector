[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunnerPath,[Parameter(Mandatory)][string]$AccountName,[Parameter(Mandatory)][string]$OwnerMarker)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS-ne'true' -or $env:RUNNER_ENVIRONMENT-ne'github-hosted' -or $PSVersionTable.PSVersion.Major-ne5){throw 'Disposable hosted Windows PowerShell 5.1 only.'}
if($AccountName-cnotmatch'^clsa[0-9a-f]{10}$' -or $OwnerMarker-cnotmatch'^cnb:[A-Za-z0-9_-]{43}$'){throw 'Isolated fixture identity differs.'}
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($RunnerPath,[ref]$tokens,[ref]$errors)
$functions=@($ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object{$_.Name-in@('Invoke-SmbLsaRightsOperation','Ensure-SmbServiceIdentityRights')})
if($errors.Count -or $functions.Count-ne2){throw 'Actual service identity functions unavailable.'}
. ([scriptblock]::Create(($functions.Extent.Text-join"`n")))
$required=@('SeDenyInteractiveLogonRight','SeDenyRemoteInteractiveLogonRight')
$user=$null;$failure=$null;$cleanup=$false;$readback=$false;$idempotent=$false
try{
 if(Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue){throw 'Disposable account already exists.'}
 $user=New-LocalUser -Name $AccountName -Description $OwnerMarker -NoPassword -Disabled
 $initial=@(Invoke-SmbLsaRightsOperation -Operation Read -Sid $user.SID -Rights $required)
 if($initial.Count-ne0){throw 'Fresh fixture unexpectedly has explicit LSA rights.'}
 Ensure-SmbServiceIdentityRights -Sid $user.SID
 $observed=@(Invoke-SmbLsaRightsOperation -Operation Read -Sid $user.SID -Rights $required)
 if($observed.Count-ne2 -or (($observed|Sort-Object)-join',') -cne (($required|Sort-Object)-join',')){throw 'Both exact deny rights were not read back.'}
 $readback=$true
 Ensure-SmbServiceIdentityRights -Sid $user.SID
 $again=@(Invoke-SmbLsaRightsOperation -Operation Read -Sid $user.SID -Rights $required)
 if((($again|Sort-Object)-join',') -cne (($observed|Sort-Object)-join',')){throw 'Repeated identity restriction changed rights.'}
 $idempotent=$true
}catch{$failure=$_}finally{
 if($user){
  $actual=Get-LocalUser -Name $AccountName -ErrorAction Stop
  if($actual.SID.Value-cne$user.SID.Value -or $actual.Enabled -or $actual.Description-cne$OwnerMarker){throw 'Disposable account ownership/state changed; no cleanup.'}
  if('SmbServiceIdentity.NativeLsa'-as[type]){
   Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace SmbFixtureCleanup {
 public static class Native {
  [DllImport("advapi32.dll")] public static extern uint LsaRemoveAccountRights(IntPtr policy, IntPtr sid, [MarshalAs(UnmanagedType.U1)] bool all, IntPtr rights, uint count);
 }
}
'@
   $handle=[IntPtr]::Zero;$sidPtr=[IntPtr]::Zero
   try{
    $attributes=New-Object SmbServiceIdentity.NativeLsa+LSA_OBJECT_ATTRIBUTES
    $attributes.Length=[uint32][Runtime.InteropServices.Marshal]::SizeOf($attributes)
    $status=[SmbServiceIdentity.NativeLsa]::LsaOpenPolicy([IntPtr]::Zero,[ref]$attributes,0x800,[ref]$handle)
    if($status-ne0){throw 'Disposable LSA cleanup policy open failed.'}
    $sidBytes=New-Object byte[] $user.SID.BinaryLength;$user.SID.GetBinaryForm($sidBytes,0)
    $sidPtr=[Runtime.InteropServices.Marshal]::AllocHGlobal($sidBytes.Length)
    [Runtime.InteropServices.Marshal]::Copy($sidBytes,0,$sidPtr,$sidBytes.Length)
    $status=[SmbFixtureCleanup.Native]::LsaRemoveAccountRights($handle,$sidPtr,$true,[IntPtr]::Zero,0)
    if($status-ne0 -and $status-ne[uint32]3221225524){throw 'Disposable LSA rights cleanup failed.'}
    $remaining=@(Invoke-SmbLsaRightsOperation -Operation Read -Sid $user.SID -Rights $required)
    if($remaining.Count-ne0){throw 'Disposable LSA rights remain.'}
   }finally{
    if($handle-ne[IntPtr]::Zero){[void][SmbServiceIdentity.NativeLsa]::LsaClose($handle)}
    if($sidPtr-ne[IntPtr]::Zero){[Runtime.InteropServices.Marshal]::FreeHGlobal($sidPtr)}
   }
  }
  Remove-LocalUser -Name $AccountName
  if(Get-LocalUser -Name $AccountName -ErrorAction SilentlyContinue){throw 'Disposable SAM account remains.'}
  $cleanup=$true
 }
}
if($failure){throw $failure}
if(!$cleanup -or !$readback -or !$idempotent){throw 'Native identity fixture incomplete.'}
@{runtime='WindowsPowerShell5.1';actualProductionFunctions=$true;exactDenyRightsReadback=$readback;idempotent=$idempotent;ownedSidRightsAndSamCleaned=$cleanup;productionTouched=$false}|ConvertTo-Json -Compress
