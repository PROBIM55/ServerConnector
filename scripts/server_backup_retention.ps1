# Backup retention policy для C:\Connector\backup\.
#
# Покрывает оба типа артефактов, которые туда складывает CI:
#   * connector.db.<sha>-<ts>           ← snapshot SQLite БД перед каждым деплоем
#   * ConnectorApi.old.xml.<ts>         ← XML предыдущего Scheduled Task'а
#   * server.archive-<date>             ← одноразовый snapshot старой раскладки
#
# Политика хранения:
#   - daily: оставлять все snapshot'ы за последние 30 дней
#   - weekly: дальше — один (новейший) per ISO-неделя за следующие 12 недель (~3 мес)
#   - monthly: дальше — один (новейший) per календарный месяц за следующие 6 мес
#   - всё старше 30 + 12*7 + 6*30 = 300 дней → удаляется
#
# Запускается ежедневно через Scheduled Task ConnectorBackupRetention.
# При -DryRun ничего не удаляется, только пишет в лог что было бы удалено.

param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$backupDir = 'C:\Connector\backup'
$logsDir   = 'C:\Connector\runtime\logs'
$logFile   = Join-Path $logsDir 'backup_retention.log'

if (-not (Test-Path $backupDir)) {
    "[$(Get-Date -Format s)] backup dir not found: $backupDir" | Add-Content -Path $logFile -ErrorAction SilentlyContinue
    return
}
if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}

$now      = Get-Date
$daily    = 30
$weekly   = $daily + 12 * 7
$monthly  = $weekly + 6 * 30

# Считаем как один артефакт каждый файл и каждую папку верхнего уровня
$items = Get-ChildItem -Path $backupDir -Force | Sort-Object LastWriteTime

$keepDaily    = @()   # все snapshot'ы за последние 30 дней — все сохраняются
$keepBucketed = @{}   # bucketKey -> newest в bucket (для weekly/monthly)
$delete       = @()

foreach ($item in $items) {
    $ageDays = [Math]::Floor(($now - $item.LastWriteTime).TotalDays)

    if ($ageDays -le $daily) {
        # Daily — без бакетирования, держим всё.
        $keepDaily += $item
        continue
    }

    if ($ageDays -le $weekly) {
        $iso = [Globalization.ISOWeek]::GetWeekOfYear($item.LastWriteTime)
        $bucket = "weekly-$($item.LastWriteTime.Year)-W$('{0:D2}' -f $iso)"
    } elseif ($ageDays -le $monthly) {
        $bucket = "monthly-$($item.LastWriteTime.ToString('yyyy-MM'))"
    } else {
        $delete += $item
        continue
    }

    # Sorted ascending → новейший в bucket затирает предыдущий, тот уходит в delete.
    if ($keepBucketed.ContainsKey($bucket)) {
        $delete += $keepBucketed[$bucket]
    }
    $keepBucketed[$bucket] = $item
}

$kept = $keepDaily + @($keepBucketed.Values)

"[$(Get-Date -Format s)] retention scan: total=$($items.Count) keep=$($kept.Count) delete=$($delete.Count) dry_run=$($DryRun.IsPresent)" |
    Add-Content -Path $logFile

foreach ($item in $delete) {
    $msg = "[$(Get-Date -Format s)] delete: $($item.FullName) (age=$([Math]::Floor(($now - $item.LastWriteTime).TotalDays))d, size=$($item.Length))"
    if ($DryRun) {
        "$msg [DRY-RUN]" | Add-Content -Path $logFile
    } else {
        try {
            Remove-Item -LiteralPath $item.FullName -Recurse -Force
            $msg | Add-Content -Path $logFile
        } catch {
            "[$(Get-Date -Format s)] delete-failed: $($item.FullName): $_" | Add-Content -Path $logFile
        }
    }
}

if ($DryRun -or $delete.Count -eq 0) {
    Write-Host "RETENTION_DONE keep=$($kept.Count) delete=$($delete.Count) dry_run=$($DryRun.IsPresent)"
}
