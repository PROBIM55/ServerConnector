$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProj = Join-Path $root 'Connector.Desktop\Connector.Desktop.csproj'
$setupProj = Join-Path $root 'Connector.Desktop.Setup\Connector.Desktop.Setup.wixproj'
$setupBinDir = Join-Path $root 'Connector.Desktop.Setup\bin'
$setupObjDir = Join-Path $root 'Connector.Desktop.Setup\obj'
$publishDir = Join-Path $root 'publish'
$outputDir = Join-Path $root 'artifacts'
$bundledGitDir = Join-Path $root 'Connector.Desktop\tools\git'
$ensureGitScript = Join-Path $root 'scripts\ensure_bundled_git.ps1'

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE"
    }
}

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}

if (Test-Path $outputDir) {
    Remove-Item -Path $outputDir -Recurse -Force
}

if (Test-Path $setupBinDir) {
    Remove-Item -Path $setupBinDir -Recurse -Force
}

if (Test-Path $setupObjDir) {
    Remove-Item -Path $setupObjDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $ensureGitScript -TargetDir $bundledGitDir

Invoke-ExternalCommand -Description 'dotnet publish' -Command {
    dotnet publish $appProj -c Release -r win-x64 -p:PublishSingleFile=false -p:SelfContained=true -o $publishDir
}

Invoke-ExternalCommand -Description 'dotnet build' -Command {
    dotnet build $setupProj -c Release -t:Rebuild -o $outputDir
}

$msiFiles = Get-ChildItem $outputDir -Filter *.msi
if (-not $msiFiles) {
    throw "No MSI files were produced in $outputDir"
}

$now = Get-Date
foreach ($msi in $msiFiles) {
    $msi.LastWriteTime = $now
}

$msiFiles | Select-Object FullName, Length, LastWriteTime
