# Daily backup of \\62.113.36.107\BIM_Models -> Yandex.Disk via rclone.
#
# Запускается Scheduled Task'ом ConnectorYandexBackup ежедневно в 03:00 MSK
# (под SYSTEM, тот же runtime/.venv не нужен — это PowerShell + rclone.exe).
#
# Стратегия:
#   * source: C:\BIM_Models (full SMB share live на хосте)
#   * dest:   yandex_disk:Structura/BIM_Backup/current/
#   * archive: yandex_disk:Structura/BIM_Backup/archive/<yyyy-MM-dd>/
#       (туда rclone --backup-dir перемещает заменённые/удалённые файлы)
#
# rclone copy + --backup-dir сохраняет точечную историю изменений по дате.
# Через 90 дней (см. retention в конце скрипта) archive/<date>/ удаляется.
#
# Логи: C:\Connector\runtime\logs\backup_yandex.log с ротацией (rclone сам не
# ротирует; делаем перенос >50 MB в backup_yandex.log.<N>).

$ErrorActionPreference = 'Stop'

$rclone        = 'C:\Tools\rclone\rclone.exe'
$rcloneConfig  = 'C:\Connector\runtime\rclone.conf'
$source        = 'C:\BIM_Models'
$remoteName    = 'yandex_disk'
$destBase      = 'Structura/BIM_Backup'
$today         = Get-Date -Format 'yyyy-MM-dd'
$logFile       = 'C:\Connector\runtime\logs\backup_yandex.log'
$logDir        = Split-Path $logFile -Parent
$retentionDays = 90

# Pre-flight
if (-not (Test-Path $rclone)) { throw "rclone not installed at $rclone" }
if (-not (Test-Path $rcloneConfig)) { throw "rclone config not found at $rcloneConfig (run OAuth setup first)" }
if (-not (Test-Path $source)) { throw "Source not found: $source" }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# Ротация лога перед запуском (если >50 MB)
if ((Test-Path $logFile) -and ((Get-Item $logFile).Length -gt 50MB)) {
    Move-Item $logFile "$logFile.$(Get-Date -Format 'yyyyMMdd-HHmmss')" -Force -ErrorAction SilentlyContinue
}

$startedAt = Get-Date
"=== BACKUP START $(($startedAt).ToString('s')) (today=$today) ===" | Add-Content -Path $logFile

# Основной sync с архивацией заменённых/удалённых файлов
$rcloneArgs = @(
    'sync', $source, "${remoteName}:${destBase}/current",
    '--config', $rcloneConfig,
    '--backup-dir', "${remoteName}:${destBase}/archive/$today",
    '--transfers', '8',
    '--checkers', '16',
    '--tpslimit', '5',
    '--log-file', $logFile,
    '--log-level', 'INFO',
    '--stats', '1m',
    '--exclude', '*.tmp',
    '--exclude', 'Thumbs.db',
    '--exclude', '~$*',
    '--exclude', 'desktop.ini'
)
& $rclone @rcloneArgs
$syncExit = $LASTEXITCODE

"=== SYNC EXIT $syncExit ($([math]::Round((New-TimeSpan -Start $startedAt -End (Get-Date)).TotalMinutes,1)) min) ===" |
    Add-Content -Path $logFile

if ($syncExit -ne 0) {
    Write-Error "rclone sync failed with exit $syncExit; see $logFile"
    exit $syncExit
}

# Retention: удаляем archive/<date>/ старше $retentionDays
"=== RETENTION cleanup (>${retentionDays}d) ===" | Add-Content -Path $logFile
$retentionArgs = @(
    'delete', "${remoteName}:${destBase}/archive",
    '--config', $rcloneConfig,
    '--min-age', "${retentionDays}d",
    '--rmdirs',
    '--log-file', $logFile,
    '--log-level', 'INFO'
)
& $rclone @retentionArgs
$retExit = $LASTEXITCODE

"=== RETENTION EXIT $retExit ===" | Add-Content -Path $logFile
"=== BACKUP DONE total=$([math]::Round((New-TimeSpan -Start $startedAt -End (Get-Date)).TotalMinutes,1)) min ===" |
    Add-Content -Path $logFile

# Retention exit code != 0 не считаем фатальным (старые файлы могут не существовать)
exit 0
