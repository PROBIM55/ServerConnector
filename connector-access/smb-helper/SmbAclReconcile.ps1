$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-CanonicalResource {
    param($Resource)
    $rootItem = Get-Item -LiteralPath ([string]$Resource.rootPath) -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Configured SMB root is not a plain directory.'
    }
    $root = [IO.Path]::GetFullPath($rootItem.FullName).TrimEnd('\')
    $share = Get-SmbShare -Name ([string]$Resource.shareName) -ErrorAction Stop
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
        $secure = ConvertTo-SecureString ([string]$request.password) -AsPlainText -Force
        if ($null -eq $user) {
            New-LocalUser -Name $userName -Password $secure -Description $ownerMarker -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword | Out-Null
        } else {
            Set-LocalUser -Name $userName -Password $secure -PasswordNeverExpires $true
            Enable-LocalUser -Name $userName
        }
        $user = Get-LocalUser -Name $userName -ErrorAction Stop
        if (-not [string]::Equals([string]$user.Description, $ownerMarker, [StringComparison]::Ordinal)) {
            throw 'Managed local user ownership marker changed.'
        }
    }

    $sid = if ($null -ne $user) { [Security.Principal.SecurityIdentifier]::new($user.SID.Value) }
        elseif (-not [string]::IsNullOrWhiteSpace([string]$request.expectedLocalUserSid)) {
            [Security.Principal.SecurityIdentifier]::new([string]$request.expectedLocalUserSid)
        } else { $null }
    if ($null -ne $sid) {
        $account = "$env:COMPUTERNAME\$userName"
        foreach ($resource in $resources) { Remove-OwnedAcl $resource $account $sid }
        if ($shouldEnable) {
            foreach ($resource in $resources | Where-Object { $_.permission -ne 'none' }) {
                Add-OwnedAcl $resource $account $sid
            }
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
    [Console]::Error.WriteLine('SMB reconciliation failed.')
    exit 1
}
