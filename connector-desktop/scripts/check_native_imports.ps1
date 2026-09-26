param(
    # Folder with the application files (publish folder or an administrative MSI extraction).
    [Parameter(Mandatory = $true)]
    [string]$Dir,
    # Native files of the AGR converter whose import closure must be complete.
    [string[]]$Files = @('Assimp64.dll', 'gltfpack.exe'),
    # Auto: dumpbin found through vswhere, otherwise the PowerShell PE reader below.
    [ValidateSet('Auto', 'Dumpbin', 'PowerShell')]
    [string]$Parser = 'Auto'
)

# Import closure of the converter's native files. Every imported DLL (normal and delay-load) must be either next to
# the file in $Dir (then its own imports are checked too) or a Windows system DLL: api-ms-win-* / ext-ms-* API sets or
# one of the names in $systemDlls (KnownDLLs and other inbox System32 libraries). Anything else is a build error:
# on a PC without that DLL the converter cannot load (C2c-fix: Assimp64.dll needs the Visual C++ runtime).
# Files next to the application must be x64 PE images.

$ErrorActionPreference = 'Stop'

$systemDlls = @(
    # KnownDLLs (HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs, Windows 10/11) and ntdll/kernelbase.
    'advapi32', 'clbcatq', 'combase', 'comdlg32', 'coml2', 'gdi32', 'gdiplus', 'imagehlp', 'imm32', 'kernel32',
    'kernelbase', 'msctf', 'msvcrt', 'normaliz', 'nsi', 'ntdll', 'ole32', 'oleaut32', 'psapi', 'rpcrt4', 'sechost',
    'setupapi', 'shcore', 'shell32', 'shlwapi', 'user32', 'wldap32', 'ws2_32',
    # Other inbox System32 libraries present on every supported Windows.
    'bcrypt', 'cfgmgr32', 'comctl32', 'crypt32', 'dbghelp', 'dwmapi', 'iphlpapi', 'ncrypt', 'netapi32', 'powrprof',
    'secur32', 'userenv', 'uxtheme', 'version', 'winhttp', 'wininet', 'winmm', 'wintrust'
)

function Test-SystemDll([string]$Name) {
    $n = $Name.ToLowerInvariant()
    if ($n.StartsWith('api-ms-win-') -or $n.StartsWith('ext-ms-')) { return $true }
    return $systemDlls -contains [System.IO.Path]::GetFileNameWithoutExtension($n)
}

function Get-VsInstallPaths {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { return @() }
    $paths = & $vswhere -all -products * -format value -property installationPath
    if ($LASTEXITCODE -ne 0) { return @() }
    return @($paths | Where-Object { $_ })
}

function Find-Dumpbin {
    foreach ($vs in Get-VsInstallPaths) {
        $tools = Join-Path $vs 'VC\Tools\MSVC'
        if (-not (Test-Path -LiteralPath $tools)) { continue }
        $versions = Get-ChildItem -LiteralPath $tools -Directory |
            Where-Object { $_.Name -match '^\d+(\.\d+)+$' } |
            Sort-Object { [version]$_.Name } -Descending
        foreach ($v in $versions) {
            $exe = Join-Path $v.FullName 'bin\Hostx64\x64\dumpbin.exe'
            if (Test-Path -LiteralPath $exe) { return $exe }
        }
    }
    return $null
}

# --- Small PE reader: machine type, import and delay-import directories. ---

function Get-PeLayout([byte[]]$Bytes, [string]$Path) {
    if ($Bytes.Length -lt 0x40 -or [BitConverter]::ToUInt16($Bytes, 0) -ne 0x5A4D) { throw "Not a PE file (no MZ): $Path" }
    $pe = [BitConverter]::ToInt32($Bytes, 0x3C)
    if ($pe -lt 0 -or $pe + 24 -gt $Bytes.Length -or [BitConverter]::ToUInt32($Bytes, $pe) -ne 0x00004550) {
        throw "Not a PE file (no PE signature): $Path"
    }
    $coff = $pe + 4
    $machine = [BitConverter]::ToUInt16($Bytes, $coff)
    $sectionCount = [BitConverter]::ToUInt16($Bytes, $coff + 2)
    $optionalSize = [BitConverter]::ToUInt16($Bytes, $coff + 16)
    $opt = $coff + 20
    $magic = [BitConverter]::ToUInt16($Bytes, $opt)
    if ($magic -eq 0x20B) {
        $imageBase = [BitConverter]::ToUInt64($Bytes, $opt + 24); $dirs = $opt + 112
    } elseif ($magic -eq 0x10B) {
        $imageBase = [uint64][BitConverter]::ToUInt32($Bytes, $opt + 28); $dirs = $opt + 96
    } else {
        throw "Unknown optional header magic 0x$('{0:X}' -f $magic): $Path"
    }
    $dirCount = [BitConverter]::ToUInt32($Bytes, $dirs - 4)
    $sections = @()
    $s = $opt + $optionalSize
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $o = $s + 40 * $i
        $sections += [pscustomobject]@{
            Va = [BitConverter]::ToUInt32($Bytes, $o + 12)
            VirtualSize = [BitConverter]::ToUInt32($Bytes, $o + 8)
            RawSize = [BitConverter]::ToUInt32($Bytes, $o + 16)
            RawPtr = [BitConverter]::ToUInt32($Bytes, $o + 20)
        }
    }
    return [pscustomobject]@{ Machine = $machine; ImageBase = $imageBase; Dirs = $dirs; DirCount = $dirCount; Sections = $sections }
}

