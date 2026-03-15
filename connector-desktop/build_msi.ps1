$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj = Join-Path $root 'Connector.Desktop\Connector.Desktop.csproj'
$setupProj = Join-Path $root 'Connector.Desktop.Setup\Connector.Desktop.Setup.wixproj'
$publishDir = Join-Path $root 'publish'
$outputDir = Join-Path $root 'artifacts'
$bundledGitDir = Join-Path $root 'Connector.Desktop\tools\git'
$ensureGitScript = Join-Path $root 'scripts\ensure_bundled_git.ps1'

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $ensureGitScript -TargetDir $bundledGitDir

dotnet publish $appProj -c Release -r win-x64 -p:PublishSingleFile=false -p:SelfContained=true -o $publishDir
dotnet build $setupProj -c Release -o $outputDir

$msiFiles = Get-ChildItem $outputDir -Filter *.msi
$now = Get-Date
foreach ($msi in $msiFiles) {
    $msi.LastWriteTime = $now
}

$msiFiles | Select-Object FullName, Length, LastWriteTime
