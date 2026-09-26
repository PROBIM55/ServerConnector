param(
    # Publish folder: the DLLs go next to Assimp64.dll and Connector.Desktop.exe.
    [Parameter(Mandatory = $true)]
    [string]$TargetDir,
    [string[]]$Files = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
)

# App-local Visual C++ runtime for Assimp64.dll (C2c-fix). Microsoft allows redistributing the files listed in
# redist.txt of Visual Studio next to the application. They are taken only from
# VC\Redist\MSVC\<version>\x64\Microsoft.VC14x.CRT of an installed Visual Studio / Build Tools (found through
# vswhere), never from System32. A missing file is a build error. A file of the same name already in $TargetDir
# (for example from the .NET runtime pack) is replaced only by a newer file version.

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "vswhere.exe was not found at ${vswhere}: install Visual Studio or Build Tools with the C++ workload"
}
$installs = @(& $vswhere -all -products * -format value -property installationPath | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { throw "vswhere failed with exit code $LASTEXITCODE" }

# Candidate CRT folders from all installations, newest redist version first.
$candidates = @()
foreach ($vs in $installs) {
    $redist = Join-Path $vs 'VC\Redist\MSVC'
    if (-not (Test-Path -LiteralPath $redist)) { continue }
    foreach ($v in Get-ChildItem -LiteralPath $redist -Directory | Where-Object { $_.Name -match '^\d+(\.\d+)+$' }) {
        $x64 = Join-Path $v.FullName 'x64'
        if (-not (Test-Path -LiteralPath $x64)) { continue }
        foreach ($crt in Get-ChildItem -LiteralPath $x64 -Directory -Filter 'Microsoft.VC14*.CRT') {
            $candidates += [pscustomobject]@{ Version = [version]$v.Name; Path = $crt.FullName }
        }
    }
}
$crtDir = $null
foreach ($c in ($candidates | Sort-Object Version -Descending)) {
    if (@($Files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $c.Path $_) -PathType Leaf) }).Count -eq 0) {
        $crtDir = $c.Path
        break
    }
}
if (-not $crtDir) {
    $looked = if ($installs) { $installs -join '; ' } else { 'no Visual Studio installations' }
    throw "Visual C++ x64 CRT ($($Files -join ', ')) was not found under VC\Redist\MSVC\<version>\x64\Microsoft.VC14x.CRT ($looked)"
}
$systemRoot = [System.IO.Path]::GetFullPath($env:SystemRoot).TrimEnd('\') + '\'
if ([System.IO.Path]::GetFullPath($crtDir).StartsWith($systemRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Visual C++ CRT source $crtDir is inside $env:SystemRoot; only Visual Studio redist folders are allowed"
}
Write-Host "Visual C++ runtime source: $crtDir"

function Get-FileVersionOf([string]$Path) {
    $vi = (Get-Item -LiteralPath $Path).VersionInfo
    return [version]::new($vi.FileMajorPart, $vi.FileMinorPart, $vi.FileBuildPart, $vi.FilePrivatePart)
}

foreach ($name in $Files) {
    $src = Join-Path $crtDir $name
    $dst = Join-Path $TargetDir $name
    $srcVersion = Get-FileVersionOf $src
    $srcSha = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.ToLowerInvariant()
    $existing = 'no file of this name in publish folder before copy'
    if (Test-Path -LiteralPath $dst) {
        $dstVersion = Get-FileVersionOf $dst
        if ($dstVersion -ge $srcVersion) {
            Write-Host "  $name $dstVersion already in publish folder (not older than redist $srcVersion): kept"
            continue
        }
        $existing = "replaced older $dstVersion from publish folder"
    }
    Copy-Item -LiteralPath $src -Destination $dst -Force
    Write-Host "  $name $srcVersion size=$((Get-Item -LiteralPath $dst).Length) sha256=$srcSha ($existing)"
}
