$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:RUNNER_OS -cne 'Windows' -or $env:PRIVATE_SOURCE_SHA -cnotmatch '^[a-f0-9]{40}$') {throw 'Exact source and disposable hosted Windows are required'}
if([string]::IsNullOrWhiteSpace($env:PRIVATE_PLATFORM_READ_KEY)){throw 'Temporary read-only source access is unavailable'}
$root=Join-Path $env:RUNNER_TEMP 'platform-cloud-private'
New-Item -ItemType Directory -Path $root|Out-Null
$key=Join-Path $root 'source-read-key.pem';$hosts=Join-Path $root 'known_hosts'
$source=Join-Path $root 'source';$log=Join-Path $root 'private-build.log'
[IO.File]::WriteAllText($log,'',[Text.UTF8Encoding]::new($false))
$exitCode=1
$builder=$null
. (Join-Path $PSScriptRoot 'cloud-build-supervision.ps1')
$coordinatorLog=Join-Path $root 'coordinator.log'
$terminationLog=Join-Path $root 'termination.log'
$memoryStop=Join-Path $root 'memory-reserve-stop.txt'
$hostOut=$null;$hostError=$null
try {
    $os=Get-CimInstance Win32_OperatingSystem
    Write-Output ('Hosted memory worker: totalMiB='+[math]::Round($os.TotalVisibleMemorySize/1024)+' freeMiB='+[math]::Round($os.FreePhysicalMemory/1024))
    if([long]$os.FreePhysicalMemory*1KB -lt 12GB){throw 'Hosted worker requires 12 GiB free, including a 4 GiB reserve'}

    $sdk=@(& dotnet --list-sdks|ForEach-Object {if($_ -match '^(8\.0\.\d+) '){$Matches[1]}}|Sort-Object {[version]$_}|Select-Object -Last 1)
    if($sdk.Count -ne 1){throw 'Pinned .NET 8 SDK is unavailable'}
    @{sdk=@{version=$sdk[0];rollForward='disable'}}|ConvertTo-Json|Set-Content (Join-Path $root 'global.json') -Encoding utf8
    [IO.File]::WriteAllText($key,$env:PRIVATE_PLATFORM_READ_KEY,[Text.UTF8Encoding]::new($false))
    $env:PRIVATE_PLATFORM_READ_KEY=''
    $acl=Get-Acl $key;$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User,'Read','Allow'))
    Set-Acl $key $acl
    # GitHub SSH host keys are pinned with this reviewed executor; source checkout needs no anonymous API quota.
    [IO.File]::WriteAllLines($hosts,@('github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl','github.com ecdsa-sha2-nistp256 AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBEmKSENjQEezOmxkZMy7opKgwFB9nkt5YRrYMjNuG5N87uRgg6CLrbo5wAdT/y6v0mKV0U2w0WZ2YB/++Tpockg=','github.com ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABgQCj7ndNxQowgcQnjshcLrqPEiiphnt+VTTvDP6mHBL9j1aNUkY4Ue1gvwnGLVlOhGeYrnZaMgRK6+PKCUXaDbC7qtbW8gIkhL7aGCsOr/C56SJMy/BCZfxd1nWzAOxSDPgVsmerOBYfNqltV9/hWCqBywINIR+5dIg6JTJ72pcEpEjcYgXkE2YEFXV1JHnsKgbLWNlhScqb2UmyRkQyytRLtL+38TGxkxCflmO+5Z8CSSNY7GidjMIZ7Q4zMjA2n1nGrlTDkzwDCsw+wqFPGQA179cnfGWOWRVruj16z6XyvxvjJwbz0wQZ75XK5tKSb7FNyeIEs4TT4jk+S4dhPeAUC5y+bDYirYgM4GC7uEnztnZyaVWQ7B381AK4Qdrwt51ZqExKbQpTUNn+EjqoTwvqNj4kqx5QUCI0ThS/YkOxJCXmPUWZbhjpCg56i+2aB6CmK2JGhn57K5mj0MNdBXA4/WnwH6XoPWJzK5Nyu2zB3nAZp+S5hpQs+p1vN1/wsjk='),[Text.UTF8Encoding]::new($false))
    $env:GIT_SSH_COMMAND='ssh -i "'+$key+'" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile="'+$hosts+'"'
    $env:GIT_TERMINAL_PROMPT='0';$env:GIT_LFS_SKIP_SMUDGE='1'
    & git clone --quiet --filter=blob:none --no-checkout git@github.com:PROBIM55/platform.git $source *> $log
    if($LASTEXITCODE -ne 0){throw 'Private source checkout failed'}
    & git -C $source fetch --quiet --depth=1 origin $env:PRIVATE_SOURCE_SHA *>> $log
    if($LASTEXITCODE -ne 0){throw 'Exact source fetch failed'}
    & git -C $source checkout --quiet --detach $env:PRIVATE_SOURCE_SHA *>> $log
    if($LASTEXITCODE -ne 0){throw 'Exact source checkout failed'}
    $actual=(& git -C $source rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0 -or $actual -cne $env:PRIVATE_SOURCE_SHA){throw 'Private source identity differs'}
    $tree=(& git -C $source rev-parse 'HEAD^{tree}').Trim()
    Remove-Item -LiteralPath $key;$env:GIT_SSH_COMMAND=''
    $evidence=[ordered]@{sourceRepository='PROBIM55/platform';sourceSha=$actual;sourceTree=$tree;
        executionRepository=$env:GITHUB_REPOSITORY;executorSha=$env:GITHUB_SHA;runId=[long]$env:GITHUB_RUN_ID;runAttempt=[int]$env:GITHUB_RUN_ATTEMPT}
    $evidencePath=Join-Path $root 'cloud-evidence.json'
    $evidence|ConvertTo-Json|Set-Content $evidencePath -Encoding utf8
    Write-Output 'Exact private source available; product compilation runs only on disposable hosted Windows.'
    # The coordinator never executes the private builder in its own process.
    # Source credentials are gone before any builder child or monitor is started.
    $builderEntry=Join-Path $root 'invoke-private-builder.ps1'
    $builderScript=@'
