$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Assert-SmbSshDenyPolicy {
    param(
        [Parameter(Mandatory)][string]$UserName,
        [Parameter(Mandatory)][string]$SshdPath,
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][datetime]$ServiceStartTime,
        [Parameter(Mandatory)][string]$EffectiveOutput
    )
    $expectedBinary = Join-Path $env:WINDIR 'System32\OpenSSH\sshd.exe'
    if (-not [IO.Path]::IsPathRooted($SshdPath) -or
        -not [string]::Equals([IO.Path]::GetFullPath($SshdPath), [IO.Path]::GetFullPath($expectedBinary), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows OpenSSH service binary could not be verified.'
    }
    $config = Get-Item -LiteralPath $ConfigPath -ErrorAction Stop
    if ($config.PSIsContainer) { throw 'OpenSSH configuration path is not a file.' }
    $defaultConfig = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'ssh\sshd_config'))
    if (-not [string]::Equals([IO.Path]::GetFullPath($config.FullName), $defaultConfig, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OpenSSH configuration path is not the Windows service default.'
    }
    if ([IO.File]::ReadAllText($config.FullName) -match '(?im)^\s*Include\s+') {
        throw 'OpenSSH configuration uses Include directives and cannot be safely bound to the running service.'
    }
    if ($ServiceStartTime -le $config.LastWriteTime) {
        throw 'OpenSSH service may have loaded configuration before its latest change.'
    }
    if ([string]::IsNullOrWhiteSpace($EffectiveOutput)) { throw 'Effective OpenSSH policy is unavailable.' }
    $denyLine = @($EffectiveOutput -split "`r?`n" | Where-Object { $_ -match '^denyusers\s+' })
    if ($denyLine.Count -ne 1) { throw 'Effective OpenSSH DenyUsers policy is missing or ambiguous.' }
    $names = @($denyLine[0] -split '\s+' | Select-Object -Skip 1)
    $exactDeny = $names -contains $UserName
    $ownedFamilyDeny = $UserName -match '^scn_[A-Za-z0-9_]+$' -and $names -contains 'scn_*'
    if (-not $exactDeny -and -not $ownedFamilyDeny) { throw 'Effective OpenSSH DenyUsers policy does not explicitly deny the managed account.' }
}

