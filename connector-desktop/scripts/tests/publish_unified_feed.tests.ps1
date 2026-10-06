#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$publisher=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\publish_unified_feed.ps1'))
$signer=Join-Path $PSScriptRoot '..\sign_update_feed.ps1'
$root=Join-Path ([IO.Path]::GetTempPath()) ('unified-feed-test-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$key=[Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
$keyFile=Join-Path $root 'test-public.pem'; [IO.File]::WriteAllText($keyFile,$key.ExportSubjectPublicKeyInfoPem())
$expectedGood=$true
function Assert([bool]$condition,[string]$message){if(!$condition){throw "ASSERT: $message"}}
function New-Fixture([string]$version,[string]$payload='package-one',[string]$assetName=$null){
    $dir=Join-Path $script:root ('source-'+[guid]::NewGuid().ToString('N')); [IO.Directory]::CreateDirectory($dir)|Out-Null
    if(!$assetName){$assetName="Structura.Connector.Desktop-$version-preview-full.nupkg"}
    $asset=Join-Path $dir $assetName; [IO.File]::WriteAllBytes($asset,[Text.Encoding]::UTF8.GetBytes($payload))
    $fi=Get-Item $asset; $sha=(Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant(); $sha1=(Get-FileHash $asset -Algorithm SHA1).Hash.ToUpperInvariant()
    $catalog=@{Assets=@(@{PackageId='Structura.Connector.Desktop';Version=$version;Type='Full';FileName=$assetName;SHA1=$sha1;SHA256=$sha.ToUpperInvariant();Size=$fi.Length})}|ConvertTo-Json -Depth 5 -Compress
    [IO.File]::WriteAllText((Join-Path $dir 'releases.preview.json'),$catalog,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $dir 'RELEASES-preview'),"$sha1 $assetName $($fi.Length)",[Text.UTF8Encoding]::new($false))
    $m=[ordered]@{schemaVersion=1;applicationId='Structura.Connector.Desktop';channel='preview';assets=@(@{fileName=$assetName;version=$version;type='Full';size=$fi.Length;sha256=$sha})}
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($m|ConvertTo-Json -Depth 5 -Compress)); [IO.File]::WriteAllBytes((Join-Path $dir 'connector-release.preview.json'),$bytes)
    $signature=$script:key.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    [IO.File]::WriteAllBytes((Join-Path $dir 'connector-release.preview.json.sig'),$signature)
    $exe=Join-Path $dir 'Setup.exe';$msi=Join-Path $dir 'Machine.msi';[IO.File]::WriteAllBytes($exe,[byte[]](1,2,3));[IO.File]::WriteAllBytes($msi,[byte[]](4,5,6))
    return [pscustomobject]@{Dir=$dir;Asset=$asset;Exe=$exe;Msi=$msi;ExeHash=(Get-FileHash $exe -Algorithm SHA256).Hash;MsiHash=(Get-FileHash $msi -Algorithm SHA256).Hash;Version=$version;Name=$assetName}
}
function Add-DeltaAsset($fixture){
    $name="Structura.Connector.Desktop-$($fixture.Version)-preview-delta.1.0.0.nupkg";$path=Join-Path $fixture.Dir $name
    [IO.File]::WriteAllBytes($path,[Text.Encoding]::UTF8.GetBytes('delta-package'))
    $files=@($fixture.Name,$name)|ForEach-Object{Join-Path $fixture.Dir $_}
    $assetData=@();foreach($p in $files){$f=Get-Item $p;$isDelta=$f.Name -like '*-delta.*.nupkg';$assetData+=@{PackageId='Structura.Connector.Desktop';Version=$fixture.Version;Type=$(if($isDelta){'Delta'}else{'Full'});FileName=$f.Name;SHA1=(Get-FileHash $p -Algorithm SHA1).Hash.ToUpperInvariant();SHA256=(Get-FileHash $p -Algorithm SHA256).Hash.ToUpperInvariant();Size=$f.Length}}
    [IO.File]::WriteAllText((Join-Path $fixture.Dir 'releases.preview.json'),(@{Assets=$assetData}|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
    $manifestAssets=@();foreach($a in $assetData){$manifestAssets+=@{fileName=$a.FileName;version=$a.Version;type=$a.Type;size=$a.Size;sha256=$a.SHA256.ToLowerInvariant()}}
    $manifest=[ordered]@{schemaVersion=1;applicationId='Structura.Connector.Desktop';channel='preview';assets=$manifestAssets};$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($manifest|ConvertTo-Json -Depth 5 -Compress));[IO.File]::WriteAllBytes((Join-Path $fixture.Dir 'connector-release.preview.json'),$bytes)
    [IO.File]::WriteAllBytes((Join-Path $fixture.Dir 'connector-release.preview.json.sig'),$script:key.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation))
    $lines=@();foreach($a in $assetData){$lines+="$($a.SHA1) $($a.FileName) $($a.Size)"};[IO.File]::WriteAllLines((Join-Path $fixture.Dir 'RELEASES-preview'),$lines,[Text.UTF8Encoding]::new($false))
    return $path
}
function Add-PreviousFullAsset($fixture,[string]$oldVersion){
    $name="Structura.Connector.Desktop-$oldVersion-preview-full.nupkg";$path=Join-Path $fixture.Dir $name;[IO.File]::WriteAllBytes($path,[Text.Encoding]::UTF8.GetBytes('previous-full-package'))
    $catalog=Get-Content (Join-Path $fixture.Dir 'releases.preview.json') -Raw|ConvertFrom-Json
    $catalog.Assets+=@{PackageId='Structura.Connector.Desktop';Version=$oldVersion;Type='Full';FileName=$name;SHA1=(Get-FileHash $path -Algorithm SHA1).Hash.ToUpperInvariant();SHA256=(Get-FileHash $path -Algorithm SHA256).Hash.ToUpperInvariant();Size=(Get-Item $path).Length}
    [IO.File]::WriteAllText((Join-Path $fixture.Dir 'releases.preview.json'),($catalog|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
    $manifestAssets=@();foreach($a in $catalog.Assets){$manifestAssets+=@{fileName=$a.FileName;version=$a.Version;type=$a.Type;size=$a.Size;sha256=$a.SHA256.ToLowerInvariant()}}
    $manifest=[ordered]@{schemaVersion=1;applicationId='Structura.Connector.Desktop';channel='preview';assets=$manifestAssets};$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($manifest|ConvertTo-Json -Depth 5 -Compress));[IO.File]::WriteAllBytes((Join-Path $fixture.Dir 'connector-release.preview.json'),$bytes)
    [IO.File]::WriteAllBytes((Join-Path $fixture.Dir 'connector-release.preview.json.sig'),$script:key.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation))
    $lines=@();foreach($a in $catalog.Assets){$lines+="$($a.SHA1) $($a.FileName) $($a.Size)"};[IO.File]::WriteAllLines((Join-Path $fixture.Dir 'RELEASES-preview'),$lines,[Text.UTF8Encoding]::new($false))
    return $path
}
function Invoke-Publish($fixture,[string]$feed,[string]$PublisherPath=$script:publisher){
    & $PublisherPath -PackageDirectory $fixture.Dir -Channel preview -FeedRoot $feed -InstallerPath $fixture.Exe -MachineMsiPath $fixture.Msi -ExpectedInstallerSha256 $fixture.ExeHash -ExpectedMachineMsiSha256 $fixture.MsiHash -PublicKeyPemPath $script:keyFile
}
function Expect-Failure([scriptblock]$action,[string]$label){$threw=$false;try{&$action}catch{$threw=$true};Assert $threw "$label must fail"}
function Remove-TestRoot([string]$path){
    $parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar;$full=[IO.Path]::GetFullPath($path)
    if(!$full.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase)-or[IO.Path]::GetFileName($full)-notmatch'^unified-feed-test-[0-9a-f]{32}$'){throw 'Refusing test cleanup outside its unique temp root.'}
    if(Test-Path -LiteralPath $full){$item=Get-Item -LiteralPath $full -Force;if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Refusing test cleanup through a reparse root.'};foreach($child in Get-ChildItem -LiteralPath $full -Force -Recurse){if(($child.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Refusing test cleanup containing reparse paths.'}};Remove-Item -LiteralPath $full -Recurse -Force}
}
try {
    $feed=Join-Path $root 'feed'; $good=New-Fixture '1.1.0-preview.11';$delta=Add-DeltaAsset $good;$oldAsset=Add-PreviousFullAsset $good '1.1.0-preview.10'
    # Invoke the real signer from an isolated repository-shaped copy. Its ordinary pinned-key path
    # resolves to this temporary fixture key, so production key configuration is untouched.
    $signerFixture=New-Fixture '1.1.0-preview.11';[void](Add-DeltaAsset $signerFixture);[void](Add-PreviousFullAsset $signerFixture '1.1.0-preview.10')
    Remove-Item -LiteralPath (Join-Path $signerFixture.Dir 'connector-release.preview.json'),(Join-Path $signerFixture.Dir 'connector-release.preview.json.sig')
    $fixtureRepo=Join-Path $root 'signer-repo';$fixtureScripts=Join-Path $fixtureRepo 'scripts';$fixturePublic=Join-Path $fixtureRepo 'Connector.Desktop/Assets'
    [IO.Directory]::CreateDirectory($fixtureScripts)|Out-Null;[IO.Directory]::CreateDirectory($fixturePublic)|Out-Null
    Copy-Item -LiteralPath $script:signer -Destination (Join-Path $fixtureScripts 'sign_update_feed.ps1')
    [IO.File]::WriteAllText((Join-Path $fixturePublic 'update-public-key.pem'),$key.ExportSubjectPublicKeyInfoPem())
    $privateFile=Join-Path $root 'fixture-private.pem';[IO.File]::WriteAllText($privateFile,$key.ExportPkcs8PrivateKeyPem())
    & (Join-Path $fixtureScripts 'sign_update_feed.ps1') -PackageDirectory $signerFixture.Dir -Channel preview -PrivateKeyPemPath $privateFile
    $signed=Get-Content (Join-Path $signerFixture.Dir 'connector-release.preview.json') -Raw|ConvertFrom-Json
    $signedAssets=@($signed.assets)
    Assert ($signedAssets.Count -eq 3 -and @($signedAssets|Where-Object{$_.version -ceq '1.1.0-preview.11'}).Count -eq 2 -and @($signedAssets|Where-Object{$_.version -ceq '1.1.0-preview.10'}).Count -eq 1 -and @($signedAssets|Where-Object{$_.type -ceq 'Delta'}).Count -eq 1) 'actual signer must serialize canonical multi-version and full/delta entries'
    $duplicateSigner=New-Fixture '1.1.0-preview.11';[void](Add-DeltaAsset $duplicateSigner);$duplicateLine=(Get-Content (Join-Path $duplicateSigner.Dir 'RELEASES-preview'))[0];[IO.File]::WriteAllText((Join-Path $duplicateSigner.Dir 'RELEASES-preview'),"$duplicateLine`r`n$duplicateLine")
    Expect-Failure {& (Join-Path $fixtureScripts 'sign_update_feed.ps1') -PackageDirectory $duplicateSigner.Dir -Channel preview -PrivateKeyPemPath $privateFile} 'signer duplicate full/delta index entry'
    [IO.File]::WriteAllText((Join-Path $good.Dir 'old-cache.nupkg'),'preserve me')
    $sourceSnapshot=(Get-ChildItem $good.Dir -File|Sort-Object Name|ForEach-Object{"$($_.Name):$((Get-FileHash $_.FullName).Hash)"}) -join '|'
    $race=New-Fixture '1.0.0-preview.2';$raceFeed=Join-Path $root 'race-feed';$raceReplacement=[Text.Encoding]::UTF8.GetBytes('replaced-after-validation')
    $slotInputLine=(Select-String -LiteralPath $publisher -Pattern '^\$slotInputs=@\{\}$').LineNumber
    Assert ($slotInputLine -gt 0) 'publisher must retain the validated-input boundary for the deterministic race regression'
    $racePublisher=Join-Path $root 'publish_unified_feed.race.ps1';$publisherText=[IO.File]::ReadAllText($publisher);$boundary='$slotInputs=@{}'
    Assert (([regex]::Matches($publisherText,[regex]::Escape($boundary))).Count -eq 1) 'publisher race boundary must be unique'
    $raceAssetLiteral=$race.Asset.Replace("'","''");$raceBytesLiteral=[Convert]::ToBase64String($raceReplacement)
    $mutation="[IO.File]::WriteAllBytes('$raceAssetLiteral',[Convert]::FromBase64String('$raceBytesLiteral'))`r`n$boundary"
    [IO.File]::WriteAllText($racePublisher,$publisherText.Replace($boundary,$mutation),[Text.UTF8Encoding]::new($false))
    $raceFailed=$false;try { Invoke-Publish $race $raceFeed $racePublisher } catch { $raceFailed=$true; Write-Output "Race rejection: $($_.Exception.Message)" }
    Assert $raceFailed 'source replacement after validation must fail publication'
    Assert ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($race.Asset)) -ceq 'replaced-after-validation') 'deterministic race mutation must run after source validation'
    Assert (!(Test-Path (Join-Path $raceFeed 'preview/current.json')) -and !(Test-Path (Join-Path $raceFeed "preview/releases/$($race.Version)"))) 'changed source bytes must never become an activated release'
    $raceStages=@(Get-ChildItem (Join-Path $raceFeed 'preview/releases') -Directory -Force -ErrorAction SilentlyContinue|Where-Object Name -like '.stage-*')
    Assert ($raceStages.Count -eq 0) 'failed race publication must remove its temporary stage'
    Invoke-Publish $good $feed
    $pointer=Get-Content (Join-Path $feed 'preview/current.json') -Raw|ConvertFrom-Json
    Assert ($pointer.release -ceq '1.1.0-preview.11' -and @($pointer.PSObject.Properties.Name).Count -eq 2) 'pointer must match exact route contract and canonical native version'
    Assert (Test-Path (Join-Path $feed "preview/releases/$($good.Version)/$($good.Name)")) 'immutable release copy must exist'
    Assert (Test-Path (Join-Path $feed "preview/releases/$($good.Version)/$([IO.Path]::GetFileName($delta))")) 'delta asset must be included in the complete release copy'
    Assert ((Get-ChildItem (Join-Path $feed 'preview/releases') -Directory -Force|Measure-Object).Count -eq 2) 'all version package slots must be activated'
    $serverPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\connector\server')).Replace('\','/')
    $feedForPython=$feed.Replace('\','/')
    $python="import sys;sys.path.insert(0,r'$serverPath');from pathlib import Path;from unified_updates import resolve_feed_file;r=Path(r'$feedForPython');items=[('connector-release.preview.json',Path(r'$((Join-Path $good.Dir 'connector-release.preview.json').Replace('\','/'))')),('$($good.Name)',Path(r'$($good.Asset.Replace('\','/'))')),('$([IO.Path]::GetFileName($delta))',Path(r'$($delta.Replace('\','/'))')),('$([IO.Path]::GetFileName($oldAsset))',Path(r'$($oldAsset.Replace('\','/'))')),('Structura.Connector.Installer.exe',Path(r'$($good.Exe.Replace('\','/'))')),('Connector.Unified.Setup.msi',Path(r'$($good.Msi.Replace('\','/'))'))];[(lambda got,expected: (_ for _ in ()).throw(AssertionError(name)) if got.read_bytes()!=expected.read_bytes() else None)(resolve_feed_file(r,'preview',name)[0],expected) for name,expected in items];print('route integration passed for metadata, full/delta versions, exe and msi')"
    & python -c $python
    if ($LASTEXITCODE -ne 0) { throw 'Actual unified feed resolve_feed_file integration failed.' }
    Invoke-Publish $good $feed # exact idempotent retry
    $sourceAfter=(Get-ChildItem $good.Dir -File|Sort-Object Name|ForEach-Object{"$($_.Name):$((Get-FileHash $_.FullName).Hash)"}) -join '|'
    Assert ($sourceSnapshot -ceq $sourceAfter) 'publisher must not mutate source packages'

    $bad=New-Fixture '1.2.0-preview.1';$sigPath=Join-Path $bad.Dir 'connector-release.preview.json.sig';$sigBytes=[IO.File]::ReadAllBytes($sigPath);$sigBytes[0]=$sigBytes[0] -bxor 1;[IO.File]::WriteAllBytes($sigPath,$sigBytes)
    Expect-Failure {Invoke-Publish $bad $feed} 'invalid signature'
    $tamper=New-Fixture '1.3.0-preview.1';[IO.File]::AppendAllText($tamper.Asset,'tampered')
    Expect-Failure {Invoke-Publish $tamper $feed} 'asset tamper'
    $wrong=New-Fixture '1.4.0-preview.1';[IO.File]::WriteAllText((Join-Path $wrong.Dir 'RELEASES-preview'),'0000000000000000000000000000000000000000 '+$wrong.Name+' 8')
    Expect-Failure {Invoke-Publish $wrong $feed} 'wrong native index'
    $duplicateIndex=New-Fixture '1.4.1-preview.1';$duplicateDelta=Add-DeltaAsset $duplicateIndex;$line=(Get-Content (Join-Path $duplicateIndex.Dir 'RELEASES-preview'))[0];[IO.File]::WriteAllText((Join-Path $duplicateIndex.Dir 'RELEASES-preview'),"$line`r`n$line")
    Expect-Failure {Invoke-Publish $duplicateIndex $feed} 'duplicate full/delta index entry'
    $traversal=New-Fixture '1.5.0-preview.1' -assetName '../escape.nupkg'
    Expect-Failure {Invoke-Publish $traversal $feed} 'traversal asset'
    $downgrade=New-Fixture '1.0.9-preview.99'
    Expect-Failure {Invoke-Publish $downgrade $feed} 'pointer downgrade'
    $current=Get-Content (Join-Path $feed 'preview/current.json') -Raw|ConvertFrom-Json
    Assert ($current.release -ceq '1.1.0-preview.11') 'all rejected publications must leave pointer unchanged'
    Assert ((Test-Path -LiteralPath $good.Asset) -and (Test-Path -LiteralPath $good.Exe) -and (Test-Path -LiteralPath $good.Msi) -and (Test-Path (Join-Path $good.Dir 'old-cache.nupkg'))) 'old/source packages and installers must remain'
    Assert (!(Test-Path (Join-Path $feed "preview/releases/$($good.Version)/old-cache.nupkg"))) 'unlisted stale source packages must not enter release copy'
    $newer=New-Fixture '1.2.0-preview.1';Invoke-Publish $newer $feed
    Assert (Test-Path (Join-Path $feed "preview/releases/$($good.Version)/$($good.Name)")) 'publishing a new version must preserve old release packages'
    $current=Get-Content (Join-Path $feed 'preview/current.json') -Raw|ConvertFrom-Json
    Assert ($current.release -ceq '1.2.0-preview.1') 'new verified version must advance current pointer'
    Expect-Failure {Invoke-Publish $good $feed} 'late pointer downgrade'
    $current=Get-Content (Join-Path $feed 'preview/current.json') -Raw|ConvertFrom-Json
    Assert ($current.release -ceq '1.2.0-preview.1') 'late downgrade must leave current pointer unchanged'
    Write-Output 'PASS publish_unified_feed: valid, signature, tamper, wrong index, traversal, idempotent retry, downgrade and pointer invariants'
} finally { $key.Dispose(); Remove-TestRoot $root }
