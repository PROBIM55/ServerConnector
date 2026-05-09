# Создаёт/обновляет Scheduled Task ConnectorYandexBackup.
#
# Запускается один раз вручную на VPS после того как настроен rclone-config
# (OAuth) и проверен initial backup. Идемпотентен — можно перезапускать.
#
# Расписание: ежедневно в 03:00 MSK (Windows time = UTC+3 на VPS, проверить
# что timezone правильный).

$ErrorActionPreference = 'Stop'

$taskName = 'ConnectorYandexBackup'
$script   = 'C:\Connector\src\scripts\backup_bim_to_yandex.ps1'

if (-not (Test-Path $script)) { throw "Backup script not found: $script" }

$tr = "powershell -NoProfile -ExecutionPolicy Bypass -File $script"

# native-command stderr с EAP=Stop триггерит исключение даже если schtasks
# /Delete выдаёт "task not found" (норма для первого запуска). Снижаем EAP
# на короткое время и опираемся только на $LASTEXITCODE для /Create.
$oldEAP = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & schtasks /Delete /TN $taskName /F 2>&1 | Out-Null
    & schtasks /Create /SC DAILY /ST 03:00 /TN $taskName /TR $tr /RU SYSTEM /RL HIGHEST /F | Out-Null
    $createExit = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $oldEAP
}
if ($createExit -ne 0) { throw "schtasks /Create exited $createExit" }

Write-Host "Task $taskName created/updated:"
Get-ScheduledTask -TaskName $taskName | Select-Object TaskName, State
(Get-ScheduledTask $taskName).Triggers | Select-Object StartBoundary, DaysInterval | Format-List
(Get-ScheduledTask $taskName).Actions | Format-List Execute, Arguments
