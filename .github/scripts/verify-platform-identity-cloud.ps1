$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
   $env:RUNNER_OS -cne 'Windows' -or $env:PRIVATE_SOURCE_SHA -cnotmatch '^[a-f0-9]{40}$') {
    throw 'Exact source and disposable hosted Windows are required.'
}
if([string]::IsNullOrWhiteSpace($env:PRIVATE_PLATFORM_READ_KEY)) {throw 'Temporary read-only source access is unavailable.'}
$root=Join-Path $env:RUNNER_TEMP 'platform-identity-private'
New-Item -ItemType Directory -Path $root | Out-Null
$key=Join-Path $root 'source-read-key.pem'
$hosts=Join-Path $root 'known_hosts'
$source=Join-Path $root 'source'
$log=Join-Path $root 'private-build.log'
$exitCode=1
try {
    [IO.File]::WriteAllText($key,$env:PRIVATE_PLATFORM_READ_KEY,[Text.UTF8Encoding]::new($false))
    $env:PRIVATE_PLATFORM_READ_KEY=''
    $acl=Get-Acl -LiteralPath $key
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,'Read','Allow'))
    Set-Acl -LiteralPath $key -AclObject $acl
    $meta=Invoke-RestMethod -Uri 'https://api.github.com/meta' -TimeoutSec 20
    [IO.File]::WriteAllLines($hosts,@($meta.ssh_keys|ForEach-Object {'github.com '+$_}),[Text.UTF8Encoding]::new($false))
    $env:GIT_SSH_COMMAND='ssh -i "'+$key+'" -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile="'+$hosts+'"'
    $env:GIT_TERMINAL_PROMPT='0'
    $env:GIT_LFS_SKIP_SMUDGE='1'
    & git clone --quiet --filter=blob:none --no-checkout git@github.com:PROBIM55/platform.git $source *> $log
    if($LASTEXITCODE -ne 0){throw 'Private source checkout failed.'}
    & git -C $source fetch --quiet --depth=1 origin $env:PRIVATE_SOURCE_SHA *>> $log
    if($LASTEXITCODE -ne 0){throw 'Exact source fetch failed.'}
    & git -C $source checkout --quiet --detach $env:PRIVATE_SOURCE_SHA *>> $log
    if($LASTEXITCODE -ne 0){throw 'Exact source checkout failed.'}
    $actual=(& git -C $source rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0 -or $actual -cne $env:PRIVATE_SOURCE_SHA){throw 'Private source identity differs.'}
    Remove-Item -LiteralPath $key
    $env:GIT_SSH_COMMAND=''
    Push-Location $source
    try {
        & python -m unittest discover -s scripts/ops/tests -p test_structura_identity.py -v *>> $log
        if($LASTEXITCODE -ne 0){throw 'Directory reconciliation checks failed.'}
        & dotnet test apps/server/tests/Platform.Server.Tests/Platform.Server.Tests.csproj --configuration Release --maxcpucount:1 `
            -p:UseSharedCompilation=false -p:BuildInParallel=false `
            --filter 'FullyQualifiedName~StructuraSignInTests|FullyQualifiedName~ConnectorBrowserSignInEndpointTests|FullyQualifiedName~ConnectorAccessCompanyScopeTests' `
            --logger 'trx;LogFileName=identity.trx' *>> $log
        $exitCode=$LASTEXITCODE
    } finally {Pop-Location}
} catch {
    # Neither exception text nor compiler output from the private repository is public.
    [IO.File]::AppendAllText($log,"`nVerification stage failed: "+$_.Exception.Message)
    $exitCode=1
} finally {
    if(Test-Path -LiteralPath $key){Remove-Item -LiteralPath $key}
    $env:GIT_SSH_COMMAND=''
    $env:PRIVATE_PLATFORM_READ_KEY=''
    $diagnostics=[ordered]@{sourceSha=$env:PRIVATE_SOURCE_SHA;exitCode=$exitCode;log=([IO.File]::ReadAllText($log));testResults=@()}
    if(Test-Path -LiteralPath $source){
        $diagnostics.testResults=@(Get-ChildItem -LiteralPath (Join-Path $source 'apps/server/tests') -Filter identity.trx -Recurse -File |
            ForEach-Object { [IO.File]::ReadAllText($_.FullName) })
    }
    $plain=[Text.Encoding]::UTF8.GetBytes(($diagnostics|ConvertTo-Json -Depth 5 -Compress))
    $symmetric=[Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $nonce=[Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $cipher=[byte[]]::new($plain.Length);$tag=[byte[]]::new(16)
    $aes=[Security.Cryptography.AesGcm]::new($symmetric,16)
    $rsa=[Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($env:DIAGNOSTICS_PUBLIC_KEY_PEM)
        $aes.Encrypt($nonce,$plain,$cipher,$tag)
        $wrapped=$rsa.Encrypt($symmetric,[Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
        [ordered]@{schema=1;algorithm='RSA-OAEP-SHA256+A256GCM';key=[Convert]::ToBase64String($wrapped);
            nonce=[Convert]::ToBase64String($nonce);tag=[Convert]::ToBase64String($tag);ciphertext=[Convert]::ToBase64String($cipher)}|
            ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $root 'diagnostics.enc.json') -Encoding utf8
    } finally {
        $aes.Dispose();$rsa.Dispose();[Array]::Clear($symmetric);[Array]::Clear($plain)
    }
}
if($exitCode -ne 0){throw 'Private identity verification failed. Details are in the encrypted diagnostics artifact.'}
Write-Output 'Exact private source compiled and targeted identity checks passed; no local or production build was started.'