$ErrorActionPreference='Stop'
try {
    Set-Location -LiteralPath '__SOURCE__'
    & './scripts/release/build_platform_release_bundle.ps1' -ExpectedSha '__SHA__' -CloudBuildEvidencePath '__EVIDENCE__' *>> '__LOG__'
    if($LASTEXITCODE -ne 0){throw 'Private product builder failed'}
    exit 0
} catch {
    [IO.File]::AppendAllText('__LOG__',"`nPrivate builder process failed: "+$_.Exception.Message)
    exit 1
}
'@
    foreach($pair in @(@('__SOURCE__',$source),@('__SHA__',$actual),@('__EVIDENCE__',$evidencePath),@('__LOG__',$log))){
        $builderScript=$builderScript.Replace($pair[0],([string]$pair[1]).Replace("'","''"))
    }
    [IO.File]::WriteAllText($builderEntry,$builderScript,[Text.UTF8Encoding]::new($false))
    $hostOut=Join-Path $root 'builder-host.stdout.log';$hostError=Join-Path $root 'builder-host.stderr.log'
    $builder=Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @('-NoProfile','-NonInteractive','-File',('"'+$builderEntry+'"')) `
        -WorkingDirectory $source -WindowStyle Hidden -RedirectStandardOutput $hostOut -RedirectStandardError $hostError -PassThru
    while(!$builder.WaitForExit(2000)){
        if([long](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory*1KB -lt 4GB){
            [IO.File]::WriteAllText($memoryStop,'Hosted builder stop requested to preserve 4 GiB free RAM.')
            Stop-PlatformCloudBuilderTree -Process $builder -TerminationLog $terminationLog
            throw 'Hosted builder was stopped at the memory reserve'
        }
    }
    if($builder.ExitCode -ne 0 -or (Test-Path -LiteralPath $memoryStop)){throw 'Private builder did not complete within its memory policy'}
    $builder.Dispose();$builder=$null
    Push-Location $source
    try {
        . ./scripts/release/cloud_candidate_crypto.ps1
        $payload=Join-Path $root 'ready-bundles.zip'
        $archive=[IO.Compression.ZipFile]::Open($payload,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach($file in @('release-manifest.json','platform-server.zip','web-static.zip','analysis-worker.zip','platform-source.zip')){
                $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $source ('artifacts/platform-release/'+$file)),$file,[IO.Compression.CompressionLevel]::NoCompression)
            }
        } finally {$archive.Dispose()}
        Protect-PlatformCloudFile $payload (Join-Path $root 'payload.enc') (Join-Path $root 'payload.envelope.json') $env:CLOUD_PUBLIC_KEY_PEM @{kind='platform-ready-bundles';sourceSha=$actual;sourceTree=$tree;executorSha=$env:GITHUB_SHA;runId=[long]$env:GITHUB_RUN_ID;runAttempt=[int]$env:GITHUB_RUN_ATTEMPT}
        $exitCode=0
    } finally {Pop-Location}
} catch {
    [IO.File]::AppendAllText($coordinatorLog,"`nPrivate build stage failed: "+$_.Exception.Message)
} finally {
    if($builder){
        try {Stop-PlatformCloudBuilderTree -Process $builder -TerminationLog $terminationLog}
        catch {$exitCode=1;[IO.File]::AppendAllText($coordinatorLog,"`nBuilder stop failed: "+$_.Exception.Message)}
        $builder.Dispose()
    }
    if(Test-Path -LiteralPath $key){Remove-Item -LiteralPath $key}
    $env:GIT_SSH_COMMAND='';$env:PRIVATE_PLATFORM_READ_KEY=''
    $diagnosticLog=(@($log,$hostOut,$hostError,$coordinatorLog,$terminationLog,$memoryStop)|ForEach-Object {Read-PlatformCloudLog $_}) -join "`n"
    $diagnostics=[Text.Encoding]::UTF8.GetBytes((@{sourceSha=$env:PRIVATE_SOURCE_SHA;exitCode=$exitCode;log=$diagnosticLog}|ConvertTo-Json -Depth 4 -Compress))
    $rsa=[Security.Cryptography.RSA]::Create();$symmetric=[Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $nonce=[Security.Cryptography.RandomNumberGenerator]::GetBytes(12);$tag=[byte[]]::new(16);$cipher=[byte[]]::new($diagnostics.Length)
    $aes=[Security.Cryptography.AesGcm]::new($symmetric,16)
    try {
        $rsa.ImportFromPem($env:CLOUD_PUBLIC_KEY_PEM);$aes.Encrypt($nonce,$diagnostics,$cipher,$tag)
        @{schema=1;algorithm='RSA-OAEP-SHA256+A256GCM';key=[Convert]::ToBase64String($rsa.Encrypt($symmetric,[Security.Cryptography.RSAEncryptionPadding]::OaepSHA256));
            nonce=[Convert]::ToBase64String($nonce);tag=[Convert]::ToBase64String($tag);ciphertext=[Convert]::ToBase64String($cipher)}|ConvertTo-Json -Compress|Set-Content (Join-Path $root 'diagnostics.enc.json') -Encoding utf8
    } finally {$rsa.Dispose();$aes.Dispose();[Array]::Clear($symmetric);[Array]::Clear($diagnostics)}
}
if($exitCode -ne 0){throw 'Private cloud product build failed; details are only in the encrypted diagnostic artifact'}
Write-Output 'Complete cloud candidate built and encrypted. Official Platform receipt and production deployment are separate checks.'
