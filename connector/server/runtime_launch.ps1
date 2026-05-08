# Production launcher for Connector API.
#
# Запускается Scheduled Task'ом ConnectorApi на VPS.
# Ожидаемая раскладка на сервере (Phase 1+):
#   C:\Connector\src\connector\server\   <- этот скрипт + app.py (код из git)
#   C:\Connector\runtime\.venv\          <- Python virtual environment
#   C:\Connector\runtime\config.json     <- runtime config (persistent)
#   C:\Connector\runtime\connector.db    <- SQLite (persistent)
#   C:\Connector\runtime\logs\           <- runner и uvicorn логи
#
# Если переменные окружения CONNECTOR_CONFIG_PATH / CONNECTOR_DB_PATH
# не выставлены вызывающей стороной, скрипт устанавливает их сам, чтобы
# app.py читал данные из C:\Connector\runtime\.

$ErrorActionPreference = 'Stop'

$serverSrcDir = 'C:\Connector\src\connector\server'
$runtimeDir   = 'C:\Connector\runtime'
$venvPython   = Join-Path $runtimeDir '.venv\Scripts\python.exe'
$logsDir      = Join-Path $runtimeDir 'logs'
$runnerLog    = Join-Path $logsDir 'runner.log'
$outLog       = Join-Path $logsDir 'uvicorn.out.log'
$errLog       = Join-Path $logsDir 'uvicorn.err.log'

if (-not (Test-Path $logsDir)) {
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}

if (-not $env:CONNECTOR_CONFIG_PATH) {
    $env:CONNECTOR_CONFIG_PATH = Join-Path $runtimeDir 'config.json'
}
if (-not $env:CONNECTOR_DB_PATH) {
    $env:CONNECTOR_DB_PATH = Join-Path $runtimeDir 'connector.db'
}

"[$(Get-Date -Format s)] runner start (cfg=$env:CONNECTOR_CONFIG_PATH db=$env:CONNECTOR_DB_PATH)" |
    Add-Content -Path $runnerLog

# Останавливаем уже запущенные uvicorn app:app, чтобы избежать дубликатов на 8080.
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'python.exe' -and $_.CommandLine -like '*uvicorn app:app*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Start-Process -WindowStyle Hidden `
    -WorkingDirectory $serverSrcDir `
    -FilePath $venvPython `
    -ArgumentList '-m','uvicorn','app:app','--host','0.0.0.0','--port','8080' `
    -RedirectStandardOutput $outLog `
    -RedirectStandardError $errLog

Start-Sleep -Seconds 2

$proc = Get-CimInstance Win32_Process |
    Where-Object { $_.Name -eq 'python.exe' -and $_.CommandLine -like '*uvicorn app:app*' } |
    Select-Object -First 1

if ($proc) {
    "[$(Get-Date -Format s)] runner success pid=$($proc.ProcessId)" | Add-Content -Path $runnerLog
} else {
    "[$(Get-Date -Format s)] runner failed to start uvicorn" | Add-Content -Path $runnerLog
    throw "uvicorn did not start"
}
