$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Disposable hosted Windows only.'}
$root=Join-Path $env:RUNNER_TEMP 'connector-dev-services';New-Item -ItemType Directory -Path $root|Out-Null
$runtime=Join-Path $root 'runtime';$replay=[long]$env:READY_RUNTIME_RUN_ID
if($replay -lt0){throw 'Invalid ready runtime run.'}
$source=(& git rev-parse HEAD).Trim();if($LASTEXITCODE-ne0){throw 'Source identity unavailable.'}
$driverPath='.github/scripts/dev-service-loop.ps1'
$workflowPath='.github/workflows/build-unified-connector-candidate.yml'
function GitBlob([string]$Commit,[string]$Path){
 $value=(& git rev-parse ($Commit+':'+$Path)).Trim()
 if($LASTEXITCODE-ne0 -or $value -cnotmatch '^[a-f0-9]{40}$'){throw 'Source blob identity unavailable.'};$value
}
function SourceHash([string]$Commit){
 # Every tracked input is protected, including NuGet.Config, editorconfig,
 # content assets and imported build files. Only these scenario/docs may vary.
 $allowed=@('connector-desktop/scripts/invoke_hosted_encrypted_smb_fixture.ps1','connector-desktop/docs/DEV_SERVICES_RU.md')
 $rows=@(& git ls-tree -r $Commit|Where-Object{($_ -split "`t",2)[1] -cnotin $allowed})
 if($LASTEXITCODE-ne0 -or $rows.Count -eq0){throw 'Source inventory unavailable.'}
 [Array]::Sort($rows,[StringComparer]::Ordinal)
 [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))).ToLowerInvariant()
}
$driver=GitBlob $source $driverPath;$workflow=GitBlob $source $workflowPath;$codeHash=SourceHash $source
$tests=@(
 @{project='connector-access/tests/Connector.Access.Client.Tests';name='Connector.Access.Client.Tests';filter=$null},
 @{project='connector-access/tests/Connector.Access.Smb.Tests';name='Connector.Access.Smb.Tests';filter=$null},
 @{project='connector-desktop/tests/Connector.Network.Tests';name='Connector.Network.Tests';filter=$null},
 @{project='connector-desktop/Connector.Desktop.Tests';name='Connector.Desktop.Tests';filter='FullyQualifiedName~CommonConnection|FullyQualifiedName~CommonAccessRuntime|FullyQualifiedName~CommonSmbAccess|FullyQualifiedName~LegacyEncryptedSmb|FullyQualifiedName~ConnectorBrowserSignIn'}
)
if((dotnet --version) -cne '8.0.426'){throw 'Unexpected service runtime SDK.'}
& node --test connector-desktop/design/tests/connection-ui.test.cjs
if($LASTEXITCODE-ne0){throw 'Connection and folder UI scenarios failed.'}
$manifestName='dev-runtime-manifest.json'
if($replay -eq0){
 Remove-Item Env:GH_ARTIFACT_TOKEN
 foreach($test in $tests){
  $testArguments=@('test',$test.project,'-c','Release','-p:RestoreLockedMode=true','-m:1','-nodeReuse:false','-p:BuildInParallel=false','-p:UseSharedCompilation=false')
  if($test.filter){$testArguments+=@('--filter',$test.filter)}
  & dotnet @testArguments
  if($LASTEXITCODE-ne0){throw 'Focused service tests failed.'}
 }
 dotnet publish connector-desktop/installer/Connector.Smb.NativeProbe -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=8.0.32 -p:TargetLatestRuntimePatch=false -o $runtime -m:1 -nodeReuse:false -p:BuildInParallel=false -p:UseSharedCompilation=false
 if($LASTEXITCODE-ne0){throw 'Service probe compilation failed.'}
 foreach($test in $tests){
  $destination=Join-Path $runtime ('tests/'+$test.name);New-Item -ItemType Directory -Path $destination|Out-Null
  Copy-Item -Path (Join-Path $test.project 'bin/Release/net8.0-windows/*') -Destination $destination -Recurse
 }
 $files=@(Get-ChildItem -LiteralPath $runtime -Recurse -File -Force|ForEach-Object{@{path=[IO.Path]::GetRelativePath($runtime,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
 $manifest=@{schemaVersion=1;sourceSha=$source;runId=[long]$env:GITHUB_RUN_ID;compiledSourceSha=$source;compiledRunId=[long]$env:GITHUB_RUN_ID;codeHash=$codeHash;driverBlob=$driver;workflowBlob=$workflow;purpose='isolated-service-tests-only';files=$files}
 [IO.File]::WriteAllText((Join-Path $runtime $manifestName),($manifest|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}else{
 $client=[Net.Http.HttpClient]::new();$client.DefaultRequestHeaders.UserAgent.ParseAdd('Connector-dev-service-replay')
 $client.DefaultRequestHeaders.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$env:GH_ARTIFACT_TOKEN)
 Remove-Item Env:GH_ARTIFACT_TOKEN
 try{
  $url="https://api.github.com/repos/PROBIM55/ServerConnector/actions/runs/$replay"
  $producer=$client.GetStringAsync($url).GetAwaiter().GetResult()|ConvertFrom-Json
  if($producer.status -cne 'completed' -or $producer.conclusion -cne 'success' -or $producer.run_attempt -ne1 -or
     $producer.path -cne '.github/workflows/build-unified-connector-candidate.yml' -or $producer.event -cne 'workflow_dispatch'){throw 'Ready runtime producer is not accepted.'}
  & git fetch --no-tags --depth=1 origin $producer.head_sha
  if($LASTEXITCODE-ne0){throw 'Producer source identity unavailable.'}
  $producerDriver=GitBlob $producer.head_sha $driverPath;$producerWorkflow=GitBlob $producer.head_sha $workflowPath
  $producerCodeHash=SourceHash $producer.head_sha
  # One immutable first-run adapter: upload-artifact omitted dot-named coverage
  # path metadata. Reuse its verified DLLs rather than compiling them again.
  $firstRuntime=($replay -eq39485812064 -and $producer.head_sha -ceq '684c29c0aff1e78b4ae1b3d278d83b2f0883d29b' -and
   $producerDriver -ceq 'c0b5399b97ef47ab4dbe8438f7e00c0ddbdbfbba' -and $producerWorkflow -ceq '8e6e46c5f0484a6bb1af8833bab33e4fc01064bf')
  if($firstRuntime){
   $runnerPath='connector-access/smb-helper/SmbAclReconcile.ps1'
   $allowedHarness=@($driverPath,$workflowPath,'connector-desktop/scripts/invoke_hosted_encrypted_smb_fixture.ps1','connector-desktop/scripts/test_hosted_smb_identity_rights.ps1','connector-desktop/docs/DEV_SERVICES_RU.md',$runnerPath)
   $changed=@(& git diff --no-renames --name-only $producer.head_sha $source)
   if($LASTEXITCODE-ne0 -or @($changed|Where-Object{$_ -cnotin $allowedHarness}).Count){throw 'First runtime managed inputs changed.'}
   if($runnerPath -cin $changed){
    if((GitBlob $source $runnerPath) -cne '74703b569f31cec7f5d8b0ee2bb0e4b12a3d03a5'){throw 'Interpreted runner overlay is not the reviewed blob.'}
    # The runner is interpreted PowerShell, not a compiled DLL input. Permit
    # only the exact ownership-marker and LSA API corrections on the first run.
    $original=(& git show ($producer.head_sha+':'+$runnerPath)) -join "`n"
    if($LASTEXITCODE-ne0){throw 'Original interpreted runner unavailable.'}
    $current=(& git show ($source+':'+$runnerPath)) -join "`n"
    if($LASTEXITCODE-ne0){throw 'Current interpreted runner unavailable.'}
    $oldMarker='$ownerMarker = ''Structura Connector SMB '' + [BitConverter]::ToString($ownerBytes).Replace(''-'', '''').ToLowerInvariant()'
    $newMarker='$ownerMarker = ''cnb:'' + [Convert]::ToBase64String($ownerBytes).TrimEnd(''='').Replace(''+'', ''-'').Replace(''/'', ''_'')'
    $oldOpen='        $status = [SmbServiceIdentity.NativeLsa]::LsaOpenPolicy([IntPtr]::Zero, [ref]$attributes, 0x800, [ref]$policy)'
    $newOpen='        $policyAccess = if ($Operation -eq ''Add'') { 0x810 } else { 0x800 }'+"`n"+'        $status = [SmbServiceIdentity.NativeLsa]::LsaOpenPolicy([IntPtr]::Zero, [ref]$attributes, $policyAccess, [ref]$policy)'
    $oldReturn='        return ,$observed';$newReturn='        return $observed'
    foreach($fragment in @($oldMarker,$oldOpen,$oldReturn)){
     if(!$original.Contains($fragment) -or $original.IndexOf($fragment)-ne$original.LastIndexOf($fragment)){throw 'Original interpreted runner correction is not unique.'}
    }
    if($original.Replace($oldMarker,$newMarker).Replace($oldOpen,$newOpen).Replace($oldReturn,$newReturn) -cne $current){throw 'Interpreted runner overlay is outside the exact reviewed fixes.'}
   }
  }elseif($producerDriver -cne $driver -or $producerWorkflow -cne $workflow -or $producerCodeHash -cne $codeHash){throw 'Producer source inputs differ.'}
  $artifacts=($client.GetStringAsync($url+'/artifacts').GetAwaiter().GetResult()|ConvertFrom-Json).artifacts
  $artifact=@($artifacts|Where-Object{$_.name -ceq 'connector-dev-service-runtime' -and !$_.expired})
  if($artifact.Count -ne1 -or $artifact[0].size_in_bytes -gt268435456 -or $artifact[0].digest -cnotmatch '^sha256:[a-f0-9]{64}$'){throw 'Ready runtime artifact differs.'}
  if($firstRuntime -and ($artifact[0].id -ne12293073927 -or $artifact[0].size_in_bytes -ne89832657 -or
     $artifact[0].digest -cne 'sha256:885decbb83a1e05fbd9317cd43b9fee3f9b245de535958d261c1a8242aa286a4')){throw 'First runtime artifact differs.'}
  $zip=Join-Path $root 'ready.zip'
  $response=$client.GetAsync(('https://api.github.com/repos/PROBIM55/ServerConnector/actions/artifacts/'+$artifact[0].id+'/zip'),[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
  try{$response.EnsureSuccessStatusCode()|Out-Null;$read=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();$output=[IO.File]::Create($zip)
   try{$buffer=New-Object byte[] 1048576;$total=0L;while(($n=$read.Read($buffer,0,$buffer.Length)) -gt0){$total+=$n;if($total -gt268435456){throw 'Runtime artifact exceeds bound.'};$output.Write($buffer,0,$n)}}finally{$output.Dispose();$read.Dispose()}
  }finally{$response.Dispose()}
  if(('sha256:'+(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()) -cne $artifact[0].digest){throw 'Ready runtime artifact digest differs.'}
 }finally{$client.Dispose()}
 New-Item -ItemType Directory -Path $runtime|Out-Null
 $archive=[IO.Compression.ZipFile]::OpenRead($zip)
 try{
  if($archive.Entries.Count -eq0 -or $archive.Entries.Count -gt10000){throw 'Runtime member count differs.'}
  $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$expanded=0L
  foreach($entry in $archive.Entries){
   $expanded+=$entry.Length
   $target=[IO.Path]::GetFullPath((Join-Path $runtime $entry.FullName))
   if(!$target.StartsWith($runtime+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $entry.FullName.Contains('..') -or
      $entry.FullName.Contains(':') -or $entry.FullName.Contains('\') -or !$names.Add($target) -or
      $entry.Length -gt134217728 -or $expanded -gt536870912 -or ($entry.ExternalAttributes -band0x400) -ne0){throw 'Runtime member boundary differs.'}
  };[IO.Compression.ZipFile]::ExtractToDirectory($zip,$runtime)
 }finally{$archive.Dispose()}
 $manifest=Get-Content (Join-Path $runtime $manifestName) -Raw|ConvertFrom-Json
 if($manifest.schemaVersion -ne1 -or $manifest.sourceSha -cne $producer.head_sha -or $manifest.runId -ne $replay -or
    $manifest.compiledRunId -ne $replay -or $manifest.compiledSourceSha -cne $producer.head_sha -or
    $manifest.codeHash -cne $producerCodeHash -or $manifest.driverBlob -cne $producerDriver -or $manifest.workflowBlob -cne $producerWorkflow -or
    $manifest.purpose -cne 'isolated-service-tests-only'){throw 'Use the original successful compile run for unchanged source inputs.'}
 $expectedFiles=@($manifest.files);$omitted=@()
 if($firstRuntime){
  $omitted=@($expectedFiles|Where-Object{! (Test-Path -LiteralPath (Join-Path $runtime $_.path)) -and
    $_.path -cmatch '^tests/Connector\.(Access\.Client|Access\.Smb|Network|Desktop)\.Tests/\.msCoverageSourceRootsMapping_[A-Za-z0-9_.-]+$'})
  if($omitted.Count-gt8){throw 'Unexpected first runtime coverage metadata count.'}
  $omittedPaths=@($omitted|ForEach-Object{$_.path})
  $expectedFiles=@($expectedFiles|Where-Object{$_.path -cnotin $omittedPaths})
 }
 $actual=@(Get-ChildItem -LiteralPath $runtime -Recurse -File -Force|Where-Object{$_.Name -cne $manifestName})
 [IO.File]::WriteAllText((Join-Path $root 'runtime-inventory.json'),(@{actualCount=$actual.Count;expectedCount=$expectedFiles.Count;omittedCoverageMetadata=@($omitted|ForEach-Object{$_.path});producerRun=$replay}|ConvertTo-Json -Depth 4))
 if($actual.Count -ne $expectedFiles.Count){throw 'Runtime inventory count differs.'}
 $verified=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
 foreach($file in $expectedFiles){
  $path=[IO.Path]::GetFullPath((Join-Path $runtime $file.path))
  if(!$path.StartsWith($runtime+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
     !$verified.Add($path) -or $file.path.Contains(':') -or $file.path.Contains('..') -or
     (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Ready runtime file differs.'}
 }
 foreach($file in $actual){if(!$verified.Contains($file.FullName)){throw 'Unverified runtime member.'}}
 foreach($test in $tests){
  # Existing component tests resolve checked-in fixture scripts relative to
  # their normal output directory. Restore ready files there, never build them.
  $destination=[IO.Path]::GetFullPath((Join-Path $test.project 'bin/Release/net8.0-windows'))
  $checkout=[IO.Path]::GetFullPath($PWD.Path).TrimEnd('\')+'\'
  if(!$destination.StartsWith($checkout,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $destination)){throw 'Ready test output destination is not a new checkout directory.'}
  New-Item -ItemType Directory -Path $destination -Force|Out-Null
  foreach($entry in @(Get-ChildItem -LiteralPath (Join-Path $runtime ('tests/'+$test.name)) -Force)){
   Copy-Item -LiteralPath $entry.FullName -Destination $destination -Recurse
  }
  $prefix='tests/'+$test.name+'/'
  foreach($file in @($expectedFiles|Where-Object{$_.path.StartsWith($prefix,[StringComparison]::Ordinal)})){
   $copied=Join-Path $destination $file.path.Substring($prefix.Length)
   if((Get-Item -LiteralPath $copied).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $copied).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Restored ready test bytes differ.'}
  }
  $testArguments=@('vstest',(Join-Path $destination ($test.name+'.dll')),'/Logger:console;Verbosity=normal')
  if($test.filter){$testArguments+=('/TestCaseFilter:'+$test.filter)}
  & dotnet @testArguments
  if($LASTEXITCODE-ne0){throw 'Compiled service regression tests failed.'}
 }
}
& connector-desktop/scripts/invoke_hosted_encrypted_smb_fixture.ps1 -ProbeExecutable (Join-Path $runtime 'Connector.Smb.NativeProbe.exe') -EvidencePath (Join-Path $root 'native-smb.json')
[IO.File]::WriteAllText((Join-Path $root 'iteration.json'),(@{schemaVersion=1;sourceSha=$source;codeHash=$codeHash;readyRuntimeRun=$replay;compiled=($replay -eq0);installerBuilt=$false;pcUsed=$false;productionTouched=$false;scope='isolated native SMB plus compiled account/session/network/recovery component tests; no live production SSO acceptance'}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