function ConvertTo-FileOffset($Layout, [uint64]$Rva, [string]$Path) {
    foreach ($sec in $Layout.Sections) {
        $size = [Math]::Max($sec.VirtualSize, $sec.RawSize)
        if ($Rva -ge $sec.Va -and $Rva -lt $sec.Va + $size) { return [int]($Rva - $sec.Va + $sec.RawPtr) }
    }
    throw "RVA 0x$('{0:X}' -f $Rva) is outside all sections: $Path"
}

function Read-AsciiZ([byte[]]$Bytes, [int]$Offset) {
    $end = $Offset
    while ($end -lt $Bytes.Length -and $Bytes[$end] -ne 0) { $end++ }
    return [System.Text.Encoding]::ASCII.GetString($Bytes, $Offset, $end - $Offset)
}

function Get-PeMachine([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return (Get-PeLayout $bytes $Path).Machine
}

function Get-PeImportsPowerShell([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $layout = Get-PeLayout $bytes $Path
    $names = New-Object System.Collections.Generic.List[string]
    # Directory 1: import table, 20-byte descriptors, Name RVA at +12, terminated by an all-zero descriptor.
    if ($layout.DirCount -gt 1) {
        $rva = [BitConverter]::ToUInt32($bytes, $layout.Dirs + 8)
        if ($rva -ne 0) {
            $o = ConvertTo-FileOffset $layout $rva $Path
            while ($true) {
                $nameRva = [BitConverter]::ToUInt32($bytes, $o + 12)
                $thunk = [BitConverter]::ToUInt32($bytes, $o)
                $iat = [BitConverter]::ToUInt32($bytes, $o + 16)
                if ($nameRva -eq 0 -and $thunk -eq 0 -and $iat -eq 0) { break }
                $names.Add((Read-AsciiZ $bytes (ConvertTo-FileOffset $layout $nameRva $Path)))
                $o += 20
            }
        }
    }
    # Directory 13: delay-load import table, 32-byte descriptors, DllName at +4 (an RVA when Attributes bit 0 is set,
    # a VA in the old VC6 format), terminated by DllName = 0.
    if ($layout.DirCount -gt 13) {
        $rva = [BitConverter]::ToUInt32($bytes, $layout.Dirs + 13 * 8)
        if ($rva -ne 0) {
            $o = ConvertTo-FileOffset $layout $rva $Path
            while ($true) {
                $attributes = [BitConverter]::ToUInt32($bytes, $o)
                $nameRef = [uint64][BitConverter]::ToUInt32($bytes, $o + 4)
                if ($nameRef -eq 0) { break }
                if (($attributes -band 1) -eq 0) { $nameRef -= $layout.ImageBase }
                $names.Add((Read-AsciiZ $bytes (ConvertTo-FileOffset $layout $nameRef $Path)))
                $o += 32
            }
        }
    }
    return @($names)
}

function Get-PeImportsDumpbin([string]$Dumpbin, [string]$Path) {
    $out = & $Dumpbin -nologo -dependents $Path
    if ($LASTEXITCODE -ne 0) { throw "dumpbin -dependents failed with exit code $LASTEXITCODE for $Path" }
    $names = New-Object System.Collections.Generic.List[string]
    $inList = $false
    foreach ($line in $out) {
        if ($line -match 'Image has the following (delay load )?dependencies:') { $inList = $true; continue }
        if ($line -match '^\s*Summary\s*$') { $inList = $false; continue }
        if ($inList -and $line -match '^\s+(\S+\.(dll|exe|sys|drv|ocx|cpl))\s*$') { $names.Add($Matches[1]) }
    }
    return @($names)
}

$Dir = (Resolve-Path -LiteralPath $Dir).Path
$dumpbin = $null
if ($Parser -ne 'PowerShell') {
    $dumpbin = Find-Dumpbin
    if (-not $dumpbin -and $Parser -eq 'Dumpbin') { throw 'dumpbin.exe was not found through vswhere' }
}
if ($dumpbin) { Write-Host "Native import check: parser dumpbin ($dumpbin)" } else { Write-Host 'Native import check: parser PowerShell PE reader' }
Write-Host "Native import check: folder $Dir"

$queue = New-Object System.Collections.Generic.Queue[string]
foreach ($f in $Files) { $queue.Enqueue($f) }
$seen = @{}
$problems = New-Object System.Collections.Generic.List[string]

while ($queue.Count -gt 0) {
    $name = $queue.Dequeue()
    $key = $name.ToLowerInvariant()
    if ($seen.ContainsKey($key)) { continue }
    $seen[$key] = $true

    $path = Join-Path $Dir $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $problems.Add("$name is missing from $Dir")
        continue
    }
    $machine = Get-PeMachine $path
    if ($machine -ne 0x8664) {
        $problems.Add("$name is not an x64 image (machine 0x$('{0:X4}' -f $machine))")
    }
    $imports = if ($dumpbin) { Get-PeImportsDumpbin $dumpbin $path } else { Get-PeImportsPowerShell $path }
    $parts = @()
    foreach ($imp in ($imports | Sort-Object -Unique)) {
        if (Test-SystemDll $imp) {
            $parts += "$imp [system]"
        } elseif (Test-Path -LiteralPath (Join-Path $Dir $imp) -PathType Leaf) {
            $parts += "$imp [local]"
            $queue.Enqueue($imp)
        } else {
            $parts += "$imp [MISSING]"
            $problems.Add("$name imports $imp, which is neither in $Dir nor a Windows system DLL")
        }
    }
    Write-Host "  $name -> $($parts -join ', ')"
}

if ($problems.Count -gt 0) {
    throw "Native import closure is incomplete:`n  - $($problems -join "`n  - ")"
}
Write-Host "Native import check: OK, $($seen.Count) native file(s) closed"
