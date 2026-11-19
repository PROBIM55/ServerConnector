#Requires -Version 7.4
# Cloud end-to-end check of automatic updates on a disposable hosted VM (K9):
#   1. install N; an ordinary user's application runs, and one in the runner's own session;
#   2. the service finds a deliberately failing N+2 (CiInjectedInstallFault), installs it, the transaction fails after
#      N was removed, and Windows Installer restores N (MajorUpgrade afterInstallInitialize); the service N resolves it;
#   3. the catalog then offers N+1; the service downloads it (first answer: 503 busy), installs it by itself, the new
#      service resolves the installation, the application is started again in the runner's session.
# The descriptions are signed by the separate job sign-connector-ci-release.yml for channel ci-<run id> only.
# N+1 and N+2 never leave this VM and are not uploaded. The descriptions travel as dispatch inputs and the
# signatures as a one-day artifact of that job, so in this public repository the pair is visible; it is harmless:
# a ci-<run id> channel is accepted only by a computer whose administrator wrote exactly that channel.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupN, [Parameter(Mandatory)][string]$VersionN,
    [Parameter(Mandatory)][string]$SetupNext, [Parameter(Mandatory)][string]$VersionNext,
    [Parameter(Mandatory)][string]$SetupFault, [Parameter(Mandatory)][string]$VersionFault,
    [Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Disposable hosted Windows required.' }
if ($env:GITHUB_RUN_ID -cnotmatch '^[1-9][0-9]*$' -or $env:GITHUB_SHA -cnotmatch '^[a-f0-9]{40}$') { throw 'Run identity missing.' }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$desktopRoot = Split-Path -Parent $PSScriptRoot
$channel = 'ci-' + $env:GITHUB_RUN_ID
$work = Join-Path $env:RUNNER_TEMP ('update-e2e-' + [Guid]::NewGuid().ToString('N'))   # never uploaded
$catalog = Join-Path $work 'catalog'
New-Item -ItemType Directory -Path (Join-Path $catalog "$channel/files") -Force | Out-Null
$appRoot = Join-Path $env:ProgramFiles 'Platform Connector'
$statusPath = Join-Path $env:ProgramData 'Structura Connector\Updates\update-status.json'
$upgradeCode = '{67B43FCD-4FE8-4D29-9F64-6F190FCD6E51}'
$msi = New-Object -ComObject WindowsInstaller.Installer
$evidence = [ordered]@{channel=$channel; versionN=$VersionN; versionNext=$VersionNext; versionFault=$VersionFault}

function Installed-Version {
    $codes = @($msi.RelatedProducts($upgradeCode))
    if ($codes.Count -ne 1) { return $null }
    return $msi.ProductInfo($codes[0], 'VersionString')
}
function Read-Status { try { return Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json } catch { return $null } }
function Wait-Until([scriptblock]$Condition, [int]$Minutes, [string]$What) {
    $until = [DateTime]::UtcNow.AddMinutes($Minutes)
    while ([DateTime]::UtcNow -lt $until) { if (& $Condition) { return }; Start-Sleep -Seconds 10 }
    throw "Timed out: $What"
}
function New-Description([string]$Version, [string]$Setup, [string]$Minimum) {
    $file = Get-Item -LiteralPath $Setup
    $json = "{`n  `"schemaVersion`": 2,`n  `"applicationId`": `"Structura.PlatformConnector`",`n  `"channel`": `"$channel`",`n" +
            "  `"version`": `"$Version`",`n  `"publishedAtUtc`": `"$([DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))`",`n" +
            "  `"paused`": false,`n  `"minimumVersion`": `"$Minimum`",`n" +
            "  `"installer`": { `"fileName`": `"PlatformConnector-$Version.exe`", `"size`": $($file.Length), `"sha256`": `"$((Get-FileHash -LiteralPath $Setup).Hash.ToLowerInvariant())`" },`n" +
            "  `"notes`": [`"Проверка автоматического обновления в облаке`"]`n}"
    return [Text.UTF8Encoding]::new($false).GetBytes($json)
}
function Request-Signatures([byte[]]$First, [byte[]]$Second) {
    $requestId = [Guid]::NewGuid().ToString('N')
    $inputs = @{request_id=$requestId; ci_run_id=$env:GITHUB_RUN_ID; source_sha=$env:GITHUB_SHA
        document1_base64=[Convert]::ToBase64String($First); document1_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($First)).ToLowerInvariant()
        document2_base64=[Convert]::ToBase64String($Second); document2_sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Second)).ToLowerInvariant()}
    $requestPath = Join-Path $work 'sign-request.json'
    [IO.File]::WriteAllText($requestPath, (@{ref=$env:GITHUB_REF_NAME; inputs=$inputs} | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    & gh api --method POST "repos/$($env:GITHUB_REPOSITORY)/actions/workflows/sign-connector-ci-release.yml/dispatches" --input $requestPath
    if ($LASTEXITCODE -ne 0) { throw 'Signing dispatch failed.' }
    Remove-Item -LiteralPath $requestPath
    $deadline = [DateTime]::UtcNow.AddMinutes(30); $run = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $runs = & gh run list --repo $env:GITHUB_REPOSITORY --workflow sign-connector-ci-release.yml --limit 30 --json databaseId,displayTitle,headSha,status,conclusion | ConvertFrom-Json
        $found = @($runs | Where-Object displayTitle -CEQ "Connector CI signature $requestId")
        if ($found.Count -gt 1) { throw 'Ambiguous signing request.' }
        if ($found.Count -eq 1) {
            $run = $found[0]
            if ($run.headSha -cne $env:GITHUB_SHA) { throw 'Signer source differs.' }
            if ($run.status -ceq 'completed') { break }
        }
        Write-Host 'Waiting for the production signature (approval of the environment may be required).'
        Start-Sleep -Seconds 15
    }
    if (-not $run -or $run.status -cne 'completed' -or $run.conclusion -cne 'success') { throw 'Signing did not succeed.' }
    $result = Join-Path $work 'signatures'
    & gh run download $run.databaseId --repo $env:GITHUB_REPOSITORY --name "connector-ci-signature-$requestId" --dir $result
    if ($LASTEXITCODE -ne 0) { throw 'Signature download failed.' }
    $public = [Security.Cryptography.ECDsa]::Create()
    try {
        $public.ImportFromPem([IO.File]::ReadAllText((Join-Path $desktopRoot 'Connector.Desktop/Assets/update-public-key.pem')))
        $signatures = @()
        foreach ($pair in @(@($First,'document1.sig'), @($Second,'document2.sig'))) {
            $signature = [IO.File]::ReadAllBytes((Join-Path $result $pair[1]))
            if ($signature.Length -ne 64 -or -not $public.VerifyData([byte[]]$pair[0], $signature, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Signature does not match the pinned key.' }
            $signatures += ,$signature
        }
        return ,$signatures
    } finally { $public.Dispose() }
}
function Publish-Release([byte[]]$Description, [byte[]]$Signature, [string]$Setup, [string]$Version) {
    Copy-Item -LiteralPath $Setup -Destination (Join-Path $catalog "$channel/files/PlatformConnector-$Version.exe") -Force
    [IO.File]::WriteAllBytes((Join-Path $catalog "$channel/connector-release.json.tmp"), $Description)
    [IO.File]::WriteAllBytes((Join-Path $catalog "$channel/connector-release.json.sig.tmp"), $Signature)
    Move-Item -LiteralPath (Join-Path $catalog "$channel/connector-release.json.tmp") -Destination (Join-Path $catalog "$channel/connector-release.json") -Force
    Move-Item -LiteralPath (Join-Path $catalog "$channel/connector-release.json.sig.tmp") -Destination (Join-Path $catalog "$channel/connector-release.json.sig") -Force
}
function Run-Setup([string]$Setup, [string]$Label) {
    $process = Start-Process -FilePath $Setup -ArgumentList @('/quiet','/norestart','/install','/log',('"' + (Join-Path $EvidenceDirectory "$Label.log") + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(600000)) { $process.Kill($true); throw "$Label timed out." }
    if ($process.ExitCode -notin @(0,3010)) { throw "$Label failed: $($process.ExitCode)" }
}

$server = $null; $sslBound = $false; $hostsAdded = $false; $user = $null; $userName = $null; $apps = @()
$hosts = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$hostsBackup = Join-Path $work 'hosts.original'
try {
    # --- descriptions and signatures (production job; this job never sees the key) ---
    $faultDescription = New-Description $VersionFault $SetupFault $VersionN
    $nextDescription = New-Description $VersionNext $SetupNext $VersionN
    $signatures = Request-Signatures $faultDescription $nextDescription
    $evidence.signedBySeparateProductionJob = $true

    # --- disposable TLS for bimplatforma.ru, trusted only on this VM ---
    $root = New-SelfSignedCertificate -Type Custom -Subject "CN=K9 disposable CI root $($env:GITHUB_RUN_ID)" -KeyUsage CertSign,CRLSign,DigitalSignature `
        -KeyExportPolicy NonExportable -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddHours(4) -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')
    $tls = New-SelfSignedCertificate -Subject 'CN=bimplatforma.ru' -DnsName 'bimplatforma.ru' -Signer $root -KeyExportPolicy NonExportable `
        -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddHours(4) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
    $rootPublic = Join-Path $work 'root.cer'
    Export-Certificate -Cert $root -FilePath $rootPublic | Out-Null
    Import-Certificate -FilePath $rootPublic -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Remove-Item -LiteralPath "Cert:\LocalMachine\My\$($root.Thumbprint)" -DeleteKey   # nothing else can ever be issued
    & netsh http add sslcert ipport=0.0.0.0:443 certhash=$($tls.Thumbprint) appid='{5b1d0f7e-6c55-4e0a-9d0e-4c1a3f0b7e21}' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'TLS binding failed.' }
    $sslBound = $true
    Copy-Item -LiteralPath $hosts -Destination $hostsBackup
    Add-Content -LiteralPath $hosts -Value "`r`n127.0.0.1 bimplatforma.ru"
    $hostsAdded = $true
    Clear-DnsClientCache
    $server = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile','-NonInteractive','-File',
        (Join-Path $PSScriptRoot 'ci_update_catalog_server.ps1'),'-Root',$catalog,'-BusyOnce') -WindowStyle Hidden -PassThru
    Publish-Release $faultDescription $signatures[0] $SetupFault $VersionFault
    Wait-Until { try { (Invoke-WebRequest -Uri "https://bimplatforma.ru/api/platform/connectors/unified/$channel/connector-release.json" -UseBasicParsing).StatusCode -eq 200 } catch { $false } } 2 'local catalog'

    # --- N, with the application running for an ordinary user and in the runner's session ---
    Run-Setup $SetupN 'install-n'
    if ((Installed-Version) -cne $VersionN) { throw 'N was not installed.' }
    Set-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Structura\PlatformConnector' -Name UpdateChannel -Value $channel
    $userName = 'k9u' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $plain = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)) + '!aA1'
    $password = ConvertTo-SecureString $plain -AsPlainText -Force; $plain = $null
    $user = New-LocalUser -Name $userName -Password $password -PasswordNeverExpires
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $user
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $appRoot 'Connector.Desktop.exe'))
    $start.UseShellExecute = $false; $start.UserName = $userName; $start.Domain = $env:COMPUTERNAME; $start.Password = $password
    $start.LoadUserProfile = $true; $start.WorkingDirectory = $appRoot
    $apps += [Diagnostics.Process]::Start($start)
    $apps += Start-Process -FilePath (Join-Path $appRoot 'Connector.Desktop.exe') -WorkingDirectory $appRoot -PassThru
    $runnerSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $evidence.runnerSession = $runnerSession
    Restart-Service PlatformConnectorUpdater   # first check after 2–10 minutes

    # --- failing N+2: installed by the service, rolled back to N ---
    Wait-Until { $s = Read-Status; $s -and $s.lastErrorCode -ceq 'installer-failed' -and $s.availableVersion -ceq $VersionFault } 30 'failed N+2 resolved by the service'
    Wait-Until { (Get-Service PlatformConnectorUpdater).Status -eq 'Running' } 3 'service N running after rollback'
    if ((Installed-Version) -cne $VersionN) { throw 'Rollback did not restore N.' }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $appRoot 'Updater\PlatformConnectorUpdater.exe')).FileVersion -cne "$VersionN.0" -or
        [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $appRoot 'Connector.Desktop.exe')).ProductVersion -notlike "$VersionN*") { throw 'Files of N were not restored.' }
    $evidence.rollbackToN = $true

    # --- N+1: offered next, installed by the service without any user action ---
    Publish-Release $nextDescription $signatures[1] $SetupNext $VersionNext
    Wait-Until { (Installed-Version) -ceq $VersionNext } 40 'N+1 installed by the service'
    Wait-Until { $s = Read-Status; $s -and $s.installedVersion -ceq $VersionNext -and $s.lastUpdate -and $s.lastUpdate.version -ceq $VersionNext -and $s.phase -ceq 'idle' } 10 'N+1 resolved by the new service'
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='PlatformConnectorUpdater'"
    if ($service.State -ne 'Running' -or $service.StartName -ine 'LocalSystem') { throw 'Service N+1 is not running as LocalSystem.' }
    $evidence.updatedToNext = $true
    $evidence.ordinaryUserApplicationClosed = $apps[0].HasExited
    if ($runnerSession -ne 0) {
        Wait-Until { @(Get-CimInstance Win32_Process -Filter "Name='Connector.Desktop.exe'" | Where-Object {
            $_.SessionId -eq $runnerSession -and $_.ExecutablePath -ieq (Join-Path $appRoot 'Connector.Desktop.exe') }).Count -ge 1 } 5 'application started again in its session'
        $evidence.applicationRestartedInSession = $true
    } else { $evidence.applicationRestartedInSession = 'not-verifiable-runner-in-session-0' }
    $requests = @(Get-Content -LiteralPath (Join-Path $catalog 'requests.log'))
    $evidence.catalogBusyAnswered = @($requests | Where-Object { $_ -match '/files/ -> 503$' }).Count -ge 1
    $evidence.resumedOrPartial = @($requests | Where-Object { $_ -match '/files/ -> 206$' }).Count
    $evidence.success = $true
} catch {
    $evidence.success = $false
    $evidence.failure = $_.Exception.Message
    try { Copy-Item -LiteralPath $statusPath -Destination (Join-Path $EvidenceDirectory 'update-status.json') -ErrorAction SilentlyContinue } catch { }
    try { Copy-Item -LiteralPath (Join-Path $appRoot 'Updater\state\logs\updater.log') -Destination (Join-Path $EvidenceDirectory 'updater.log') -ErrorAction SilentlyContinue } catch { }
    throw
} finally {
    foreach ($app in $apps) { try { if ($app -and !$app.HasExited) { $app.Kill($true) } } catch { } }
    Get-Process Connector.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($server -and !$server.HasExited) { $server.Kill($true) }
    if ($sslBound) { & netsh http delete sslcert ipport=0.0.0.0:443 | Out-Null }
    if ($hostsAdded) { Copy-Item -LiteralPath $hostsBackup -Destination $hosts -Force }
    Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -like 'CN=K9 disposable CI root*' | Remove-Item -ErrorAction SilentlyContinue
    if ($user) { Remove-LocalUser -Name $userName -ErrorAction SilentlyContinue }
    # Descriptions, signatures and installers stay on this VM and are deleted with the working folder.
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    $evidence | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'update-e2e.json') -Encoding utf8
}