function Assert-SmbSshCanonicalCommandLine {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$CommandLine,
        [Parameter(Mandatory)][string]$ExpectedBinary
    )
    $expected = [IO.Path]::GetFullPath($ExpectedBinary)
    $command = $CommandLine.Trim()
    $quotedExpected = '"' + $expected + '"'
    if (-not [string]::Equals($command, $expected, [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($command, $quotedExpected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OpenSSH service command line is not the canonical sshd executable without arguments.'
    }
    return $expected
}

function Get-SmbSshEffectivePolicy {
    param([Parameter(Mandatory)][string]$SshdPath, [Parameter(Mandatory)][string]$UserName)
    $effective = @(& $SshdPath -T -C "user=$UserName,host=localhost,addr=127.0.0.1" 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'OpenSSH effective policy evaluation failed.' }
    return ($effective -join "`n")
}

function Get-SmbSshServicePolicy {
    param([Parameter(Mandatory)][string]$UserName)
    $service = Get-CimInstance Win32_Service -Filter "Name='sshd'" -ErrorAction Stop
    if ($null -eq $service -or [int]$service.ProcessId -le 0) { throw 'OpenSSH service is not running with a verifiable process.' }
    $expectedBinary = Join-Path $env:WINDIR 'System32\OpenSSH\sshd.exe'
    $sshdPath = Assert-SmbSshCanonicalCommandLine -CommandLine ([string]$service.PathName) -ExpectedBinary $expectedBinary
    $serviceProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$service.ProcessId)" -ErrorAction Stop
    if ($null -eq $serviceProcess -or [int]$serviceProcess.ProcessId -ne [int]$service.ProcessId) {
        throw 'OpenSSH service process could not be verified.'
    }
    $processBinary = [string]$serviceProcess.ExecutablePath
    if (-not [string]::Equals([IO.Path]::GetFullPath($processBinary), [IO.Path]::GetFullPath($expectedBinary), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OpenSSH service process executable could not be verified.'
    }
    [void](Assert-SmbSshCanonicalCommandLine -CommandLine ([string]$serviceProcess.CommandLine) -ExpectedBinary $expectedBinary)
    $configPath = Join-Path $env:ProgramData 'ssh\sshd_config'
    $process = Get-Process -Id ([int]$service.ProcessId) -ErrorAction Stop
    $effective = Get-SmbSshEffectivePolicy -SshdPath $sshdPath -UserName $UserName
    Assert-SmbSshDenyPolicy -UserName $UserName -SshdPath $sshdPath -ConfigPath $configPath -ServiceStartTime $process.StartTime -EffectiveOutput $effective
}

function Invoke-SmbLsaRightsOperation {
    param([ValidateSet('Read','Add')][string]$Operation, [Security.Principal.SecurityIdentifier]$Sid, [string[]]$Rights)
    if (-not ('SmbServiceIdentity.NativeLsa' -as [type])) {
        Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            namespace SmbServiceIdentity {
                public static class NativeLsa {
                    [StructLayout(LayoutKind.Sequential)] public struct LSA_OBJECT_ATTRIBUTES { public uint Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
                    [StructLayout(LayoutKind.Sequential)] public struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
                    [DllImport("advapi32.dll")] public static extern uint LsaOpenPolicy(IntPtr SystemName, ref LSA_OBJECT_ATTRIBUTES ObjectAttributes, uint DesiredAccess, out IntPtr PolicyHandle);
                    [DllImport("advapi32.dll")] public static extern uint LsaAddAccountRights(IntPtr PolicyHandle, IntPtr AccountSid, LSA_UNICODE_STRING[] UserRights, uint CountOfRights);
                    [DllImport("advapi32.dll")] public static extern uint LsaEnumerateAccountRights(IntPtr PolicyHandle, IntPtr AccountSid, out IntPtr UserRights, out uint CountOfRights);
                    [DllImport("advapi32.dll")] public static extern uint LsaFreeMemory(IntPtr Buffer);
                    [DllImport("advapi32.dll")] public static extern uint LsaClose(IntPtr PolicyHandle);
                    [DllImport("advapi32.dll")] public static extern uint LsaNtStatusToWinError(uint Status);
                }
            }
'@
    }
    $sidBytes = New-Object byte[] $Sid.BinaryLength
    $Sid.GetBinaryForm($sidBytes, 0)
    $sidPtr = [Runtime.InteropServices.Marshal]::AllocHGlobal($sidBytes.Length)
    $policy = [IntPtr]::Zero
    $buffer = [IntPtr]::Zero
    try {
        [Runtime.InteropServices.Marshal]::Copy($sidBytes, 0, $sidPtr, $sidBytes.Length)
        $attributes = New-Object SmbServiceIdentity.NativeLsa+LSA_OBJECT_ATTRIBUTES
        $attributes.Length = [uint32][Runtime.InteropServices.Marshal]::SizeOf($attributes)
        $status = [SmbServiceIdentity.NativeLsa]::LsaOpenPolicy([IntPtr]::Zero, [ref]$attributes, 0x800, [ref]$policy)
        if ($status -ne 0) { throw [ComponentModel.Win32Exception]::new([int][SmbServiceIdentity.NativeLsa]::LsaNtStatusToWinError($status)) }
        if ($Operation -eq 'Add') {
            $native = @()
            try {
                foreach ($right in $Rights) {
                    $ptr = [Runtime.InteropServices.Marshal]::StringToHGlobalUni($right)
                    $u = New-Object SmbServiceIdentity.NativeLsa+LSA_UNICODE_STRING
                    $u.Buffer = $ptr; $u.Length = [uint16]($right.Length * 2); $u.MaximumLength = [uint16](($right.Length + 1) * 2)
                    $native += $u
                }
                $status = [SmbServiceIdentity.NativeLsa]::LsaAddAccountRights($policy, $sidPtr, $native, [uint32]$native.Count)
                if ($status -ne 0) { throw [ComponentModel.Win32Exception]::new([int][SmbServiceIdentity.NativeLsa]::LsaNtStatusToWinError($status)) }
            } finally { foreach ($u in $native) { if ($u.Buffer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::FreeHGlobal($u.Buffer) } } }
        }
        $count = [uint32]0
        $status = [SmbServiceIdentity.NativeLsa]::LsaEnumerateAccountRights($policy, $sidPtr, [ref]$buffer, [ref]$count)
        if ($status -eq [uint32]3221225524) { return @() } # STATUS_OBJECT_NAME_NOT_FOUND: account has no assigned rights yet.
        if ($status -ne 0) { throw [ComponentModel.Win32Exception]::new([int][SmbServiceIdentity.NativeLsa]::LsaNtStatusToWinError($status)) }
        $size = [Runtime.InteropServices.Marshal]::SizeOf([type][SmbServiceIdentity.NativeLsa+LSA_UNICODE_STRING])
        $observed = @()
        for ($i = 0; $i -lt $count; $i++) {
            $entry = [Runtime.InteropServices.Marshal]::PtrToStructure([IntPtr]::Add($buffer, $i * $size), [type][SmbServiceIdentity.NativeLsa+LSA_UNICODE_STRING])
            $observed += [Runtime.InteropServices.Marshal]::PtrToStringUni($entry.Buffer, [int]($entry.Length / 2))
        }
        return ,$observed
    } finally {
        if ($buffer -ne [IntPtr]::Zero) { [void][SmbServiceIdentity.NativeLsa]::LsaFreeMemory($buffer) }
        if ($policy -ne [IntPtr]::Zero) { [void][SmbServiceIdentity.NativeLsa]::LsaClose($policy) }
        [Runtime.InteropServices.Marshal]::FreeHGlobal($sidPtr)
    }
}

function Ensure-SmbServiceIdentityRights {
    param([Parameter(Mandatory)][Security.Principal.SecurityIdentifier]$Sid)
    $required = @('SeDenyInteractiveLogonRight', 'SeDenyRemoteInteractiveLogonRight')
    $observed = @(Invoke-SmbLsaRightsOperation -Operation Read -Sid $Sid -Rights $required)
    $missing = @($required | Where-Object { $observed -notcontains $_ })
    if ($missing.Count -gt 0) { Invoke-SmbLsaRightsOperation -Operation Add -Sid $Sid -Rights $missing | Out-Null }
    $observed = @(Invoke-SmbLsaRightsOperation -Operation Read -Sid $Sid -Rights $required)
    foreach ($right in $required) { if ($observed -notcontains $right) { throw 'SMB service identity logon restrictions failed readback.' } }
}

$newUserNameToDisable = $null
$newUserOwnerMarker = $null

function Get-CanonicalResource {
    param($Resource)
    $rootItem = Get-Item -LiteralPath ([string]$Resource.rootPath) -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Configured SMB root is not a plain directory.'
    }
    $root = [IO.Path]::GetFullPath($rootItem.FullName).TrimEnd('\')
    $share = Get-SmbShare -Name ([string]$Resource.shareName) -ErrorAction Stop
    Assert-SafeShareAcl $share
    $shareItem = Get-Item -LiteralPath ([string]$share.Path) -Force
    if (-not $shareItem.PSIsContainer -or ($shareItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Configured SMB share target is not a plain directory.'
    }
    $shareRoot = [IO.Path]::GetFullPath($shareItem.FullName).TrimEnd('\')
    if (-not [string]::Equals($root, $shareRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Configured SMB root does not exactly match the share target.'
    }
    [pscustomobject]@{
        resourceId = [string]$Resource.resourceId
        shareName = [string]$Resource.shareName
        rootPath = $root
        permission = [string]$Resource.permission
    }
}

function Assert-SafeShareAcl {
    param($Share)
    $administratorSid = 'S-1-5-32-544'
    $systemSid = 'S-1-5-18'
    try {
        $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new([string]$Share.SecurityDescriptor)
        $rules = $descriptor.DiscretionaryAcl
    } catch {
        throw 'SMB share security descriptor could not be validated.'
    }
    if ($null -eq $rules) { throw 'SMB share has a null or missing DACL.' }

    foreach ($rule in $rules) {
        if ($rule -isnot [Security.AccessControl.CommonAce] -or $null -eq $rule.SecurityIdentifier) {
            throw 'SMB share contains an unsupported ACL entry.'
        }
        $sid = $rule.SecurityIdentifier
        if ($sid.Value -eq $administratorSid -or $sid.Value -eq $systemSid) { continue }
        try { $localUser = Get-LocalUser -SID $sid -ErrorAction Stop }
        catch { throw 'SMB share ACL contains an unresolved principal.' }
        if ($null -eq $localUser -or $null -eq $localUser.SID -or $localUser.SID.Value -ne $sid.Value) {
            throw 'SMB share ACL contains a non-user or unresolved principal.'
        }
    }
}

function Test-LocalGroupContainsSid {
    param(
        [Security.Principal.SecurityIdentifier]$GroupSid,
        [Security.Principal.SecurityIdentifier]$UserSid,
        [Collections.Generic.HashSet[string]]$Visited
    )
    if (-not $Visited.Add($GroupSid.Value)) { return $false }
    $members = @(Get-LocalGroupMember -SID $GroupSid -ErrorAction Stop)
    $localAuthority = $UserSid.Value -replace '-\d+$', ''
    foreach ($member in $members) {
        if ($null -eq $member.SID) { throw 'Local group membership contains an unresolved principal.' }
        $memberSid = [Security.Principal.SecurityIdentifier]::new($member.SID.Value)
        if ($memberSid.Value -eq $UserSid.Value) { return $true }
        if ($member.ObjectClass -notin @('User', 'Group')) {
            throw 'Local group membership contains an unresolved principal type.'
        }
        $isBuiltinGroup = $memberSid.Value -match '^S-1-5-32-\d+$'
        $isLocalGroup = $memberSid.Value.StartsWith($localAuthority + '-', [StringComparison]::Ordinal)
        if ($member.ObjectClass -eq 'Group' -and ($isBuiltinGroup -or $isLocalGroup) -and
            (Test-LocalGroupContainsSid $memberSid $UserSid $Visited)) { return $true }
    }
    return $false
}

function Assert-ManagedUserNotElevated {
    param([Security.Principal.SecurityIdentifier]$Sid)
    $isAdministrator = $false
    try {
        $memberships = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $administratorSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
        $isAdministrator = Test-LocalGroupContainsSid $administratorSid $Sid $memberships
    } catch {
        throw 'Managed local user group membership could not be verified.'
    }
    if ($isAdministrator) { throw 'Managed local user belongs to the local Administrators group.' }
}

function Remove-OwnedAcl {
    param($Resource, [string]$Account, [Security.Principal.SecurityIdentifier]$Sid)
    Revoke-SmbShareAccess -Name $Resource.shareName -AccountName $Account -Force -ErrorAction SilentlyContinue | Out-Null
    $acl = Get-Acl -LiteralPath $Resource.rootPath
    $owned = $acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) |
        Where-Object { -not $_.IsInherited -and $_.IdentityReference.Value -eq $Sid.Value }
    foreach ($rule in $owned) { $acl.RemoveAccessRuleSpecific($rule) }
    Set-Acl -LiteralPath $Resource.rootPath -AclObject $acl
}

function Add-OwnedAcl {
    param($Resource, [string]$Account, [Security.Principal.SecurityIdentifier]$Sid)
    $shareRight = if ($Resource.permission -eq 'read') { 'Read' } else { 'Change' }
    Grant-SmbShareAccess -Name $Resource.shareName -AccountName $Account -AccessRight $shareRight -Force | Out-Null
    $rights = if ($Resource.permission -eq 'read') {
        [Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize
    } else {
        [Security.AccessControl.FileSystemRights]::Modify -bor [Security.AccessControl.FileSystemRights]::Synchronize
    }
    $acl = Get-Acl -LiteralPath $Resource.rootPath
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $Sid, $rights,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    Set-Acl -LiteralPath $Resource.rootPath -AclObject $acl
}

function Close-OwnedSessions {
    param([string]$UserName)
    Get-OwnedSessions $UserName |
        ForEach-Object { Close-SmbSession -SessionId $_.SessionId -Force -ErrorAction Stop }
}

function Get-OwnedSessions {
    param([string]$UserName)
    $principal = "$env:COMPUTERNAME\$UserName"
    Get-SmbSession -ErrorAction Stop |
        Where-Object { [string]::Equals([string]$_.ClientUserName, $principal, [StringComparison]::OrdinalIgnoreCase) }
}

function Get-ObservedPermission {
    param($Resource, [string]$Account, [Security.Principal.SecurityIdentifier]$Sid)
    $shareRules = @(Get-SmbShareAccess -Name $Resource.shareName -ErrorAction Stop |
        Where-Object { [string]::Equals($_.AccountName, $Account, [StringComparison]::OrdinalIgnoreCase) })
    $sharePermission = 'none'
    if ($shareRules.Count -eq 1 -and $shareRules[0].AccessControlType -eq 'Allow') {
        if ($shareRules[0].AccessRight -eq 'Read') { $sharePermission = 'read' }
        elseif ($shareRules[0].AccessRight -eq 'Change') { $sharePermission = 'change' }
        else { $sharePermission = 'mismatch' }
    } elseif ($shareRules.Count -ne 0) { $sharePermission = 'mismatch' }

    $acl = Get-Acl -LiteralPath $Resource.rootPath
    $ntfsRules = @($acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier]) |
        Where-Object { -not $_.IsInherited -and $_.IdentityReference.Value -eq $Sid.Value })
    $ntfsPermission = 'none'
    if ($ntfsRules.Count -eq 1 -and $ntfsRules[0].AccessControlType -eq 'Allow' -and
        $ntfsRules[0].InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' -and
        $ntfsRules[0].PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None) {
        $readRights = [int64]([Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [Security.AccessControl.FileSystemRights]::Synchronize)
        $changeRights = [int64]([Security.AccessControl.FileSystemRights]::Modify -bor [Security.AccessControl.FileSystemRights]::Synchronize)
        $observedRights = [int64]$ntfsRules[0].FileSystemRights
        if ($observedRights -eq $readRights) { $ntfsPermission = 'read' }
        elseif ($observedRights -eq $changeRights) { $ntfsPermission = 'change' }
        else { $ntfsPermission = 'mismatch' }
    } elseif ($ntfsRules.Count -ne 0) { $ntfsPermission = 'mismatch' }
    [pscustomobject]@{ share = $sharePermission; ntfs = $ntfsPermission }
}

try {
    $raw = [Console]::In.ReadToEnd()
    if ([Text.Encoding]::UTF8.GetByteCount($raw) -gt 65536) { throw 'Payload too large.' }
    $payload = $raw | ConvertFrom-Json
    $request = $payload.request
    $resources = @($payload.resources | ForEach-Object { Get-CanonicalResource $_ })
    $userName = [string]$request.localUserName
    if ($userName -notmatch '^[A-Za-z0-9_]{1,20}$') { throw 'Invalid managed local user name.' }
    $ownerHasher = [Security.Cryptography.SHA256]::Create()
    try {
        $ownerBytes = $ownerHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes([string]$request.deviceId))
        $ownerMarker = 'Structura Connector SMB ' + [BitConverter]::ToString($ownerBytes).Replace('-', '').ToLowerInvariant()
    } finally { $ownerHasher.Dispose() }

    $user = Get-LocalUser -Name $userName -ErrorAction SilentlyContinue
    if ($null -ne $user -and -not [string]::Equals([string]$user.Description, $ownerMarker, [StringComparison]::Ordinal)) {
        throw 'Managed local user ownership marker does not match.'
    }
    $shouldEnable = $request.action -eq 'apply' -and @($request.grants).Count -gt 0
    if ($shouldEnable) {
        $createdNow = $false
        # Validate the effective, currently loaded sshd policy before credentials or ACLs change.
        Get-SmbSshServicePolicy -UserName $userName
        if ($null -ne $user) { Assert-ManagedUserNotElevated ([Security.Principal.SecurityIdentifier]::new($user.SID.Value)) }
        $secure = ConvertTo-SecureString ([string]$request.password) -AsPlainText -Force
        if ($null -eq $user) {
            $newUserNameToDisable = $userName
            $newUserOwnerMarker = $ownerMarker
            New-LocalUser -Name $userName -Password $secure -Description $ownerMarker -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword -Disabled | Out-Null
            $createdNow = $true
            $user = Get-LocalUser -Name $userName -ErrorAction Stop
            if (-not [string]::Equals([string]$user.Description, $ownerMarker, [StringComparison]::Ordinal)) {
                throw 'Managed local user ownership marker changed.'
            }
        }
        $user = Get-LocalUser -Name $userName -ErrorAction Stop
        if (-not [string]::Equals([string]$user.Description, $ownerMarker, [StringComparison]::Ordinal)) {
            throw 'Managed local user ownership marker changed.'
        }
        Ensure-SmbServiceIdentityRights ([Security.Principal.SecurityIdentifier]::new($user.SID.Value))
        if (-not $createdNow) { Set-LocalUser -Name $userName -Password $secure -PasswordNeverExpires $true }
    }

    $sid = if ($null -ne $user) { [Security.Principal.SecurityIdentifier]::new($user.SID.Value) }
        elseif (-not [string]::IsNullOrWhiteSpace([string]$request.expectedLocalUserSid)) {
            [Security.Principal.SecurityIdentifier]::new([string]$request.expectedLocalUserSid)
        } else { $null }
    if ($null -ne $sid) {
        Assert-ManagedUserNotElevated $sid
        $account = "$env:COMPUTERNAME\$userName"
        foreach ($resource in $resources) { Remove-OwnedAcl $resource $account $sid }
        if ($shouldEnable) {
            foreach ($resource in $resources | Where-Object { $_.permission -ne 'none' }) {
                Add-OwnedAcl $resource $account $sid
            }
            Enable-LocalUser -Name $userName
        } else {
            Close-OwnedSessions $userName
            if ($null -ne $user) { Disable-LocalUser -Name $userName }
        }

        $user = Get-LocalUser -Name $userName -ErrorAction SilentlyContinue
        $observed = @()
        foreach ($resource in $resources) {
            $permission = Get-ObservedPermission $resource $account $sid
            $observed += [pscustomobject]@{
                resourceId = $resource.resourceId
                shareName = $resource.shareName
                canonicalRootPath = $resource.rootPath
                shareAccess = $permission.share
                ntfsAccess = $permission.ntfs
            }
        }
        $sessionCount = @(Get-OwnedSessions $userName).Count
        $response = [pscustomobject]@{
            schemaVersion = 2
            commandId = [string]$request.commandId
            deviceId = [string]$request.deviceId
            revision = [long]$request.revision
            action = [string]$request.action
            localUserName = $userName
            localAccountAuthority = [string]$env:COMPUTERNAME
            localUserSid = $sid.Value
            accountEnabled = if ($null -ne $user) { [bool]$user.Enabled } else { $false }
            activeSessionCount = $sessionCount
            resources = $observed
        }
    } else {
        $response = [pscustomobject]@{
            schemaVersion = 2; commandId = [string]$request.commandId; deviceId = [string]$request.deviceId
            revision = [long]$request.revision; action = [string]$request.action; localUserName = $userName
            localAccountAuthority = [string]$env:COMPUTERNAME
            localUserSid = $null; accountEnabled = $false; activeSessionCount = 0
            resources = @($resources | ForEach-Object {
                [pscustomobject]@{ resourceId = $_.resourceId; shareName = $_.shareName; canonicalRootPath = $_.rootPath; shareAccess = 'none'; ntfsAccess = 'none' }
            })
        }
    }
    $response | ConvertTo-Json -Depth 8 -Compress
} catch {
    if ($null -ne $newUserNameToDisable) {
        try {
            $failedUser = Get-LocalUser -Name $newUserNameToDisable -ErrorAction Stop
            if ([string]::Equals([string]$failedUser.Description, $newUserOwnerMarker, [StringComparison]::Ordinal)) {
                Disable-LocalUser -Name $newUserNameToDisable -ErrorAction Stop
            }
        } catch { }
    }
    [Console]::Error.WriteLine('SMB reconciliation failed.')
    exit 1
}
