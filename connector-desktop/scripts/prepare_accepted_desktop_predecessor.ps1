[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupExecutable,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Hosted predecessor extraction only.'}
$expected='2EA234DEA9238F59BC4DEF852D9B0EF72872612706ED70891B53A6B2AECFF769'
if((Get-Item -LiteralPath $SetupExecutable).Length -ne 416519356 -or (Get-FileHash -LiteralPath $SetupExecutable).Hash -cne $expected){throw 'Accepted predecessor EXE identity mismatch.'}
$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Predecessor output must be new.'}
New-Item -ItemType Directory -Path $out|Out-Null
$tool=Join-Path $out 'tool'
dotnet tool install wix --version 6.0.2 --tool-path $tool
if($LASTEXITCODE -ne 0){throw 'Pinned WiX extraction tool unavailable.'}
& (Join-Path $tool 'wix.exe') burn extract $SetupExecutable -out (Join-Path $out 'payload') -outba (Join-Path $out 'bootstrapper')
if($LASTEXITCODE -ne 0){throw 'Predecessor extraction failed.'}
$installer=New-Object -ComObject WindowsInstaller.Installer
$inventory=[Collections.Generic.List[object]]::new()
# Burn containers can name embedded payloads by their IDs rather than their
# original extensions. Identify MSI compound files by content, then by the
# package's own UpgradeCode; filenames are not an identity boundary.
$matches=@(Get-ChildItem -LiteralPath (Join-Path $out 'payload') -Recurse -File -Force|Where-Object {
    $header=New-Object byte[] 8;$stream=[IO.File]::OpenRead($_.FullName)
    try{$read=$stream.Read($header,0,8)}finally{$stream.Dispose()}
    if($read -ne 8 -or [BitConverter]::ToString($header) -cne 'D0-CF-11-E0-A1-B1-1A-E1'){return $false}
    $db=$installer.OpenDatabase($_.FullName,0)
    $view=$db.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''UpgradeCode''');$view.Execute();$row=$view.Fetch();$value=if($row){$row.StringData(1)}else{''};$view.Close()
    $inventory.Add(@{payload=[IO.Path]::GetRelativePath($out,$_.FullName);bytes=$_.Length;upgradeCode=$value})
    $value -ieq '{67B43FCD-4FE8-4D29-9F64-6F190FCD6E51}'
})
$inventory.ToArray()|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $out 'msi-inventory.json') -Encoding utf8
if($matches.Count -ne 1){throw 'Expected exactly one accepted application MSI.'}
$applicationMsi=Join-Path $out 'accepted-application.msi'
Copy-Item -LiteralPath $matches[0].FullName -Destination $applicationMsi
if((Get-FileHash -LiteralPath $applicationMsi).Hash -cne (Get-FileHash -LiteralPath $matches[0].FullName).Hash){throw 'Extracted application MSI copy changed.'}
$admin=Join-Path $out 'administrative-image'
$log=Join-Path $out 'extract.log'
$process=Start-Process -FilePath msiexec.exe -ArgumentList @('/a',('"'+$applicationMsi+'"'),'/qn',('TARGETDIR="'+$admin+'"'),'/l*v',('"'+$log+'"')) -WindowStyle Hidden -PassThru
try{if(!$process.WaitForExit(120000)){ $process.Kill($true);throw 'Predecessor administrative extraction timed out.' };if($process.ExitCode -ne 0){throw 'Predecessor administrative extraction failed.'}}finally{$process.Dispose()}
$desktops=@(Get-ChildItem -LiteralPath $admin -Recurse -File -Force -Filter 'Connector.Desktop.exe')
if($desktops.Count -ne 1){throw 'Predecessor desktop image is ambiguous.'}
$manifest=Join-Path $out 'accepted-1.1.9-image.json'
& (Join-Path $PSScriptRoot 'write_desktop_image_manifest.ps1') -Directory $desktops[0].DirectoryName -OutputFile $manifest -Version '1.1.9'
$body=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
$body|Add-Member -NotePropertyName predecessorExeSha256 -NotePropertyValue $expected.ToLowerInvariant()
$body|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $manifest -Encoding utf8
