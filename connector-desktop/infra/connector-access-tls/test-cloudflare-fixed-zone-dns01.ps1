$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path $PSScriptRoot 'cloudflare-fixed-zone-dns01.ps1'
$fixturePath = Join-Path $PSScriptRoot 'cloudflare-fixed-zone-dns01.fixtures.json'
. $scriptPath -SelfTest
$script:TestAdditionalSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$script:fixtures = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
$script:fixtureApiToken = [string]$script:fixtures.credential
$script:fixtureRecords = New-Object 'System.Collections.Generic.List[object]'
$script:fixtureCalls = New-Object 'System.Collections.Generic.List[object]'
$script:fixtureFault = ''
$script:fixturePostCount = 0
$script:fixtureDeleteCount = 0
$script:testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('cloudflare-dns01-' + [Guid]::NewGuid().ToString('N'))
$script:ownerFixturePath = ''
$script:ownerFixtureAcl = $null

function Get-Acl {
    param([string]$LiteralPath)
    if ($script:ownerFixturePath -and [string]::Equals($LiteralPath, $script:ownerFixturePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $script:ownerFixtureAcl
    }
    Microsoft.PowerShell.Security\Get-Acl -LiteralPath $LiteralPath
}

function Reset-FixtureApi {
    $script:fixtureRecords.Clear()
    $script:fixtureCalls.Clear()
    $script:fixtureFault = ''
    $script:fixturePostCount = 0
    $script:fixtureDeleteCount = 0
}

function New-TestCase([string]$Name) {
    $root = Join-Path $script:testRoot $Name
    [void](New-Item -ItemType Directory -Path $root)
    Set-PrivateAcl -Path $root -Directory
    $config = Join-Path $root 'config.json'
    $credential = Join-Path $root 'credential.txt'
    $journal = Join-Path $root 'journal.json'
    $lock = Join-Path $root 'journal.lock'
    Set-TestConfig -Path $config -ZoneId ([string]$script:fixtures.zoneId)
    [System.IO.File]::WriteAllText($credential, $script:fixtureApiToken, (New-Object System.Text.UTF8Encoding($false)))
    Set-PrivateAcl -Path $credential
    return [pscustomobject]@{ Root=$root; Config=$config; Credential=$credential; Journal=$journal; Lock=$lock }
}

function Set-TestConfig([string]$Path, [string]$ZoneId) {
    $config = [pscustomobject]@{ SchemaVersion=1; ZoneName='structura-most.ru'; ZoneId=$ZoneId }
    [System.IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $config -Depth 3), (New-Object System.Text.UTF8Encoding($false)))
    Set-PrivateAcl -Path $Path
}

function Invoke-FixtureTransport([string]$Method, [string]$Uri, [object]$Body, [string]$ApiToken) {
    if ($ApiToken -cne $script:fixtureApiToken) { throw 'Fixture authorization mismatch.' }
    if ($Uri.Contains($script:fixtureApiToken)) { throw 'Credential was included in the request URI.' }
    if ($null -ne $Body -and (ConvertTo-Json -InputObject $Body -Depth 5 -Compress).Contains($script:fixtureApiToken)) {
        throw 'Credential was included in the request body.'
    }
    $script:fixtureCalls.Add([pscustomobject]@{ Method=$Method; Uri=$Uri })
    if ($Uri -notmatch '/zones/([a-f0-9]{32})/dns_records') { throw 'Unexpected fixture endpoint.' }
    $requestedZone = $Matches[1]
    if ($requestedZone -cne [string]$script:fixtures.zoneId) { return $script:fixtures.responses.wrongZone }

    if ($Method -eq 'GET') {
        if ($script:fixtureFault -eq 'ApiFailure') { return $script:fixtures.responses.apiFailure }
        if ($script:fixtureFault -eq 'WrongName') {
            $records = @($script:fixtures.responses.wrongNameTxt)
        } else {
            $records = @($script:fixtureRecords.ToArray())
        }
        return [pscustomobject]@{ success=$true; result=$records; result_info=[pscustomobject]@{ page=1; per_page=100; total_pages=1 } }
    }
    if ($Method -eq 'POST') {
        $script:fixturePostCount++
        if ($script:fixtureFault -eq 'PostBeforeCommit') { $script:fixtureFault=''; throw 'Injected request failure before commit.' }
        $record = $script:fixtures.responses.createdRecord | ConvertTo-Json -Depth 5 | ConvertFrom-Json
        $record.name = [string]$Body.name
        $record.content = [string]$Body.content
        $record.comment = [string]$Body.comment
        $script:fixtureRecords.Add($record)
        if ($script:fixtureFault -eq 'PostAfterCommit') { $script:fixtureFault=''; throw 'Injected lost response after commit.' }
        return [pscustomobject]@{ success=$true; result=$record }
    }
    if ($Method -eq 'DELETE') {
        $script:fixtureDeleteCount++
        $recordId = [System.IO.Path]::GetFileName(([System.Uri]$Uri).AbsolutePath)
        $record = @($script:fixtureRecords | Where-Object { $_.id -ceq $recordId })
        if ($record.Count -ne 1) { return [pscustomobject]@{ success=$false; errors=@(@{code=81044;message='not found'}); result=$null } }
        [void]$script:fixtureRecords.Remove($record[0])
        if ($script:fixtureFault -eq 'DeleteAfterCommit') { $script:fixtureFault=''; throw 'Injected lost delete response after commit.' }
        return [pscustomobject]@{ success=$true; result=[pscustomobject]@{ id=$recordId } }
    }
    throw 'Unexpected fixture method.'
}

