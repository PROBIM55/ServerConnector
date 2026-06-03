param(
    [Parameter(Mandatory = $true)]
    [string]$UserName,
    [string]$Password = "",
    [Parameter(Mandatory = $true)]
    [string]$ShareName,
    [string]$SharePath = "",
    [ValidateSet('Provision', 'Disable', 'Remove')]
    [string]$Action = 'Provision'
)

$ErrorActionPreference = 'Stop'

function Close-UserSmbSessions {
    param([string]$User)
    try {
        Get-SmbSession -ErrorAction SilentlyContinue |
            Where-Object { $_.ClientUserName -like "*\$User" } |
            ForEach-Object { Close-SmbSession -SessionId $_.SessionId -Force -ErrorAction SilentlyContinue }
    } catch {
        # best-effort: closing live sessions is not critical to revoking access
    }
}

# --- Disable: block access but keep the account (reversible; for token revoke) ---
if ($Action -eq 'Disable') {
    $account = "$env:COMPUTERNAME\$UserName"
    Revoke-SmbShareAccess -Name $ShareName -AccountName $account -Force -ErrorAction SilentlyContinue | Out-Null
    $existing = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    if ($existing) { Disable-LocalUser -Name $UserName }
    Close-UserSmbSessions -User $UserName
    Write-Output ("disabled:{0}" -f $UserName)
    return
}

# --- Remove: delete the account and its share access (for token delete) ---
if ($Action -eq 'Remove') {
    $account = "$env:COMPUTERNAME\$UserName"
    Revoke-SmbShareAccess -Name $ShareName -AccountName $account -Force -ErrorAction SilentlyContinue | Out-Null
    Close-UserSmbSessions -User $UserName
    $existing = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue
    if ($existing) { Remove-LocalUser -Name $UserName }
    Write-Output ("removed:{0}" -f $UserName)
    return
}

# --- Provision (default; unchanged behavior) ---
if (-not (Test-Path $SharePath)) {
    $share = Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue
    if ($share -and (Test-Path $share.Path)) {
        $SharePath = $share.Path
    } else {
        throw "Share path not found: $SharePath"
    }
}

$securePassword = ConvertTo-SecureString $Password -AsPlainText -Force
$existingUser = Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue

if ($existingUser) {
    Set-LocalUser -Name $UserName -Password $securePassword
    Set-LocalUser -Name $UserName -PasswordNeverExpires $true
    Enable-LocalUser -Name $UserName
} else {
    New-LocalUser -Name $UserName -Password $securePassword -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires | Out-Null
}

$account = "$env:COMPUTERNAME\$UserName"

Grant-SmbShareAccess -Name $ShareName -AccountName $account -AccessRight Change -Force | Out-Null
& icacls $SharePath /grant "${account}:(OI)(CI)M" /T /C | Out-Null

Write-Output ("ok:{0}:{1}" -f $account, $ShareName)
