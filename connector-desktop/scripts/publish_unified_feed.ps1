#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]*$')][string]$Channel,
    [Parameter(Mandatory)][string]$FeedRoot,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$MachineMsiPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedInstallerSha256,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedMachineMsiSha256,
    [string]$PublicKeyPemPath = (Join-Path $PSScriptRoot '..\Connector.Desktop\Assets\update-public-key.pem')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$appId = 'Structura.Connector.Desktop'
$feed = [IO.Path]::GetFullPath($FeedRoot)
$source = [IO.Path]::GetFullPath($PackageDirectory)
$versionPattern = '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$'

function Assert-PathTree([string]$path, [bool]$mustExist) {
    $p = [IO.Path]::GetFullPath($path)
    if ($mustExist -and !(Test-Path -LiteralPath $p)) { throw "Required path missing: $p" }
    $probe = $p
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            $item = Get-Item -LiteralPath $probe -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse path rejected: $probe" }
        }
        $parent = [IO.Directory]::GetParent($probe)
        if (!$parent -or $parent.FullName -eq $probe) { break }
        $probe = $parent.FullName
    }
}
function Assert-Leaf([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name) -or $name -in '.', '..' -or $name -match '[\\/:?#%]' -or
        [IO.Path]::IsPathRooted($name) -or $name -ne [IO.Path]::GetFileName($name)) { throw "Unsafe feed filename: $name" }
}
function Assert-NoDuplicates([System.Text.Json.JsonElement]$node) {
    if ($node.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($p in $node.EnumerateObject()) { if (!$seen.Add($p.Name)) { throw "Duplicate JSON property: $($p.Name)" }; Assert-NoDuplicates $p.Value }
    } elseif ($node.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) { foreach ($x in $node.EnumerateArray()) { Assert-NoDuplicates $x } }
}
function Assert-Properties([System.Text.Json.JsonElement]$node,[string[]]$expected) {
    if ($node.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Unexpected JSON value type.' }
    $actual=@($node.EnumerateObject() | ForEach-Object Name)
    if ($actual.Count -ne $expected.Count -or @($expected | Where-Object { $_ -cnotin $actual }).Count) { throw 'Unexpected or missing JSON property.' }
}
function Get-Sha([string]$p, [string]$algo='SHA256') { (Get-FileHash -LiteralPath $p -Algorithm $algo).Hash.ToUpperInvariant() }
function Get-FileDigests([string]$path) {
    $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $sha256=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $sha1=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    try {
        $buffer=[byte[]]::new(65536); $length=0L
        while (($read=$stream.Read($buffer,0,$buffer.Length)) -gt 0) { $sha256.AppendData($buffer,0,$read); $sha1.AppendData($buffer,0,$read); $length+=$read }
        return [pscustomobject]@{Length=$length;SHA256=[Convert]::ToHexString($sha256.GetHashAndReset());SHA1=[Convert]::ToHexString($sha1.GetHashAndReset())}
    } finally { $stream.Dispose(); $sha256.Dispose(); $sha1.Dispose() }
}
function New-BytesRecord([string]$name,[string]$path,[byte[]]$bytes) {
    return [pscustomobject]@{Name=$name;Path=$path;Bytes=$bytes;Length=[long]$bytes.LongLength;SHA256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))}
}
function Remove-OwnedStage([string]$path,[string]$parent) {
    $base=[IO.Path]::GetFullPath($parent).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $full=[IO.Path]::GetFullPath($path)
    if (!$full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($full) -notmatch '^\.stage-[0-9a-f]{32}$') { throw 'Refusing cleanup outside this publisher run staging directory.' }
    if (Test-Path -LiteralPath $full) { Assert-PathTree $full $true; foreach($child in Get-ChildItem -LiteralPath $full -Force -Recurse){if(($child.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Refusing to remove a staging tree containing reparse points.'}}; Remove-Item -LiteralPath $full -Recurse -Force }
}
function Compare-Version([string]$a, [string]$b) {
    $pa = [regex]::Match($a, '^(\d+)\.(\d+)\.(\d+)(?:[-+](.*))?$')
    $pb = [regex]::Match($b, '^(\d+)\.(\d+)\.(\d+)(?:[-+](.*))?$')
    if (!$pa.Success -or !$pb.Success) { throw 'Unsupported release version.' }
    for ($i=1; $i -le 3; $i++) { $c=[long]$pa.Groups[$i].Value - [long]$pb.Groups[$i].Value; if ($c) { return [Math]::Sign($c) } }
    $ra=$pa.Groups[4].Value; $rb=$pb.Groups[4].Value
    if (!$ra -and !$rb) { return 0 }; if (!$ra) { return 1 }; if (!$rb) { return -1 }
    $ia=$ra.Split('.'); $ib=$rb.Split('.')
    for ($i=0; $i -lt [Math]::Max($ia.Length,$ib.Length); $i++) {
        if ($i -ge $ia.Length) { return -1 }; if ($i -ge $ib.Length) { return 1 }
        $na=0L; $nb=0L; $an=[long]::TryParse($ia[$i],[ref]$na); $bn=[long]::TryParse($ib[$i],[ref]$nb)
        if ($an -and $bn) { if ($na -ne $nb) { return [Math]::Sign($na-$nb) } }
        elseif ($an) { return -1 } elseif ($bn) { return 1 }
        else { $c=[string]::CompareOrdinal($ia[$i],$ib[$i]); if ($c) { return [Math]::Sign($c) } }
    }
    return 0
}

Assert-PathTree $source $true; Assert-PathTree $feed $false
Assert-PathTree $InstallerPath $true; Assert-PathTree $MachineMsiPath $true; Assert-PathTree $PublicKeyPemPath $true
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw 'PackageDirectory must be a directory.' }
$installer=[IO.Path]::GetFullPath($InstallerPath); $msi=[IO.Path]::GetFullPath($MachineMsiPath)
if ([IO.Path]::GetExtension($installer) -ine '.exe' -or [IO.Path]::GetExtension($msi) -ine '.msi') { throw 'Trusted installer inputs must be an EXE and machine MSI.' }
$installerRecord=Get-FileDigests $installer; $msiRecord=Get-FileDigests $msi
if ($installerRecord.SHA256 -cne $ExpectedInstallerSha256.ToUpperInvariant() -or
    $msiRecord.SHA256 -cne $ExpectedMachineMsiSha256.ToUpperInvariant()) { throw 'Installer or machine MSI does not match the trusted build hash.' }
$manifestName="connector-release.$Channel.json"; $sigName="$manifestName.sig"; $catalogName="releases.$Channel.json"; $indexName="RELEASES-$Channel"
$manifestPath=Join-Path $source $manifestName; $sigPath=Join-Path $source $sigName; $catalogPath=Join-Path $source $catalogName; $indexPath=Join-Path $source $indexName
foreach ($p in @($manifestPath,$sigPath,$catalogPath,$indexPath)) { Assert-PathTree $p $true; if (!(Test-Path -LiteralPath $p -PathType Leaf)) { throw "Required feed file missing: $p" } }
$manifestBytes=[IO.File]::ReadAllBytes($manifestPath); $sig=[IO.File]::ReadAllBytes($sigPath)
$manifestRecord=New-BytesRecord $manifestName $manifestPath $manifestBytes
$signatureRecord=New-BytesRecord $sigName $sigPath $sig
$key=[Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyPemPath)))
    if ($key.KeySize -ne 256 -or $sig.Length -ne 64 -or !$key.VerifyData($manifestBytes,$sig,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)) { throw 'Signed release manifest signature is invalid.' }
} finally { $key.Dispose() }
$md=[System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($manifestBytes)); try { Assert-NoDuplicates $md.RootElement; $m=$md.RootElement; Assert-Properties $m @('schemaVersion','applicationId','channel','assets') }
catch { $md.Dispose(); throw }
if ($m.ValueKind -ne 'Object' -or $m.GetProperty('schemaVersion').GetInt32() -ne 1 -or $m.GetProperty('applicationId').GetString() -cne $appId -or $m.GetProperty('channel').GetString() -cne $Channel) { $md.Dispose(); throw 'Signed manifest identity/schema mismatch.' }
$assets=@(); $assetNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $fullVersions=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($a in $m.GetProperty('assets').EnumerateArray()) {
    Assert-Properties $a @('fileName','version','type','size','sha256')
    $n=$a.GetProperty('fileName').GetString(); Assert-Leaf $n
    $v=$a.GetProperty('version').GetString(); $t=$a.GetProperty('type').GetString(); $size=$a.GetProperty('size').GetInt64(); $hash=$a.GetProperty('sha256').GetString()
    if (!$assetNames.Add($n) -or $v -notmatch $versionPattern -or $t -notin 'Full','Delta' -or $size -le 0 -or $hash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid or duplicate signed asset.' }
    $nameRx='^'+[regex]::Escape($appId)+'-'+[regex]::Escape($v)+'-'+[regex]::Escape($Channel)+'-(?<kind>full|delta(?:\.\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)?)\.nupkg$'
    $nameMatch=[regex]::Match($n,$nameRx,[Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (!$nameMatch.Success -or (($t -ceq 'Full') -ne ($nameMatch.Groups['kind'].Value -match '^full$'))) { throw "Asset filename does not match channel/version/type: $n" }
    if ($t -ceq 'Full' -and !$fullVersions.Add($v)) { throw "Duplicate full package version: $v" }
    $p=Join-Path $source $n; Assert-PathTree $p $true
    if (!(Test-Path -LiteralPath $p -PathType Leaf)) { throw "Signed asset verification failed: $n" }
    $assetRecord=Get-FileDigests $p
    if ($assetRecord.Length -ne $size -or $assetRecord.SHA256 -cne $hash.ToUpperInvariant()) { throw "Signed asset verification failed: $n" }
    $assets+=,[ordered]@{ FileName=$n; Version=$v; Type=$t; Size=$size; Length=$assetRecord.Length; SHA256=$assetRecord.SHA256; SHA1=$assetRecord.SHA1 }
}
if (!$assets.Count) { throw 'Signed manifest has no assets.' }
foreach ($v in @($assets.Version | Select-Object -Unique)) { if (!$fullVersions.Contains($v)) { throw "Release version has deltas but no full package: $v" } }
$version=$null
foreach ($v in $fullVersions) { if ($null -eq $version -or (Compare-Version $v $version) -gt 0) { $version=$v } }
$catalogBytes=[IO.File]::ReadAllBytes($catalogPath); $catalogRecord=New-BytesRecord $catalogName $catalogPath $catalogBytes
$catDoc=[System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new($catalogBytes)); Assert-NoDuplicates $catDoc.RootElement; Assert-Properties $catDoc.RootElement @('Assets'); $cat=@($catDoc.RootElement.GetProperty('Assets').EnumerateArray() | ForEach-Object { $_ })
if (@($cat).Count -ne $assets.Count) { throw 'Native catalog asset count differs from signed manifest.' }
foreach ($s in $assets) {
    $matches=@($cat | Where-Object { $_.GetProperty('FileName').GetString() -ceq $s.FileName })
    if ($matches.Count -ne 1) { throw "Native catalog differs from signed manifest: $($s.FileName)" }; $c=$matches[0]
    Assert-Properties $c @('PackageId','Version','Type','FileName','SHA1','SHA256','Size')
    if ($c.GetProperty('PackageId').GetString() -cne $appId -or $c.GetProperty('Version').GetString() -cne $s.Version -or $c.GetProperty('Type').GetString() -cne $s.Type -or
        $c.GetProperty('SHA256').GetString() -cne $s.SHA256 -or $c.GetProperty('Size').GetInt64() -ne $s.Size) { throw "Native catalog differs from signed manifest: $($s.FileName)" }
    if ($c.GetProperty('SHA1').GetString() -ine $s.SHA1) { throw "Native catalog SHA1 differs from package: $($s.FileName)" }
}
$indexBytes=[IO.File]::ReadAllBytes($indexPath); $indexRecord=New-BytesRecord $indexName $indexPath $indexBytes
$indexMemory=[IO.MemoryStream]::new($indexBytes,$false); $indexReader=[IO.StreamReader]::new($indexMemory,$true)
try { $indexLines=[Collections.Generic.List[string]]::new(); while (($indexLine=$indexReader.ReadLine()) -ne $null) { $indexLines.Add($indexLine) }; $indexLines=$indexLines.ToArray() } finally { $indexReader.Dispose(); $indexMemory.Dispose() }
if ($indexLines.Count -ne $assets.Count) { throw 'Velopack RELEASES index asset count mismatch.' }
$indexedNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in $indexLines) {
    $parts=$line -split ' '; if ($parts.Count -ne 3) { throw 'Malformed Velopack RELEASES line.' }; if (!$indexedNames.Add($parts[1])) { throw 'Duplicate package in Velopack RELEASES index.' }; $found=@($assets | Where-Object FileName -CEQ $parts[1])
    if ($found.Count -ne 1 -or $parts[0] -cne $found[0].SHA1 -or $parts[2] -cne [string]$found[0].Size) { throw 'Velopack RELEASES index does not match exact asset SHA1/size.' }
}
$catDoc.Dispose(); $md.Dispose()
$installerName='Structura.Connector.Installer.exe'; $msiName='Connector.Unified.Setup.msi'
$slotInputs=@{}
foreach ($a in $assets) { if (!$slotInputs.ContainsKey($a.Version)) { $slotInputs[$a.Version]=[Collections.Generic.List[object]]::new() }; $slotInputs[$a.Version].Add([pscustomobject]@{Name=$a.FileName;Path=(Join-Path $source $a.FileName);Length=$a.Length;SHA256=$a.SHA256;Bytes=$null}) }
$metadataRecords=@($manifestRecord,$signatureRecord,$catalogRecord,$indexRecord)
foreach ($record in $metadataRecords) { $slotInputs[$version].Add($record) }
$slotInputs[$version].Add([pscustomobject]@{Name=$installerName;Path=$installer;Length=$installerRecord.Length;SHA256=$installerRecord.SHA256;Bytes=$null})
$slotInputs[$version].Add([pscustomobject]@{Name=$msiName;Path=$msi;Length=$msiRecord.Length;SHA256=$msiRecord.SHA256;Bytes=$null})
$slotPayloads=@{}
foreach ($slotVersion in $slotInputs.Keys) {
    $slotFiles=[Collections.Generic.List[object]]::new();$slotNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $slotInputs[$slotVersion]) { if (!$slotNames.Add($item.Name)) { throw "Duplicate staged filename: $($item.Name)" }; $slotFiles.Add([pscustomobject]@{Name=$item.Name;Path=$item.Path;Length=$item.Length;SHA256=$item.SHA256;Bytes=$item.Bytes}) }
    $slotPayloads[$slotVersion]=$slotFiles
}
$channelRoot=Join-Path $feed $Channel; $releases=Join-Path $channelRoot 'releases'
[IO.Directory]::CreateDirectory($releases) | Out-Null
$lockPath=Join-Path $channelRoot '.publisher.lock'; $lockStream=$null
try {
    Assert-PathTree $releases $true
    if (Test-Path -LiteralPath $lockPath) { Assert-PathTree $lockPath $true }
    $lockStream=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    Assert-PathTree $channelRoot $true
    $currentPath=Join-Path $channelRoot 'current.json'; $prior=$null
    if (Test-Path -LiteralPath $currentPath) { Assert-PathTree $currentPath $true }
    if (Test-Path -LiteralPath $currentPath -PathType Leaf) {
        $priorDoc=[System.Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]::new([IO.File]::ReadAllBytes($currentPath)))
        try { Assert-NoDuplicates $priorDoc.RootElement; Assert-Properties $priorDoc.RootElement @('schemaVersion','release'); $priorVersion=$priorDoc.RootElement.GetProperty('release').GetString(); if ($priorDoc.RootElement.GetProperty('schemaVersion').GetInt32() -ne 1 -or $priorVersion -notmatch $versionPattern) { throw 'Existing current pointer is invalid.' } }
        finally { $priorDoc.Dispose() }
        if ((Compare-Version $version $priorVersion) -lt 0) { throw 'Refusing to downgrade current release pointer.' }
    } elseif (Test-Path -LiteralPath $currentPath) { throw 'current.json must be a regular file.' }
    $prepared=[Collections.Generic.List[object]]::new()
    try {
        foreach ($slotVersion in @($slotPayloads.Keys | Sort-Object { $_ })) {
            $slotTarget=Join-Path $releases $slotVersion; $payload=$slotPayloads[$slotVersion]
            if (Test-Path -LiteralPath $slotTarget) {
                Assert-PathTree $slotTarget $true
                if ($slotVersion -ceq $version) {
                    $existing=@(Get-ChildItem -LiteralPath $slotTarget -Force)
                    if ($existing.Count -ne $payload.Count) { throw 'Immutable active release version already exists with different contents.' }
                }
                foreach ($f in $payload) { $p=Join-Path $slotTarget $f.Name; $existingItem=Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; if (!$existingItem -or ($existingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $existingItem.PSIsContainer -or $existingItem.Length -ne $f.Length -or (Get-Sha $p) -cne $f.SHA256) { throw "Immutable release package/metadata differs: $($f.Name)" } }
            } else {
                $stage=Join-Path $releases ('.stage-'+[guid]::NewGuid().ToString('N')); [IO.Directory]::CreateDirectory($stage) | Out-Null
                $prepared.Add([pscustomobject]@{Stage=$stage;Target=$slotTarget})
                foreach ($f in $payload) {
                    $stagedPath=Join-Path $stage $f.Name
                    if ($null -ne $f.Bytes) { [IO.File]::WriteAllBytes($stagedPath,$f.Bytes) } else { [IO.File]::Copy($f.Path,$stagedPath) }
                    $stagedRecord=Get-FileDigests $stagedPath
                    if ($stagedRecord.Length -ne $f.Length -or $stagedRecord.SHA256 -cne $f.SHA256) { throw "Staged copy verification failed: $($f.Name)" }
                }
            }
        }
    } catch { foreach ($p in $prepared) { Remove-OwnedStage $p.Stage $releases }; throw }
    try { foreach ($p in $prepared) { [IO.Directory]::Move($p.Stage,$p.Target) } }
    catch { foreach ($p in $prepared) { Remove-OwnedStage $p.Stage $releases }; throw }
    $pointer=[ordered]@{schemaVersion=1;release=$version}
    $pointerBytes=[Text.UTF8Encoding]::new($false).GetBytes(($pointer | ConvertTo-Json -Compress))
    $temp=Join-Path $channelRoot ('.current-'+[guid]::NewGuid().ToString('N')+'.tmp')
    try { [IO.File]::WriteAllBytes($temp,$pointerBytes); [IO.File]::Move($temp,$currentPath,$true) } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
} finally { if ($lockStream) { $lockStream.Dispose() } }
Write-Host "Published $Channel $version to $(Join-Path $releases $version)"