function Invoke-TestAction([object]$Case, [string]$Action, [string]$HostName, [string]$Name, [string]$Content, [int]$LockTimeout = 2) {
    Invoke-Dns01Challenge -Action $Action -HostName $HostName -DnsRecordName $Name -DnsToken $Content `
        -ConfigPath $Case.Config -CredentialPath $Case.Credential -JournalPath $Case.Journal -LockPath $Case.Lock `
        -SecurityRoot $Case.Root -Transport { param($method,$uri,$body,$apiToken) Invoke-FixtureTransport $method $uri $body $apiToken } `
        -LockTimeoutSeconds $LockTimeout
}

function Assert-ActionFails([object]$Case, [string]$Action, [string]$HostName, [string]$Name, [string]$Content, [string]$Label, [int]$LockTimeout = 2) {
    $failed = $false
    $message = ''
    try { Invoke-TestAction $Case $Action $HostName $Name $Content $LockTimeout | Out-Null } catch { $failed=$true; $message=$_.Exception.Message }
    if (-not $failed) { throw "Expected failure was not raised: $Label" }
    if ($message.Contains($script:fixtureApiToken) -or $message.Contains($Content)) { throw "An error exposed a credential or challenge value: $Label" }
}

try {
    [void](New-Item -ItemType Directory -Path $script:testRoot)
    Set-PrivateAcl -Path $script:testRoot -Directory
    $challenge = $script:fixtures.challenge
    $hostName = [string]$challenge.identifier
    $recordName = [string]$challenge.recordName
    $content = [string]$challenge.content

    # Create, repeated create, delete, and repeated delete are idempotent.
    Reset-FixtureApi
    $case = New-TestCase 'lifecycle'
    Invoke-TestAction $case 'create' $hostName $recordName $content
    if ($script:fixturePostCount -ne 1 -or $script:fixtureRecords.Count -ne 1) { throw 'Create did not issue one owned TXT record.' }
    $journal = Read-DnsJournal $case.Journal $case.Root
    if ($journal.Entries.Count -ne 1 -or $journal.Entries[0].State -cne 'Active') { throw 'Create did not persist verified ownership.' }
    Invoke-TestAction $case 'create' $hostName $recordName $content
    if ($script:fixturePostCount -ne 1) { throw 'Repeated create made a duplicate API POST.' }
    Invoke-TestAction $case 'delete' $hostName $recordName $content
    if ($script:fixtureDeleteCount -ne 1 -or $script:fixtureRecords.Count -ne 0) { throw 'Delete did not remove the owned record.' }
    Invoke-TestAction $case 'delete' $hostName $recordName $content
    if ($script:fixtureDeleteCount -ne 1 -or $script:fixtureRecords.Count -ne 0) { throw 'Repeated delete was not idempotent.' }
    if (@($script:fixtureCalls | Where-Object { $_.Uri -match '/zones/' }).Count -lt 6) { throw 'Expected fixed-zone API calls were not recorded.' }

    # A timed-out POST that committed is adopted by its pending journal marker on retry.
    Reset-FixtureApi
    $case = New-TestCase 'post-retry'
    $script:fixtureFault = 'PostAfterCommit'
    Assert-ActionFails $case 'create' $hostName $recordName $content 'commit response lost'
    if ($script:fixtureRecords.Count -ne 1 -or (Read-DnsJournal $case.Journal $case.Root).Entries[0].State -cne 'Pending') { throw 'Lost POST response did not leave a recoverable pending journal entry.' }
    Invoke-TestAction $case 'create' $hostName $recordName $content
    if ($script:fixturePostCount -ne 1 -or (Read-DnsJournal $case.Journal $case.Root).Entries[0].State -cne 'Active') { throw 'Retry did not safely adopt the uniquely marked record.' }

    # A failed POST with no remote side effect can be retried using the same marker.
    Reset-FixtureApi
    $case = New-TestCase 'precommit-retry'
    $script:fixtureFault = 'PostBeforeCommit'
    Assert-ActionFails $case 'create' $hostName $recordName $content 'precommit failure'
    Invoke-TestAction $case 'create' $hostName $recordName $content
    if ($script:fixturePostCount -ne 2 -or $script:fixtureRecords.Count -ne 1) { throw 'Retry after precommit failure did not create exactly one record.' }

    # Foreign content, unexpected duplicates, and wrong names are fail-closed and untouched.
    Reset-FixtureApi
    $case = New-TestCase 'foreign-record'
    $script:fixtureRecords.Add($script:fixtures.responses.foreignTxt)
    Assert-ActionFails $case 'create' $hostName $recordName $content 'foreign content'
    Assert-ActionFails $case 'delete' $hostName $recordName $content 'foreign cleanup'
    if ($script:fixtureRecords.Count -ne 1 -or $script:fixtureDeleteCount -ne 0 -or $script:fixturePostCount -ne 0) { throw 'Foreign TXT record was modified.' }

    Reset-FixtureApi
    $case = New-TestCase 'duplicate-records'
    $script:fixtureRecords.Add($script:fixtures.responses.foreignTxt)
    $script:fixtureRecords.Add(($script:fixtures.responses.foreignTxt | ConvertTo-Json | ConvertFrom-Json))
    Assert-ActionFails $case 'create' $hostName $recordName $content 'duplicate TXT records'
    if ($script:fixtureRecords.Count -ne 2 -or $script:fixturePostCount -ne 0) { throw 'Duplicate TXT records were not preserved.' }

    Reset-FixtureApi
    $case = New-TestCase 'wrong-name'
    $script:fixtureFault = 'WrongName'
    Assert-ActionFails $case 'create' $hostName $recordName $content 'wrong-name API result'
    if ($script:fixturePostCount -ne 0 -or $script:fixtureDeleteCount -ne 0) { throw 'Wrong-name response caused a mutation.' }

    # The selected zone ID is the only zone endpoint; a denied wrong ID never falls back to zone discovery.
    Reset-FixtureApi
    $case = New-TestCase 'wrong-zone'
    Set-TestConfig $case.Config ([string]$script:fixtures.wrongZoneId)
    Assert-ActionFails $case 'create' $hostName $recordName $content 'wrong zone authorization'
    if ($script:fixtureCalls.Count -ne 1 -or $script:fixtureCalls[0].Method -cne 'GET' -or $script:fixturePostCount -ne 0) { throw 'Wrong-zone response triggered more than the scoped read.' }

    Reset-FixtureApi
    $case = New-TestCase 'api-failure'
    $script:fixtureFault = 'ApiFailure'
    Assert-ActionFails $case 'create' $hostName $recordName $content 'API error sanitization'

    # A lost DELETE response is safely retried: absence clears only the matching journal entry.
    Reset-FixtureApi
    $case = New-TestCase 'delete-retry'
    Invoke-TestAction $case 'create' $hostName $recordName $content
    $script:fixtureFault = 'DeleteAfterCommit'
    Assert-ActionFails $case 'delete' $hostName $recordName $content 'delete already committed'
    Invoke-TestAction $case 'delete' $hostName $recordName $content
    if ((Read-DnsJournal $case.Journal $case.Root).Entries.Count -ne 0 -or $script:fixtureRecords.Count -ne 0) { throw 'Delete retry did not reconcile the owned journal.' }

    # Invalid identifier/name and an inherited credential ACL cause no API request.
    Reset-FixtureApi
    $case = New-TestCase 'input-and-acl'
    Assert-ActionFails $case 'create' 'not-approved.structura-most.ru' $recordName $content 'unknown identifier'
    Assert-ActionFails $case 'create' $hostName '_acme-challenge.other.structura-most.ru' $content 'wrong record name'
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $case.Credential '/inheritance:e' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare inherited-ACL negative fixture.' }
    Assert-ActionFails $case 'create' $hostName $recordName $content 'inherited token ACL'
    if ($script:fixtureCalls.Count -ne 0) { throw 'Invalid input or credential ACL reached Cloudflare transport.' }

    # Inject unapproved owner metadata into the real validation entry points; no privileged chown is used.
    $unapprovedDirectoryAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $unapprovedDirectoryAcl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19')))
    $script:ownerFixturePath = $case.Root
    $script:ownerFixtureAcl = $unapprovedDirectoryAcl
    $ownerRejected = $false
    try { Assert-SafeParentAcl -Directory $case.Root -SecurityRoot $case.Root } catch { $ownerRejected = $_.Exception.Message -match 'owner' }
    if (-not $ownerRejected) { throw 'An unapproved protected-directory owner was not rejected.' }
    $unapprovedFileAcl = New-Object System.Security.AccessControl.FileSecurity
    $unapprovedFileAcl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-19')))
    $script:ownerFixturePath = $case.Credential
    $script:ownerFixtureAcl = $unapprovedFileAcl
    $ownerRejected = $false
    try { Assert-PrivateFileAcl -Path $case.Credential } catch { $ownerRejected = $_.Exception.Message -match 'owner' }
    if (-not $ownerRejected) { throw 'An unapproved protected-file owner was not rejected.' }
    $script:ownerFixturePath = ''
    $script:ownerFixtureAcl = $null

    # The lock serializes processes sharing this journal and is never deleted.
    Reset-FixtureApi
    $case = New-TestCase 'lock'
    [System.IO.File]::WriteAllBytes($case.Lock, [byte[]]@())
    Set-PrivateAcl -Path $case.Lock
    $held = [System.IO.File]::Open($case.Lock, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try { Assert-ActionFails $case 'create' $hostName $recordName $content 'concurrent lock' 0 } finally { $held.Dispose() }
    if (-not (Test-Path -LiteralPath $case.Lock -PathType Leaf)) { throw 'The persistent journal lock file was removed.' }
    if ($script:fixtureCalls.Count -ne 0) { throw 'A locked invocation reached Cloudflare transport.' }

    # A junction supplied as the existing lock path must fail before any file open/API call.
    Reset-FixtureApi
    $case = New-TestCase 'reparse-lock'
    $junction = Join-Path $case.Root 'lock-junction'
    $junctionTarget = Join-Path $case.Root 'junction-target'
    [void](New-Item -ItemType Directory -Path $junctionTarget)
    [void](New-Item -ItemType Junction -Path $junction -Target $junctionTarget)
    $reparseRejected = $false
    try { Enter-DnsJournalLock -LockPath $junction -SecurityRoot $case.Root -TimeoutSeconds 0 } catch { $reparseRejected = $_.Exception.Message -match 'reparse' }
    if (-not $reparseRejected) { throw 'A reparse-point lock path was not rejected.' }
    if ($script:fixtureCalls.Count -ne 0) { throw 'A reparse-point lock reached Cloudflare transport.' }

    Write-Output 'PASS: fixed-zone DNS-01 lifecycle, retries, foreign-record protection, wrong zone/name, API failure sanitization, ACL owner, reparse and lock fixtures.'
} finally {
    if (Test-Path -LiteralPath $script:testRoot) { Remove-Item -LiteralPath $script:testRoot -Recurse -Force }
}
