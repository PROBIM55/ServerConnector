# Идёмпотентная настройка rclone remote 'yandex_disk' для Yandex.Disk.
#
# Запускается на VPS один раз после получения OAuth-токена (см. doc/YANDEX_BACKUP_RU.md).
# Токен передаётся обязательным параметром -Token (строка JSON, в одинарных кавычках).
#
# Создаёт C:\Connector\runtime\rclone.conf — конфиг хранится в runtime\
# (не в src\), потому что содержит секрет.

param(
    [Parameter(Mandatory=$true)][string]$Token
)

$ErrorActionPreference = 'Stop'

$rclone       = 'C:\Tools\rclone\rclone.exe'
$rcloneConfig = 'C:\Connector\runtime\rclone.conf'
$remoteName   = 'yandex_disk'

if (-not (Test-Path $rclone)) { throw "rclone not installed at $rclone" }

# Validate token is JSON-ish (must contain access_token key)
if ($Token -notmatch '"access_token"') {
    throw "Token does not look like rclone OAuth JSON (no 'access_token' field)"
}

# rclone config create yandex_disk yandex token <json>
$args = @(
    'config', 'create', $remoteName, 'yandex',
    'token', $Token,
    '--config', $rcloneConfig,
    '--non-interactive'
)
& $rclone @args
if ($LASTEXITCODE -ne 0) { throw "rclone config create failed: $LASTEXITCODE" }

Write-Host ''
Write-Host '=== Test connection ==='
& $rclone --config $rcloneConfig lsd "${remoteName}:" --max-depth 1
if ($LASTEXITCODE -ne 0) { throw "rclone lsd failed: $LASTEXITCODE" }

Write-Host ''
Write-Host "RCLONE_CONFIG_OK at $rcloneConfig"
