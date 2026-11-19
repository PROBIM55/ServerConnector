$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Disposable hosted Windows only.'}
$runId=39481669515;$artifactId=12292295285
$source='b38baa5f4fa5c86ba88e0a24e823abd86b216283'
$zipSha='d4dbfcc3d5b39fe1071b7b68cff1bcdaef6fdc70bf826095eb466a6515a5c09c'
$packageSha='57af22b3d8334c0da2477717cc8ef7720b262f140b164f49878ee21a1e1ebe0f'
function ShaBytes([byte[]]$Bytes){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()}
$client=[Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.UserAgent.ParseAdd('Connector-verified-SDK-repack')
$client.DefaultRequestHeaders.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$env:GH_ARTIFACT_TOKEN)
Remove-Item Env:GH_ARTIFACT_TOKEN
try{
 $run=$client.GetStringAsync("https://api.github.com/repos/PROBIM55/ServerConnector/actions/runs/$runId").GetAwaiter().GetResult()|ConvertFrom-Json
 if($run.status -cne 'completed' -or $run.conclusion -cne 'success' -or $run.head_sha -cne $source -or $run.run_attempt -ne1 -or
    $run.path -cne '.github/workflows/build-unified-connector-candidate.yml'){throw 'Original successful package producer identity differs.'}
 $response=$client.GetAsync("https://api.github.com/repos/PROBIM55/ServerConnector/actions/artifacts/$artifactId/zip",[Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
 try{$response.EnsureSuccessStatusCode()|Out-Null;if($response.Content.Headers.ContentLength -gt2097152){throw 'Source artifact exceeds size bound.'};$bytes=$response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()}finally{$response.Dispose()}
}finally{$client.Dispose()}
if($bytes.Length -ne30923 -or (ShaBytes $bytes) -cne $zipSha){throw 'Source artifact bytes differ.'}
$stream=[IO.MemoryStream]::new($bytes);$archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Read)
try{
 if($archive.Entries.Count -ne2 -or @($archive.Entries|Where-Object{$_.FullName -notin @('smb-packages/Connector.Access.Smb.0.2.12.nupkg','encrypted-smb-native.json')}).Count){throw 'Unexpected source artifact members.'}
 $entry=$archive.GetEntry('smb-packages/Connector.Access.Smb.0.2.12.nupkg');$buffer=[IO.MemoryStream]::new();$inputStream=$entry.Open()
 try{$inputStream.CopyTo($buffer);$package=$buffer.ToArray()}finally{$inputStream.Dispose();$buffer.Dispose()}
 $reader=[IO.StreamReader]::new($archive.GetEntry('encrypted-smb-native.json').Open())
 try{$native=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
 if(!$native.actualPrivacyVerified -or !$native.ordinaryUser -or $native.cleanup -cne 'confirmed' -or
    (@($native.steps)-join',') -cne 'plain-mapping-rejected-by-crypto-readback,encrypted-native-read-and-reuse,revoked-account-denied'){throw 'Verified native proof differs.'}
}finally{$archive.Dispose();$stream.Dispose()}
if($package.Length -ne30723 -or (ShaBytes $package) -cne $packageSha){throw 'Original package bytes differ.'}
$out=Join-Path $env:RUNNER_TEMP 'smb-sdk-corrected';New-Item -ItemType Directory -Path $out|Out-Null
$packageStream=[IO.MemoryStream]::new($package);$original=[IO.Compression.ZipArchive]::new($packageStream,[IO.Compression.ZipArchiveMode]::Read)
$targetPath=Join-Path $out 'Connector.Access.Smb.0.2.12.nupkg'
$output=[IO.File]::Create($targetPath);$target=[IO.Compression.ZipArchive]::new($output,[IO.Compression.ZipArchiveMode]::Create)
$hashes=[ordered]@{};$nuspecChanged=$false
try{
 foreach($entry in $original.Entries){
  if($entry.FullName.StartsWith('/') -or $entry.FullName.Contains('..') -or $entry.FullName.Contains('\')){throw 'Noncanonical package member.'}
  $raw=[IO.MemoryStream]::new();$entryStream=$entry.Open()
  try{$entryStream.CopyTo($raw);$content=$raw.ToArray()}finally{$entryStream.Dispose();$raw.Dispose()}
  $before=ShaBytes $content
  if($entry.FullName -ceq 'Connector.Access.Smb.nuspec'){
   $xml=[Xml.XmlDocument]::new();$xmlStream=[IO.MemoryStream]::new($content)
   try{$xml.Load($xmlStream)}finally{$xmlStream.Dispose()}
   if($xml.package.metadata.id -cne 'Connector.Access.Smb' -or $xml.package.metadata.version -cne '0.2.12'){throw 'Package identity changed.'}
   $deps=@($xml.SelectNodes('//*[local-name()="dependency"]'))
   if($deps.Count -ne2 -or @($deps|Where-Object{$_.id -notin @('Connector.Access','Connector.Access.Contracts') -or $_.version -cne '0.2.12'}).Count){throw 'Unexpected original dependency metadata.'}
   foreach($dep in $deps){$dep.version=if($dep.id -ceq 'Connector.Access'){'0.2.7'}else{'0.2.4'}}
   $writerBuffer=[IO.MemoryStream]::new();$settings=[Xml.XmlWriterSettings]::new();$settings.Encoding=[Text.UTF8Encoding]::new($false)
   $writer=[Xml.XmlWriter]::Create($writerBuffer,$settings)
   try{$xml.Save($writer);$writer.Flush();$content=$writerBuffer.ToArray()}finally{$writer.Dispose();$writerBuffer.Dispose()}
   $nuspecChanged=$true
  }
  $new=$target.CreateEntry($entry.FullName);$new.LastWriteTime=$entry.LastWriteTime;$write=$new.Open()
  try{$write.Write($content,0,$content.Length)}finally{$write.Dispose()}
  $hashes[$entry.FullName]=@{beforeSha256=$before;afterSha256=(ShaBytes $content)}
  if($entry.FullName -cne 'Connector.Access.Smb.nuspec' -and $hashes[$entry.FullName].beforeSha256 -cne $hashes[$entry.FullName].afterSha256){throw 'Compiled package member changed.'}
 }
}finally{$target.Dispose();$output.Dispose();$original.Dispose();$packageStream.Dispose()}
if(!$nuspecChanged){throw 'Package dependency metadata missing.'}
$verify=[IO.Compression.ZipFile]::OpenRead($targetPath)
try{
 if($verify.Entries.Count -ne $hashes.Count){throw 'Corrected package member count differs.'}
 foreach($entry in $verify.Entries){
  $read=$entry.Open();$buffer=[IO.MemoryStream]::new()
  try{$read.CopyTo($buffer);$actual=ShaBytes $buffer.ToArray()}finally{$read.Dispose();$buffer.Dispose()}
  if(!$hashes.Contains($entry.FullName) -or $actual -cne $hashes[$entry.FullName].afterSha256){throw 'Corrected package readback differs.'}
 }
}finally{$verify.Dispose()}
$proof=@{schemaVersion=1;sourceRunId=$runId;sourceSha=$source;sourceArtifactId=$artifactId;sourceZipSha256=$zipSha;sourcePackageSha256=$packageSha;packageSha256=(Get-FileHash $targetPath).Hash.ToLowerInvariant();compilation=$false;onlyChangedMember='Connector.Access.Smb.nuspec';dependencies=@{ 'Connector.Access'='0.2.7';'Connector.Access.Contracts'='0.2.4'};members=$hashes;nativeProof=$native}
[IO.File]::WriteAllText((Join-Path $out 'provenance.json'),($proof|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
