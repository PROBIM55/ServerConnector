[CmdletBinding()]
param(
    [string]$TeklaVersion = "2020.0",
    [string]$TeklaIniPath = $null
)

$ErrorActionPreference = "Stop"
if (-not $TeklaIniPath) {
    $TeklaIniPath = "C:\TeklaStructures\$TeklaVersion\nt\bin\teklastructures.ini"
}
if (-not (Test-Path $TeklaIniPath)) {
    throw "Tekla settings file does not exist: $TeklaIniPath"
}

$content = Get-Content -LiteralPath $TeklaIniPath -Raw
if ($content -match '(?im)^\s*set\s+XS_PLUGIN_DEVELOPER_MODE\s*=\s*TRUE\s*$') {
    Write-Host "[fachwerk] XS_PLUGIN_DEVELOPER_MODE is already enabled."
    exit 0
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backup = "$TeklaIniPath.bak-$stamp"
Copy-Item -LiteralPath $TeklaIniPath -Destination $backup -Force

if ($content -match '(?im)^\s*set\s+XS_PLUGIN_DEVELOPER_MODE\s*=.*$') {
    $content = [regex]::Replace($content, '(?im)^\s*set\s+XS_PLUGIN_DEVELOPER_MODE\s*=.*$', 'set XS_PLUGIN_DEVELOPER_MODE=TRUE')
} else {
    $content = $content.TrimEnd() + [Environment]::NewLine + [Environment]::NewLine +
        '/** Fachwerk native component: reload plugin DLLs during development. **/' + [Environment]::NewLine +
        'set XS_PLUGIN_DEVELOPER_MODE=TRUE' + [Environment]::NewLine
}

[IO.File]::WriteAllText($TeklaIniPath, $content, [Text.Encoding]::Default)
Write-Host "[fachwerk] XS_PLUGIN_DEVELOPER_MODE enabled. Backup: $backup"
Write-Host "[fachwerk] Restart Tekla once. Later updates use ReloadFachwerkPlugin macro."
