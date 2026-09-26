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
$publishGitDir = Join-Path $publishDir 'tools\git'
$publishGitBundle = Join-Path $publishDir 'git-bundle.zip'
$bundledAwgDir = Join-Path $root 'Connector.Desktop\tools\awg'
$ensureAwgScript = Join-Path $root 'scripts\ensure_bundled_amneziawg.ps1'
$bundledGltfpackDir = Join-Path $root 'Connector.Desktop\tools\gltfpack'
$ensureGltfpackScript = Join-Path $root 'scripts\ensure_bundled_gltfpack.ps1'
$copyVcRuntimeScript = Join-Path $root 'scripts\copy_vc_runtime.ps1'
$checkNativeImportsScript = Join-Path $root 'scripts\check_native_imports.ps1'
# Pinned gltfpack.exe (meshoptimizer v1.2); must match scripts/ensure_bundled_gltfpack.ps1.
$gltfpackExeSha256 = 'ff64f45e84aac9a1f58880e40934b3f29277413e2d0b3ed257322261ec021d2b'

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
# VPN is enabled in production, so a release without the bundled client would be incomplete.
# The preparation script uses a pinned version and verifies SHA-256 before extraction.
& $ensureAwgScript -TargetDir $bundledAwgDir
# The AGR converter (Converter tab) does not work without gltfpack: any failure here is a build error.
& $ensureGltfpackScript -TargetDir $bundledGltfpackDir

Invoke-ExternalCommand -Description 'dotnet publish' -Command {
    dotnet publish $appProj -c Release -r win-x64 -p:PublishSingleFile=false -p:SelfContained=true -o $publishDir
}

if (-not (Test-Path $publishGitDir)) {
    throw "Bundled git was not published to $publishGitDir"
}

if (Test-Path $publishGitBundle) {
    Remove-Item -Path $publishGitBundle -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $publishGitDir,
    $publishGitBundle,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Remove-Item -Path $publishGitDir -Recurse -Force
$publishToolsDir = Join-Path $publishDir 'tools'
if ((Test-Path $publishToolsDir) -and -not (Get-ChildItem $publishToolsDir -Force | Select-Object -First 1)) {
    Remove-Item -Path $publishToolsDir -Force
}

# gltfpack.exe goes next to Connector.Desktop.exe: AgrGltfpackPartConverter.DefaultGltfpackPath is
# AppContext.BaseDirectory\gltfpack.exe.
$publishGltfpack = Join-Path $publishDir 'gltfpack.exe'
Copy-Item -LiteralPath (Join-Path $bundledGltfpackDir 'gltfpack.exe') -Destination $publishGltfpack -Force
$publishedGltfpackSha256 = (Get-FileHash -LiteralPath $publishGltfpack -Algorithm SHA256).Hash.ToLowerInvariant()
if ($publishedGltfpackSha256 -ne $gltfpackExeSha256) {
    throw "Published gltfpack.exe SHA-256 mismatch: expected $gltfpackExeSha256, got $publishedGltfpackSha256"
}

# Everything else the converter needs must be in the publish folder, otherwise the MSI is incomplete.
foreach ($name in @('Connector.AgrConversion.dll', 'Assimp64.dll', 'Silk.NET.Assimp.dll', 'Silk.NET.Core.dll',
                    'SharpGLTF.Core.dll', 'SharpGLTF.Toolkit.dll', 'THIRD_PARTY_NOTICES.txt')) {
    if (-not (Test-Path (Join-Path $publishDir $name))) {
        throw "AGR converter file $name was not published to $publishDir"
    }
}

# Assimp64.dll is built with MSVC and imports msvcp140.dll, vcruntime140.dll and vcruntime140_1.dll. They go next to it
# (app-local, redist.txt of Visual Studio): a PC without the Visual C++ 2015-2022 x64 runtime could not load Assimp.
& $copyVcRuntimeScript -TargetDir $publishDir
# Every DLL imported by the converter's native files must be in the publish folder or be a Windows system DLL.
& $checkNativeImportsScript -Dir $publishDir -Files @('Assimp64.dll', 'gltfpack.exe')

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
