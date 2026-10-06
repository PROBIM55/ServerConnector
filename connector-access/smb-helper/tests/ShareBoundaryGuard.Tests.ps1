$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot '../SmbAclReconcile.ps1'
$source = [IO.File]::ReadAllText((Resolve-Path $scriptPath))
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'SMB helper script syntax errors.' }
foreach ($name in @('Get-CanonicalResource', 'Assert-SafeShareAcl', 'Test-LocalGroupContainsSid', 'Assert-ManagedUserNotElevated')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $false)
    if ($null -eq $function) { throw "Missing function $name" }
    Invoke-Expression $function.Extent.Text
}

$preflight = $source.IndexOf('$resources = @($payload.resources | ForEach-Object { Get-CanonicalResource $_ })', [StringComparison]::Ordinal)
$mutationCalls = @(
    'New-LocalUser -Name', 'Set-LocalUser -Name', 'Enable-LocalUser -Name', 'Disable-LocalUser -Name',
    'Remove-OwnedAcl $resource', 'Add-OwnedAcl $resource', 'Close-OwnedSessions $userName'
)
if ($preflight -lt 0) { throw 'Missing resource preflight.' }
foreach ($mutation in $mutationCalls) {
    $position = $source.IndexOf($mutation, $preflight, [StringComparison]::Ordinal)
    if ($position -lt 0 -or $preflight -gt $position) {
        throw "Resource preflight must remain before mutation: $mutation"
    }
}

$script:knownSids = @('S-1-5-21-1-2-3-1001')
$script:shareSddl = ''
$script:mutationCalls = 0
$script:groupMembers = @{}
$script:groupReadFails = $false
function Get-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [switch]$Force)
    [pscustomobject]@{ FullName = $LiteralPath; PSIsContainer = $true; Attributes = [IO.FileAttributes]::Directory }
}
function Get-SmbShare {
    [CmdletBinding()]
    param([string]$Name)
    [pscustomobject]@{ Name = $Name; Path = 'C:\FixtureRoot'; SecurityDescriptor = $script:shareSddl }
}
function Get-LocalUser {
    [CmdletBinding()]
    param([Security.Principal.SecurityIdentifier]$SID)
    if ($SID -and $script:knownSids -contains $SID.Value) {
        return [pscustomobject]@{ SID = $SID }
    }
    return $null
}
function Get-LocalGroupMember {
    [CmdletBinding()]
    param([Security.Principal.SecurityIdentifier]$SID)
    if ($script:groupReadFails) { throw 'Fixture membership lookup failure.' }
    if ($script:groupMembers.ContainsKey($SID.Value)) { return @($script:groupMembers[$SID.Value]) }
    return @()
}
function Grant-SmbShareAccess {
    [CmdletBinding()]
    param([string]$Name, [string]$AccountName, [string]$AccessRight, [switch]$Force)
    $script:mutationCalls++
}

function Invoke-PreflightThenMarkerMutation([string]$Sddl) {
    $script:shareSddl = $Sddl
    $resource = [pscustomobject]@{ resourceId = 'fixture'; shareName = 'fixture'; rootPath = 'C:\FixtureRoot'; permission = 'read' }
    $canonical = Get-CanonicalResource $resource
    Grant-SmbShareAccess -Name $canonical.shareName -AccountName 'fixture\user' -AccessRight 'Read' -Force | Out-Null
}

$allowed = 'D:(A;;FA;;;S-1-5-32-544)(A;;0x001301bf;;;S-1-5-21-1-2-3-1001)'
Invoke-PreflightThenMarkerMutation $allowed
if ($script:mutationCalls -ne 1) { throw 'Known individual local user SID was not accepted.' }

$deniedSids = @(
    'S-1-1-0',                         # Everyone
    'S-1-5-32-545',                    # BUILTIN Users
    'S-1-5-21-1-2-3-1500',             # custom group or unresolved account SID
    'S-1-5-99-1234'                    # unknown authority
)
foreach ($sid in $deniedSids) {
    $script:mutationCalls = 0
    $denied = $false
    try {
        Invoke-PreflightThenMarkerMutation "D:(A;;FA;;;S-1-5-32-544)(A;;0x001301bf;;;$sid)"
    } catch {
        $denied = $true
    }
    if (-not $denied) { throw "Unsafe share principal was accepted: $sid" }
    if ($script:mutationCalls -ne 0) { throw "A mutation ran before rejecting share principal: $sid" }
}

$nullDaclDenied = $false
try { Invoke-PreflightThenMarkerMutation 'D:NO_ACCESS_CONTROL' } catch { $nullDaclDenied = $true }
if (-not $nullDaclDenied -or $script:mutationCalls -ne 0) { throw 'A null/empty share DACL was not rejected before mutation.' }

$managedSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1001')
$adminSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$localGroupSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-1100')
$script:groupMembers = @{}
Assert-ManagedUserNotElevated $managedSid

$script:groupMembers[$adminSid.Value] = @([pscustomobject]@{ SID = $managedSid; ObjectClass = 'User' })
$directMembershipDenied = $false
try { Assert-ManagedUserNotElevated $managedSid } catch { $directMembershipDenied = $true }
if (-not $directMembershipDenied) { throw 'Direct Administrators membership was accepted.' }

$script:groupMembers = @{
    $adminSid.Value = @([pscustomobject]@{ SID = $localGroupSid; ObjectClass = 'Group' })
    $localGroupSid.Value = @([pscustomobject]@{ SID = $managedSid; ObjectClass = 'User' })
}
$nestedMembershipDenied = $false
try { Assert-ManagedUserNotElevated $managedSid } catch { $nestedMembershipDenied = $true }
if (-not $nestedMembershipDenied) { throw 'Nested Administrators membership was accepted.' }

$script:groupMembers = @{ $adminSid.Value = @([pscustomobject]@{ SID = $localGroupSid; ObjectClass = 'Unknown' }) }
$unknownMembershipDenied = $false
try { Assert-ManagedUserNotElevated $managedSid } catch { $unknownMembershipDenied = $true }
if (-not $unknownMembershipDenied) { throw 'Unresolved Administrators membership type was accepted.' }

$script:groupMembers = @{}
$script:groupReadFails = $true
$unverifiedMembershipDenied = $false
try { Assert-ManagedUserNotElevated $managedSid } catch { $unverifiedMembershipDenied = $true }
if (-not $unverifiedMembershipDenied) { throw 'Unavailable group membership readback was accepted.' }

Write-Output 'PASS: known local-user SID accepted; broad/group/unknown/null-DACL principals rejected before mutation; direct/nested/unknown/unreadable Administrators membership rejected.'
