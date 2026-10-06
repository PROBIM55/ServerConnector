$ErrorActionPreference = 'Stop'
$hook = Join-Path $PSScriptRoot 'renew-platform-connector-certificate.ps1'
. $hook -SelfTest

# The real parser accepts only one exact -File launch, never a launcher path
# merely mentioned inside another PowerShell command.
$script:taskFixtureArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\Platform\runtime\runtime_launch.ps1"'
function Get-ScheduledTask { param([string]$TaskName,[string]$ErrorAction); [pscustomobject]@{ Actions=@([pscustomobject]@{Execute='powershell.exe';Arguments=$script:taskFixtureArguments}); Principal=[pscustomobject]@{UserId='S-1-5-18'} } }
$parserSid = Assert-PlatformTaskAction 'C:\Platform'
if ($parserSid -cne 'S-1-5-18') { throw 'Exact expected Platform task action was not accepted.' }
$invalidTaskArguments = @(
    '-Command "Write-Output C:\Platform\runtime\runtime_launch.ps1; Start-Sleep -Seconds 1"',
    '-File C:\Platform\other.ps1 -Command C:\Platform\runtime\runtime_launch.ps1',
    '-File C:\Platform\runtime\runtime_launch.ps1 -Extra'
)
foreach ($badArguments in $invalidTaskArguments) {
    $script:taskFixtureArguments = $badArguments
    try { [void](Assert-PlatformTaskAction 'C:\Platform'); throw 'Unsafe task action was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'must invoke only the exact runtime launcher') { throw } }
}

# Exercise the restart boundary with fixture process/task adapters. No Windows
# task, process, listener, certificate store, or external endpoint is touched.
$script:fixtureProcesses = @([pscustomobject]@{
    ProcessId = 4123
    CreationDate = '20261006120000.000000+000'
    Name = 'dotnet.exe'
    ExecutablePath = 'C:\Platform\dotnet\dotnet.exe'
    CommandLine = '"C:\Platform\dotnet\dotnet.exe" "C:\Platform\runtime\current\Platform.Server.dll" --urls http://127.0.0.1:8090'
})
$script:fixtureStops = [System.Collections.Generic.List[int]]::new()
$script:fixtureTaskRuns = [System.Collections.Generic.List[string]]::new()
$script:fixtureTaskStops = [System.Collections.Generic.List[string]]::new()
$script:fixtureCurrent = $script:fixtureProcesses[0]
function Assert-PlatformTaskAction([string]$RuntimeRoot) { if ($RuntimeRoot -ne 'C:\Platform') { throw 'wrong task root' } }
function Get-ExactPlatformServerProcesses([string]$RuntimeRoot) { @($script:fixtureProcesses) }
function Get-CimInstance { param([string]$ClassName,[string]$Filter,[string]$ErrorAction); $script:fixtureCurrent }
function Stop-Process { param([int]$Id,[switch]$Force,[string]$ErrorAction); $script:fixtureStops.Add($Id); $script:fixtureProcesses = @(); $script:fixtureCurrent = $null }
function Get-ScheduledTask { param([string]$TaskName,[string]$ErrorAction); [pscustomobject]@{ State='Ready' } }
function Stop-ScheduledTask { param([string]$TaskName,[string]$ErrorAction); $script:fixtureTaskStops.Add($TaskName) }
function Start-ScheduledTask { param([string]$TaskName,[string]$ErrorAction); $script:fixtureTaskRuns.Add($TaskName); $script:fixtureCurrent = [pscustomobject]@{ ProcessId=4124; CreationDate='20261006120003.000000+000'; Name='dotnet.exe'; ExecutablePath='C:\Platform\dotnet\dotnet.exe'; CommandLine='"C:\Platform\dotnet\dotnet.exe" "C:\Platform\runtime\current\Platform.Server.dll" --urls http://127.0.0.1:8090' }; $script:fixtureProcesses = @($script:fixtureCurrent) }
function Start-Sleep { param([int]$Milliseconds,[int]$Seconds); }

$started = @(Invoke-PlatformRestart -RuntimeRoot 'C:\Platform' -Timeout 2)
if ($script:fixtureStops.Count -ne 1 -or $script:fixtureStops[0] -ne 4123) { throw 'Restart did not stop exactly the snapshotted fixture process.' }
if ($script:fixtureTaskStops.Count -ne 1 -or $script:fixtureTaskStops[0] -cne 'PlatformServerApp') { throw 'Restart did not stop only PlatformServerApp before restarting.' }
if ($script:fixtureTaskRuns.Count -ne 1 -or $script:fixtureTaskRuns[0] -cne 'PlatformServerApp') { throw 'Restart did not start only PlatformServerApp.' }
if ($started.Count -ne 1 -or $started[0].ProcessId -ne 4124) { throw 'Restart did not return the new exact fixture process identity.' }
$probeResult = Test-ConnectorTlsHealth -HostName 'connector-access.structura-most.ru' -Port 24443 -Timeout 1 -ExpectedThumbprint 'fixture-thumbprint' -Probe {
    param($hostName,$port,$thumbprint)
    if ($hostName -cne 'connector-access.structura-most.ru' -or $port -ne 24443 -or $thumbprint -cne 'fixture-thumbprint') { throw 'Mocked HTTPS health probe received unexpected target metadata.' }
    return $true
}
if (-not $probeResult) { throw 'Mocked HTTPS health probe did not pass.' }

$slowDeadline = [DateTimeOffset]::UtcNow.AddMilliseconds(90)
$firstSlowPart = [Threading.Tasks.Task]::Delay(50)
[void](Wait-ConnectorAsyncResult $firstSlowPart $slowDeadline { param($op) $op.GetAwaiter().GetResult() })
$secondSlowPart = [Threading.Tasks.Task]::Delay(80)
$slowWatch = [Diagnostics.Stopwatch]::StartNew()
try {
    [void](Wait-ConnectorAsyncResult $secondSlowPart $slowDeadline { param($op) $op.GetAwaiter().GetResult() })
    throw 'Shared deadline did not reject a slow-drip fixture.'
} catch {
    if ($_.Exception.Message -notmatch 'overall deadline') { throw }
}
if ($slowWatch.ElapsedMilliseconds -gt 300) { throw 'Slow-drip fixture exceeded the expected bounded timeout.' }

# File and certificate validation fixture: private key, serverAuth EKU and both SANs.
$temp = Join-Path ([IO.Path]::GetTempPath()) ('connector-tls-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$testSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$script:testSid = $testSid
$secretName = 'PLATFORM_CONNECTOR_ACCESS_SERVER_CERT_PASSWORD'
$secretDir = Join-Path $temp 'runtime\connector-access'
New-Item -ItemType Directory -Path $secretDir -Force | Out-Null
$secretFile = Join-Path $secretDir 'service-secrets.json'
[IO.File]::WriteAllText($secretFile,'{}',(New-Object Text.UTF8Encoding($false)))
function Set-FixtureProtectedDirectory([string]$Path) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($sidText in @('S-1-5-18','S-1-5-32-544',$testSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sidText),'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
$script:aclFixtureMockPath = ''
$script:aclFixtureMock = $null
function Get-Acl {
    param([string]$LiteralPath)
    if ($script:aclFixtureMockPath -and [string]::Equals($LiteralPath,$script:aclFixtureMockPath,[StringComparison]::OrdinalIgnoreCase)) { return $script:aclFixtureMock }
    Microsoft.PowerShell.Security\Get-Acl -LiteralPath $LiteralPath
}
$tempAcl = [Security.AccessControl.DirectorySecurity]::new()
$tempAcl.SetAccessRuleProtection($true, $false)
foreach ($sidText in @('S-1-5-18','S-1-5-32-544',$testSid)) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sidText), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    [void]$tempAcl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $temp -AclObject $tempAcl
Set-FixtureProtectedDirectory $secretDir
Set-PrivateFileAcl $secretFile -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid)
try {
    if (-not (Test-Path -LiteralPath $secretDir -PathType Container)) { throw 'Secret fixture directory was not created.' }
    $fixtureSecret = 'fixture-json-password-3f5a'
    function Write-SecretFixture([string]$Json) {
        [IO.File]::WriteAllText($secretFile,$Json,(New-Object Text.UTF8Encoding($false)))
        Set-PrivateFileAcl $secretFile -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid)
    }
    Write-SecretFixture ('{"' + $secretName + '":"' + $fixtureSecret + '","OTHER_SERVICE_PASSWORD":"not-selected"}')
    $jsonPassword = Get-ConnectorAccessCertificatePassword -SecretFilePath $secretFile -SecretName $secretName -Root $temp -AllowedSid @($testSid)
    if ($jsonPassword -cne $fixtureSecret) { throw 'Protected JSON secret lookup returned the wrong fixture value.' }
    $jsonPassword = $null

    # Reproduce the foreign explicit trustee left after CREATOR OWNER-style
    # inheritance materializes on a file; /inheritance:r alone leaves it.
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $secretFile '/grant' '*S-1-5-32-545:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare foreign creator-derived ACL fixture.' }
    $creatorOwnerSid = 'S-1-5-32-545'
    if (-not (@((Get-Acl -LiteralPath $secretFile).Access | Where-Object {
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $creatorOwnerSid
    }).Count)) { throw 'Foreign creator-derived fixture ACE was not present before ACL reset.' }
    Set-PrivateFileAcl $secretFile -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid)
    $repairedAcl = Get-Acl -LiteralPath $secretFile
    if ($repairedAcl.Access.Count -ne 3 -or @($repairedAcl.Access | Where-Object {
        $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $creatorOwnerSid
    }).Count) { throw 'Exact ACL reset retained the foreign creator-derived fixture ACE.' }

    foreach ($badCase in @(
        [pscustomobject]@{ Json='{}'; Code='SERVICE_SECRET_REQUIRED_VALUE_MISSING' },
        [pscustomobject]@{ Json=('{"' + $secretName + '":42}'); Code='SERVICE_SECRET_REQUIRED_VALUE_INVALID' },
        [pscustomobject]@{ Json='{"broken-json":"fixture-json-password-3f5a"'; Code='SERVICE_SECRET_JSON_INVALID' },
        [pscustomobject]@{ Json=(' ' * 70000); Code='SERVICE_SECRET_JSON_INVALID' }
    )) {
        Write-SecretFixture $badCase.Json
        try { [void](Get-ConnectorAccessCertificatePassword -SecretFilePath $secretFile -SecretName $secretName -Root $temp -AllowedSid @($testSid)); throw 'Invalid service secret fixture was accepted.' }
        catch {
            if ($_.Exception.Message -notmatch [regex]::Escape($badCase.Code) -or $_.Exception.Message.Contains('fixture-json-password-3f5a')) { throw 'Invalid service secret did not fail closed with a generic non-secret error.' }
        }
    }
    try { [void](Get-ConnectorAccessCertificatePassword -SecretFilePath $secretFile -SecretName 'UNSUPPORTED_PASSWORD_NAME' -Root $temp -AllowedSid @($testSid)); throw 'Unsupported secret name was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'Unsupported certificate secret name') { throw } }
    try { [void](Get-ConnectorAccessCertificatePassword -SecretFilePath (Join-Path $temp 'other-secret.json') -SecretName $secretName -Root $temp -AllowedSid @($testSid)); throw 'Noncanonical secret path was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'SERVICE_SECRET_PATH_OR_ACL_INVALID') { throw } }
    $traversalSecretPath = Join-Path $temp 'runtime\connector-access\..\connector-access\service-secrets.json'
    try { [void](Get-ConnectorAccessCertificatePassword -SecretFilePath $traversalSecretPath -SecretName $secretName -Root $temp -AllowedSid @($testSid)); throw 'Noncanonical traversal path was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'SERVICE_SECRET_PATH_OR_ACL_INVALID') { throw } }

    $reparseRoot = Join-Path $temp 'reparse-secret-root'
    New-Item -ItemType Directory -Path $reparseRoot | Out-Null
    $runtimeJunction = Join-Path $reparseRoot 'runtime'
    [void](New-Item -ItemType Junction -Path $runtimeJunction -Target (Join-Path $temp 'runtime'))
    try { [void](Get-ConnectorAccessCertificatePassword -SecretFilePath (Join-Path $reparseRoot 'runtime\connector-access\service-secrets.json') -SecretName $secretName -Root $reparseRoot -AllowedSid @($testSid)); throw 'Reparse parent in the service secret path was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'SERVICE_SECRET_PATH_OR_ACL_INVALID') { throw } }

    Write-SecretFixture ('{"' + $secretName + '":"' + $fixtureSecret + '"}')
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $secretFile '/grant' '*S-1-1-0:RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare untrusted-read fixture.' }
    try { [void](Assert-PrivateFileAcl $secretFile -AdditionalAllowedSid @($testSid)); throw 'Untrusted read access to service secrets was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'unapproved identity') { throw } }
    Set-PrivateFileAcl $secretFile -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid)
    $unapprovedOwnerAcl = [Security.AccessControl.FileSecurity]::new()
    $unapprovedOwnerAcl.SetSecurityDescriptorSddlForm('O:S-1-5-32-545',[Security.AccessControl.AccessControlSections]::Owner)
    $unapprovedOwnerAcl.SetSecurityDescriptorSddlForm('D:(A;;FA;;;SY)(A;;FA;;;BA)',[Security.AccessControl.AccessControlSections]::Access)
    $script:aclFixtureMockPath = $secretFile
    $script:aclFixtureMock = $unapprovedOwnerAcl
    try {
        $unapprovedOwnerRejected = $false
        try { [void](Assert-PrivateFileAcl $secretFile) }
        catch {
            if ($_.Exception.Message -cne 'Certificate file owner is not an approved runtime identity.') { throw }
            $unapprovedOwnerRejected = $true
        }
        if (-not $unapprovedOwnerRejected) { throw 'Unapproved owner for service secrets was accepted.' }
    } finally { $script:aclFixtureMockPath = ''; $script:aclFixtureMock = $null }

    $readOnlyDirectory = Join-Path $temp 'readonly-parent-fixture'
    New-Item -ItemType Directory -Path $readOnlyDirectory | Out-Null
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $readOnlyDirectory '/grant' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare parent read-only fixture.' }
    Assert-NoUnapprovedWriteAcl $readOnlyDirectory @($testSid,'S-1-5-18','S-1-5-32-544')
    & (Join-Path $env:SystemRoot 'System32\icacls.exe') $readOnlyDirectory '/grant' '*S-1-5-32-545:(OI)(CI)M' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare parent writer fixture.' }
    try { Assert-NoUnapprovedWriteAcl $readOnlyDirectory @($testSid,'S-1-5-18','S-1-5-32-544'); throw 'Unapproved write ACL was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'Unapproved write access') { throw } }

    $nullFileAcl = [Security.AccessControl.FileSecurity]::new()
    $nullFileAcl.SetSecurityDescriptorSddlForm('D:NO_ACCESS_CONTROL',[Security.AccessControl.AccessControlSections]::Access)
    $script:aclFixtureMockPath = $secretFile; $script:aclFixtureMock = $nullFileAcl
    try { Assert-PrivateFileAcl $secretFile -AdditionalAllowedSid @($testSid); throw 'Null DACL on a secret file was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'null DACL') { throw } }
    $nullDirectoryAcl = [Security.AccessControl.DirectorySecurity]::new()
    $nullDirectoryAcl.SetSecurityDescriptorSddlForm('D:NO_ACCESS_CONTROL',[Security.AccessControl.AccessControlSections]::Access)
    $script:aclFixtureMockPath = $readOnlyDirectory; $script:aclFixtureMock = $nullDirectoryAcl
    try { Assert-NoUnapprovedWriteAcl $readOnlyDirectory @($testSid,'S-1-5-18','S-1-5-32-544'); throw 'Null DACL on a protected parent was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'null DACL') { throw } }
    $script:aclFixtureMockPath = ''; $script:aclFixtureMock = $null

    $unsafeDirectory = Join-Path $temp 'untrusted-write-fixture'
    New-Item -ItemType Directory -Path $unsafeDirectory | Out-Null
    $unsafeAcl = Get-Acl -LiteralPath $unsafeDirectory
    $everyone = [Security.Principal.SecurityIdentifier]::new('S-1-1-0')
    [void]$unsafeAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($everyone,'Modify','ContainerInherit,ObjectInherit','None','Allow'))
    Set-Acl -LiteralPath $unsafeDirectory -AclObject $unsafeAcl
    try { [void](Assert-ProtectedDirectoryPath $unsafeDirectory @($testSid,'S-1-5-18','S-1-5-32-544') $temp); throw 'Unapproved parent write ACL was accepted.' }
    catch { if ($_.Exception.Message -notmatch 'Unapproved write access') { throw } }
    Remove-Item -LiteralPath $unsafeDirectory -Recurse -Force

    $permissionFixture = Join-Path $temp 'stage-permissions.pfx'
    [IO.File]::WriteAllBytes($permissionFixture,[byte[]]@())
    Set-StageFileAcl $permissionFixture @('S-1-5-20') $testSid
    $permissionAces = (Get-Acl -LiteralPath $permissionFixture).Access
    $networkServiceAce = @($permissionAces | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-20' -and $_.AccessControlType -eq 'Allow' })
    if ($networkServiceAce.Count -ne 1 -or ($networkServiceAce[0].FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl) { throw 'Non-administrative runtime principal received FullControl on the staged PFX.' }

    $secure = ConvertTo-SecureString 'fixture-only-password' -AsPlainText -Force
    $certs = @()
    $cert = New-SelfSignedCertificate -Subject 'CN=connector-access.structura-most.ru' -DnsName 'connector-access.structura-most.ru','connector-gateway.structura-most.ru' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage DigitalSignature,KeyEncipherment -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    $newCert = New-SelfSignedCertificate -Subject 'CN=connector-access.structura-most.ru' -DnsName 'connector-access.structura-most.ru','connector-gateway.structura-most.ru' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 -KeyUsage DigitalSignature,KeyEncipherment -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    $certs += $cert; $certs += $newCert
    try {
        $pfx = Join-Path $temp 'fixture.pfx'
        $newPfx = Join-Path $temp 'fixture-new.pfx'
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
        Export-PfxCertificate -Cert $newCert -FilePath $newPfx -Password $secure | Out-Null
        Set-PrivateFileAcl $pfx -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid)
        Set-PrivateFileAcl $newPfx -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid)
        $loaded = Get-ValidatedServerCertificate $pfx 'fixture-only-password' @($testSid)
        try { if ($loaded.Thumbprint -cne $cert.Thumbprint) { throw 'Certificate validator returned a different fixture certificate.' } } finally { $loaded.Dispose() }
        $script:realSetPrivateFileAcl = (Get-Command Set-PrivateFileAcl -CommandType Function).ScriptBlock
        $script:realSetStageFileAcl = (Get-Command Set-StageFileAcl -CommandType Function).ScriptBlock
        $script:realAssertActiveAcl = (Get-Command Assert-ActivatedCertificateAcl -CommandType Function).ScriptBlock
        $script:realHealth = (Get-Command Test-ConnectorTlsHealth -CommandType Function).ScriptBlock
        $script:txnActive = $false
        $script:txnRoot = ''
        $script:txnRuntimeSid = 'S-1-5-32-544'
        $script:txnProcesses = @()
        $script:txnNextPid = 6000
        $script:txnFailAclPath = ''
        $script:txnFailAclOnce = $false
        $script:txnFailThumb = ''
        $script:txnStartFailures = 0
        $script:txnSawEmptyStage = $false
        $script:txnStops = [System.Collections.Generic.List[int]]::new()
        $script:txnRuns = [System.Collections.Generic.List[string]]::new()
        $script:txnLog = [System.Collections.Generic.List[string]]::new()
        function Assert-PlatformTaskAction([string]$RuntimeRoot) { return $script:txnRuntimeSid }
        function Get-ExactPlatformServerProcesses([string]$RuntimeRoot) { @($script:txnProcesses) }
        function Get-CimInstance { param([string]$ClassName,[string]$Filter,[string]$ErrorAction); if ($Filter -match 'ProcessId=(\d+)') { $pidValue=[int]$Matches[1]; return $script:txnProcesses | Where-Object ProcessId -eq $pidValue | Select-Object -First 1 }; return $script:txnProcesses }
        function Stop-Process { param([int]$Id,[switch]$Force,[string]$ErrorAction); $script:txnStops.Add($Id); $script:txnProcesses=@($script:txnProcesses|Where-Object ProcessId -ne $Id) }
        function Get-ScheduledTask { param([string]$TaskName,[string]$ErrorAction); [pscustomobject]@{State='Ready'} }
        function Stop-ScheduledTask { param([string]$TaskName,[string]$ErrorAction); $script:txnRuns.Add("stop:$TaskName") }
        function Start-ScheduledTask { param([string]$TaskName,[string]$ErrorAction); if ($script:txnStartFailures -gt 0) { $script:txnStartFailures--; throw 'injected task start failure' }; $script:txnRuns.Add("start:$TaskName"); $script:txnNextPid++; $script:txnProcesses=@([pscustomobject]@{ProcessId=$script:txnNextPid;CreationDate=[DateTime]::UtcNow.ToString('yyyyMMddHHmmss.ffffff+000');Name='dotnet.exe';ExecutablePath='C:\Platform\dotnet\dotnet.exe';CommandLine='"C:\Platform\dotnet\dotnet.exe" "C:\Platform\runtime\current\Platform.Server.dll" --urls http://127.0.0.1:8090'}) }
        function Set-PrivateFileAcl { param([string]$Path,[string[]]$AdditionalAllowedSid=@(),[string[]]$FullControlSid=@()); $script:txnLog.Add("SetFileAcl:$([IO.Path]::GetFileName($Path))"); if ($script:txnFailAclOnce -and $Path -ceq $script:txnFailAclPath) { $script:txnFailAclOnce=$false; throw 'injected active ACL failure' }; & $script:realSetPrivateFileAcl -Path $Path -AdditionalAllowedSid $AdditionalAllowedSid -FullControlSid $FullControlSid }
        function Set-StageFileAcl { param([string]$Path,[string[]]$RuntimeSid,[string]$WriterSid); if ((Get-Item -LiteralPath $Path).Length -ne 0) { throw 'stage ACL was applied after certificate bytes were written' }; $script:txnSawEmptyStage=$true; $script:txnLog.Add('SetStageAclEmpty'); & $script:realSetStageFileAcl -Path $Path -RuntimeSid $RuntimeSid -WriterSid $WriterSid }
        function Assert-ActivatedCertificateAcl { param([string]$Path,[string[]]$RuntimeSid,[string]$WriterSid); $script:txnLog.Add("AssertActiveAcl:$([IO.Path]::GetFileName($Path))"); if ($script:txnFailAclOnce -and $Path -ceq $script:txnFailAclPath) { $script:txnFailAclOnce=$false; throw 'injected post-replace active ACL validation failure' }; & $script:realAssertActiveAcl -Path $Path -RuntimeSid $RuntimeSid -WriterSid $WriterSid }
        function Test-ConnectorTlsHealth { param([string]$HostName,[int]$Port,[int]$Timeout,[string]$ExpectedThumbprint='',[scriptblock]$Probe=$null); if ($Port -ne 24443 -or $HostName -cne 'connector-access.structura-most.ru') { throw 'transaction used an unexpected health target' }; if ($script:txnFailThumb -and $ExpectedThumbprint -ceq $script:txnFailThumb) { throw 'injected health failure' }; return $true }

        function Invoke-TlsTransactionFixture([string]$Name,[string]$Failure,[string]$ExpectedResult) {
            $caseRoot=Join-Path $temp $Name; $certDir=Join-Path $caseRoot 'runtime\connector-access'; $deployDir=Join-Path $caseRoot 'deploy'
            New-Item -ItemType Directory -Path $certDir,$deployDir -Force | Out-Null
            $active=Join-Path $certDir 'server.pfx'; $source=Join-Path $certDir 'renewed-server.pfx'
            [IO.File]::Copy($pfx,$active); [IO.File]::Copy($newPfx,$source)
            Set-PrivateFileAcl $active -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid); Set-PrivateFileAcl $source -AdditionalAllowedSid @($testSid) -FullControlSid @($testSid)
            $script:txnRoot=$caseRoot; $script:txnActive=$true; $script:txnProcesses=@([pscustomobject]@{ProcessId=5500;CreationDate='20261006120000.000000+000';Name='dotnet.exe';ExecutablePath='C:\Platform\dotnet\dotnet.exe';CommandLine='"C:\Platform\dotnet\dotnet.exe" "C:\Platform\runtime\current\Platform.Server.dll" --urls http://127.0.0.1:8090'})
            $script:txnNextPid=6000; $script:txnFailAclPath=''; $script:txnFailAclOnce=$false; $script:txnFailThumb=''; $script:txnStartFailures=0; $script:txnRuns.Clear(); $script:txnStops.Clear(); $script:txnSawEmptyStage=$false
            $script:txnLog.Clear()
            if ($Failure -eq 'acl') { $script:txnFailAclPath=$active; $script:txnFailAclOnce=$true }
            if ($Failure -eq 'health') { $script:txnFailThumb=$newCert.Thumbprint }
            if ($Failure -eq 'restart') { $script:txnStartFailures=1 }
            [Environment]::SetEnvironmentVariable('CONNECTOR_TLS_FIXTURE_PASSWORD','fixture-only-password','Process')
            if ($Failure) {
                try { Invoke-ConnectorAccessTlsRenewal -Incoming $source -Active $active -SecretName 'CONNECTOR_TLS_FIXTURE_PASSWORD' -Root $caseRoot -Port 24443 -Timeout 2; throw 'Expected injected transaction failure.' }
                catch { if ($_.Exception.Message -notmatch 'Renewal failed; previous certificate and Platform runtime rollback verified') { throw } }
            } else {
                $outcome=Invoke-ConnectorAccessTlsRenewal -Incoming $source -Active $active -SecretName 'CONNECTOR_TLS_FIXTURE_PASSWORD' -Root $caseRoot -Port 24443 -Timeout 2
                if ($outcome -cne $ExpectedResult) { throw "Unexpected transaction outcome: $outcome" }
            }
            $activeCert=Get-ValidatedServerCertificate $active 'fixture-only-password' @($testSid)
            try {
                $expectedThumb=if ($Failure) {$cert.Thumbprint} else {$newCert.Thumbprint}
                if ($activeCert.Thumbprint -cne $expectedThumb) { throw "$Name did not preserve the expected active certificate." }
            } finally { $activeCert.Dispose() }
            if (Test-Path -LiteralPath "$active.previous") { throw "$Name left an unexpected previous-certificate backup." }
            if (-not $script:txnSawEmptyStage) { throw "$Name did not create/protect an empty stage before writing." }
            if ($Failure -and $script:txnProcesses.Count -ne 1) { throw "$Name rollback did not restore one Platform process." }
            if ($Failure -and $script:txnFailThumb -and $script:txnStops.Count -lt 2) { throw 'Health failure did not stop the new exact process and restart the old runtime.' }
            $script:txnActive=$false
            Remove-Item -LiteralPath $caseRoot -Recurse -Force
        }

        Invoke-TlsTransactionFixture 'success' '' 'CERTIFICATE_RENEWAL_VERIFIED'
        Invoke-TlsTransactionFixture 'acl-failure' 'acl' ''
        Invoke-TlsTransactionFixture 'restart-failure' 'restart' ''
        Invoke-TlsTransactionFixture 'health-failure' 'health' ''
    } finally { foreach ($fixtureCert in $certs) { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $fixtureCert.Thumbprint) -Force -ErrorAction SilentlyContinue; $fixtureCert.Dispose() } }
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Output 'TLS_HOOK_FIXTURE_TESTS_PASSED'
