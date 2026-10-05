# Bridge Plugin redeploy: собирает Release, делает timestamped бэкап
# текущих бинарей в Tekla extensions folder, копирует свежие .dll/.exe/.inp.
#
# Usage:
#   .\redeploy_plugin.ps1                                 # default Tekla 2025.0
#   .\redeploy_plugin.ps1 -TeklaVersion 2024.0
#   .\redeploy_plugin.ps1 -SkipBuild                      # переиспользовать существующий bin/Release
#   .\redeploy_plugin.ps1 -DryRun                         # показать что будет, не копировать
#
# Откат: каждый файл бэкапится в *.bak-<YYYYMMDD-HHmmss>. Чтобы откатиться:
#   $bak = Get-ChildItem $extDir -Filter "*.bak-*" | Sort-Object LastWriteTime -Descending | Select-Object -First 7
#   $bak | ForEach-Object { Copy-Item $_.FullName ($_.FullName -replace '\.bak-.+$', '') -Force }

[CmdletBinding()]
param(
    [string]$TeklaVersion = "2025.0",
    [string]$TeklaExtensionsRoot = $null,
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Write-Host "[redeploy_plugin] repo root: $repoRoot"

if (-not $TeklaExtensionsRoot) {
    $TeklaExtensionsRoot = "C:\TeklaStructures\$TeklaVersion\Environments\common\Extensions\BridgeComponent"
}
Write-Host "[redeploy_plugin] target: $TeklaExtensionsRoot"

if (-not (Test-Path $TeklaExtensionsRoot)) {
    throw "Target Tekla extensions dir does not exist: $TeklaExtensionsRoot. Создать вручную и повторить."
}

if (-not $SkipBuild) {
    Write-Host "[redeploy_plugin] dotnet build Release..."
    Push-Location $repoRoot
    try {
        dotnet build "Bridge.Plugin.sln" -c Release | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    } finally { Pop-Location }
}

# Бэкап + копирование. Каждый файл = один шаг; failure на любом не оставит
# half-applied state потому что Tekla не использует bin до перезапуска.
$stamp = (Get-Date -Format "yyyyMMdd-HHmmss")
# Source DLL — единственная сборка Bridge.TeklaPlugin.dll, которая в проде
# деплоится как два байт-идентичных файла под именами BridgeGirderPlugin.dll
# и BridgeMvpPlugin.dll (точное зеркало прежнего .bak-20260311 — cmp на них
# даёт IDENTICAL). Тем же подходом: один build, два copy.
# Bridge.Plugin.Geometry source вкомпилирован в TeklaBridge.exe через
# <Compile Include>, поэтому отдельный .dll не нужен.
$pairs = @(
    @{ src = "src\BridgeGirderPlugin\bin\Release\net48\Bridge.TeklaPlugin.dll"; dst = "BridgeGirderPlugin.dll" },
    @{ src = "src\BridgeGirderPlugin\bin\Release\net48\Bridge.TeklaPlugin.dll"; dst = "BridgeMvpPlugin.dll" },
    @{ src = "src\CrossMemberPlugin\bin\Release\net48\CrossMemberPlugin.dll";   dst = "CrossMemberPlugin.dll" },
    @{ src = "src\TeklaBridge\bin\Release\net48\TeklaBridge.exe";               dst = "TeklaBridge.exe" },
    @{ src = "deploy\component\BridgeGirderPlugin.inp";                          dst = "BridgeGirderPlugin.inp" },
    @{ src = "deploy\component\BridgeMvpPlugin.inp";                             dst = "BridgeMvpPlugin.inp" },
    @{ src = "deploy\component\CrossMemberPlugin.inp";                           dst = "CrossMemberPlugin.inp" }
)

# Cleanup: удалить отдельный Bridge.Plugin.Geometry.dll если был от
# предыдущей попытки. Теперь геометрия скомпилирована внутрь TeklaBridge.exe,
# отдельный .dll создал бы дублирование типов с риском type-id conflict при
# probing в Tekla AppDomain.
$staleGeometry = Join-Path $TeklaExtensionsRoot "Bridge.Plugin.Geometry.dll"
if (Test-Path $staleGeometry) {
    if ($DryRun) {
        Write-Host "[dry-run] would rename stale Bridge.Plugin.Geometry.dll → .stale-$stamp"
    } else {
        try {
            Move-Item $staleGeometry "$staleGeometry.stale-$stamp" -Force
            Write-Host "  cleanup: Bridge.Plugin.Geometry.dll → .stale-$stamp (compiled-into-TeklaBridge.exe теперь)"
        } catch {
            Write-Warning "Не удалось убрать stale Bridge.Plugin.Geometry.dll (locked?): $($_.Exception.Message)"
        }
    }
}

foreach ($pair in $pairs) {
    $srcPath = Join-Path $repoRoot $pair.src
    $dstPath = Join-Path $TeklaExtensionsRoot $pair.dst
    if (-not (Test-Path $srcPath)) {
        Write-Warning "Source missing: $srcPath — пропуск (вероятно SkipBuild без актуального bin)."
        continue
    }
    if (Test-Path $dstPath) {
        $backupPath = "$dstPath.bak-$stamp"
        if ($DryRun) {
            Write-Host "[dry-run] would backup: $dstPath -> $backupPath"
        } else {
            # Tekla держит DLL'ки как loaded image — Copy-Item для backup работает
            # (read), но Copy-Item для overwrite потом ПАДАЕТ потому что target
            # locked для write. Решение: всегда Move-Item (rename) для backup —
            # NTFS rename работает на open files и освобождает path. Затем
            # обычный Copy-Item source→dst на свободный path.
            Move-Item $dstPath $backupPath -Force
            Write-Host "  backup: $($pair.dst) -> $($pair.dst).bak-$stamp"
        }
    }
    if ($DryRun) {
        Write-Host "[dry-run] would copy:   $srcPath -> $dstPath"
    } else {
        Copy-Item $srcPath $dstPath -Force
        Write-Host "  copy:   $($pair.dst)"
    }
}

Write-Host ""
Write-Host "[redeploy_plugin] done. Restart Tekla чтобы плагин перезагрузился."
if (-not $DryRun) {
    Write-Host "Откат: см. комментарий в начале скрипта или удалите .bak-$stamp файлы если всё ОК."
}
