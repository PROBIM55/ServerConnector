[CmdletBinding()]
param(
    [string]$Operation,
    [string]$Identifier,
    [string]$RecordName,
    [string]$Token,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:SelfTest = [bool]$SelfTest

function Get-Dns01AllowedSids {
    $sids = @('S-1-5-18', 'S-1-5-32-544')
    if ($script:SelfTest -and $script:TestAdditionalSid -match '^S-1-\d+(?:-\d+)+$') {
        $sids += $script:TestAdditionalSid
    }
    return $sids
}

$script:AllowedIdentifiers = @(
    'connector-access.structura-most.ru',
    'connector-gateway.structura-most.ru'
)
$script:ApiRoot = 'https://api.cloudflare.com/client/v4'
$script:ProductionRoot = 'C:\Platform'
$script:ProductionDirectory = 'C:\Platform\runtime\connector-access'
$script:ProductionConfigPath = Join-Path $script:ProductionDirectory 'cloudflare-dns01.json'
$script:ProductionTokenPath = Join-Path $script:ProductionDirectory 'cloudflare-dns01.token'
$script:ProductionJournalPath = Join-Path $script:ProductionDirectory 'cloudflare-dns01-journal.json'
$script:ProductionLockPath = Join-Path $script:ProductionDirectory 'cloudflare-dns01-journal.lock'

function ConvertTo-NormalizedSid([System.Security.Principal.IdentityReference]$Identity) {
    try {
        return $Identity.Translate([System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw 'A filesystem ACL contains an identity that cannot be resolved.'
    }
}

function Assert-NoReparsePath([string]$Path, [string]$SecurityRoot) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($SecurityRoot)) {
        throw 'A required filesystem path is missing.'
    }
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullRoot = [System.IO.Path]::GetFullPath($SecurityRoot).TrimEnd('\')
    if (-not [string]::Equals($fullPath, $fullRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($fullRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'A protected DNS-01 file is outside its approved root.'
    }
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw 'The protected DNS-01 root directory is unavailable.'
    }

    $cursor = $fullPath
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A reparse point exists in a protected DNS-01 path.'
            }
        } elseif ([string]::Equals($cursor, $fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'The protected DNS-01 root directory is unavailable.'
        }
        if ([string]::Equals($cursor, $fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [System.IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName.TrimEnd('\')
    }
}

function Assert-SafeParentAcl([string]$Directory, [string]$SecurityRoot) {
    Assert-NoReparsePath -Path $Directory -SecurityRoot $SecurityRoot
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw 'A protected DNS-01 directory is unavailable.'
    }
    $allowedSids = @(Get-Dns01AllowedSids)
    $writeMask = [int][System.Security.AccessControl.FileSystemRights]::WriteData -bor
        [int][System.Security.AccessControl.FileSystemRights]::AppendData -bor
        [int][System.Security.AccessControl.FileSystemRights]::CreateFiles -bor
        [int][System.Security.AccessControl.FileSystemRights]::CreateDirectories -bor
        [int][System.Security.AccessControl.FileSystemRights]::Delete -bor
        [int][System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [int][System.Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [int][System.Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [int][System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [int][System.Security.AccessControl.FileSystemRights]::TakeOwnership

    $cursor = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $root = [System.IO.Path]::GetFullPath($SecurityRoot).TrimEnd('\')
    while ($cursor -and $cursor.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        $acl = Get-Acl -LiteralPath $cursor
        Assert-ApprovedOwner -Acl $acl -Description 'protected DNS-01 directory'
        foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
            $sid = $rule.IdentityReference.Value
            if ($sid -notin $allowedSids -and (([int]$rule.FileSystemRights -band $writeMask) -ne 0)) {
                throw 'A protected DNS-01 directory grants write access to an unapproved identity.'
            }
        }
        if ([string]::Equals($cursor, $root, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = [System.IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName.TrimEnd('\')
    }
}

function Assert-ApprovedOwner([object]$Acl, [string]$Description) {
    $approvedOwners = @(Get-Dns01AllowedSids)
    try {
        $ownerSid = $Acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        throw ('The owner of a ' + $Description + ' cannot be verified.')
    }
    if ($ownerSid -notin $approvedOwners) {
        throw ('The owner of a ' + $Description + ' is not an approved protected identity.')
    }
}

function Set-PrivateAcl([string]$Path, [switch]$Directory) {
    $icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
    $grants = @('/inheritance:r', '/grant:r')
    foreach ($sidText in @(Get-Dns01AllowedSids)) {
        $suffix = if ($Directory) { '(OI)(CI)F' } else { 'F' }
        $grants += ('*' + $sidText + ':' + $suffix)
    }
    & $icacls $Path @grants | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not apply the private DNS-01 ACL.' }
}

function Assert-PrivateFileAcl([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    Assert-ApprovedOwner -Acl $acl -Description 'protected DNS-01 file'
    if (-not $acl.AreAccessRulesProtected) { throw 'A DNS-01 secret or journal file has an inheriting ACL.' }
    $rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
    $allowed = @(Get-Dns01AllowedSids)
    if ($rules.Count -ne $allowed.Count) { throw 'A DNS-01 secret or journal file ACL is not exact.' }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow -or
            $rule.IdentityReference.Value -notin $allowed -or
            ($rule.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -ne [System.Security.AccessControl.FileSystemRights]::FullControl -or
            $rule.IsInherited) {
            throw 'A DNS-01 secret or journal file ACL is not restricted to SYSTEM and Administrators.'
        }
    }
    foreach ($sid in $allowed) {
        if (@($rules | Where-Object { $_.IdentityReference.Value -eq $sid }).Count -ne 1) {
            throw 'A DNS-01 secret or journal file ACL is missing a required administrator identity.'
        }
    }
}

function Assert-PrivateFile([string]$Path, [string]$SecurityRoot) {
    Assert-NoReparsePath -Path $Path -SecurityRoot $SecurityRoot
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'A required protected DNS-01 file is unavailable.' }
    Assert-SafeParentAcl -Directory ([System.IO.Path]::GetDirectoryName($Path)) -SecurityRoot $SecurityRoot
    Assert-PrivateFileAcl -Path $Path
}

function Assert-ChallengeInput([string]$Action, [string]$HostName, [string]$DnsRecordName, [string]$DnsToken) {
    if ($Action -notin @('create', 'delete')) { throw 'Unsupported DNS-01 action.' }
    $allowed = @($script:AllowedIdentifiers | Where-Object { [string]::Equals($_, $HostName, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($allowed.Count -ne 1) { throw 'The ACME identifier is not approved for this DNS-01 integration.' }
    $expectedRecordName = '_acme-challenge.' + $allowed[0]
    if (-not [string]::Equals($expectedRecordName, $DnsRecordName, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'The ACME DNS record name does not match the approved identifier.'
    }
    if ([string]::IsNullOrWhiteSpace($DnsToken) -or $DnsToken.Length -gt 255 -or $DnsToken -notmatch '^[A-Za-z0-9_-]+$') {
        throw 'The ACME challenge content is invalid.'
    }
    return $allowed[0].ToLowerInvariant()
}

function Get-ProtectedConfiguration([string]$ConfigPath, [string]$CredentialPath, [string]$SecurityRoot) {
    Assert-PrivateFile -Path $ConfigPath -SecurityRoot $SecurityRoot
    Assert-PrivateFile -Path $CredentialPath -SecurityRoot $SecurityRoot
    try {
        $config = [System.IO.File]::ReadAllText($ConfigPath) | ConvertFrom-Json
    } catch {
        throw 'The protected DNS-01 configuration is invalid.'
    }
    if ($null -eq $config -or $config.SchemaVersion -ne 1 -or
        -not [string]::Equals([string]$config.ZoneName, 'structura-most.ru', [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]$config.ZoneId -notmatch '^[A-Fa-f0-9]{32}$') {
        throw 'The protected DNS-01 zone configuration is invalid.'
    }
    try {
        $apiToken = [System.IO.File]::ReadAllText($CredentialPath, [System.Text.Encoding]::UTF8)
    } catch {
        throw 'The protected Cloudflare credential file cannot be read.'
    }
    $apiToken = $apiToken.TrimEnd("`r", "`n")
    if ($apiToken.Length -lt 20 -or $apiToken.Length -gt 256 -or $apiToken -match '\s') {
        throw 'The protected Cloudflare credential file is invalid.'
    }
    return [pscustomobject]@{ ZoneId = ([string]$config.ZoneId).ToLowerInvariant(); ApiToken = $apiToken }
}

function Invoke-CloudflareApi([string]$Method, [string]$Uri, [string]$ApiToken, [object]$Body, [scriptblock]$Transport) {
    if ($Transport) {
        try { return & $Transport $Method $Uri $Body $ApiToken } catch { throw 'Cloudflare API request failed.' }
    }
    $headers = @{ Authorization = 'Bearer ' + $ApiToken }
    try {
        if ($Method -eq 'GET' -or $Method -eq 'DELETE') {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -TimeoutSec 20 -ErrorAction Stop
        }
        $jsonBody = ConvertTo-Json -InputObject $Body -Depth 5 -Compress
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType 'application/json' -Body $jsonBody -TimeoutSec 20 -ErrorAction Stop
    } catch {
        throw 'Cloudflare API request failed.'
    } finally {
        $headers.Clear()
    }
}

function Assert-CloudflareSuccess([object]$Response) {
    if ($null -eq $Response -or $Response.success -ne $true) { throw 'Cloudflare rejected a DNS-01 request.' }
}

function Get-CloudflareRecords([string]$ZoneId, [string]$DnsRecordName, [string]$ApiToken, [scriptblock]$Transport) {
    $encodedName = [System.Uri]::EscapeDataString($DnsRecordName)
    $uri = $script:ApiRoot + '/zones/' + $ZoneId + '/dns_records?type=TXT&name.exact=' + $encodedName + '&per_page=100&page=1'
    $response = Invoke-CloudflareApi -Method 'GET' -Uri $uri -ApiToken $ApiToken -Body $null -Transport $Transport
    Assert-CloudflareSuccess $response
    if ($null -eq $response.result_info -or [int]$response.result_info.total_pages -gt 1) {
        throw 'The Cloudflare DNS record query was incomplete or unexpectedly paginated.'
    }
    $records = @($response.result)
    foreach ($record in $records) {
        if ($null -eq $record -or $record.type -cne 'TXT' -or
            -not [string]::Equals([string]$record.name, $DnsRecordName, [System.StringComparison]::OrdinalIgnoreCase) -or
            [string]$record.id -notmatch '^[A-Fa-f0-9]{32}$') {
            throw 'Cloudflare returned an unexpected DNS record.'
        }
    }
    return ,$records
}

function Read-DnsJournal([string]$JournalPath, [string]$SecurityRoot) {
    if (-not (Test-Path -LiteralPath $JournalPath -PathType Leaf)) {
        return [pscustomobject]@{ SchemaVersion = 1; Entries = @() }
    }
    Assert-PrivateFile -Path $JournalPath -SecurityRoot $SecurityRoot
    try { $journal = [System.IO.File]::ReadAllText($JournalPath) | ConvertFrom-Json } catch { throw 'The protected DNS-01 journal is invalid.' }
    if ($null -eq $journal -or $journal.SchemaVersion -ne 1 -or $null -eq $journal.Entries) {
        throw 'The protected DNS-01 journal has an unsupported format.'
    }
    $entries = @($journal.Entries)
    $seen = @{}
    foreach ($entry in $entries) {
        if ($null -eq $entry -or $entry.Identifier -notin $script:AllowedIdentifiers -or
            $entry.RecordName -cne ('_acme-challenge.' + $entry.Identifier) -or
            [string]$entry.Content -notmatch '^[A-Za-z0-9_-]{1,255}$' -or
            [string]$entry.Comment -notmatch '^structura-acme-dns01:[0-9a-f]{32}$' -or
            $entry.State -notin @('Pending', 'Active')) {
            throw 'The protected DNS-01 journal contains an invalid entry.'
        }
        if ($entry.RecordId -and [string]$entry.RecordId -notmatch '^[A-Fa-f0-9]{32}$') {
            throw 'The protected DNS-01 journal contains an invalid record identifier.'
        }
        $key = ([string]$entry.Identifier).ToLowerInvariant() + '|' + [string]$entry.Content
        if ($seen.ContainsKey($key)) { throw 'The protected DNS-01 journal contains duplicate ownership entries.' }
        $seen[$key] = $true
    }
    return [pscustomobject]@{ SchemaVersion = 1; Entries = $entries }
}

function Save-DnsJournal([string]$JournalPath, [string]$SecurityRoot, [object]$Journal) {
    $directory = [System.IO.Path]::GetDirectoryName($JournalPath)
    Assert-SafeParentAcl -Directory $directory -SecurityRoot $SecurityRoot
    $temporaryPath = $JournalPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $created = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $created.Dispose()
        Set-PrivateAcl -Path $temporaryPath
        Assert-PrivateFileAcl -Path $temporaryPath
        $json = ConvertTo-Json -InputObject $Journal -Depth 8
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)
        $stream = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::Truncate, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if (Test-Path -LiteralPath $JournalPath -PathType Leaf) {
            Assert-PrivateFile -Path $JournalPath -SecurityRoot $SecurityRoot
            $backupPath = $JournalPath + '.' + [Guid]::NewGuid().ToString('N') + '.bak'
            [System.IO.File]::Replace($temporaryPath, $JournalPath, $backupPath)
            Assert-PrivateFileAcl -Path $backupPath
            [System.IO.File]::Delete($backupPath)
        } else {
            [System.IO.File]::Move($temporaryPath, $JournalPath)
        }
        Assert-PrivateFile -Path $JournalPath -SecurityRoot $SecurityRoot
    } finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { [System.IO.File]::Delete($temporaryPath) }
    }
}

function Enter-DnsJournalLock([string]$LockPath, [string]$SecurityRoot, [int]$TimeoutSeconds) {
    Assert-NoReparsePath -Path $LockPath -SecurityRoot $SecurityRoot
    Assert-SafeParentAcl -Directory ([System.IO.Path]::GetDirectoryName($LockPath)) -SecurityRoot $SecurityRoot
    # The lock is a persistent sentinel. Secure it before acquiring the exclusive
    # handle; Set-Acl while FileShare.None is held can fail on Windows.
    if (-not (Test-Path -LiteralPath $LockPath -PathType Leaf)) {
        $created = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::ReadWrite)
        $created.Dispose()
        Assert-NoReparsePath -Path $LockPath -SecurityRoot $SecurityRoot
        Set-PrivateAcl -Path $LockPath
    }
    Assert-PrivateFileAcl -Path $LockPath
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $stream = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            try {
                Assert-PrivateFileAcl -Path $LockPath
                return $stream
            } catch {
                $stream.Dispose()
                throw
            }
        } catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 100
        }
    }
    throw 'The DNS-01 journal is busy; retry after the current challenge completes.'
}

function Get-JournalEntry([object]$Journal, [string]$HostName, [string]$DnsToken) {
    $matches = @($Journal.Entries | Where-Object {
        [string]::Equals([string]$_.Identifier, $HostName, [System.StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals([string]$_.Content, $DnsToken, [System.StringComparison]::Ordinal)
    })
    if ($matches.Count -gt 1) { throw 'The protected DNS-01 journal has ambiguous ownership data.' }
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}

function Assert-OwnedRecord([object]$Record, [object]$Entry, [string]$DnsToken) {
    if ($null -eq $Record -or $Record.id -cne $Entry.RecordId -or
        $Record.type -cne 'TXT' -or
        -not [string]::Equals([string]$Record.name, [string]$Entry.RecordName, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$Record.content, $DnsToken, [System.StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$Record.comment, [string]$Entry.Comment, [System.StringComparison]::Ordinal)) {
        throw 'The Cloudflare TXT record no longer matches its journaled ownership.'
    }
}

function Invoke-Dns01Challenge {
    param(
        [Parameter(Mandatory=$true)][string]$Action,
        [Parameter(Mandatory=$true)][string]$HostName,
        [Parameter(Mandatory=$true)][string]$DnsRecordName,
        [Parameter(Mandatory=$true)][string]$DnsToken,
        [Parameter(Mandatory=$true)][string]$ConfigPath,
        [Parameter(Mandatory=$true)][string]$CredentialPath,
        [Parameter(Mandatory=$true)][string]$JournalPath,
        [Parameter(Mandatory=$true)][string]$LockPath,
        [Parameter(Mandatory=$true)][string]$SecurityRoot,
        [scriptblock]$Transport,
        [int]$LockTimeoutSeconds = 15
    )
    $normalizedHost = Assert-ChallengeInput -Action $Action -HostName $HostName -DnsRecordName $DnsRecordName -DnsToken $DnsToken
    $DnsRecordName = '_acme-challenge.' + $normalizedHost
    $config = Get-ProtectedConfiguration -ConfigPath $ConfigPath -CredentialPath $CredentialPath -SecurityRoot $SecurityRoot
    $lock = Enter-DnsJournalLock -LockPath $LockPath -SecurityRoot $SecurityRoot -TimeoutSeconds $LockTimeoutSeconds
    try {
        $journal = Read-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot
        $entry = Get-JournalEntry -Journal $journal -HostName $normalizedHost -DnsToken $DnsToken
        $records = Get-CloudflareRecords -ZoneId $config.ZoneId -DnsRecordName $DnsRecordName -ApiToken $config.ApiToken -Transport $Transport

        if ($Action -eq 'create') {
            if ($records.Count -gt 1) { throw 'Multiple TXT records exist at the ACME challenge name.' }
            if ($null -eq $entry) {
                if ($records.Count -ne 0) { throw 'A TXT record already exists without journaled ownership.' }
                $entry = [pscustomobject]@{
                    Identifier = $normalizedHost
                    RecordName = $DnsRecordName
                    Content = $DnsToken
                    Comment = 'structura-acme-dns01:' + [Guid]::NewGuid().ToString('N')
                    RecordId = ''
                    State = 'Pending'
                }
                $journal.Entries = @($journal.Entries) + @($entry)
                Save-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot -Journal $journal
            } elseif ($records.Count -eq 1) {
                $candidate = $records[0]
                if ($candidate.content -cne $DnsToken -or $candidate.comment -cne $entry.Comment -or
                    ($entry.RecordId -and $candidate.id -cne $entry.RecordId)) {
                    throw 'An existing TXT record does not match the journaled challenge.'
                }
                if (-not $entry.RecordId) {
                    $entry.RecordId = [string]$candidate.id
                    $entry.State = 'Active'
                    Save-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot -Journal $journal
                }
                return
            } elseif ($entry.State -eq 'Active') {
                throw 'A journaled active TXT record is missing from Cloudflare.'
            }

            $recordBody = [ordered]@{ type = 'TXT'; name = $DnsRecordName; content = $DnsToken; ttl = 120; comment = $entry.Comment; proxied = $false }
            $createUri = $script:ApiRoot + '/zones/' + $config.ZoneId + '/dns_records'
            $created = Invoke-CloudflareApi -Method 'POST' -Uri $createUri -ApiToken $config.ApiToken -Body $recordBody -Transport $Transport
            Assert-CloudflareSuccess $created
            $result = $created.result
            if ($null -eq $result -or [string]$result.id -notmatch '^[A-Fa-f0-9]{32}$' -or
                $result.type -cne 'TXT' -or
                -not [string]::Equals([string]$result.name, $DnsRecordName, [System.StringComparison]::OrdinalIgnoreCase) -or
                $result.content -cne $DnsToken -or $result.comment -cne $entry.Comment) {
                throw 'Cloudflare returned an unexpected created DNS record.'
            }
            $entry.RecordId = [string]$result.id
            $entry.State = 'Pending'
            Save-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot -Journal $journal
            $verified = Get-CloudflareRecords -ZoneId $config.ZoneId -DnsRecordName $DnsRecordName -ApiToken $config.ApiToken -Transport $Transport
            if ($verified.Count -ne 1) { throw 'The newly created TXT record could not be verified uniquely.' }
            Assert-OwnedRecord -Record $verified[0] -Entry $entry -DnsToken $DnsToken
            $entry.State = 'Active'
            Save-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot -Journal $journal
            return
        }

        if ($null -eq $entry) {
            if ($records.Count -eq 0) { return }
            throw 'A TXT record exists without journaled ownership; it was left untouched.'
        }
        if ($records.Count -gt 1) { throw 'Multiple TXT records exist at the ACME challenge name; cleanup stopped.' }
        if ($records.Count -eq 0) {
            $journal.Entries = @($journal.Entries | Where-Object { $_ -ne $entry })
            Save-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot -Journal $journal
            return
        }
        if (-not $entry.RecordId) {
            if ($records[0].content -cne $DnsToken -or $records[0].comment -cne $entry.Comment) {
                throw 'The pending TXT record does not match journaled ownership; it was left untouched.'
            }
            $entry.RecordId = [string]$records[0].id
            $entry.State = 'Active'
            Save-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot -Journal $journal
        }
        Assert-OwnedRecord -Record $records[0] -Entry $entry -DnsToken $DnsToken
        $deleteUri = $script:ApiRoot + '/zones/' + $config.ZoneId + '/dns_records/' + $entry.RecordId
        $deleted = Invoke-CloudflareApi -Method 'DELETE' -Uri $deleteUri -ApiToken $config.ApiToken -Body $null -Transport $Transport
        Assert-CloudflareSuccess $deleted
        if ($deleted.result.id -and $deleted.result.id -cne $entry.RecordId) { throw 'Cloudflare returned a different deleted DNS record identifier.' }
        $remaining = Get-CloudflareRecords -ZoneId $config.ZoneId -DnsRecordName $DnsRecordName -ApiToken $config.ApiToken -Transport $Transport
        if ($remaining.Count -ne 0) { throw 'A DNS record remains after cleanup; it was left untouched.' }
        $journal.Entries = @($journal.Entries | Where-Object { $_ -ne $entry })
        Save-DnsJournal -JournalPath $JournalPath -SecurityRoot $SecurityRoot -Journal $journal
    } finally {
        $lock.Dispose()
    }
}

if ($SelfTest) { return }

try {
    Invoke-Dns01Challenge -Action $Operation -HostName $Identifier -DnsRecordName $RecordName -DnsToken $Token `
        -ConfigPath $script:ProductionConfigPath -CredentialPath $script:ProductionTokenPath `
        -JournalPath $script:ProductionJournalPath -LockPath $script:ProductionLockPath `
        -SecurityRoot $script:ProductionRoot
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
